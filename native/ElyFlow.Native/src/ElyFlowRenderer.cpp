// ELYFLOW experimental renderer.
//
// libmpv's render API only exposes OpenGL and software targets (render.h),
// never raw D3D11 textures. The only clean zero-copy bridge on NVIDIA is
// WGL_NV_DX_interop2: a D3D11 texture is registered as the storage of a GL
// texture, mpv renders into an FBO wrapping it, and the very same GPU memory
// is then visible to D3D11 — no CPU roundtrip anywhere.
//
// Pipeline (all on one dedicated render thread):
//   mpv decode -> mpv_render_context_render(FBO/GL) -> D3D11 texture
//   -> CopyResource into shared "current" (GPU copy)
//   -> ElyFlow_ProcessFrame (NvOFFRUC, fence-synchronised, official sample
//      pattern: SHARED|NTHANDLE textures + ID3D11Fence)
//   -> interpolated texture -> swapchain backbuffer -> Present.
// Fallback: any failure is reported through ElyFlowRendererState and the C#
// side reverts to the classic mpv HWND backend.

#include "ElyFlowNative.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <avrt.h>
#include <d3d11_4.h>
#include <dxgi1_3.h>
#include <GL/gl.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <future>
#include <mutex>
#include <string>
#include <thread>

#include <mpv/client.h>
#include <mpv/render.h>
#include <mpv/render_gl.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "opengl32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "avrt.lib")

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

// ---------------------------------------------------------------------------
// GL / WGL declarations not provided by the ancient <GL/gl.h>.
// ---------------------------------------------------------------------------
#ifndef GL_FRAMEBUFFER
#define GL_FRAMEBUFFER 0x8D40
#define GL_COLOR_ATTACHMENT0 0x8CE0
#define GL_FRAMEBUFFER_COMPLETE 0x8CD5
#endif
#define WGL_ACCESS_READ_WRITE_NV 0x0001

typedef void (WINAPI* PFNGLGENFRAMEBUFFERS)(GLsizei, GLuint*);
typedef void (WINAPI* PFNGLDELETEFRAMEBUFFERS)(GLsizei, const GLuint*);
typedef void (WINAPI* PFNGLBINDFRAMEBUFFER)(GLenum, GLuint);
typedef void (WINAPI* PFNGLFRAMEBUFFERTEXTURE2D)(GLenum, GLenum, GLenum, GLuint, GLint);
typedef GLenum (WINAPI* PFNGLCHECKFRAMEBUFFERSTATUS)(GLenum);

typedef HANDLE (WINAPI* PFNWGLDXOPENDEVICENV)(void*);
typedef BOOL (WINAPI* PFNWGLDXCLOSEDEVICENV)(HANDLE);
typedef HANDLE (WINAPI* PFNWGLDXREGISTEROBJECTNV)(HANDLE, void*, GLuint, GLenum, GLenum);
typedef BOOL (WINAPI* PFNWGLDXUNREGISTEROBJECTNV)(HANDLE, HANDLE);
typedef BOOL (WINAPI* PFNWGLDXLOCKOBJECTSNV)(HANDLE, GLint, HANDLE*);
typedef BOOL (WINAPI* PFNWGLDXUNLOCKOBJECTSNV)(HANDLE, GLint, HANDLE*);

// libmpv render exports, resolved from the libmpv-2.dll already loaded by the
// player process — no import library, no second copy of mpv.
typedef int (*PFN_mpv_render_context_create)(mpv_render_context**, mpv_handle*, mpv_render_param*);
typedef void (*PFN_mpv_render_context_set_update_callback)(mpv_render_context*, mpv_render_update_fn, void*);
typedef uint64_t (*PFN_mpv_render_context_update)(mpv_render_context*);
typedef int (*PFN_mpv_render_context_render)(mpv_render_context*, mpv_render_param*);
typedef void (*PFN_mpv_render_context_free)(mpv_render_context*);

namespace
{
    template <typename T> void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

    struct GlApi
    {
        PFNGLGENFRAMEBUFFERS GenFramebuffers = nullptr;
        PFNGLDELETEFRAMEBUFFERS DeleteFramebuffers = nullptr;
        PFNGLBINDFRAMEBUFFER BindFramebuffer = nullptr;
        PFNGLFRAMEBUFFERTEXTURE2D FramebufferTexture2D = nullptr;
        PFNGLCHECKFRAMEBUFFERSTATUS CheckFramebufferStatus = nullptr;

        PFNWGLDXOPENDEVICENV DXOpenDeviceNV = nullptr;
        PFNWGLDXCLOSEDEVICENV DXCloseDeviceNV = nullptr;
        PFNWGLDXREGISTEROBJECTNV DXRegisterObjectNV = nullptr;
        PFNWGLDXUNREGISTEROBJECTNV DXUnregisterObjectNV = nullptr;
        PFNWGLDXLOCKOBJECTSNV DXLockObjectsNV = nullptr;
        PFNWGLDXUNLOCKOBJECTSNV DXUnlockObjectsNV = nullptr;

        bool LoadFbo()
        {
            GenFramebuffers = reinterpret_cast<PFNGLGENFRAMEBUFFERS>(wglGetProcAddress("glGenFramebuffers"));
            DeleteFramebuffers = reinterpret_cast<PFNGLDELETEFRAMEBUFFERS>(wglGetProcAddress("glDeleteFramebuffers"));
            BindFramebuffer = reinterpret_cast<PFNGLBINDFRAMEBUFFER>(wglGetProcAddress("glBindFramebuffer"));
            FramebufferTexture2D = reinterpret_cast<PFNGLFRAMEBUFFERTEXTURE2D>(wglGetProcAddress("glFramebufferTexture2D"));
            CheckFramebufferStatus = reinterpret_cast<PFNGLCHECKFRAMEBUFFERSTATUS>(wglGetProcAddress("glCheckFramebufferStatus"));
            return GenFramebuffers && DeleteFramebuffers && BindFramebuffer && FramebufferTexture2D && CheckFramebufferStatus;
        }

