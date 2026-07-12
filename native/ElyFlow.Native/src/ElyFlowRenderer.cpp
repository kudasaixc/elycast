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
#include "ElyAudioCoreScene.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <avrt.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
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
        ID3D11VideoContext1* videoContext1 = nullptr;
        // Dedicated D3D11 device for the RTX VSR VideoProcessor work. The main
        // device is opened for OpenGL interop (wglDXOpenDeviceNV); dispatching
        // NVIDIA's driver-internal VSR kernels on that device crashed the
        // driver (DXGI_ERROR_DRIVER_INTERNAL_ERROR at the first engaged
        // frame). No known VSR consumer (mpv, Chromium, MPC-VR) mixes the two;
        // the video work therefore runs on its own device, bridged with
        // keyed-mutex shared textures.
        ID3D11Device* vsrDevice = nullptr;
        ID3D11DeviceContext* vsrDeviceContext = nullptr;
        ID3D11VideoDevice* vsrVideoDevice = nullptr;
        ID3D11VideoContext* vsrVideoContext = nullptr;
        ID3D11VideoContext1* vsrVideoContext1 = nullptr;
        IDXGISwapChain1* swapChain = nullptr;
        ID3D11Fence* fence = nullptr;
        uint64_t fenceValue = 0;

        HANDLE interopDevice = nullptr;
        HANDLE interopTexture = nullptr;
        ID3D11Texture2D* renderTarget = nullptr;   // mpv renders here via GL
        ID3D11Texture2D* vsrOutput = nullptr;      // RTX VSR output at window size
        // The NVIDIA driver only engages RTX VSR on video-format (YUV) input
        // streams: an RGBA input is classified as graphics and silently
        // bypassed (Blt succeeds, IsInUse stays false). A first VideoProcessor
        // pass therefore converts mpv's RGBA render target to NV12 on the GPU,
        // and the VSR VideoProcessor consumes that NV12 — the exact input
        // Chromium / mpv d3d11vpp / MPC-VR feed when VSR does engage.
        // Cross-device bridge (keyed mutex): the RGBA source frame written by
        // the main device, read by the video device; the RGBA VSR result
        // written by the video device, read back by the main device.
        ID3D11Texture2D* vsrCopy = nullptr;        // main device, shared, source size
        ID3D11Texture2D* vsrCopyRemote = nullptr;  // same texture opened on vsrDevice
        IDXGIKeyedMutex* copyMutexMain = nullptr;
        IDXGIKeyedMutex* copyMutexRemote = nullptr;
        ID3D11Texture2D* vsrOutputMain = nullptr;  // vsrOutput opened on main device
        IDXGIKeyedMutex* outMutexMain = nullptr;
        IDXGIKeyedMutex* outMutexRemote = nullptr;
        ID3D11Texture2D* vsrResult = nullptr;      // main device, window-size composition
        ID3D11Texture2D* vsrNv12 = nullptr;        // vsrDevice: RGB->NV12, VSR input
        // Letterbox offsets applied AFTER the VSR pass. The VsrProbe matrix
        // proved that a VSR Blt whose destination rect differs from the output
        // rect crashes the NVIDIA driver (device removed); every other tested
        // variation survives with VSR engaged. The VP therefore always runs
        // full-frame to a content-sized target, and centering happens with a
        // plain CopySubresourceRegion on the main device.
        uint32_t vsrDestOffsetX = 0;
        uint32_t vsrDestOffsetY = 0;
        ID3D11VideoProcessorEnumerator* convEnumerator = nullptr;
        ID3D11VideoProcessor* convProcessor = nullptr;
        ID3D11VideoProcessorInputView* convInputView = nullptr;
        ID3D11VideoProcessorOutputView* convOutputView = nullptr;
        ID3D11VertexShader* convVs = nullptr;
        ID3D11PixelShader* convPsLuma = nullptr;
        ID3D11PixelShader* convPsChroma = nullptr;
        ID3D11SamplerState* convSampler = nullptr;
        ID3D11ShaderResourceView* convSrcSrv = nullptr;
        ID3D11RenderTargetView* nv12LumaRtv = nullptr;
        ID3D11RenderTargetView* nv12ChromaRtv = nullptr;
        bool convUseShader = false;
        ID3D11VideoProcessorEnumerator* vsrEnumerator = nullptr;
        ID3D11VideoProcessor* vsrProcessor = nullptr;
        ID3D11VideoProcessorInputView* vsrInputView = nullptr;
        ID3D11VideoProcessorOutputView* vsrOutputView = nullptr;
        // GPU timing of the VSR Blt (proof of kernel execution: an engaged
        // VSR pass costs milliseconds, a plain driver scaler ~0.1 ms).
        ID3D11Query* vsrTsDisjoint = nullptr;
        ID3D11Query* vsrTsBegin = nullptr;
        ID3D11Query* vsrTsEnd = nullptr;
        bool vsrTsPending = false;
        double vsrBltAvgMs = 0;
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
        bool vsrAvailable = false;
        bool vsrEffective = false;
        uint32_t vsrLevel = 0;
        uint32_t adapterVendorId = 0;
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
        std::atomic<bool> audioCoreScene{ false };
        ElyAudioCoreScene audioCore;
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
        const double fps = r.sourceFpsHint.load();
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
        if (remaining <= 0 || r.quit.load()) return;

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

    HRESULT QueryNvidiaVsr(Renderer& r)
    {
        // Primary probe: 4-byte bitfield (bit0 available, bit1 fields valid,
        // bit3 in-use, bits4..6 level).
        UINT info = 0;
        const HRESULT status = r.vsrVideoContext->VideoProcessorGetStreamExtension(
            r.vsrProcessor, 0, &NvidiaPpeInterfaceGuid, sizeof(info), &info);
        if (SUCCEEDED(status))
        {
            std::lock_guard lock(r.stateMutex);
            r.state.vsrQueryRaw = info;
            r.vsrAvailable = (info & 0x1u) != 0;
            const bool otherFieldsValid = (info & 0x2u) != 0;
            r.vsrEffective = otherFieldsValid && (info & 0x8u) != 0;
            r.vsrLevel = otherFieldsValid ? ((info >> 4) & 0x7u) : 0;
            return S_OK;
        }

        // Fallback probe: same 12-byte struct shape used by the setter; some
        // driver branches only accept the full payload once the stream ran.
        struct NvidiaExtension { unsigned int version; unsigned int method; unsigned int enable; };
        NvidiaExtension query{ 1, 2, 0 };
        const HRESULT structStatus = r.vsrVideoContext->VideoProcessorGetStreamExtension(
            r.vsrProcessor, 0, &NvidiaPpeInterfaceGuid, sizeof(query), &query);
        std::lock_guard lock(r.stateMutex);
        if (SUCCEEDED(structStatus))
        {
            // Marker 0x4000_0000 distinguishes the struct probe in the audit.
            r.state.vsrQueryRaw = 0x40000000u | (query.enable & 0xFFFFu);
            r.vsrEffective = query.enable != 0;
            r.vsrLevel = 0;
            return S_OK;
        }

        // Both probes rejected: record the failing HRESULT for the audit but
        // keep the availability established at initialisation. Also snapshot
        // the video-device health — a silently removed device explains
        // "successful" Blts that actually execute nothing.
        r.state.vsrQueryRaw = static_cast<uint32_t>(status);
        const HRESULT removed = r.vsrDevice ? r.vsrDevice->GetDeviceRemovedReason() : S_OK;
        r.state.lastVsrStatus = static_cast<int32_t>(removed);
        r.vsrEffective = false;
        r.vsrLevel = 0;
        return status;
    }

    void ReleaseVsrResources(Renderer& r)
    {
        SafeRelease(r.convVs);
        SafeRelease(r.convPsLuma);
        SafeRelease(r.convPsChroma);
        SafeRelease(r.convSampler);
        SafeRelease(r.convSrcSrv);
        SafeRelease(r.nv12LumaRtv);
        SafeRelease(r.nv12ChromaRtv);
        r.convUseShader = false;
        SafeRelease(r.vsrInputView);
        SafeRelease(r.vsrOutputView);
        SafeRelease(r.vsrProcessor);
        SafeRelease(r.vsrEnumerator);
        SafeRelease(r.convInputView);
        SafeRelease(r.convOutputView);
        SafeRelease(r.convProcessor);
        SafeRelease(r.convEnumerator);
        SafeRelease(r.copyMutexMain);
        SafeRelease(r.copyMutexRemote);
        SafeRelease(r.outMutexMain);
        SafeRelease(r.outMutexRemote);
        SafeRelease(r.vsrCopyRemote);
        SafeRelease(r.vsrCopy);
        SafeRelease(r.vsrOutputMain);
        SafeRelease(r.vsrResult);
        SafeRelease(r.vsrNv12);
        SafeRelease(r.vsrOutput);
        SafeRelease(r.vsrTsDisjoint);
        SafeRelease(r.vsrTsBegin);
        SafeRelease(r.vsrTsEnd);
        r.vsrTsPending = false;
        r.vsrBltAvgMs = 0;
        r.vsrActive = false;
        r.vsrAvailable = false;
        r.vsrEffective = false;
        r.vsrLevel = 0;
        r.vsrFrameIndex = 0;
        r.vsrContentWidth = 0;
        r.vsrContentHeight = 0;
        std::lock_guard lock(r.stateMutex);
        r.state.videoProcessorCreated = 0;
        r.state.vsrExtensionEnabled = 0;
        r.state.vsrConverterActive = 0;
    }

    HRESULT InitializeVsrConversionShaders(Renderer& r)
    {
        static constexpr char source[] = R"hlsl(
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VsMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 t = float2((id << 1) & 2, id & 2);
    o.pos = float4(t.x * 2.0 - 1.0, 1.0 - t.y * 2.0, 0.0, 1.0);
    o.uv = t;
    return o;
}
Texture2D srcTex : register(t0);
SamplerState smp : register(s0);
float PsLuma(VSOut i) : SV_Target
{
    float3 rgb = srcTex.Sample(smp, i.uv).rgb;
    float y = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return 16.0 / 255.0 + y * (219.0 / 255.0);
}
float2 PsChroma(VSOut i) : SV_Target
{
    float3 rgb = srcTex.Sample(smp, i.uv).rgb;
    float cb = dot(rgb, float3(-0.114572, -0.385428, 0.5));
    float cr = dot(rgb, float3(0.5, -0.454153, -0.045847));
    return float2(128.0 / 255.0 + cb * (224.0 / 255.0),
                  128.0 / 255.0 + cr * (224.0 / 255.0));
}
)hlsl";

        auto compile = [&](const char* entry, const char* target, ID3DBlob** code)
        {
            ID3DBlob* errors = nullptr;
            const HRESULT hr = D3DCompile(source, sizeof(source) - 1, "ElyFlowVsrConversion",
                nullptr, nullptr, entry, target,
                D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3,
                0, code, &errors);
            SafeRelease(errors);
            return hr;
        };

        ID3DBlob* vsCode = nullptr;
        ID3DBlob* lumaCode = nullptr;
        ID3DBlob* chromaCode = nullptr;
        HRESULT hr = compile("VsMain", "vs_5_0", &vsCode);
        if (SUCCEEDED(hr)) hr = compile("PsLuma", "ps_5_0", &lumaCode);
        if (SUCCEEDED(hr)) hr = compile("PsChroma", "ps_5_0", &chromaCode);
        if (SUCCEEDED(hr)) hr = r.vsrDevice->CreateVertexShader(
            vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &r.convVs);
        if (SUCCEEDED(hr)) hr = r.vsrDevice->CreatePixelShader(
            lumaCode->GetBufferPointer(), lumaCode->GetBufferSize(), nullptr, &r.convPsLuma);
        if (SUCCEEDED(hr)) hr = r.vsrDevice->CreatePixelShader(
            chromaCode->GetBufferPointer(), chromaCode->GetBufferSize(), nullptr, &r.convPsChroma);
        SafeRelease(vsCode);
        SafeRelease(lumaCode);
        SafeRelease(chromaCode);
        if (FAILED(hr)) return hr;

        hr = r.vsrDevice->CreateShaderResourceView(r.vsrCopyRemote, nullptr, &r.convSrcSrv);
        D3D11_RENDER_TARGET_VIEW_DESC view{};
        view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        view.Format = DXGI_FORMAT_R8_UNORM;
        if (SUCCEEDED(hr))
            hr = r.vsrDevice->CreateRenderTargetView(r.vsrNv12, &view, &r.nv12LumaRtv);
        view.Format = DXGI_FORMAT_R8G8_UNORM;
        if (SUCCEEDED(hr))
            hr = r.vsrDevice->CreateRenderTargetView(r.vsrNv12, &view, &r.nv12ChromaRtv);

        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        if (SUCCEEDED(hr)) hr = r.vsrDevice->CreateSamplerState(&sampler, &r.convSampler);
        return hr;
    }

    // Both stages of the VSR chain share this rect/colorspace setup helper.
    void ConfigureProcessorStream(Renderer& r, ID3D11VideoProcessor* processor,
                                  const RECT& sourceRect, const RECT& destinationRect,
                                  const RECT& outputRect,
                                  DXGI_COLOR_SPACE_TYPE streamColorSpace,
                                  DXGI_COLOR_SPACE_TYPE outputColorSpace)
    {
        r.vsrVideoContext->VideoProcessorSetStreamFrameFormat(
            processor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        r.vsrVideoContext->VideoProcessorSetStreamSourceRect(processor, 0, TRUE, &sourceRect);
        r.vsrVideoContext->VideoProcessorSetStreamDestRect(processor, 0, TRUE, &destinationRect);
        r.vsrVideoContext->VideoProcessorSetOutputTargetRect(processor, TRUE, &outputRect);
        r.vsrVideoContext->VideoProcessorSetStreamAutoProcessingMode(processor, 0, FALSE);
        r.vsrVideoContext->VideoProcessorSetStreamOutputRate(
            processor, 0, D3D11_VIDEO_PROCESSOR_OUTPUT_RATE_NORMAL, FALSE, nullptr);
        D3D11_VIDEO_COLOR background{};
        background.RGBA.A = 1.0f;
        r.vsrVideoContext->VideoProcessorSetOutputBackgroundColor(processor, FALSE, &background);
        if (r.vsrVideoContext1)
        {
            r.vsrVideoContext1->VideoProcessorSetStreamColorSpace1(processor, 0, streamColorSpace);
            r.vsrVideoContext1->VideoProcessorSetOutputColorSpace1(processor, outputColorSpace);
        }
        else
        {
            // Windows 8.x fallback: legacy colour-space description. 709 matrix,
            // studio range on the YCbCr side, full range on the RGB side.
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE stream{};
            stream.Usage = 0;
            stream.RGB_Range = 0;                 // full-range RGB
            stream.YCbCr_Matrix = 1;              // BT.709
            stream.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
            r.vsrVideoContext->VideoProcessorSetStreamColorSpace(processor, 0, &stream);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE output = stream;
            r.vsrVideoContext->VideoProcessorSetOutputColorSpace(processor, &output);
        }
    }

    // Opens a keyed-mutex shared texture created on one device onto another.
    bool OpenSharedOnDevice(ID3D11Texture2D* created, ID3D11Device* target,
                            ID3D11Texture2D** opened, IDXGIKeyedMutex** createdMutex,
                            IDXGIKeyedMutex** openedMutex)
    {
        IDXGIResource* resource = nullptr;
        if (FAILED(created->QueryInterface(__uuidof(IDXGIResource), reinterpret_cast<void**>(&resource))))
            return false;
        HANDLE handle = nullptr;
        const HRESULT handleHr = resource->GetSharedHandle(&handle);
        resource->Release();
        if (FAILED(handleHr) || !handle) return false;

        if (FAILED(target->OpenSharedResource(handle, __uuidof(ID3D11Texture2D),
                reinterpret_cast<void**>(opened))))
            return false;
        if (FAILED(created->QueryInterface(__uuidof(IDXGIKeyedMutex), reinterpret_cast<void**>(createdMutex))))
            return false;
        return SUCCEEDED((*opened)->QueryInterface(__uuidof(IDXGIKeyedMutex), reinterpret_cast<void**>(openedMutex)));
    }

    bool InitializeVsrResources(Renderer& r, uint32_t inputWidth, uint32_t inputHeight,
                                uint32_t outputWidth, uint32_t outputHeight, HRESULT& status)
    {
        status = E_FAIL;
        if (!r.vsrVideoDevice || !r.vsrVideoContext || !r.renderTarget) return false;
        if (r.adapterVendorId != 0x10DE)
        {
            status = E_NOTIMPL;
            return false;
        }

        // NV12 is 4:2:0: chroma plane dimensions must be integral.
        inputWidth &= ~1u;
        inputHeight &= ~1u;
        if (inputWidth < 2 || inputHeight < 2) return false;

        // Aspect-fitted content size: the VSR VP renders full-frame into a
        // target of exactly this size (dest rect == output rect, mandatory —
        // see vsrDestOffsetX comment), centering happens post-VSR.
        const double scale = std::min(outputWidth / static_cast<double>(inputWidth),
                                      outputHeight / static_cast<double>(inputHeight));
        const uint32_t fittedWidth = std::max<uint32_t>(2,
            static_cast<uint32_t>(std::lround(inputWidth * scale)) & ~1u);
        const uint32_t fittedHeight = std::max<uint32_t>(2,
            static_cast<uint32_t>(std::lround(inputHeight * scale)) & ~1u);
        r.vsrContentWidth = fittedWidth;
        r.vsrContentHeight = fittedHeight;
        r.vsrDestOffsetX = (outputWidth - std::min(outputWidth, fittedWidth)) / 2;
        r.vsrDestOffsetY = (outputHeight - std::min(outputHeight, fittedHeight)) / 2;

        // ---- Stage 1: RGBA (mpv render target) -> NV12 at source size. ----
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC convContent{};
        convContent.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        convContent.InputFrameRate = { 60, 1 };
        convContent.InputWidth = inputWidth;
        convContent.InputHeight = inputHeight;
        convContent.OutputFrameRate = { 60, 1 };
        convContent.OutputWidth = inputWidth;
        convContent.OutputHeight = inputHeight;
        convContent.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        status = r.vsrVideoDevice->CreateVideoProcessorEnumerator(&convContent, &r.convEnumerator);
        if (FAILED(status)) return false;

        UINT rgbaSupport = 0, nv12Support = 0;
        if (FAILED(r.convEnumerator->CheckVideoProcessorFormat(DXGI_FORMAT_R8G8B8A8_UNORM, &rgbaSupport)) ||
            FAILED(r.convEnumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &nv12Support)) ||
            (rgbaSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0 ||
            (nv12Support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        {
            status = DXGI_ERROR_UNSUPPORTED;
            return false;
        }

        status = r.vsrVideoDevice->CreateVideoProcessor(r.convEnumerator, 0, &r.convProcessor);
        if (FAILED(status)) return false;

        // NV12 intermediate lives entirely on the video device. The VsrProbe
        // matrix proved a plain RENDER_TARGET NV12 engages VSR — no decoder
        // bind or 16-alignment required.
        D3D11_TEXTURE2D_DESC nv12Desc{};
        nv12Desc.Width = inputWidth;
        nv12Desc.Height = inputHeight;
        nv12Desc.MipLevels = 1;
        nv12Desc.ArraySize = 1;
        nv12Desc.Format = DXGI_FORMAT_NV12;
        nv12Desc.SampleDesc.Count = 1;
        nv12Desc.Usage = D3D11_USAGE_DEFAULT;
        nv12Desc.BindFlags = D3D11_BIND_RENDER_TARGET; // required by the VP output view
        status = r.vsrDevice->CreateTexture2D(&nv12Desc, nullptr, &r.vsrNv12);
        if (FAILED(status)) return false;

        // Source-frame bridge: created shared on the main device (which copies
        // the mpv render target into it), consumed by the video device.
        D3D11_TEXTURE2D_DESC copyDesc{};
        copyDesc.Width = inputWidth;
        copyDesc.Height = inputHeight;
        copyDesc.MipLevels = 1;
        copyDesc.ArraySize = 1;
        copyDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        copyDesc.SampleDesc.Count = 1;
        copyDesc.Usage = D3D11_USAGE_DEFAULT;
        copyDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        copyDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        status = r.device->CreateTexture2D(&copyDesc, nullptr, &r.vsrCopy);
        if (FAILED(status)) return false;
        if (!OpenSharedOnDevice(r.vsrCopy, r.vsrDevice, &r.vsrCopyRemote,
                &r.copyMutexMain, &r.copyMutexRemote))
        {
            status = E_FAIL;
            return false;
        }

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC convInDesc{};
        convInDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        status = r.vsrVideoDevice->CreateVideoProcessorInputView(
            r.vsrCopyRemote, r.convEnumerator, &convInDesc, &r.convInputView);
        if (FAILED(status)) return false;

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC convOutDesc{};
        convOutDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        status = r.vsrVideoDevice->CreateVideoProcessorOutputView(
            r.vsrNv12, r.convEnumerator, &convOutDesc, &r.convOutputView);
        if (FAILED(status)) return false;

        RECT convRect{ 0, 0, static_cast<LONG>(inputWidth), static_cast<LONG>(inputHeight) };
        ConfigureProcessorStream(r, r.convProcessor, convRect, convRect, convRect,
            DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709,
            DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);

        // Keep the VP converter alive as a fallback, but prefer graphics-pipeline
        // writes: NVIDIA's VSR works in both cases, while NVCP only recognizes
        // the latter as an active Super Resolution session.
        const HRESULT shaderStatus = InitializeVsrConversionShaders(r);
        const bool forceVpConverter =
            GetEnvironmentVariableA("ELYFLOW_VSR_CONV_VP", nullptr, 0) != 0;
        r.convUseShader = SUCCEEDED(shaderStatus) && !forceVpConverter;
        if (FAILED(shaderStatus))
        {
            std::lock_guard lock(r.stateMutex);
            r.state.lastConvStatus = static_cast<int32_t>(shaderStatus);
        }

        // ---- Stage 2: NV12 -> RGBA at fitted content size, RTX VSR on. ----
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate = { 60, 1 };
        content.InputWidth = inputWidth;
        content.InputHeight = inputHeight;
        content.OutputFrameRate = { 60, 1 };
        content.OutputWidth = fittedWidth;
        content.OutputHeight = fittedHeight;
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        status = r.vsrVideoDevice->CreateVideoProcessorEnumerator(&content, &r.vsrEnumerator);
        if (FAILED(status)) return false;

        UINT inputSupport = 0, outputSupport = 0;
        if (FAILED(r.vsrEnumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &inputSupport)) ||
            FAILED(r.vsrEnumerator->CheckVideoProcessorFormat(DXGI_FORMAT_R8G8B8A8_UNORM, &outputSupport)) ||
            (inputSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0 ||
            (outputSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        {
            status = DXGI_ERROR_UNSUPPORTED;
            return false;
        }

        status = r.vsrVideoDevice->CreateVideoProcessor(r.vsrEnumerator, 0, &r.vsrProcessor);
        if (FAILED(status)) return false;
        {
            std::lock_guard lock(r.stateMutex);
            r.state.videoProcessorCreated = 1;
        }

        status = QueryNvidiaVsr(r);
        if (FAILED(status) || !r.vsrAvailable)
        {
            if (SUCCEEDED(status)) status = DXGI_ERROR_UNSUPPORTED;
            return false;
        }

        // VSR result bridge (content-sized): written by the video device, read
        // by the main device; plus the window-sized composition target the
        // rest of the pipeline consumes.
        D3D11_TEXTURE2D_DESC outputDesc{};
        outputDesc.Width = fittedWidth;
        outputDesc.Height = fittedHeight;
        outputDesc.MipLevels = 1;
        outputDesc.ArraySize = 1;
        outputDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        outputDesc.SampleDesc.Count = 1;
        outputDesc.Usage = D3D11_USAGE_DEFAULT;
        outputDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        outputDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        status = r.vsrDevice->CreateTexture2D(&outputDesc, nullptr, &r.vsrOutput);
        if (FAILED(status)) return false;
        if (!OpenSharedOnDevice(r.vsrOutput, r.device, &r.vsrOutputMain,
                &r.outMutexRemote, &r.outMutexMain))
        {
            status = E_FAIL;
            return false;
        }
        outputDesc.Width = outputWidth;
        outputDesc.Height = outputHeight;
        outputDesc.MiscFlags = 0;
        status = r.device->CreateTexture2D(&outputDesc, nullptr, &r.vsrResult);
        if (FAILED(status)) return false;
        // Black background once: the letterbox bars live in vsrResult, only
        // the fitted content region is refreshed every frame.
        ID3D11RenderTargetView* clearView = nullptr;
        if (SUCCEEDED(r.device->CreateRenderTargetView(r.vsrResult, nullptr, &clearView)))
        {
            const float black[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
            r.context->ClearRenderTargetView(clearView, black);
            clearView->Release();
        }

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDesc{};
        inputViewDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        inputViewDesc.Texture2D.MipSlice = 0;
        inputViewDesc.Texture2D.ArraySlice = 0;
        status = r.vsrVideoDevice->CreateVideoProcessorInputView(
            r.vsrNv12, r.vsrEnumerator, &inputViewDesc, &r.vsrInputView);
        if (FAILED(status)) return false;

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDesc{};
        outputViewDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        outputViewDesc.Texture2D.MipSlice = 0;
        status = r.vsrVideoDevice->CreateVideoProcessorOutputView(
            r.vsrOutput, r.vsrEnumerator, &outputViewDesc, &r.vsrOutputView);
        if (FAILED(status)) return false;

        RECT sourceRect{ 0, 0, static_cast<LONG>(inputWidth), static_cast<LONG>(inputHeight) };
        RECT fittedRect{ 0, 0, static_cast<LONG>(fittedWidth), static_cast<LONG>(fittedHeight) };
        ConfigureProcessorStream(r, r.vsrProcessor, sourceRect, fittedRect, fittedRect,
            DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709,
            DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);

        // Debug isolation switch: ELYFLOW_VSR_NO_EXT=1 keeps the whole NV12
        // chain but never enables the NVIDIA extension — separates "the VP
        // chain crashes" from "the VSR kernel crashes".
        const bool skipExtension = GetEnvironmentVariableA("ELYFLOW_VSR_NO_EXT", nullptr, 0) != 0;
        if (!skipExtension)
        {
            struct NvidiaExtension { unsigned int version; unsigned int method; unsigned int enable; };
            NvidiaExtension extension{ 1, 2, 1 };
            status = r.vsrVideoContext->VideoProcessorSetStreamExtension(
                r.vsrProcessor, 0, &NvidiaPpeInterfaceGuid, sizeof(extension), &extension);
            if (FAILED(status)) return false;
        }
        {
            std::lock_guard lock(r.stateMutex);
            r.state.vsrExtensionEnabled = skipExtension ? 0 : 1;
            r.state.vsrConverterActive = 1;
        }

        // GPU timestamp instrumentation around the VSR Blt (optional).
        D3D11_QUERY_DESC disjointDesc{ D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
        D3D11_QUERY_DESC timestampDesc{ D3D11_QUERY_TIMESTAMP, 0 };
        r.vsrDevice->CreateQuery(&disjointDesc, &r.vsrTsDisjoint);
        r.vsrDevice->CreateQuery(&timestampDesc, &r.vsrTsBegin);
        r.vsrDevice->CreateQuery(&timestampDesc, &r.vsrTsEnd);
        r.vsrTsPending = false;

        r.vsrActive = true;
        status = S_OK;
        return true;
    }

    // Collects the previous frame's VSR Blt GPU time without stalling: the
    // query results are simply skipped when not ready yet.
    void CollectVsrTiming(Renderer& r)
    {
        if (!r.vsrTsPending || !r.vsrTsDisjoint) return;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
        // Flushed at the end of the previous RunVsr: by the next frame the
        // result is normally ready. S_FALSE (not ready) simply retries later.
        if (r.vsrDeviceContext->GetData(r.vsrTsDisjoint, &disjoint, sizeof(disjoint), 0) != S_OK)
            return;
        r.vsrTsPending = false;
        if (disjoint.Disjoint || disjoint.Frequency == 0) return;
        UINT64 begin = 0, end = 0;
        if (r.vsrDeviceContext->GetData(r.vsrTsBegin, &begin, sizeof(begin), 0) != S_OK ||
            r.vsrDeviceContext->GetData(r.vsrTsEnd, &end, sizeof(end), 0) != S_OK ||
            end <= begin)
            return;
        const double ms = (end - begin) * 1000.0 / static_cast<double>(disjoint.Frequency);
        r.vsrBltAvgMs = r.vsrBltAvgMs == 0 ? ms : r.vsrBltAvgMs * 0.9 + ms * 0.1;
        std::lock_guard lock(r.stateMutex);
        r.state.vsrBltAvgMs = r.vsrBltAvgMs;
    }

    bool RunVsr(Renderer& r, HRESULT& status)
    {
        // Stage 0 (main device): hand the freshly rendered frame to the video
        // device. The mpv render target itself is registered with the WGL
        // interop and must never be touched by another device/engine.
        status = E_FAIL;
        if (r.copyMutexMain->AcquireSync(0, 100) != S_OK) return false;
        r.context->CopyResource(r.vsrCopy, r.renderTarget);
        r.copyMutexMain->ReleaseSync(1);
        r.context->Flush();

        // Stage 1 (video device): RGB full 709 -> NV12 studio 709.
        if (r.copyMutexRemote->AcquireSync(1, 100) != S_OK) return false;
        if (r.convUseShader)
        {
            ID3D11DeviceContext* context = r.vsrDeviceContext;
            context->IASetInputLayout(nullptr);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(r.convVs, nullptr, 0);
            context->PSSetSamplers(0, 1, &r.convSampler);
            context->PSSetShaderResources(0, 1, &r.convSrcSrv);
            context->RSSetState(nullptr);
            context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
            context->OMSetDepthStencilState(nullptr, 0);

            D3D11_VIEWPORT viewport{ 0.0f, 0.0f,
                static_cast<float>(r.renderWidth & ~1u),
                static_cast<float>(r.renderHeight & ~1u), 0.0f, 1.0f };
            context->RSSetViewports(1, &viewport);
            context->OMSetRenderTargets(1, &r.nv12LumaRtv, nullptr);
            context->PSSetShader(r.convPsLuma, nullptr, 0);
            context->Draw(3, 0);

            viewport.Width = static_cast<float>((r.renderWidth & ~1u) / 2);
            viewport.Height = static_cast<float>((r.renderHeight & ~1u) / 2);
            context->RSSetViewports(1, &viewport);
            context->OMSetRenderTargets(1, &r.nv12ChromaRtv, nullptr);
            context->PSSetShader(r.convPsChroma, nullptr, 0);
            context->Draw(3, 0);

            ID3D11ShaderResourceView* nullSrv = nullptr;
            context->PSSetShaderResources(0, 1, &nullSrv);
            ID3D11RenderTargetView* nullRtv = nullptr;
            context->OMSetRenderTargets(1, &nullRtv, nullptr);
            status = S_OK;
        }
        else
        {
            D3D11_VIDEO_PROCESSOR_STREAM convStream{};
            convStream.Enable = TRUE;
            convStream.pInputSurface = r.convInputView;
            convStream.InputFrameOrField = r.vsrFrameIndex;
            status = r.vsrVideoContext->VideoProcessorBlt(
                r.convProcessor, r.convOutputView, r.vsrFrameIndex, 1, &convStream);
        }
        r.copyMutexRemote->ReleaseSync(0);
        {
            std::lock_guard lock(r.stateMutex);
            r.state.lastConvStatus = static_cast<int32_t>(status);
        }
        if (FAILED(status)) return false;

        // Stage 2 (video device): NV12 -> RGBA upscale — the pass RTX VSR
        // actually processes. GPU-timed as execution proof.
        CollectVsrTiming(r);
        if (r.outMutexRemote->AcquireSync(0, 100) != S_OK) return false;
        const bool timeThisFrame = !r.vsrTsPending && r.vsrTsDisjoint;
        if (timeThisFrame)
        {
            r.vsrDeviceContext->Begin(r.vsrTsDisjoint);
            r.vsrDeviceContext->End(r.vsrTsBegin);
        }
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = r.vsrInputView;
        stream.InputFrameOrField = r.vsrFrameIndex;
        status = r.vsrVideoContext->VideoProcessorBlt(
            r.vsrProcessor, r.vsrOutputView, r.vsrFrameIndex++, 1, &stream);
        if (timeThisFrame)
        {
            r.vsrDeviceContext->End(r.vsrTsEnd);
            r.vsrDeviceContext->End(r.vsrTsDisjoint);
            r.vsrTsPending = true;
        }
        r.outMutexRemote->ReleaseSync(1);
        r.vsrDeviceContext->Flush();
        if (FAILED(status)) return false;

        // Debug isolation switch: ELYFLOW_VSR_NO_QUERY=1 keeps VSR enabled but
        // never issues the state query after Blt — separates "the VSR kernel
        // crashes" from "querying the extension while VSR runs crashes".
        static const bool skipQuery = GetEnvironmentVariableA("ELYFLOW_VSR_NO_QUERY", nullptr, 0) != 0;
        if (!skipQuery)
            QueryNvidiaVsr(r);

        // Stage 3 (main device): compose the content-sized result centered
        // into the window-sized target (the letterbox the VP must not do).
        if (r.outMutexMain->AcquireSync(1, 100) != S_OK) return false;
        r.context->CopySubresourceRegion(r.vsrResult, 0,
            r.vsrDestOffsetX, r.vsrDestOffsetY, 0, r.vsrOutputMain, 0, nullptr);
        r.outMutexMain->ReleaseSync(0);
        status = S_OK;
        return true;
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
            r.state.vsrAvailable = 0;
            r.state.vsrEffective = 0;
            r.state.vsrLevel = 0;
        }
    }

    bool EnsureTargets(Renderer& r, uint32_t w, uint32_t h)
    {
        if (w == 0 || h == 0) return false;
        const uint32_t sourceWidth = r.sourceWidthHint.load();
        const uint32_t sourceHeight = r.sourceHeightHint.load();
        // AudioCore+ must render in swapchain coordinates. Feeding it through
        // the video VSR source-size/aspect-fit path letterboxes the scene and
        // makes its WPF overlay coordinates impossible to align.
        const bool upscaleRequested = !r.audioCoreScene.load() &&
            r.vsrWanted.load() &&
            sourceWidth > 0 && sourceHeight > 0 &&
            (sourceWidth < w || sourceHeight < h) &&
            r.vsrVideoDevice && r.vsrVideoContext;
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
            {
                std::lock_guard lock(r.stateMutex);
                r.state.targetRebuilds++;
            }
            r.width = w;
            r.height = h;
            // NV12 (the VSR input) is 4:2:0: keep the source-size render target
            // even in both dimensions so the conversion pass maps 1:1.
            r.renderWidth = enableVsr ? (sourceWidth & ~1u) : w;
            r.renderHeight = enableVsr ? (sourceHeight & ~1u) : h;

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
                {
                    std::lock_guard lock(r.stateMutex);
                    r.state.swapchainResizes++;
                }
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
                r.state.vsrActive = r.vsrEffective ? 1 : 0;
                r.state.lastVsrStatus = static_cast<int32_t>(vsrStatus);
                r.state.vsrInputWidth = r.vsrActive ? r.renderWidth : 0;
                r.state.vsrInputHeight = r.vsrActive ? r.renderHeight : 0;
                r.state.vsrContentWidth = r.vsrActive ? r.vsrContentWidth : 0;
                r.state.vsrContentHeight = r.vsrActive ? r.vsrContentHeight : 0;
                r.state.vsrAvailable = r.vsrAvailable ? 1 : 0;
                r.state.vsrRequested = r.vsrWanted.load() ? 1 : 0;
                r.state.vsrEffective = r.vsrEffective ? 1 : 0;
                r.state.vsrLevel = static_cast<int32_t>(r.vsrLevel);
                r.state.adapterVendorId = r.adapterVendorId;
                r.state.vsrInputFormat = enableVsr ? DXGI_FORMAT_NV12 : DXGI_FORMAT_R8G8B8A8_UNORM;
                r.state.vsrOutputFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
                r.state.vsrColorSpace = enableVsr
                    ? DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709
                    : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
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
        const UINT syncInterval = r.audioCoreScene.load() && r.audioCore.VSync() ? 1u : 0u;
        const HRESULT hr = r.swapChain->Present(syncInterval, 0);
        std::lock_guard lock(r.stateMutex);
        if (FAILED(hr))
        {
            r.state.presentErrors++;
            // DEVICE_REMOVED/RESET here is the signature of a driver TDR — the
            // swapchain then shows black. Surface it in the state message so
            // the C# diagnostics can tell this apart from a pacing problem.
            const HRESULT removed = r.device ? r.device->GetDeviceRemovedReason() : S_OK;
            char buf[160];
            sprintf_s(buf, "Present a echoue (0x%08X), GetDeviceRemovedReason=0x%08X.",
                static_cast<unsigned>(hr), static_cast<unsigned>(removed));
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
        r.context->QueryInterface(__uuidof(ID3D11VideoContext1), reinterpret_cast<void**>(&r.videoContext1));
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
        DXGI_ADAPTER_DESC adapterDescription{};
        if (SUCCEEDED(adapter->GetDesc(&adapterDescription)))
        {
            r.adapterVendorId = adapterDescription.VendorId;
            WideCharToMultiByte(CP_UTF8, 0, adapterDescription.Description, -1,
                r.state.adapterName, static_cast<int>(sizeof(r.state.adapterName)), nullptr, nullptr);
        }

        // Dedicated video-processing device on the same adapter (see the
        // Renderer member comment: VSR must not run on the GL-interop device).
        if (SUCCEEDED(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION,
                &r.vsrDevice, nullptr, &r.vsrDeviceContext)))
        {
            r.vsrDevice->QueryInterface(__uuidof(ID3D11VideoDevice), reinterpret_cast<void**>(&r.vsrVideoDevice));
            r.vsrDeviceContext->QueryInterface(__uuidof(ID3D11VideoContext), reinterpret_cast<void**>(&r.vsrVideoContext));
            r.vsrDeviceContext->QueryInterface(__uuidof(ID3D11VideoContext1), reinterpret_cast<void**>(&r.vsrVideoContext1));
        }
        LARGE_INTEGER driverVersion{};
        if (SUCCEEDED(adapter->CheckInterfaceSupport(__uuidof(IDXGIDevice), &driverVersion)))
        {
            const auto high = static_cast<uint64_t>(driverVersion.QuadPart) >> 32;
            const auto low = static_cast<uint64_t>(driverVersion.QuadPart) & 0xffffffffULL;
            sprintf_s(r.state.driverVersion, "%llu.%llu.%llu.%llu",
                (high >> 16) & 0xffff, high & 0xffff, (low >> 16) & 0xffff, low & 0xffff);
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

        bool previousAudioScene = false;
        double audioNextPresent = 0.0;
        double audioInterval = 1.0 / 240.0;
        while (!r.quit.load())
        {
            const bool audioScene = r.audioCoreScene.load();
            if (audioScene != previousAudioScene)
            {
                r.havePrevious = false;
                previousAudioScene = audioScene;
                audioNextPresent = 0.0;
            }
            // AudioCore+ owns its own high-resolution cadence so it is not
            // capped by WPF's CompositionTarget.Rendering (= monitor refresh).
            // The UI thread only publishes fresh geometry via PushVisualFrame
            // (which signals wakeEvent); this thread paces presentation up to
            // the configured target FPS. Video mode stays event-driven.
            if (audioScene)
            {
                const int targetFps = r.audioCore.TargetFps();
                // targetFps == 0 -> unlimited: never wait, present as fast as the
                // GPU allows (still gated by VSync when the user keeps it on).
                audioInterval = targetFps > 0 ? 1.0 / targetFps : 0.0;
                if (audioInterval > 0.0)
                {
                    const double nowSeconds = QpcSeconds();
                    if (audioNextPresent <= 0.0) audioNextPresent = nowSeconds;
                    const double remaining = audioNextPresent - nowSeconds;
                    if (remaining > 0.0011)
                    {
                        // Sleep the bulk of the interval; a fresh push wakes us
                        // early and we re-evaluate the deadline on the next lap.
                        WaitForSingleObject(r.wakeEvent, static_cast<DWORD>(remaining * 1000.0));
                        continue;
                    }
                }
            }
            else
            {
                WaitForSingleObject(r.wakeEvent, 50);
            }
            if (r.quit.load()) break;

            const uint64_t flags = r.mpvApi.Update(r.renderContext);
            const bool hasMpvFrame = (flags & MPV_RENDER_UPDATE_FRAME) != 0;
            if (!hasMpvFrame && !audioScene) continue;

            auto skipFrame = [&]
            {
                if (!hasMpvFrame) return;
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

            if (audioScene)
            {
                if (!r.audioCore.Render(r.device, r.context, r.renderTarget,
                    r.renderWidth, r.renderHeight, QpcSeconds()))
                    r.SetMessage("ELYCAST AudioCore+: compilation/rendu shader D3D11 impossible.");
                // Advance the cadence deadline; if we fell behind (GPU hitch or a
                // slow interval), resync to now so we never spiral into catch-up.
                audioNextPresent += audioInterval;
                const double afterRender = QpcSeconds();
                if (audioNextPresent < afterRender) audioNextPresent = afterRender;
                skipFrame();
            }
            // mpv -> GL FBO -> D3D11 renderTarget (same GPU memory).
            else if (!r.api.DXLockObjectsNV(r.interopDevice, 1, &r.interopTexture))
            {
                // Rendering into an unlocked interop texture is undefined —
                // that is exactly what showed up as random black frames.
                skipFrame();
                continue;
            }
            else
            {
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
            }

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
                    r.failedVsrSourceWidth = r.sourceWidthHint.load();
                    r.failedVsrSourceHeight = r.sourceHeightHint.load();
                    r.failedVsrOutputWidth = w;
                    r.failedVsrOutputHeight = h;
                    std::lock_guard lock(r.stateMutex);
                    r.state.vsrActive = 0;
                    r.state.lastVsrStatus = static_cast<int32_t>(vsrStatus);
                    strncpy_s(r.state.message,
                        "RTX VSR a échoué pendant VideoProcessorBlt; repli au prochain frame.", _TRUNCATE);
                    continue;
                }
                processedTexture = r.vsrResult;
                {
                    std::lock_guard lock(r.stateMutex);
                    r.state.videoProcessorFrames++;
                    if (r.vsrEffective) r.state.vsrFramesProcessed++;
                    else r.state.vsrFramesBypassed++;
                    r.state.vsrActive = r.vsrEffective ? 1 : 0;
                    r.state.vsrAvailable = r.vsrAvailable ? 1 : 0;
                    r.state.vsrEffective = r.vsrEffective ? 1 : 0;
                    r.state.vsrLevel = static_cast<int32_t>(r.vsrLevel);
                    if (!r.vsrEffective)
                        strncpy_s(r.state.message,
                            "Passe D3D11 active, mais le driver NVIDIA ne déclare pas RTX VSR en utilisation (réglage pilote/source).",
                            _TRUNCATE);
                }
            }
            else
            {
                std::lock_guard lock(r.stateMutex);
                r.state.vsrFramesBypassed++;
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
            const bool wantFruc = r.frucWanted.load() && !audioScene;
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
        SafeRelease(r.videoContext1);
        SafeRelease(r.videoDevice);
        SafeRelease(r.vsrVideoContext1);
        SafeRelease(r.vsrVideoContext);
        SafeRelease(r.vsrVideoDevice);
        SafeRelease(r.vsrDeviceContext);
        SafeRelease(r.vsrDevice);
        SafeRelease(r.device5);
        SafeRelease(r.context);
        SafeRelease(r.device);
        if (r.waitTimer) { CloseHandle(r.waitTimer); r.waitTimer = nullptr; }
    }
}

uint32_t ElyFlowRenderer_GetAbiVersion()
{
    return ELYFLOW_RENDERER_ABI_VERSION;
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
    r->frucWanted.store(enableFruc != 0);
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
        g_renderer->sourceFpsHint.store(sourceFps);
}

void ElyFlowRenderer_ConfigureVsr(int32_t enable, uint32_t sourceWidth, uint32_t sourceHeight)
{
    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer) return;
    g_renderer->sourceWidthHint.store(sourceWidth);
    g_renderer->sourceHeightHint.store(sourceHeight);
    g_renderer->vsrWanted.store(enable != 0);
    {
        std::lock_guard stateLock(g_renderer->stateMutex);
        g_renderer->state.vsrRequested = enable != 0 ? 1 : 0;
    }
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
}

void ElyFlowRenderer_ConfigureFruc(int32_t enable)
{
    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer) return;
    g_renderer->frucWanted.store(enable != 0);
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
}

int32_t ElyAudioCore_SetScene(int32_t enabled)
{
    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer) return ELYFLOW_RENDERER_ALREADY_ACTIVE;
    g_renderer->audioCoreScene.store(enabled != 0);
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
    return ELYFLOW_RENDERER_OK;
}

void ElyAudioCore_PushAudioFrame(const double* bands, int32_t count,
                                 float bass, float energy, float beat)
{
    std::lock_guard rendererLock(g_rendererMutex);
    if (!g_renderer || !bands || count <= 0) return;
    g_renderer->audioCore.Push(bands, count, bass, energy, beat);
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
}

void ElyAudioCore_PushVisualFrame(const ElyAudioCoreVisualFrameNative* frame,
                                  const ElyAudioCoreLineNative* bars, int32_t barCount,
                                  const ElyAudioCoreEllipseNative* particles, int32_t particleCount,
                                  const ElyAudioCoreEllipseNative* waves, int32_t waveCount)
{
    if (!frame || frame->structSize < sizeof(ElyAudioCoreVisualFrameNative)) return;
    std::lock_guard rendererLock(g_rendererMutex);
    if (!g_renderer) return;
    g_renderer->audioCore.PushVisualFrame(*frame, bars, barCount, particles, particleCount, waves, waveCount);
    if (g_renderer->wakeEvent) SetEvent(g_renderer->wakeEvent);
}

void ElyAudioCore_Beat(float strength)
{
    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) g_renderer->audioCore.Beat(strength);
}

void ElyAudioCore_SetPalette(const uint32_t* colors, int32_t count)
{
    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) g_renderer->audioCore.SetPalette(colors, count);
}

void ElyAudioCore_SetSettings(const ElyAudioCoreSettingsNative* settings)
{
    if (!settings || settings->structSize < sizeof(ElyAudioCoreSettingsNative)) return;
    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) g_renderer->audioCore.SetSettings(*settings);
}

void ElyAudioCore_SetBackground(const uint8_t* bgra, uint32_t width, uint32_t height, uint32_t stride)
{
    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) g_renderer->audioCore.SetBackground(bgra, width, height, stride);
}