        bool LoadInterop()
        {
            DXOpenDeviceNV = reinterpret_cast<PFNWGLDXOPENDEVICENV>(wglGetProcAddress("wglDXOpenDeviceNV"));
            DXCloseDeviceNV = reinterpret_cast<PFNWGLDXCLOSEDEVICENV>(wglGetProcAddress("wglDXCloseDeviceNV"));
            DXRegisterObjectNV = reinterpret_cast<PFNWGLDXREGISTEROBJECTNV>(wglGetProcAddress("wglDXRegisterObjectNV"));
            DXUnregisterObjectNV = reinterpret_cast<PFNWGLDXUNREGISTEROBJECTNV>(wglGetProcAddress("wglDXUnregisterObjectNV"));
            DXLockObjectsNV = reinterpret_cast<PFNWGLDXLOCKOBJECTSNV>(wglGetProcAddress("wglDXLockObjectsNV"));
            DXUnlockObjectsNV = reinterpret_cast<PFNWGLDXUNLOCKOBJECTSNV>(wglGetProcAddress("wglDXUnlockObjectsNV"));
            return DXOpenDeviceNV && DXCloseDeviceNV && DXRegisterObjectNV && DXUnregisterObjectNV && DXLockObjectsNV && DXUnlockObjectsNV;
        }
    };

    struct MpvApi
    {
        PFN_mpv_render_context_create Create = nullptr;
        PFN_mpv_render_context_set_update_callback SetUpdateCallback = nullptr;
        PFN_mpv_render_context_update Update = nullptr;
        PFN_mpv_render_context_render Render = nullptr;
        PFN_mpv_render_context_free Free = nullptr;

        bool Load()
        {
            const HMODULE mod = GetModuleHandleW(L"libmpv-2.dll");
            if (!mod) return false;
            Create = reinterpret_cast<PFN_mpv_render_context_create>(GetProcAddress(mod, "mpv_render_context_create"));
            SetUpdateCallback = reinterpret_cast<PFN_mpv_render_context_set_update_callback>(GetProcAddress(mod, "mpv_render_context_set_update_callback"));
            Update = reinterpret_cast<PFN_mpv_render_context_update>(GetProcAddress(mod, "mpv_render_context_update"));
            Render = reinterpret_cast<PFN_mpv_render_context_render>(GetProcAddress(mod, "mpv_render_context_render"));
            Free = reinterpret_cast<PFN_mpv_render_context_free>(GetProcAddress(mod, "mpv_render_context_free"));
            return Create && SetUpdateCallback && Update && Render && Free;
        }
    };

    void* MpvGetProcAddress(void*, const char* name)
    {
        void* p = reinterpret_cast<void*>(wglGetProcAddress(name));
        if (p) return p;
        static HMODULE gl = GetModuleHandleW(L"opengl32.dll");
        return gl ? reinterpret_cast<void*>(GetProcAddress(gl, name)) : nullptr;
    }

    // Hidden window + legacy WGL context (NVIDIA hands out a full
    // compatibility context, which is all mpv's GL renderer needs).
    struct GlContext
    {
        HWND wnd = nullptr;
        HDC dc = nullptr;
        HGLRC rc = nullptr;

        bool Create()
        {
            const wchar_t* cls = L"ElyFlowHiddenGL";
            WNDCLASSW wc{};
            wc.lpfnWndProc = DefWindowProcW;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = cls;
            RegisterClassW(&wc); // ERROR_CLASS_ALREADY_EXISTS is fine.

            wnd = CreateWindowExW(0, cls, L"", WS_OVERLAPPED, 0, 0, 8, 8, nullptr, nullptr, wc.hInstance, nullptr);
            if (!wnd) return false;
            dc = GetDC(wnd);
            if (!dc) return false;

            PIXELFORMATDESCRIPTOR pfd{};
            pfd.nSize = sizeof(pfd);
            pfd.nVersion = 1;
            pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
            pfd.iPixelType = PFD_TYPE_RGBA;
            pfd.cColorBits = 32;
            const int format = ChoosePixelFormat(dc, &pfd);
            if (format == 0 || !SetPixelFormat(dc, format, &pfd)) return false;

            rc = wglCreateContext(dc);
            return rc && wglMakeCurrent(dc, rc);
        }

        void Destroy()
        {
            if (rc) { wglMakeCurrent(nullptr, nullptr); wglDeleteContext(rc); rc = nullptr; }
            if (dc && wnd) { ReleaseDC(wnd, dc); dc = nullptr; }
            if (wnd) { DestroyWindow(wnd); wnd = nullptr; }
        }
    };

    struct Renderer
    {
        // Immutable after Create.
        mpv_handle* mpv = nullptr;
        HWND target = nullptr;

        // Render-thread state.
        GlContext gl;
        GlApi api;
        MpvApi mpvApi;
        mpv_render_context* renderContext = nullptr;

        ID3D11Device* device = nullptr;
        ID3D11Device5* device5 = nullptr;
        ID3D11DeviceContext* context = nullptr;
        ID3D11DeviceContext4* context4 = nullptr;
        ID3D11VideoDevice* videoDevice = nullptr;
        ID3D11VideoContext* videoContext = nullptr;
        IDXGISwapChain1* swapChain = nullptr;
        ID3D11Fence* fence = nullptr;
        uint64_t fenceValue = 0;

        HANDLE interopDevice = nullptr;
        HANDLE interopTexture = nullptr;
        ID3D11Texture2D* renderTarget = nullptr;   // mpv renders here via GL
        ID3D11Texture2D* vsrOutput = nullptr;      // RTX VSR output at window size
        ID3D11VideoProcessorEnumerator* vsrEnumerator = nullptr;
        ID3D11VideoProcessor* vsrProcessor = nullptr;
        ID3D11VideoProcessorInputView* vsrInputView = nullptr;
        ID3D11VideoProcessorOutputView* vsrOutputView = nullptr;
        ID3D11Texture2D* frameA = nullptr;         // shared ring: previous / current
        ID3D11Texture2D* frameB = nullptr;
        ID3D11Texture2D* interpolated = nullptr;   // FRUC output (shared)
        ID3D11Texture2D* previous = nullptr;       // aliases of frameA/frameB
        ID3D11Texture2D* current = nullptr;
        GLuint glTexture = 0;
        GLuint fbo = 0;

        uint32_t width = 0;
        uint32_t height = 0;
        uint32_t renderWidth = 0;
        uint32_t renderHeight = 0;
        uint32_t swapWidth = 0;
        uint32_t swapHeight = 0;
        double lastTargetChangeQpc = 0;
        double lastFrucInitAttemptQpc = 0;
        bool vsrActive = false;
        uint32_t vsrFrameIndex = 0;
        uint32_t vsrContentWidth = 0;
        uint32_t vsrContentHeight = 0;
        uint32_t failedVsrSourceWidth = 0;
        uint32_t failedVsrSourceHeight = 0;
        uint32_t failedVsrOutputWidth = 0;
        uint32_t failedVsrOutputHeight = 0;
        double lastFrameQpc = 0;
        double frameInterval = 1.0 / 24.0;
        bool havePrevious = false;
        double averageWorkMs = 0;
        double maxWorkMs = 0;
        std::atomic<double> sourceFpsHint{ 0 };
        std::atomic<bool> vsrWanted{ false };
        std::atomic<bool> frucWanted{ false };
        std::atomic<uint32_t> sourceWidthHint{ 0 };
        std::atomic<uint32_t> sourceHeightHint{ 0 };

        std::thread thread;
        HANDLE wakeEvent = nullptr;
        HANDLE waitTimer = nullptr;
        std::atomic<bool> quit{ false };

        // Published state.
        std::mutex stateMutex;
        ElyFlowRendererState state{};

        void SetMessage(const char* msg)
        {
            std::lock_guard lock(stateMutex);
            strncpy_s(state.message, msg, _TRUNCATE);
        }
    };

    std::mutex g_rendererMutex;
    Renderer* g_renderer = nullptr;

    double QpcSeconds()
    {
        static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        return static_cast<double>(now.QuadPart) / static_cast<double>(freq.QuadPart);
    }

    void RefreshSourceCadence(Renderer& r, double observedDelta)
    {
        const double fps = r.sourceFpsHint.load(std::memory_order_relaxed);
        if (std::isfinite(fps) && fps >= 5.0 && fps <= 240.0)
        {
            r.frameInterval = 1.0 / fps;
        }
        else if (observedDelta > 0.005 && observedDelta < 0.25 &&
                 observedDelta < r.frameInterval * 1.15)
        {
            // Never learn renderer-induced backlog as the source cadence. The
            // previous EWMA made each late frame lengthen the next sleep.
            r.frameInterval = r.frameInterval * 0.9 + observedDelta * 0.1;
        }

        std::lock_guard lock(r.stateMutex);
        r.state.sourceFps = 1.0 / r.frameInterval;
    }

    void WaitUntil(Renderer& r, double deadline)
    {
        const double remaining = deadline - QpcSeconds();
        if (remaining <= 0 || r.quit.load(std::memory_order_relaxed)) return;

        if (r.waitTimer)
        {
            LARGE_INTEGER due{};
            due.QuadPart = -std::max<LONGLONG>(1, static_cast<LONGLONG>(remaining * 1e7));
            if (SetWaitableTimer(r.waitTimer, &due, 0, nullptr, nullptr, FALSE))
            {
                WaitForSingleObject(r.waitTimer,
                    static_cast<DWORD>(std::ceil(remaining * 1000.0)) + 2);
                return;
            }
        }

        const auto milliseconds = static_cast<DWORD>(
            std::max(0.0, std::floor(remaining * 1000.0)));
        if (milliseconds > 0) Sleep(milliseconds);
    }

    void OnMpvUpdate(void* ctx)
    {
        auto* r = static_cast<Renderer*>(ctx);
        if (r && r->wakeEvent) SetEvent(r->wakeEvent);
    }