void ElyAudioCore_SetPointer(float x, float y)
{
    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) g_renderer->audioCore.SetPointer(x, y);
}

void ElyAudioCore_SetLayout(float centerX, float centerY, float innerRadius, float unitScale)
{
    std::lock_guard lock(g_rendererMutex);
    if (g_renderer) g_renderer->audioCore.SetLayout(centerX, centerY, innerRadius, unitScale);
}

int32_t ElyAudioCore_GetStats(ElyAudioCoreStatsNative* stats)
{
    if (!stats || stats->structSize < sizeof(ElyAudioCoreStatsNative)) return ELYFLOW_RENDERER_INVALID_ARGUMENT;
    std::lock_guard lock(g_rendererMutex);
    if (!g_renderer)
    {
        // No renderer bound yet: report an inactive-but-healthy snapshot rather
        // than a misleading "already active" code the managed health check
        // would mistake for a D3D11 failure.
        const uint32_t size = stats->structSize;
        *stats = ElyAudioCoreStatsNative{ size, 0, 0.0, 0.0, 0, 0 };
        return ELYFLOW_RENDERER_OK;
    }
    *stats = g_renderer->audioCore.Stats();
    stats->active = g_renderer->audioCoreScene.load() ? 1 : 0;
    return ELYFLOW_RENDERER_OK;
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