    ID3D11Texture2D* CreateSharedTexture(ID3D11Device* device, uint32_t w, uint32_t h)
    {
        // Official NvOFFRUC sample pattern (fence sync): RGBA8 +
        // SHARED | SHARED_NTHANDLE, no bind flags required.
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w;
        desc.Height = h;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        ID3D11Texture2D* tex = nullptr;
        return SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &tex)) ? tex : nullptr;
    }

    constexpr GUID NvidiaPpeInterfaceGuid =
        { 0xd43ce1b3, 0x1f4b, 0x48ac, { 0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7 } };

    void ReleaseVsrResources(Renderer& r)
    {
        SafeRelease(r.vsrInputView);
        SafeRelease(r.vsrOutputView);
        SafeRelease(r.vsrProcessor);
        SafeRelease(r.vsrEnumerator);
        SafeRelease(r.vsrOutput);
        r.vsrActive = false;
        r.vsrFrameIndex = 0;
        r.vsrContentWidth = 0;
        r.vsrContentHeight = 0;
    }

    bool InitializeVsrResources(Renderer& r, uint32_t inputWidth, uint32_t inputHeight,
                                uint32_t outputWidth, uint32_t outputHeight, HRESULT& status)
    {
        status = E_FAIL;
        if (!r.videoDevice || !r.videoContext || !r.renderTarget) return false;

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate = { 60, 1 };
        content.InputWidth = inputWidth;
        content.InputHeight = inputHeight;
        content.OutputFrameRate = { 60, 1 };
        content.OutputWidth = outputWidth;
        content.OutputHeight = outputHeight;
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        status = r.videoDevice->CreateVideoProcessorEnumerator(&content, &r.vsrEnumerator);
        if (FAILED(status)) return false;

        UINT formatSupport = 0;
        status = r.vsrEnumerator->CheckVideoProcessorFormat(
            DXGI_FORMAT_R8G8B8A8_UNORM, &formatSupport);
        if (FAILED(status) ||
            (formatSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0 ||
            (formatSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        {
            if (SUCCEEDED(status)) status = DXGI_ERROR_UNSUPPORTED;
            return false;
        }

        status = r.videoDevice->CreateVideoProcessor(r.vsrEnumerator, 0, &r.vsrProcessor);
        if (FAILED(status)) return false;

        D3D11_TEXTURE2D_DESC outputDesc{};
        outputDesc.Width = outputWidth;
        outputDesc.Height = outputHeight;
        outputDesc.MipLevels = 1;
        outputDesc.ArraySize = 1;
        outputDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        outputDesc.SampleDesc.Count = 1;
        outputDesc.Usage = D3D11_USAGE_DEFAULT;
        outputDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        status = r.device->CreateTexture2D(&outputDesc, nullptr, &r.vsrOutput);
        if (FAILED(status)) return false;

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDesc{};
        inputViewDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        inputViewDesc.Texture2D.MipSlice = 0;
        inputViewDesc.Texture2D.ArraySlice = 0;
        status = r.videoDevice->CreateVideoProcessorInputView(
            r.renderTarget, r.vsrEnumerator, &inputViewDesc, &r.vsrInputView);
        if (FAILED(status)) return false;

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDesc{};
        outputViewDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        outputViewDesc.Texture2D.MipSlice = 0;
        status = r.videoDevice->CreateVideoProcessorOutputView(
            r.vsrOutput, r.vsrEnumerator, &outputViewDesc, &r.vsrOutputView);
        if (FAILED(status)) return false;

        RECT sourceRect{ 0, 0, static_cast<LONG>(inputWidth), static_cast<LONG>(inputHeight) };
        const double scale = std::min(outputWidth / static_cast<double>(inputWidth),
                                      outputHeight / static_cast<double>(inputHeight));
        const LONG fittedWidth = std::max<LONG>(1, static_cast<LONG>(std::lround(inputWidth * scale)));
        const LONG fittedHeight = std::max<LONG>(1, static_cast<LONG>(std::lround(inputHeight * scale)));
        r.vsrContentWidth = static_cast<uint32_t>(fittedWidth);
        r.vsrContentHeight = static_cast<uint32_t>(fittedHeight);
        RECT destinationRect{
            (static_cast<LONG>(outputWidth) - fittedWidth) / 2,
            (static_cast<LONG>(outputHeight) - fittedHeight) / 2,
            (static_cast<LONG>(outputWidth) + fittedWidth) / 2,
            (static_cast<LONG>(outputHeight) + fittedHeight) / 2
        };
        RECT outputRect{ 0, 0, static_cast<LONG>(outputWidth), static_cast<LONG>(outputHeight) };
        r.videoContext->VideoProcessorSetStreamFrameFormat(
            r.vsrProcessor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        r.videoContext->VideoProcessorSetStreamSourceRect(r.vsrProcessor, 0, TRUE, &sourceRect);
        r.videoContext->VideoProcessorSetStreamDestRect(r.vsrProcessor, 0, TRUE, &destinationRect);
        r.videoContext->VideoProcessorSetOutputTargetRect(r.vsrProcessor, TRUE, &outputRect);
        r.videoContext->VideoProcessorSetStreamAutoProcessingMode(r.vsrProcessor, 0, FALSE);
        r.videoContext->VideoProcessorSetStreamOutputRate(
            r.vsrProcessor, 0, D3D11_VIDEO_PROCESSOR_OUTPUT_RATE_NORMAL, FALSE, nullptr);
        D3D11_VIDEO_COLOR background{};
        background.RGBA.A = 1.0f;
        r.videoContext->VideoProcessorSetOutputBackgroundColor(r.vsrProcessor, FALSE, &background);

        struct NvidiaExtension { unsigned int version; unsigned int method; unsigned int enable; };
        NvidiaExtension extension{ 1, 2, 1 };
        status = r.videoContext->VideoProcessorSetStreamExtension(
            r.vsrProcessor, 0, &NvidiaPpeInterfaceGuid, sizeof(extension), &extension);
        if (FAILED(status)) return false;

        r.vsrActive = true;
        status = S_OK;
        return true;
    }

    bool RunVsr(Renderer& r, HRESULT& status)
    {
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = r.vsrInputView;
        stream.InputFrameOrField = r.vsrFrameIndex;
        status = r.videoContext->VideoProcessorBlt(
            r.vsrProcessor, r.vsrOutputView, r.vsrFrameIndex++, 1, &stream);
        return SUCCEEDED(status);
    }

    void ReleaseTargets(Renderer& r)
    {
        // NvOFFRUC keeps registrations for these COM resources. Unregister
        // them before releasing the textures (window resize/fullscreen), never
        // after; the old order could leave the SDK holding freed resources.
        ElyFlow_Shutdown();
        ReleaseVsrResources(r);

        if (r.interopTexture && r.api.DXUnregisterObjectNV)
        {
            r.api.DXUnregisterObjectNV(r.interopDevice, r.interopTexture);
            r.interopTexture = nullptr;
        }
        if (r.fbo) { r.api.DeleteFramebuffers(1, &r.fbo); r.fbo = 0; }
        if (r.glTexture) { glDeleteTextures(1, &r.glTexture); r.glTexture = 0; }
        SafeRelease(r.renderTarget);
        SafeRelease(r.frameA);
        SafeRelease(r.frameB);
        SafeRelease(r.interpolated);
        r.previous = r.current = nullptr;
        r.havePrevious = false;
        {
            std::lock_guard lock(r.stateMutex);
            r.state.texturesShared = 0;
            r.state.frucInitialized = 0;
            r.state.width = 0;
            r.state.height = 0;
            r.state.vsrActive = 0;
            r.state.vsrInputWidth = 0;
            r.state.vsrInputHeight = 0;
            r.state.vsrContentWidth = 0;
            r.state.vsrContentHeight = 0;
        }
    }

    bool EnsureTargets(Renderer& r, uint32_t w, uint32_t h)
    {
        if (w == 0 || h == 0) return false;
        const uint32_t sourceWidth = r.sourceWidthHint.load(std::memory_order_relaxed);
        const uint32_t sourceHeight = r.sourceHeightHint.load(std::memory_order_relaxed);
        const bool upscaleRequested = r.vsrWanted.load(std::memory_order_relaxed) &&
            sourceWidth > 0 && sourceHeight > 0 &&
            (sourceWidth < w || sourceHeight < h) &&
            r.videoDevice && r.videoContext;
        const bool rejectedSameConfiguration =
            sourceWidth == r.failedVsrSourceWidth && sourceHeight == r.failedVsrSourceHeight &&
            w == r.failedVsrOutputWidth && h == r.failedVsrOutputHeight;
        const bool useVsr = upscaleRequested && !rejectedSameConfiguration;
        const uint32_t wantedRenderWidth = useVsr ? sourceWidth : w;
        const uint32_t wantedRenderHeight = useVsr ? sourceHeight : h;

        if (r.renderTarget && w == r.width && h == r.height &&
            wantedRenderWidth == r.renderWidth && wantedRenderHeight == r.renderHeight &&
            r.vsrActive == useVsr)
            return true;

        auto buildTargets = [&](bool enableVsr) -> bool
        {
            ReleaseTargets(r);
            r.width = w;
            r.height = h;
            r.renderWidth = enableVsr ? sourceWidth : w;
            r.renderHeight = enableVsr ? sourceHeight : h;

            // Resize the swapchain only when the window really changed size.
            // ResizeBuffers throws the on-screen buffers away, so calling it
            // for a mere VSR/FRUC reconfiguration flashed black. A
            // failed resize must abort: presenting into a mismatched backbuffer
            // makes CopyResource a silent no-op and shows black frames.
            if (r.swapChain && (w != r.swapWidth || h != r.swapHeight))
            {
                if (FAILED(r.swapChain->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, 0)))
                {
                    r.SetMessage("ResizeBuffers a échoué — reconstruction reportée au prochain frame.");
                    return false;
                }
                r.swapWidth = w;
                r.swapHeight = h;
            }

            // mpv renders at source resolution when VSR is active. RTX VSR then
            // produces the window-sized texture consumed by FRUC, all on D3D11.
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = r.renderWidth;
            desc.Height = r.renderHeight;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            if (FAILED(r.device->CreateTexture2D(&desc, nullptr, &r.renderTarget)))
                return false;

            glGenTextures(1, &r.glTexture);
            r.interopTexture = r.api.DXRegisterObjectNV(r.interopDevice, r.renderTarget,
                r.glTexture, GL_TEXTURE_2D, WGL_ACCESS_READ_WRITE_NV);
            if (!r.interopTexture)
            {
                r.SetMessage("wglDXRegisterObjectNV a refusé la texture de rendu.");
                return false;
            }

            r.api.GenFramebuffers(1, &r.fbo);
            r.api.DXLockObjectsNV(r.interopDevice, 1, &r.interopTexture);
            r.api.BindFramebuffer(GL_FRAMEBUFFER, r.fbo);
            r.api.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0,
                GL_TEXTURE_2D, r.glTexture, 0);
            const GLenum complete = r.api.CheckFramebufferStatus(GL_FRAMEBUFFER);
            r.api.BindFramebuffer(GL_FRAMEBUFFER, 0);
            r.api.DXUnlockObjectsNV(r.interopDevice, 1, &r.interopTexture);
            if (complete != GL_FRAMEBUFFER_COMPLETE)
            {
                r.SetMessage("FBO incomplet sur la texture D3D11 partagée.");
                return false;
            }

            HRESULT vsrStatus = S_OK;
            if (enableVsr && !InitializeVsrResources(r, r.renderWidth, r.renderHeight, w, h, vsrStatus))
            {
                std::lock_guard lock(r.stateMutex);
                r.state.lastVsrStatus = static_cast<int32_t>(vsrStatus);
                strncpy_s(r.state.message,
                    "RTX VSR refusé par le processeur vidéo D3D11; repli GPU direct.", _TRUNCATE);
                return false;
            }

            r.frameA = CreateSharedTexture(r.device, w, h);
            r.frameB = CreateSharedTexture(r.device, w, h);
            r.interpolated = CreateSharedTexture(r.device, w, h);
            if (!r.frameA || !r.frameB || !r.interpolated) return false;
            r.previous = r.frameA;
            r.current = r.frameB;

            {
                std::lock_guard lock(r.stateMutex);
                r.state.texturesShared = 1;
                r.state.width = w;
                r.state.height = h;
                r.state.vsrActive = r.vsrActive ? 1 : 0;
                r.state.lastVsrStatus = static_cast<int32_t>(vsrStatus);
                r.state.vsrInputWidth = r.vsrActive ? r.renderWidth : 0;
                r.state.vsrInputHeight = r.vsrActive ? r.renderHeight : 0;
                r.state.vsrContentWidth = r.vsrActive ? r.vsrContentWidth : 0;
                r.state.vsrContentHeight = r.vsrActive ? r.vsrContentHeight : 0;
            }

            // FRUC session creation is deliberately NOT done here: it is
            // expensive (NvOFFRUCCreate + resource registration) and target
            // rebuilds happen during interactive resizes and VSR toggles. The
            // render loop re-creates the session once the size has settled.
            r.lastTargetChangeQpc = QpcSeconds();
            // New target configuration: the first FRUC attempt must not be
            // held back by the failure throttle of the previous one.
            r.lastFrucInitAttemptQpc = 0;

            return true;
        };

        if (useVsr)
        {
            if (buildTargets(true)) return true;
            r.failedVsrSourceWidth = sourceWidth;
            r.failedVsrSourceHeight = sourceHeight;
            r.failedVsrOutputWidth = w;
            r.failedVsrOutputHeight = h;
        }

        return buildTargets(false);
    }

    void PresentTexture(Renderer& r, ID3D11Texture2D* tex)
    {
        ID3D11Texture2D* backBuffer = nullptr;
        if (FAILED(r.swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer))))
            return;
        r.context->CopyResource(backBuffer, tex);
        backBuffer->Release();
        const HRESULT hr = r.swapChain->Present(0, 0);
        std::lock_guard lock(r.stateMutex);
        if (FAILED(hr))
        {
            // DEVICE_REMOVED/RESET here is the signature of a driver TDR — the
            // swapchain then shows black. Surface it in the state message so
            // the C# diagnostics can tell this apart from a pacing problem.
            char buf[128];
            sprintf_s(buf, "Present a échoué (0x%08X) — device D3D11 perdu ?", static_cast<unsigned>(hr));
            strncpy_s(r.state.message, buf, _TRUNCATE);
            return;
        }
        r.state.framesPresented++;
    }

    // (Re)creates the NvOFFRUC session for the current target size. Runs on
    // the render thread only, and only after resize storms have settled: a
    // session build takes long enough to visibly stall presentation.
    void InitializeFruc(Renderer& r)
    {
        r.lastFrucInitAttemptQpc = QpcSeconds();
        ElyFlowConfig cfg{};
        cfg.structSize = sizeof(cfg);
        cfg.engine = ELYFLOW_ENGINE_NVIDIA_FRUC;
        cfg.width = r.width;
        cfg.height = r.height;
        cfg.sourceFps = 1.0 / r.frameInterval;
        cfg.targetFps = cfg.sourceFps * 2.0;
        cfg.format = ELYFLOW_FORMAT_RGBA8;
        cfg.d3d11Device = r.device;
        cfg.d3d11DeviceContext = r.context;
        cfg.d3d11Fence = r.fence;
        const auto st = ElyFlow_Initialize(&cfg);
        std::lock_guard lock(r.stateMutex);
        r.state.frucInitialized = st == ELYFLOW_STATUS_OK ? 1 : 0;
        r.state.lastFrucStatus = st;
        if (st != ELYFLOW_STATUS_OK)
            strncpy_s(r.state.message, ElyFlow_GetStatus(), _TRUNCATE);
    }

    int32_t InitOnThread(Renderer& r)
    {
        if (!r.mpvApi.Load())
            return GetModuleHandleW(L"libmpv-2.dll")
                ? ELYFLOW_RENDERER_MPV_EXPORTS_MISSING
                : ELYFLOW_RENDERER_LIBMPV_NOT_LOADED;

        // D3D11 device (hardware, single-threaded use on this thread).
        static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, 2, D3D11_SDK_VERSION, &r.device, nullptr, &r.context)))
            return ELYFLOW_RENDERER_D3D11_FAILED;
        r.device->QueryInterface(__uuidof(ID3D11Device5), reinterpret_cast<void**>(&r.device5));
        r.context->QueryInterface(__uuidof(ID3D11DeviceContext4), reinterpret_cast<void**>(&r.context4));
        r.device->QueryInterface(__uuidof(ID3D11VideoDevice), reinterpret_cast<void**>(&r.videoDevice));
        r.context->QueryInterface(__uuidof(ID3D11VideoContext), reinterpret_cast<void**>(&r.videoContext));
        if (r.device5)
            r.device5->CreateFence(0, D3D11_FENCE_FLAG_SHARED, __uuidof(ID3D11Fence), reinterpret_cast<void**>(&r.fence));

        r.waitTimer = CreateWaitableTimerExW(nullptr, nullptr,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!r.waitTimer)
            r.waitTimer = CreateWaitableTimerW(nullptr, FALSE, nullptr);

        // Swapchain on the player child window.
        IDXGIDevice* dxgiDevice = nullptr;
        IDXGIAdapter* adapter = nullptr;
        IDXGIFactory2* factory = nullptr;
        if (FAILED(r.device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice))) ||
            FAILED(dxgiDevice->GetAdapter(&adapter)) ||
            FAILED(adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory))))
        {
            SafeRelease(factory); SafeRelease(adapter); SafeRelease(dxgiDevice);
            return ELYFLOW_RENDERER_SWAPCHAIN_FAILED;
        }

        RECT rc{};
        GetClientRect(r.target, &rc);
        DXGI_SWAP_CHAIN_DESC1 sc{};
        sc.Width = std::max<LONG>(rc.right - rc.left, 16);
        sc.Height = std::max<LONG>(rc.bottom - rc.top, 16);
        sc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sc.SampleDesc.Count = 1;
        sc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sc.BufferCount = 2;
        // Use the composed blt model for this embedded child HWND. A flip-model
        // child can be promoted to an independent/MPO plane by DWM; the
        // transparent WPF OSD above it then forces promote/demote transitions on
        // focus changes, which manifests as repeated black flashes on some
        // NVIDIA drivers. SEQUENTIAL copies the completed frame into DWM's
        // surface and preserves it without those plane transitions.
        sc.SwapEffect = DXGI_SWAP_EFFECT_SEQUENTIAL;
        sc.Scaling = DXGI_SCALING_STRETCH;
        sc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        factory->MakeWindowAssociation(r.target,
            DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_NO_WINDOW_CHANGES);
        const HRESULT scHr = factory->CreateSwapChainForHwnd(r.device, r.target, &sc, nullptr, nullptr, &r.swapChain);
        SafeRelease(factory); SafeRelease(adapter); SafeRelease(dxgiDevice);
        if (FAILED(scHr)) return ELYFLOW_RENDERER_SWAPCHAIN_FAILED;
        r.swapWidth = sc.Width;
        r.swapHeight = sc.Height;

        // Hidden GL context + interop.
        if (!r.gl.Create()) return ELYFLOW_RENDERER_WGL_FAILED;
        if (!r.api.LoadFbo()) return ELYFLOW_RENDERER_GL_FUNCTIONS_MISSING;
        if (!r.api.LoadInterop()) return ELYFLOW_RENDERER_INTEROP_MISSING;
        r.interopDevice = r.api.DXOpenDeviceNV(r.device);
        if (!r.interopDevice) return ELYFLOW_RENDERER_INTEROP_MISSING;
        {
            std::lock_guard lock(r.stateMutex);
            r.state.glInterop = 1;
        }

        // mpv render context on this thread (context is current here).
        mpv_opengl_init_params glParams{};
        glParams.get_proc_address = MpvGetProcAddress;
        int advanced = 1;
        mpv_render_param params[] = {
            { MPV_RENDER_PARAM_API_TYPE, const_cast<char*>(MPV_RENDER_API_TYPE_OPENGL) },
            { MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, &glParams },
            { MPV_RENDER_PARAM_ADVANCED_CONTROL, &advanced },
            { MPV_RENDER_PARAM_INVALID, nullptr }
        };
        if (r.mpvApi.Create(&r.renderContext, r.mpv, params) < 0)
            return ELYFLOW_RENDERER_MPV_CONTEXT_FAILED;
        r.mpvApi.SetUpdateCallback(r.renderContext, OnMpvUpdate, &r);

        {
            std::lock_guard lock(r.stateMutex);
            r.state.active = 1;
            strncpy_s(r.state.message, "Pipeline ELYCORE actif (mpv -> D3D11 -> VSR/FRUC -> swapchain).", _TRUNCATE);
        }
        return ELYFLOW_RENDERER_OK;
    }

    void RenderLoop(Renderer& r)
    {
        DWORD mmcssTaskIndex = 0;
        HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Playback", &mmcssTaskIndex);
        if (mmcss)
            AvSetMmThreadPriority(mmcss, AVRT_PRIORITY_HIGH);
        else
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

        while (!r.quit.load(std::memory_order_relaxed))
        {
            WaitForSingleObject(r.wakeEvent, 50);
            if (r.quit.load(std::memory_order_relaxed)) break;

            const uint64_t flags = r.mpvApi.Update(r.renderContext);
            if (!(flags & MPV_RENDER_UPDATE_FRAME)) continue;

            auto skipFrame = [&]
            {
                // Still consume the frame so mpv does not stall.
                int one = 1;
                mpv_render_param skip[] = { { MPV_RENDER_PARAM_SKIP_RENDERING, &one }, { MPV_RENDER_PARAM_INVALID, nullptr } };
                r.mpvApi.Render(r.renderContext, skip);
            };

            RECT rc{};
            GetClientRect(r.target, &rc);
            const LONG clientW = rc.right - rc.left;
            const LONG clientH = rc.bottom - rc.top;
            if (clientW <= 0 || clientH <= 0)
            {
                // Minimized/collapsed window: keep the existing targets alive
                // instead of rebuilding the whole pipeline at a bogus size
                // (and once more on restore).
                skipFrame();
                continue;
            }
            const auto w = static_cast<uint32_t>(std::max<LONG>(clientW, 16));
            const auto h = static_cast<uint32_t>(std::max<LONG>(clientH, 16));
            if (!EnsureTargets(r, w, h))
            {
                skipFrame();
                continue;
            }

            // mpv -> GL FBO -> D3D11 renderTarget (same GPU memory).
            if (!r.api.DXLockObjectsNV(r.interopDevice, 1, &r.interopTexture))
            {
                // Rendering into an unlocked interop texture is undefined —
                // that is exactly what showed up as random black frames.
                skipFrame();
                continue;
            }
            mpv_opengl_fbo fbo{};
            fbo.fbo = static_cast<int>(r.fbo);
            fbo.w = static_cast<int>(r.renderWidth);
            fbo.h = static_cast<int>(r.renderHeight);
            // GL FBOs are bottom-up but the texture is consumed by D3D11
            // (top-down): no flip, otherwise the video shows upside down.
            int flipY = 0;
            mpv_render_param renderParams[] = {
                { MPV_RENDER_PARAM_OPENGL_FBO, &fbo },
                { MPV_RENDER_PARAM_FLIP_Y, &flipY },
                { MPV_RENDER_PARAM_INVALID, nullptr }
            };
            r.mpvApi.Render(r.renderContext, renderParams);
            glFlush();
            r.api.DXUnlockObjectsNV(r.interopDevice, 1, &r.interopTexture); // synchronises GL -> D3D

            const double now = QpcSeconds();
            double observedDelta = 0;
            if (r.lastFrameQpc > 0)
                observedDelta = now - r.lastFrameQpc;
            r.lastFrameQpc = now;

            // Use mpv's source cadence instead of timing this loop: the latter
            // includes our own GPU work and used to create positive feedback.
            RefreshSourceCadence(r, observedDelta);

            // Ring: current <- fresh frame (GPU copy).
            std::swap(r.previous, r.current);
            ID3D11Texture2D* processedTexture = r.renderTarget;
            if (r.vsrActive)
            {
                HRESULT vsrStatus = S_OK;
                if (!RunVsr(r, vsrStatus))
                {
                    r.failedVsrSourceWidth = r.sourceWidthHint.load(std::memory_order_relaxed);
                    r.failedVsrSourceHeight = r.sourceHeightHint.load(std::memory_order_relaxed);
                    r.failedVsrOutputWidth = w;
                    r.failedVsrOutputHeight = h;
                    std::lock_guard lock(r.stateMutex);
                    r.state.vsrActive = 0;
                    r.state.lastVsrStatus = static_cast<int32_t>(vsrStatus);
                    strncpy_s(r.state.message,
                        "RTX VSR a échoué pendant VideoProcessorBlt; repli au prochain frame.", _TRUNCATE);
                    continue;
                }
                processedTexture = r.vsrOutput;
            }
            r.context->CopyResource(r.current, processedTexture);
            {
                std::lock_guard lock(r.stateMutex);
                r.state.framesRendered++;
            }

            bool interpolatedReady = false;
            bool frucInit;
            {
                std::lock_guard lock(r.stateMutex);
                frucInit = r.state.frucInitialized != 0;
            }

            // Runtime FRUC toggle + deferred (re)initialisation. The session is
            // only built once the target size stayed stable for 250 ms so
            // resize storms and zaps do not stack expensive NvOFFRUCCreate
            // calls (each one froze presentation and read as a black blink).
            const bool wantFruc = r.frucWanted.load(std::memory_order_relaxed);
            if (!wantFruc && frucInit)
            {
                ElyFlow_Shutdown();
                frucInit = false;
                std::lock_guard lock(r.stateMutex);
                r.state.frucInitialized = 0;
            }
            else if (wantFruc && !frucInit && r.fence && r.context4 &&
                     now - r.lastTargetChangeQpc > 0.25 &&
                     now - r.lastFrucInitAttemptQpc > 3.0)
            {
                InitializeFruc(r);
                std::lock_guard lock(r.stateMutex);
                frucInit = r.state.frucInitialized != 0;
            }

            if (frucInit && r.havePrevious && r.fence && r.context4)
            {
                const auto t = static_cast<int64_t>(now * 1e7);
                const auto interval100 = static_cast<int64_t>(r.frameInterval * 1e7);

                r.context4->Signal(r.fence, ++r.fenceValue);
                ElyFlowFrame prev{};
                prev.structSize = sizeof(prev);
                prev.texture = r.previous;
                prev.width = w; prev.height = h;
                prev.format = ELYFLOW_FORMAT_RGBA8;
                prev.presentationTime100ns = t - interval100;

                ElyFlowFrame cur = prev;
                cur.texture = r.current;
                cur.presentationTime100ns = t;
                cur.waitFenceValue = r.fenceValue;

                ElyFlowFrame out = prev;
                out.texture = r.interpolated;
                out.presentationTime100ns = t - interval100 / 2;
                out.signalFenceValue = r.fenceValue + 1;

                const auto st = ElyFlow_ProcessFrame(&prev, &cur, &out);
                {
                    std::lock_guard lock(r.stateMutex);
                    r.state.lastFrucStatus = st;
                }
                if (st == ELYFLOW_STATUS_OK)
                {
                    r.context4->Wait(r.fence, ++r.fenceValue); // GPU waits for FRUC output
                    interpolatedReady = true;
                    std::lock_guard lock(r.stateMutex);
                    r.state.framesInterpolated++;
                }
                else
                {
                    // FRUC may or may not have signalled out.signalFenceValue
                    // before failing. Skip that value so the next Signal never
                    // reuses a fence value the SDK could already have written —
                    // a non-monotonic Signal corrupts the fence and stalls the
                    // GPU queue.
                    ++r.fenceValue;
                }
            }

            if (interpolatedReady)
            {
                // Interpolated frame sits temporally between previous and
                // current: present it first, then the real frame half an
                // interval later — doubles the displayed rate.
                PresentTexture(r, r.interpolated);
                const double currentDeadline = now + r.frameInterval * 0.5;
                const double workMs = (QpcSeconds() - now) * 1000.0;
                r.averageWorkMs = r.averageWorkMs == 0
                    ? workMs
                    : r.averageWorkMs * 0.95 + workMs * 0.05;
                r.maxWorkMs = std::max(r.maxWorkMs, workMs);
                const bool late = QpcSeconds() >= currentDeadline;

                // Only wait for the budget left after render + FRUC + Present.
                // Waiting a fresh half-frame here caused 7-10 source drops/s.
                if (!late) WaitUntil(r, currentDeadline);
                PresentTexture(r, r.current);

                std::lock_guard lock(r.stateMutex);
                if (late) r.state.latePresents++;
                r.state.averageWorkMs = r.averageWorkMs;
                r.state.maxWorkMs = r.maxWorkMs;
            }
            else
            {
                PresentTexture(r, r.current);
            }

            r.havePrevious = true;
        }

        if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
    }

    void ShutdownOnThread(Renderer& r)
    {
        if (r.renderContext)
        {
            r.mpvApi.SetUpdateCallback(r.renderContext, nullptr, nullptr);
            r.mpvApi.Free(r.renderContext);
            r.renderContext = nullptr;
        }
        ReleaseTargets(r);
        if (r.interopDevice && r.api.DXCloseDeviceNV) { r.api.DXCloseDeviceNV(r.interopDevice); r.interopDevice = nullptr; }
        r.gl.Destroy();
        SafeRelease(r.fence);
        SafeRelease(r.swapChain);
        SafeRelease(r.context4);
        SafeRelease(r.videoContext);
        SafeRelease(r.videoDevice);
        SafeRelease(r.device5);
        SafeRelease(r.context);
        SafeRelease(r.device);
        if (r.waitTimer) { CloseHandle(r.waitTimer); r.waitTimer = nullptr; }
    }
}

int32_t ElyFlowRenderer_Preflight(char* message, int32_t messageSize)
{
    auto report = [&](const char* text, int32_t code)
    {
        if (message && messageSize > 0) strncpy_s(message, static_cast<size_t>(messageSize), text, _TRUNCATE);
        return code;
    };

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, 2, D3D11_SDK_VERSION, &device, nullptr, &context)))
        return report("D3D11CreateDevice a échoué.", ELYFLOW_RENDERER_D3D11_FAILED);

    GlContext gl;
    GlApi api;
    int32_t code = ELYFLOW_RENDERER_OK;
    const char* text = "Préflight OK : D3D11 + WGL_NV_DX_interop2 disponibles.";

    if (!gl.Create()) { code = ELYFLOW_RENDERER_WGL_FAILED; text = "Contexte WGL indisponible."; }
    else if (!api.LoadFbo()) { code = ELYFLOW_RENDERER_GL_FUNCTIONS_MISSING; text = "Fonctions FBO OpenGL indisponibles."; }
    else if (!api.LoadInterop()) { code = ELYFLOW_RENDERER_INTEROP_MISSING; text = "WGL_NV_DX_interop2 absent (driver non NVIDIA ?)."; }
    else
    {
        HANDLE interop = api.DXOpenDeviceNV(device);
        if (!interop) { code = ELYFLOW_RENDERER_INTEROP_MISSING; text = "wglDXOpenDeviceNV a refusé le device D3D11."; }
        else api.DXCloseDeviceNV(interop);
    }

    gl.Destroy();
    SafeRelease(context);
    SafeRelease(device);
    return report(text, code);
}

int32_t ElyFlowRenderer_Create(void* mpvHandle, void* hwnd, int32_t enableFruc)
{
    if (!mpvHandle || !hwnd) return ELYFLOW_RENDERER_INVALID_ARGUMENT;

    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) return ELYFLOW_RENDERER_ALREADY_ACTIVE;

    auto* r = new Renderer();
    r->mpv = static_cast<mpv_handle*>(mpvHandle);
    r->target = static_cast<HWND>(hwnd);
    r->frucWanted.store(enableFruc != 0, std::memory_order_relaxed);
    r->state.structSize = sizeof(ElyFlowRendererState);
    r->wakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    std::promise<int32_t> ready;
    auto readyFuture = ready.get_future();
    r->thread = std::thread([r, &ready]
    {
        const int32_t code = InitOnThread(*r);
        ready.set_value(code);
        if (code == ELYFLOW_RENDERER_OK)
            RenderLoop(*r);
        ShutdownOnThread(*r);
    });

    const int32_t code = readyFuture.get();
    if (code != ELYFLOW_RENDERER_OK)
    {
        r->quit.store(true);
        SetEvent(r->wakeEvent);
        r->thread.join();
        CloseHandle(r->wakeEvent);
        delete r;
        return code;
    }

    g_renderer = r;
    return ELYFLOW_RENDERER_OK;
}

int32_t ElyFlowRenderer_GetState(ElyFlowRendererState* state)
{
    if (!state || state->structSize < sizeof(ElyFlowRendererState))
        return ELYFLOW_RENDERER_INVALID_ARGUMENT;

    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer)
    {
        ElyFlowRendererState empty{};
        empty.structSize = sizeof(empty);
        strncpy_s(empty.message, "Renderer ELYCORE inactif.", _TRUNCATE);
        *state = empty;
        return ELYFLOW_RENDERER_OK;
    }

    std::lock_guard stateLock(g_renderer->stateMutex);
    *state = g_renderer->state;
    return ELYFLOW_RENDERER_OK;
}

void ElyFlowRenderer_SetSourceFps(double sourceFps)
{
    if (!std::isfinite(sourceFps) || sourceFps < 5.0 || sourceFps > 240.0)
        return;

    std::lock_guard lock(g_rendererMutex);
    if (g_renderer)
        g_renderer->sourceFpsHint.store(sourceFps, std::memory_order_relaxed);
}

void ElyFlowRenderer_ConfigureVsr(int32_t enable, uint32_t sourceWidth, uint32_t sourceHeight)
{
    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer) return;
    g_renderer->sourceWidthHint.store(sourceWidth, std::memory_order_relaxed);
    g_renderer->sourceHeightHint.store(sourceHeight, std::memory_order_relaxed);
    g_renderer->vsrWanted.store(enable != 0, std::memory_order_relaxed);
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
}

void ElyFlowRenderer_ConfigureFruc(int32_t enable)
{
    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer) return;
    g_renderer->frucWanted.store(enable != 0, std::memory_order_relaxed);
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
}

void ElyFlowRenderer_Destroy()
{
    Renderer* r;
    {
        std::lock_guard lock(g_rendererMutex);
        r = g_renderer;
        g_renderer = nullptr;
    }
    if (!r) return;

    r->quit.store(true);
    SetEvent(r->wakeEvent);
    if (r->thread.joinable()) r->thread.join();
    CloseHandle(r->wakeEvent);
    delete r;
}
