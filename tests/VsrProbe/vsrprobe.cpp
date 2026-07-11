// VsrProbe — standalone matrix test of NVIDIA RTX Video Super Resolution
// through the D3D11 VideoProcessor. Each configuration runs on a FRESH device
// (a driver crash kills the device, never the probe), performs N Blts on an
// NV12 input uploaded from the CPU, then reports:
//   - SetStreamExtension HRESULT
//   - GetStreamExtension raw payload before/after the Blts
//   - GPU time of one Blt (timestamp queries)
//   - GetDeviceRemovedReason after the run
// Goal: find the exact input conditions under which the driver both engages
// VSR (query reports in-use / multi-ms kernel time) and stays alive.

#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <d3d9.h>
#include <gl/GL.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <vector>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d3d9.lib")
#pragma comment(lib, "opengl32.lib")
#pragma comment(lib, "gdi32.lib")

static const GUID NvidiaPpeInterfaceGuid =
    { 0xd43ce1b3, 0x1f4b, 0x48ac, { 0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7 } };

template <typename T> void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

struct Config
{
    const char* name;
    uint32_t srcW, srcH;
    uint32_t dstW, dstH;
    UINT inputBind;         // bind flags of the NV12 input texture
    DXGI_FORMAT outputFormat;
    bool letterbox;         // centered dest rect instead of full output
    bool alignInput16;      // allocate input aligned to 16, source rect = content
    bool setColorSpace1;
    UINT frameRate;
    bool enableExtension = true; // set the NVIDIA VSR stream extension
};

struct Result
{
    HRESULT extensionHr = E_FAIL;
    UINT queryBefore = 0xDEADBEEF;
    HRESULT queryBeforeHr = E_FAIL;
    UINT queryAfter = 0xDEADBEEF;
    HRESULT queryAfterHr = E_FAIL;
    HRESULT bltHr = E_FAIL;
    HRESULT removedReason = S_OK;
    double bltMs = -1;
    int bltsDone = 0;
    const char* failStage = "";
    std::vector<uint8_t> pixels;   // output readback (RGBA rows, tight)
};

static bool RunConfig(const Config& c, Result& out)
{
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    ID3D11VideoDevice* vdev = nullptr;
    ID3D11VideoContext* vctx = nullptr;
    ID3D11VideoContext1* vctx1 = nullptr;
    ID3D11VideoProcessorEnumerator* enu = nullptr;
    ID3D11VideoProcessor* vp = nullptr;
    ID3D11Texture2D* input = nullptr;
    ID3D11Texture2D* output = nullptr;
    ID3D11VideoProcessorInputView* inView = nullptr;
    ID3D11VideoProcessorOutputView* outView = nullptr;
    ID3D11Query* qDisjoint = nullptr;
    ID3D11Query* qBegin = nullptr;
    ID3D11Query* qEnd = nullptr;

    auto cleanup = [&]
    {
        SafeRelease(qEnd); SafeRelease(qBegin); SafeRelease(qDisjoint);
        SafeRelease(outView); SafeRelease(inView);
        SafeRelease(output); SafeRelease(input);
        SafeRelease(vp); SafeRelease(enu);
        SafeRelease(vctx1); SafeRelease(vctx); SafeRelease(vdev);
        SafeRelease(ctx); SafeRelease(device);
    };
    auto fail = [&](const char* stage) { out.failStage = stage; cleanup(); return false; };

    static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &device, nullptr, &ctx)))
        return fail("CreateDevice");
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&vdev))) ||
        FAILED(ctx->QueryInterface(IID_PPV_ARGS(&vctx))))
        return fail("VideoDevice");
    ctx->QueryInterface(IID_PPV_ARGS(&vctx1));

    const uint32_t allocW = c.alignInput16 ? (c.srcW + 15u) & ~15u : c.srcW;
    const uint32_t allocH = c.alignInput16 ? (c.srcH + 15u) & ~15u : c.srcH;

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};
    cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    cd.InputFrameRate = { c.frameRate, 1 };
    cd.InputWidth = c.srcW;
    cd.InputHeight = c.srcH;
    cd.OutputFrameRate = { c.frameRate, 1 };
    cd.OutputWidth = c.dstW;
    cd.OutputHeight = c.dstH;
    cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    if (FAILED(vdev->CreateVideoProcessorEnumerator(&cd, &enu))) return fail("Enumerator");
    if (FAILED(vdev->CreateVideoProcessor(enu, 0, &vp))) return fail("Processor");

    // NV12 input, CPU-uploaded gradient so the kernel has real content.
    D3D11_TEXTURE2D_DESC td{};
    td.Width = allocW;
    td.Height = allocH;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = c.inputBind;
    // High-frequency luma content (checkerboard + diagonals over a gradient):
    // a super-resolution kernel visibly reshapes edges, a bilinear scaler
    // does not — smooth gradients alone would hide the difference.
    std::vector<uint8_t> nv12(static_cast<size_t>(allocW) * allocH * 3 / 2);
    for (uint32_t y = 0; y < allocH; ++y)
        for (uint32_t x = 0; x < allocW; ++x)
        {
            int v = 16 + ((x * 219) / allocW + (y * 219) / allocH) / 2;
            if (((x / 4) + (y / 4)) & 1) v = 235 - v + 16;      // 4px checkerboard
            if (((x + y) % 17) == 0) v = 16;                     // diagonal dark lines
            nv12[static_cast<size_t>(y) * allocW + x] = static_cast<uint8_t>(v);
        }
    uint8_t* chroma = nv12.data() + static_cast<size_t>(allocW) * allocH;
    for (uint32_t y = 0; y < allocH / 2; ++y)
        for (uint32_t x = 0; x < allocW / 2; ++x)
        {
            chroma[(static_cast<size_t>(y) * allocW) + x * 2] = static_cast<uint8_t>(64 + (x % 128));
            chroma[(static_cast<size_t>(y) * allocW) + x * 2 + 1] = static_cast<uint8_t>(192 - (y % 128));
        }
    D3D11_SUBRESOURCE_DATA init{ nv12.data(), allocW, 0 };
    if (FAILED(device->CreateTexture2D(&td, &init, &input))) return fail("InputTexture");

    D3D11_TEXTURE2D_DESC od{};
    od.Width = c.dstW;
    od.Height = c.dstH;
    od.MipLevels = 1;
    od.ArraySize = 1;
    od.Format = c.outputFormat;
    od.SampleDesc.Count = 1;
    od.Usage = D3D11_USAGE_DEFAULT;
    od.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(device->CreateTexture2D(&od, nullptr, &output))) return fail("OutputTexture");

    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    if (FAILED(vdev->CreateVideoProcessorInputView(input, enu, &ivd, &inView))) return fail("InputView");
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
    ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    if (FAILED(vdev->CreateVideoProcessorOutputView(output, enu, &ovd, &outView))) return fail("OutputView");

    RECT srcRect{ 0, 0, static_cast<LONG>(c.srcW), static_cast<LONG>(c.srcH) };
    RECT outRect{ 0, 0, static_cast<LONG>(c.dstW), static_cast<LONG>(c.dstH) };
    RECT dstRect = outRect;
    if (c.letterbox)
    {
        const LONG w = static_cast<LONG>(c.dstW * 9 / 10) & ~1L;
        const LONG h = static_cast<LONG>(c.dstH * 9 / 10) & ~1L;
        dstRect = { (static_cast<LONG>(c.dstW) - w) / 2, (static_cast<LONG>(c.dstH) - h) / 2,
                    (static_cast<LONG>(c.dstW) + w) / 2, (static_cast<LONG>(c.dstH) + h) / 2 };
    }
    vctx->VideoProcessorSetStreamFrameFormat(vp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    vctx->VideoProcessorSetStreamSourceRect(vp, 0, TRUE, &srcRect);
    vctx->VideoProcessorSetStreamDestRect(vp, 0, TRUE, &dstRect);
    vctx->VideoProcessorSetOutputTargetRect(vp, TRUE, &outRect);
    vctx->VideoProcessorSetStreamAutoProcessingMode(vp, 0, FALSE);
    vctx->VideoProcessorSetStreamOutputRate(vp, 0, D3D11_VIDEO_PROCESSOR_OUTPUT_RATE_NORMAL, FALSE, nullptr);
    if (c.setColorSpace1 && vctx1)
    {
        vctx1->VideoProcessorSetStreamColorSpace1(vp, 0, DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
        vctx1->VideoProcessorSetOutputColorSpace1(vp, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    }

    out.queryBeforeHr = vctx->VideoProcessorGetStreamExtension(
        vp, 0, &NvidiaPpeInterfaceGuid, sizeof(out.queryBefore), &out.queryBefore);

    if (c.enableExtension)
    {
        struct { UINT version, method, enable; } ext{ 1, 2, 1 };
        out.extensionHr = vctx->VideoProcessorSetStreamExtension(
            vp, 0, &NvidiaPpeInterfaceGuid, sizeof(ext), &ext);
    }
    else
    {
        out.extensionHr = S_FALSE; // deliberately not enabled
    }

    D3D11_QUERY_DESC qd{ D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
    device->CreateQuery(&qd, &qDisjoint);
    qd.Query = D3D11_QUERY_TIMESTAMP;
    device->CreateQuery(&qd, &qBegin);
    device->CreateQuery(&qd, &qEnd);

    for (int i = 0; i < 30; ++i)
    {
        const bool timed = i == 20;
        if (timed) { ctx->Begin(qDisjoint); ctx->End(qBegin); }
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = inView;
        stream.InputFrameOrField = static_cast<UINT>(i);
        out.bltHr = vctx->VideoProcessorBlt(vp, outView, static_cast<UINT>(i), 1, &stream);
        if (timed) { ctx->End(qEnd); ctx->End(qDisjoint); }
        if (FAILED(out.bltHr)) break;
        out.bltsDone = i + 1;
    }
    ctx->Flush();

    // Resolve timing (bounded wait — a dead device never returns S_OK).
    D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj{};
    for (int spin = 0; spin < 400; ++spin)
    {
        const HRESULT hr = ctx->GetData(qDisjoint, &dj, sizeof(dj), 0);
        if (hr == S_OK) break;
        if (FAILED(hr)) { dj.Frequency = 0; break; }
        Sleep(5);
    }
    if (dj.Frequency && !dj.Disjoint)
    {
        UINT64 b = 0, e = 0;
        if (ctx->GetData(qBegin, &b, sizeof(b), 0) == S_OK &&
            ctx->GetData(qEnd, &e, sizeof(e), 0) == S_OK && e > b)
            out.bltMs = (e - b) * 1000.0 / static_cast<double>(dj.Frequency);
    }

    out.queryAfterHr = vctx->VideoProcessorGetStreamExtension(
        vp, 0, &NvidiaPpeInterfaceGuid, sizeof(out.queryAfter), &out.queryAfter);
    out.removedReason = device->GetDeviceRemovedReason();

    // Pixel readback of the final output — ground truth of what the VP wrote.
    {
        D3D11_TEXTURE2D_DESC sd = od;
        sd.Usage = D3D11_USAGE_STAGING;
        sd.BindFlags = 0;
        sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging = nullptr;
        if (SUCCEEDED(device->CreateTexture2D(&sd, nullptr, &staging)))
        {
            ctx->CopyResource(staging, output);
            D3D11_MAPPED_SUBRESOURCE map{};
            if (SUCCEEDED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &map)))
            {
                out.pixels.resize(static_cast<size_t>(c.dstW) * c.dstH * 4);
                for (uint32_t y = 0; y < c.dstH; ++y)
                    memcpy(out.pixels.data() + static_cast<size_t>(y) * c.dstW * 4,
                           static_cast<const uint8_t*>(map.pData) + static_cast<size_t>(y) * map.RowPitch,
                           static_cast<size_t>(c.dstW) * 4);
                ctx->Unmap(staging, 0);
            }
            staging->Release();
        }
    }
    cleanup();
    return true;
}

// Compares two readbacks: differing byte count and max per-channel delta.
static void ComparePixels(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b,
                          size_t& differing, int& maxDelta)
{
    differing = 0;
    maxDelta = 0;
    if (a.size() != b.size() || a.empty()) { differing = SIZE_MAX; return; }
    for (size_t i = 0; i < a.size(); ++i)
    {
        const int d = static_cast<int>(a[i]) - static_cast<int>(b[i]);
        const int ad = d < 0 ? -d : d;
        if (ad != 0) { ++differing; if (ad > maxDelta) maxDelta = ad; }
    }
}

static void PrintResult(const Config& c, const Result& res, bool completed)
{
    printf("[%-32s] ", c.name);
    if (!completed)
    {
        printf("SETUP FAILED at %s\n", res.failStage);
        return;
    }
    printf("ext=0x%08lX qBefore=0x%08X(hr=0x%08lX) blts=%d bltHr=0x%08lX gpu=%.2fms qAfter=0x%08X(hr=0x%08lX) removed=0x%08lX\n",
        static_cast<unsigned long>(res.extensionHr),
        res.queryBefore, static_cast<unsigned long>(res.queryBeforeHr),
        res.bltsDone, static_cast<unsigned long>(res.bltHr), res.bltMs,
        res.queryAfter, static_cast<unsigned long>(res.queryAfterHr),
        static_cast<unsigned long>(res.removedReason));
}

// Continuous 30 fps VSR Blt loop, optionally presenting the result through a
// visible swapchain on the SAME device. Purpose: identify what the NVIDIA
// Control Panel "Super Resolution — État" indicator actually tracks
// (VP session alone vs VP session whose device presents).
// `dual` replicates ELYCORE's exact structure: a second VideoProcessor
// (RGBA->NV12 conversion, no extension) blitting right before the VSR one —
// candidate explanation for the NVCP indicator showing "Inactif" while the
// VSR VP provably processes.
static int WatchMode(bool present, int seconds, bool dual = false,
                     bool sharedOutput = false, bool queryEveryBlt = false,
                     bool rival = false, bool withGl = false, bool withD3d9 = false,
                     bool chain = false, bool withCuda = false, bool renderChain = false)
{
    if (chain) dual = true; // chain implies the conversion VP

    // Optional resident CUDA context, replicating what nvdec/NvOFFRUC leave
    // alive in the ELYCAST process even after decode/FRUC stop.
    if (withCuda)
    {
        typedef int (WINAPI* PFNCUINIT)(unsigned);
        typedef int (WINAPI* PFNCUDEVICEGET)(int*, int);
        typedef int (WINAPI* PFNCUCTXCREATE)(void**, unsigned, int);
        HMODULE cuda = LoadLibraryA("nvcuda.dll");
        if (!cuda) { printf("nvcuda.dll introuvable\n"); return 1; }
        auto cuInit = reinterpret_cast<PFNCUINIT>(GetProcAddress(cuda, "cuInit"));
        auto cuDeviceGet = reinterpret_cast<PFNCUDEVICEGET>(GetProcAddress(cuda, "cuDeviceGet"));
        auto cuCtxCreate = reinterpret_cast<PFNCUCTXCREATE>(GetProcAddress(cuda, "cuCtxCreate_v2"));
        if (!cuInit || !cuDeviceGet || !cuCtxCreate) { printf("exports CUDA introuvables\n"); return 1; }
        int dev = 0; void* cuCtx = nullptr;
        if (cuInit(0) != 0 || cuDeviceGet(&dev, 0) != 0 || cuCtxCreate(&cuCtx, 0, dev) != 0)
        { printf("contexte CUDA KO\n"); return 1; }
        printf("contexte CUDA cree et resident.\n");
    }
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    ID3D11VideoDevice* vdev = nullptr;
    ID3D11VideoContext* vctx = nullptr;
    ID3D11VideoContext1* vctx1 = nullptr;
    static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &device, nullptr, &ctx)))
        return 1;
    device->QueryInterface(IID_PPV_ARGS(&vdev));
    ctx->QueryInterface(IID_PPV_ARGS(&vctx));
    ctx->QueryInterface(IID_PPV_ARGS(&vctx1));

    const uint32_t srcW = 960, srcH = 540, dstW = 1920, dstH = 1080;
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};
    cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    cd.InputFrameRate = { 30, 1 };
    cd.InputWidth = srcW; cd.InputHeight = srcH;
    cd.OutputFrameRate = { 30, 1 };
    cd.OutputWidth = dstW; cd.OutputHeight = dstH;
    cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    ID3D11VideoProcessorEnumerator* enu = nullptr;
    ID3D11VideoProcessor* vp = nullptr;
    if (FAILED(vdev->CreateVideoProcessorEnumerator(&cd, &enu)) ||
        FAILED(vdev->CreateVideoProcessor(enu, 0, &vp)))
        return 1;

    D3D11_TEXTURE2D_DESC td{};
    td.Width = srcW; td.Height = srcH;
    td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    if (chain || renderChain) td.BindFlags = D3D11_BIND_RENDER_TARGET; // GPU writes into it
    std::vector<uint8_t> nv12(static_cast<size_t>(srcW) * srcH * 3 / 2, 128);
    for (uint32_t y = 0; y < srcH; ++y)
        for (uint32_t x = 0; x < srcW; ++x)
            nv12[static_cast<size_t>(y) * srcW + x] =
                static_cast<uint8_t>((((x / 3) + (y / 3)) & 1) ? 200 : 40);
    D3D11_SUBRESOURCE_DATA init{ nv12.data(), srcW, 0 };
    ID3D11Texture2D* input = nullptr;
    if (FAILED(device->CreateTexture2D(&td, &init, &input))) return 1;

    // renderChain: the NV12 input is rewritten every frame through the
    // GRAPHICS pipeline (render target clears on the luma/chroma planes) —
    // what a shader-based RGB->NV12 conversion would look like to the driver.
    ID3D11RenderTargetView* lumaRtv = nullptr;
    ID3D11RenderTargetView* chromaRtv = nullptr;
    if (renderChain)
    {
        D3D11_RENDER_TARGET_VIEW_DESC rv{};
        rv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        rv.Format = DXGI_FORMAT_R8_UNORM;
        if (FAILED(device->CreateRenderTargetView(input, &rv, &lumaRtv)))
        { printf("RTV luma KO\n"); return 1; }
        rv.Format = DXGI_FORMAT_R8G8_UNORM;
        if (FAILED(device->CreateRenderTargetView(input, &rv, &chromaRtv)))
        { printf("RTV chroma KO\n"); return 1; }
    }

    D3D11_TEXTURE2D_DESC od{};
    od.Width = dstW; od.Height = dstH;
    od.MipLevels = 1; od.ArraySize = 1;
    od.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    od.SampleDesc.Count = 1;
    od.Usage = D3D11_USAGE_DEFAULT;
    od.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (sharedOutput) od.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    ID3D11Texture2D* output = nullptr;
    if (FAILED(device->CreateTexture2D(&od, nullptr, &output))) return 1;
    IDXGIKeyedMutex* outputMutex = nullptr;
    if (sharedOutput) output->QueryInterface(IID_PPV_ARGS(&outputMutex));

    ID3D11VideoProcessorInputView* inView = nullptr;
    ID3D11VideoProcessorOutputView* outView = nullptr;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
    ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    if (FAILED(vdev->CreateVideoProcessorInputView(input, enu, &ivd, &inView)) ||
        FAILED(vdev->CreateVideoProcessorOutputView(output, enu, &ovd, &outView)))
        return 1;

    RECT srcRect{ 0, 0, static_cast<LONG>(srcW), static_cast<LONG>(srcH) };
    RECT dstRect{ 0, 0, static_cast<LONG>(dstW), static_cast<LONG>(dstH) };
    vctx->VideoProcessorSetStreamFrameFormat(vp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    vctx->VideoProcessorSetStreamSourceRect(vp, 0, TRUE, &srcRect);
    vctx->VideoProcessorSetStreamDestRect(vp, 0, TRUE, &dstRect);
    vctx->VideoProcessorSetOutputTargetRect(vp, TRUE, &dstRect);
    vctx->VideoProcessorSetStreamAutoProcessingMode(vp, 0, FALSE);
    vctx->VideoProcessorSetStreamOutputRate(vp, 0, D3D11_VIDEO_PROCESSOR_OUTPUT_RATE_NORMAL, FALSE, nullptr);
    if (vctx1)
    {
        vctx1->VideoProcessorSetStreamColorSpace1(vp, 0, DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
        vctx1->VideoProcessorSetOutputColorSpace1(vp, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    }
    struct { UINT version, method, enable; } ext{ 1, 2, 1 };
    vctx->VideoProcessorSetStreamExtension(vp, 0, &NvidiaPpeInterfaceGuid, sizeof(ext), &ext);

    // Optional second VP replicating ELYCORE's conversion pass: RGBA source
    // (same size as the NV12 input) -> NV12, no extension, blitted every frame
    // right before the VSR Blt. In `chain` mode the conversion writes INTO the
    // VSR's input texture (the exact ELYCORE data flow: VP -> VP); in plain
    // `dual` mode it writes to a scratch texture and the VSR keeps consuming
    // the CPU-uploaded input.
    ID3D11VideoProcessorEnumerator* convEnu = nullptr;
    ID3D11VideoProcessor* convVp = nullptr;
    ID3D11Texture2D* convSrc = nullptr;
    ID3D11Texture2D* convDst = nullptr;
    ID3D11VideoProcessorInputView* convIn = nullptr;
    ID3D11VideoProcessorOutputView* convOut = nullptr;
    if (dual)
    {
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC cc{};
        cc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        cc.InputFrameRate = { 30, 1 };
        cc.InputWidth = srcW; cc.InputHeight = srcH;
        cc.OutputFrameRate = { 30, 1 };
        cc.OutputWidth = srcW; cc.OutputHeight = srcH;
        cc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        if (FAILED(vdev->CreateVideoProcessorEnumerator(&cc, &convEnu)) ||
            FAILED(vdev->CreateVideoProcessor(convEnu, 0, &convVp)))
            return 1;
        D3D11_TEXTURE2D_DESC rgba{};
        rgba.Width = srcW; rgba.Height = srcH;
        rgba.MipLevels = 1; rgba.ArraySize = 1;
        rgba.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        rgba.SampleDesc.Count = 1;
        rgba.Usage = D3D11_USAGE_DEFAULT;
        rgba.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        std::vector<uint8_t> px(static_cast<size_t>(srcW) * srcH * 4, 180);
        D3D11_SUBRESOURCE_DATA pinit{ px.data(), srcW * 4, 0 };
        if (FAILED(device->CreateTexture2D(&rgba, &pinit, &convSrc))) return 1;
        if (chain)
        {
            // The conversion writes straight into the VSR's input texture —
            // ELYCORE's exact VP -> VP chaining.
            convDst = input;
            convDst->AddRef();
        }
        else
        {
            D3D11_TEXTURE2D_DESC nv{};
            nv.Width = srcW; nv.Height = srcH;
            nv.MipLevels = 1; nv.ArraySize = 1;
            nv.Format = DXGI_FORMAT_NV12;
            nv.SampleDesc.Count = 1;
            nv.Usage = D3D11_USAGE_DEFAULT;
            nv.BindFlags = D3D11_BIND_RENDER_TARGET;
            if (FAILED(device->CreateTexture2D(&nv, nullptr, &convDst))) return 1;
        }
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC civ{};
        civ.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC cov{};
        cov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        if (FAILED(vdev->CreateVideoProcessorInputView(convSrc, convEnu, &civ, &convIn)) ||
            FAILED(vdev->CreateVideoProcessorOutputView(convDst, convEnu, &cov, &convOut)))
            return 1;
        vctx->VideoProcessorSetStreamFrameFormat(convVp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        vctx->VideoProcessorSetStreamSourceRect(convVp, 0, TRUE, &srcRect);
        vctx->VideoProcessorSetStreamDestRect(convVp, 0, TRUE, &srcRect);
        vctx->VideoProcessorSetOutputTargetRect(convVp, TRUE, &srcRect);
        vctx->VideoProcessorSetStreamAutoProcessingMode(convVp, 0, FALSE);
        if (vctx1)
        {
            vctx1->VideoProcessorSetStreamColorSpace1(convVp, 0, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
            vctx1->VideoProcessorSetOutputColorSpace1(convVp, DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
        }
    }

    // Optional process-level features replicating what ELYCAST has besides
    // D3D11: an OpenGL/WGL context (mpv interop) rendering every frame, and a
    // D3D9Ex device (WPF) presenting every frame. If either alone flips NVCP
    // to "Inactif", the indicator is blinded by that API's presence in the
    // process — not by anything our VSR chain does.
    HWND glWnd = nullptr; HDC glDc = nullptr; HGLRC glRc = nullptr;
    if (withGl)
    {
        WNDCLASSW gc{};
        gc.lpfnWndProc = DefWindowProcW;
        gc.hInstance = GetModuleHandleW(nullptr);
        gc.lpszClassName = L"VsrProbeGL";
        RegisterClassW(&gc);
        glWnd = CreateWindowExW(0, gc.lpszClassName, L"gl", WS_OVERLAPPEDWINDOW,
            0, 0, 64, 64, nullptr, nullptr, gc.hInstance, nullptr);
        glDc = GetDC(glWnd);
        PIXELFORMATDESCRIPTOR pfd{};
        pfd.nSize = sizeof(pfd);
        pfd.nVersion = 1;
        pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        pfd.iPixelType = PFD_TYPE_RGBA;
        pfd.cColorBits = 32;
        SetPixelFormat(glDc, ChoosePixelFormat(glDc, &pfd), &pfd);
        glRc = wglCreateContext(glDc);
        if (!glRc || !wglMakeCurrent(glDc, glRc)) { printf("WGL KO\n"); return 1; }
        printf("contexte OpenGL: %s\n", reinterpret_cast<const char*>(glGetString(GL_RENDERER)));
    }
    IDirect3D9Ex* d3d9 = nullptr;
    IDirect3DDevice9Ex* dev9 = nullptr;
    HWND d3d9Wnd = nullptr;
    if (withD3d9)
    {
        WNDCLASSW nc{};
        nc.lpfnWndProc = DefWindowProcW;
        nc.hInstance = GetModuleHandleW(nullptr);
        nc.lpszClassName = L"VsrProbeD3D9";
        RegisterClassW(&nc);
        d3d9Wnd = CreateWindowExW(0, nc.lpszClassName, L"d3d9", WS_OVERLAPPEDWINDOW,
            0, 0, 64, 64, nullptr, nullptr, nc.hInstance, nullptr);
        if (FAILED(Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d9))) { printf("D3D9Ex KO\n"); return 1; }
        D3DPRESENT_PARAMETERS pp{};
        pp.Windowed = TRUE;
        pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
        pp.BackBufferFormat = D3DFMT_A8R8G8B8;
        pp.BackBufferWidth = 64; pp.BackBufferHeight = 64;
        pp.hDeviceWindow = d3d9Wnd;
        if (FAILED(d3d9->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, d3d9Wnd,
                D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                &pp, nullptr, &dev9)))
        { printf("device D3D9Ex KO\n"); return 1; }
    }

    // Optional "rival" video session: a SECOND D3D11 device in the same
    // process running a plain VideoProcessor (NV12->RGBA, no extension) every
    // frame — simulating mpv's D3D11VA decode/VP session inside ELYCAST. If
    // this alone flips NVCP to "Inactif", the indicator reflects the other
    // (non-VSR) video session in the process, not ours.
    ID3D11Device* rDevice = nullptr;
    ID3D11DeviceContext* rCtx = nullptr;
    ID3D11VideoDevice* rVdev = nullptr;
    ID3D11VideoContext* rVctx = nullptr;
    ID3D11VideoProcessorEnumerator* rEnu = nullptr;
    ID3D11VideoProcessor* rVp = nullptr;
    ID3D11Texture2D* rInput = nullptr;
    ID3D11Texture2D* rOutput = nullptr;
    ID3D11VideoProcessorInputView* rIn = nullptr;
    ID3D11VideoProcessorOutputView* rOut = nullptr;
    if (rival)
    {
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &rDevice, nullptr, &rCtx)) ||
            FAILED(rDevice->QueryInterface(IID_PPV_ARGS(&rVdev))) ||
            FAILED(rCtx->QueryInterface(IID_PPV_ARGS(&rVctx))))
        {
            printf("rival device KO\n");
            return 1;
        }
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC rc{};
        rc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        rc.InputFrameRate = { 30, 1 };
        rc.InputWidth = srcW; rc.InputHeight = srcH;
        rc.OutputFrameRate = { 30, 1 };
        rc.OutputWidth = srcW; rc.OutputHeight = srcH;
        rc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        if (FAILED(rVdev->CreateVideoProcessorEnumerator(&rc, &rEnu)) ||
            FAILED(rVdev->CreateVideoProcessor(rEnu, 0, &rVp)))
            return 1;
        D3D11_TEXTURE2D_DESC rt = td;
        if (FAILED(rDevice->CreateTexture2D(&rt, &init, &rInput))) return 1;
        D3D11_TEXTURE2D_DESC ro{};
        ro.Width = srcW; ro.Height = srcH;
        ro.MipLevels = 1; ro.ArraySize = 1;
        ro.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        ro.SampleDesc.Count = 1;
        ro.Usage = D3D11_USAGE_DEFAULT;
        ro.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(rDevice->CreateTexture2D(&ro, nullptr, &rOutput))) return 1;
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC riv{};
        riv.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC rov{};
        rov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        if (FAILED(rVdev->CreateVideoProcessorInputView(rInput, rEnu, &riv, &rIn)) ||
            FAILED(rVdev->CreateVideoProcessorOutputView(rOutput, rEnu, &rov, &rOut)))
            return 1;
        RECT rr{ 0, 0, static_cast<LONG>(srcW), static_cast<LONG>(srcH) };
        rVctx->VideoProcessorSetStreamFrameFormat(rVp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        rVctx->VideoProcessorSetStreamSourceRect(rVp, 0, TRUE, &rr);
        rVctx->VideoProcessorSetStreamDestRect(rVp, 0, TRUE, &rr);
        rVctx->VideoProcessorSetOutputTargetRect(rVp, TRUE, &rr);
        rVctx->VideoProcessorSetStreamAutoProcessingMode(rVp, 0, FALSE);
    }

    // Optional visible swapchain on the SAME device.
    HWND hwnd = nullptr;
    IDXGISwapChain1* swap = nullptr;
    if (present)
    {
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"VsrProbeWatch";
        RegisterClassW(&wc);
        hwnd = CreateWindowExW(0, wc.lpszClassName, L"VsrProbe — VSR live", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            60, 60, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);
        IDXGIDevice* dxgiDev = nullptr; IDXGIAdapter* adapter = nullptr; IDXGIFactory2* factory = nullptr;
        device->QueryInterface(IID_PPV_ARGS(&dxgiDev));
        dxgiDev->GetAdapter(&adapter);
        adapter->GetParent(IID_PPV_ARGS(&factory));
        DXGI_SWAP_CHAIN_DESC1 sd{};
        sd.Width = dstW; sd.Height = dstH;
        sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.SampleDesc.Count = 1;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = 2;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        factory->CreateSwapChainForHwnd(device, hwnd, &sd, nullptr, nullptr, &swap);
        factory->Release(); adapter->Release(); dxgiDev->Release();
        if (!swap) { printf("swapchain KO\n"); return 1; }
    }

    printf("watch mode: present=%d dual=%d rival=%d gl=%d d3d9=%d, %d s de Blt VSR continus a 30 fps...\n",
        present ? 1 : 0, dual ? 1 : 0, rival ? 1 : 0, withGl ? 1 : 0, withD3d9 ? 1 : 0, seconds);
    const int frames = seconds * 30;
    UINT query = 0;
    for (int i = 0; i < frames; ++i)
    {
        if (dual)
        {
            D3D11_VIDEO_PROCESSOR_STREAM convStream{};
            convStream.Enable = TRUE;
            convStream.pInputSurface = convIn;
            convStream.InputFrameOrField = static_cast<UINT>(i);
            vctx->VideoProcessorBlt(convVp, convOut, static_cast<UINT>(i), 1, &convStream);
        }
        if (renderChain)
        {
            const float shade = 0.15f + 0.7f * ((i % 60) / 60.0f);
            const float luma[4] = { shade, 0, 0, 0 };
            const float chroma[4] = { 0.5f, 0.5f, 0, 0 };
            ctx->ClearRenderTargetView(lumaRtv, luma);
            ctx->ClearRenderTargetView(chromaRtv, chroma);
        }
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = inView;
        stream.InputFrameOrField = static_cast<UINT>(i);
        if (outputMutex) outputMutex->AcquireSync(0, 100);
        const HRESULT bltHr = vctx->VideoProcessorBlt(vp, outView, static_cast<UINT>(i), 1, &stream);
        if (outputMutex) { outputMutex->ReleaseSync(0); }
        if (queryEveryBlt)
            vctx->VideoProcessorGetStreamExtension(vp, 0, &NvidiaPpeInterfaceGuid, sizeof(query), &query);
        if (rival)
        {
            D3D11_VIDEO_PROCESSOR_STREAM rs{};
            rs.Enable = TRUE;
            rs.pInputSurface = rIn;
            rs.InputFrameOrField = static_cast<UINT>(i);
            rVctx->VideoProcessorBlt(rVp, rOut, static_cast<UINT>(i), 1, &rs);
            rCtx->Flush();
        }
        if (glRc)
        {
            glClearColor(0.1f, 0.2f, 0.3f, 1.0f);
            glClear(GL_COLOR_BUFFER_BIT);
            glFlush();
        }
        if (dev9)
        {
            dev9->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(30, 30, 30), 1.0f, 0);
            dev9->PresentEx(nullptr, nullptr, nullptr, nullptr, 0);
        }
        if (FAILED(bltHr))
        {
            printf("Blt KO au frame %d\n", i);
            break;
        }
        if (swap)
        {
            ID3D11Texture2D* back = nullptr;
            if (SUCCEEDED(swap->GetBuffer(0, IID_PPV_ARGS(&back))))
            {
                ctx->CopyResource(back, output);
                back->Release();
                swap->Present(1, 0);
            }
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        }
        else
        {
            ctx->Flush();
            Sleep(33);
        }
        if (i % 90 == 0)
        {
            vctx->VideoProcessorGetStreamExtension(vp, 0, &NvidiaPpeInterfaceGuid, sizeof(query), &query);
            printf("  t=%ds query=0x%08X removed=0x%08lX\n", i / 30, query,
                static_cast<unsigned long>(device->GetDeviceRemovedReason()));
            fflush(stdout);
        }
    }
    printf("watch mode termine.\n");
    return 0;
}

// ELYCORE's exact presentation topology: device A owns a visible swapchain and
// presents every frame; device B runs the VSR VP into a keyed-mutex shared
// texture that A copies to its backbuffer. If NVCP flips to "Inactif" here,
// the indicator binds to the PRESENTING device and a VSR session on a
// non-presenting device is invisible to it.
static int WatchCross(int seconds, bool interop, bool openA = true,
                      bool copyA = true, bool presentA = true)
{
    static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    const uint32_t srcW = 960, srcH = 540, dstW = 1920, dstH = 1080;

    // Device A: presents. Device B: VSR.
    ID3D11Device* devA = nullptr; ID3D11DeviceContext* ctxA = nullptr;
    ID3D11Device* devB = nullptr; ID3D11DeviceContext* ctxB = nullptr;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &devA, nullptr, &ctxA)) ||
        FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &devB, nullptr, &ctxB)))
        return 1;

    // Optional: register device A with WGL_NV_DX_interop2 and lock/unlock a
    // texture every frame, exactly like ELYCORE's mpv bridge.
    HWND glWnd = nullptr; HDC glDc = nullptr; HGLRC glRc = nullptr;
    HANDLE interopDev = nullptr, interopObj = nullptr;
    ID3D11Texture2D* interopTex = nullptr;
    typedef HANDLE (WINAPI* PFNWGLDXOPENDEVICENVPROC)(void*);
    typedef HANDLE (WINAPI* PFNWGLDXREGISTEROBJECTNVPROC)(HANDLE, void*, GLuint, GLenum, GLenum);
    typedef BOOL (WINAPI* PFNWGLDXLOCKOBJECTSNVPROC)(HANDLE, GLint, HANDLE*);
    typedef BOOL (WINAPI* PFNWGLDXUNLOCKOBJECTSNVPROC)(HANDLE, GLint, HANDLE*);
    PFNWGLDXLOCKOBJECTSNVPROC dxLock = nullptr;
    PFNWGLDXUNLOCKOBJECTSNVPROC dxUnlock = nullptr;
    if (interop)
    {
        WNDCLASSW gc{};
        gc.lpfnWndProc = DefWindowProcW;
        gc.hInstance = GetModuleHandleW(nullptr);
        gc.lpszClassName = L"VsrProbeInterop";
        RegisterClassW(&gc);
        glWnd = CreateWindowExW(0, gc.lpszClassName, L"gl", WS_OVERLAPPEDWINDOW,
            0, 0, 64, 64, nullptr, nullptr, gc.hInstance, nullptr);
        glDc = GetDC(glWnd);
        PIXELFORMATDESCRIPTOR pfd{};
        pfd.nSize = sizeof(pfd);
        pfd.nVersion = 1;
        pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        pfd.iPixelType = PFD_TYPE_RGBA;
        pfd.cColorBits = 32;
        SetPixelFormat(glDc, ChoosePixelFormat(glDc, &pfd), &pfd);
        glRc = wglCreateContext(glDc);
        if (!glRc || !wglMakeCurrent(glDc, glRc)) { printf("WGL KO\n"); return 1; }
        auto dxOpen = reinterpret_cast<PFNWGLDXOPENDEVICENVPROC>(wglGetProcAddress("wglDXOpenDeviceNV"));
        auto dxRegister = reinterpret_cast<PFNWGLDXREGISTEROBJECTNVPROC>(wglGetProcAddress("wglDXRegisterObjectNV"));
        dxLock = reinterpret_cast<PFNWGLDXLOCKOBJECTSNVPROC>(wglGetProcAddress("wglDXLockObjectsNV"));
        dxUnlock = reinterpret_cast<PFNWGLDXUNLOCKOBJECTSNVPROC>(wglGetProcAddress("wglDXUnlockObjectsNV"));
        if (!dxOpen || !dxRegister || !dxLock || !dxUnlock) { printf("WGL_NV_DX_interop indisponible\n"); return 1; }
        interopDev = dxOpen(devA);
        if (!interopDev) { printf("wglDXOpenDeviceNV KO\n"); return 1; }
        D3D11_TEXTURE2D_DESC it{};
        it.Width = srcW; it.Height = srcH;
        it.MipLevels = 1; it.ArraySize = 1;
        it.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        it.SampleDesc.Count = 1;
        it.Usage = D3D11_USAGE_DEFAULT;
        it.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(devA->CreateTexture2D(&it, nullptr, &interopTex))) return 1;
        GLuint glTex = 0;
        glGenTextures(1, &glTex);
        const GLenum WGL_ACCESS_READ_WRITE_NV = 0x0001;
        interopObj = dxRegister(interopDev, interopTex, glTex, GL_TEXTURE_2D, WGL_ACCESS_READ_WRITE_NV);
        if (!interopObj) { printf("wglDXRegisterObjectNV KO\n"); return 1; }
        printf("interop WGL actif sur le device A (%s)\n",
            reinterpret_cast<const char*>(glGetString(GL_RENDERER)));
    }

    // Device B: NV12 input + VSR VP into a keyed-mutex shared output.
    ID3D11VideoDevice* vdev = nullptr; ID3D11VideoContext* vctx = nullptr; ID3D11VideoContext1* vctx1 = nullptr;
    devB->QueryInterface(IID_PPV_ARGS(&vdev));
    ctxB->QueryInterface(IID_PPV_ARGS(&vctx));
    ctxB->QueryInterface(IID_PPV_ARGS(&vctx1));
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};
    cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    cd.InputFrameRate = { 30, 1 };
    cd.InputWidth = srcW; cd.InputHeight = srcH;
    cd.OutputFrameRate = { 30, 1 };
    cd.OutputWidth = dstW; cd.OutputHeight = dstH;
    cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    ID3D11VideoProcessorEnumerator* enu = nullptr;
    ID3D11VideoProcessor* vp = nullptr;
    if (FAILED(vdev->CreateVideoProcessorEnumerator(&cd, &enu)) ||
        FAILED(vdev->CreateVideoProcessor(enu, 0, &vp)))
        return 1;
    D3D11_TEXTURE2D_DESC td{};
    td.Width = srcW; td.Height = srcH;
    td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    std::vector<uint8_t> nv12(static_cast<size_t>(srcW) * srcH * 3 / 2, 128);
    for (uint32_t y = 0; y < srcH; ++y)
        for (uint32_t x = 0; x < srcW; ++x)
            nv12[static_cast<size_t>(y) * srcW + x] =
                static_cast<uint8_t>((((x / 3) + (y / 3)) & 1) ? 200 : 40);
    D3D11_SUBRESOURCE_DATA init{ nv12.data(), srcW, 0 };
    ID3D11Texture2D* input = nullptr;
    if (FAILED(devB->CreateTexture2D(&td, &init, &input))) return 1;
    D3D11_TEXTURE2D_DESC od{};
    od.Width = dstW; od.Height = dstH;
    od.MipLevels = 1; od.ArraySize = 1;
    od.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    od.SampleDesc.Count = 1;
    od.Usage = D3D11_USAGE_DEFAULT;
    od.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    od.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX; // legacy sharing, like ELYCORE
    ID3D11Texture2D* output = nullptr;
    if (FAILED(devB->CreateTexture2D(&od, nullptr, &output))) return 1;
    IDXGIKeyedMutex* mutexB = nullptr;
    output->QueryInterface(IID_PPV_ARGS(&mutexB));
    ID3D11VideoProcessorInputView* inView = nullptr;
    ID3D11VideoProcessorOutputView* outView = nullptr;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
    ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    if (FAILED(vdev->CreateVideoProcessorInputView(input, enu, &ivd, &inView)) ||
        FAILED(vdev->CreateVideoProcessorOutputView(output, enu, &ovd, &outView)))
        return 1;
    RECT srcRect{ 0, 0, static_cast<LONG>(srcW), static_cast<LONG>(srcH) };
    RECT dstRect{ 0, 0, static_cast<LONG>(dstW), static_cast<LONG>(dstH) };
    vctx->VideoProcessorSetStreamFrameFormat(vp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    vctx->VideoProcessorSetStreamSourceRect(vp, 0, TRUE, &srcRect);
    vctx->VideoProcessorSetStreamDestRect(vp, 0, TRUE, &dstRect);
    vctx->VideoProcessorSetOutputTargetRect(vp, TRUE, &dstRect);
    vctx->VideoProcessorSetStreamAutoProcessingMode(vp, 0, FALSE);
    vctx->VideoProcessorSetStreamOutputRate(vp, 0, D3D11_VIDEO_PROCESSOR_OUTPUT_RATE_NORMAL, FALSE, nullptr);
    if (vctx1)
    {
        vctx1->VideoProcessorSetStreamColorSpace1(vp, 0, DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
        vctx1->VideoProcessorSetOutputColorSpace1(vp, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    }
    struct { UINT version, method, enable; } ext{ 1, 2, 1 };
    vctx->VideoProcessorSetStreamExtension(vp, 0, &NvidiaPpeInterfaceGuid, sizeof(ext), &ext);

    // Device A: opens the shared texture and presents a visible swapchain.
    ID3D11Texture2D* outputOnA = nullptr;
    IDXGIKeyedMutex* mutexA = nullptr;
    if (openA)
    {
        IDXGIResource* sharedRes = nullptr;
        HANDLE sharedHandle = nullptr;
        output->QueryInterface(IID_PPV_ARGS(&sharedRes));
        sharedRes->GetSharedHandle(&sharedHandle);
        if (FAILED(devA->OpenSharedResource(sharedHandle, IID_PPV_ARGS(&outputOnA))))
        { printf("OpenSharedResource KO\n"); return 1; }
        outputOnA->QueryInterface(IID_PPV_ARGS(&mutexA));
    }
    // Private landing texture on A for the copy-without-present variant.
    ID3D11Texture2D* landingA = nullptr;
    if (copyA && !presentA)
    {
        D3D11_TEXTURE2D_DESC ld = od;
        ld.MiscFlags = 0;
        if (FAILED(devA->CreateTexture2D(&ld, nullptr, &landingA))) return 1;
    }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"VsrProbeCross";
    RegisterClassW(&wc);
    HWND hwnd = nullptr;
    IDXGISwapChain1* swap = nullptr;
    if (presentA)
    {
        hwnd = CreateWindowExW(0, wc.lpszClassName, L"VsrProbe — cross-device", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            60, 60, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);
        IDXGIDevice* dxgiDev = nullptr; IDXGIAdapter* adapter = nullptr; IDXGIFactory2* factory = nullptr;
        devA->QueryInterface(IID_PPV_ARGS(&dxgiDev));
        dxgiDev->GetAdapter(&adapter);
        adapter->GetParent(IID_PPV_ARGS(&factory));
        DXGI_SWAP_CHAIN_DESC1 sd{};
        sd.Width = dstW; sd.Height = dstH;
        sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.SampleDesc.Count = 1;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = 2;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        factory->CreateSwapChainForHwnd(devA, hwnd, &sd, nullptr, nullptr, &swap);
        factory->Release(); adapter->Release(); dxgiDev->Release();
        if (!swap) { printf("swapchain KO\n"); return 1; }
    }

    printf("cross mode: open=%d copy=%d present=%d interop=%d, %d s...\n",
        openA ? 1 : 0, copyA ? 1 : 0, presentA ? 1 : 0, interop ? 1 : 0, seconds);
    const int frames = seconds * 30;
    UINT query = 0;
    for (int i = 0; i < frames; ++i)
    {
        if (interopObj && dxLock && dxUnlock)
        {
            dxLock(interopDev, 1, &interopObj);
            glFlush();
            dxUnlock(interopDev, 1, &interopObj);
        }
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = inView;
        stream.InputFrameOrField = static_cast<UINT>(i);
        if (mutexB->AcquireSync(0, 100) != S_OK) { printf("AcquireSync B KO frame %d\n", i); break; }
        const HRESULT bltHr = vctx->VideoProcessorBlt(vp, outView, static_cast<UINT>(i), 1, &stream);
        mutexB->ReleaseSync(copyA ? 1 : 0);
        ctxB->Flush();
        if (FAILED(bltHr)) { printf("Blt KO au frame %d\n", i); break; }
        if (copyA && mutexA)
        {
            if (mutexA->AcquireSync(1, 100) == S_OK)
            {
                if (swap)
                {
                    ID3D11Texture2D* back = nullptr;
                    if (SUCCEEDED(swap->GetBuffer(0, IID_PPV_ARGS(&back))))
                    {
                        ctxA->CopyResource(back, outputOnA);
                        back->Release();
                    }
                }
                else if (landingA)
                {
                    ctxA->CopyResource(landingA, outputOnA);
                }
                mutexA->ReleaseSync(0);
                ctxA->Flush();
            }
        }
        if (swap) swap->Present(1, 0);
        else Sleep(33);
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (i % 90 == 0)
        {
            vctx->VideoProcessorGetStreamExtension(vp, 0, &NvidiaPpeInterfaceGuid, sizeof(query), &query);
            printf("  t=%ds query=0x%08X removedA=0x%08lX removedB=0x%08lX\n", i / 30, query,
                static_cast<unsigned long>(devA->GetDeviceRemovedReason()),
                static_cast<unsigned long>(devB->GetDeviceRemovedReason()));
            fflush(stdout);
        }
    }
    printf("cross mode termine.\n");
    return 0;
}

int main(int argc, char** argv)
{
    if (argc >= 2 && strcmp(argv[1], "--watch") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30);
    if (argc >= 2 && strcmp(argv[1], "--watch-present") == 0)
        return WatchMode(true, argc >= 3 ? atoi(argv[2]) : 30);
    if (argc >= 2 && strcmp(argv[1], "--watch-dual") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-shared") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-query") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-elycore") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, true, true, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-rival") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-gl") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, false, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-d3d9") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, false, false, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-gl-d3d9") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, false, false, true, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-cross") == 0)
        return WatchCross(argc >= 3 ? atoi(argv[2]) : 30, false);
    if (argc >= 2 && strcmp(argv[1], "--watch-cross-interop") == 0)
        return WatchCross(argc >= 3 ? atoi(argv[2]) : 30, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-cross-open") == 0)
        return WatchCross(argc >= 3 ? atoi(argv[2]) : 30, false, true, false, false);
    if (argc >= 2 && strcmp(argv[1], "--watch-cross-copy") == 0)
        return WatchCross(argc >= 3 ? atoi(argv[2]) : 30, false, true, true, false);
    if (argc >= 2 && strcmp(argv[1], "--watch-cross-noopen") == 0)
        return WatchCross(argc >= 3 ? atoi(argv[2]) : 30, false, false, false, false);
    if (argc >= 2 && strcmp(argv[1], "--watch-chain") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, true, false, false, false, false, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-cuda") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, false, false, false, false, false, true);
    if (argc >= 2 && strcmp(argv[1], "--watch-renderchain") == 0)
        return WatchMode(false, argc >= 3 ? atoi(argv[2]) : 30, false, false, false, false, false, false, false, false, true);

    printf("VsrProbe — NVIDIA RTX VSR D3D11 VideoProcessor matrix\n\n");

    // ---- A/B pixel proof: identical input, extension OFF vs ON. ----
    printf("== Preuve par les pixels (extension OFF vs ON, entree identique) ==\n");
    const Config pairs[][2] = {
        { { "A 640x360->1280x720 ext=OFF",   640,  360, 1280,  720, 0, DXGI_FORMAT_R8G8B8A8_UNORM, false, false, true, 30, false },
          { "A 640x360->1280x720 ext=ON",    640,  360, 1280,  720, 0, DXGI_FORMAT_R8G8B8A8_UNORM, false, false, true, 30, true  } },
        { { "B 960x540->1920x1080 ext=OFF",  960,  540, 1920, 1080, 0, DXGI_FORMAT_R8G8B8A8_UNORM, false, false, true, 30, false },
          { "B 960x540->1920x1080 ext=ON",   960,  540, 1920, 1080, 0, DXGI_FORMAT_R8G8B8A8_UNORM, false, false, true, 30, true  } },
        { { "C 1920x1080->2130x1198 ext=OFF",1920, 1080, 2130, 1198, 0, DXGI_FORMAT_R8G8B8A8_UNORM, false, false, true, 24, false },
          { "C 1920x1080->2130x1198 ext=ON", 1920, 1080, 2130, 1198, 0, DXGI_FORMAT_R8G8B8A8_UNORM, false, false, true, 24, true  } },
    };
    for (const auto& pair : pairs)
    {
        Result off{}, on{};
        const bool okOff = RunConfig(pair[0], off);
        const bool okOn = RunConfig(pair[1], on);
        PrintResult(pair[0], off, okOff);
        PrintResult(pair[1], on, okOn);
        if (okOff && okOn)
        {
            size_t differing = 0; int maxDelta = 0;
            ComparePixels(off.pixels, on.pixels, differing, maxDelta);
            const double pct = off.pixels.empty() ? 0 : differing * 100.0 / off.pixels.size();
            printf("  -> DIFF: %zu octets differents (%.2f%%), delta max par canal = %d %s\n\n",
                differing, pct, maxDelta,
                differing == 0 ? "==> SORTIES IDENTIQUES : VSR ne traite PAS" :
                maxDelta > 4 ? "==> SORTIES DIFFERENTES : le kernel VSR traite reellement" :
                               "==> difference marginale (bruit d'arrondi ?)");
        }
    }

    printf("== Matrice de stabilite ==\n");
    const Config configs[] = {
        // name                              srcW  srcH  dstW  dstH  inputBind                     outFormat                     letterbox align16 cs1  fps
        { "base 640x360->1280x720 RT",        640,  360, 1280,  720, D3D11_BIND_RENDER_TARGET,     DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 30 },
        { "base bind=0",                      640,  360, 1280,  720, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 30 },
        { "base bind=DECODER",                640,  360, 1280,  720, D3D11_BIND_DECODER,           DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 30 },
        { "aligned16 bind=0",                 640,  360, 1280,  720, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    true,   true, 30 },
        { "output BGRA",                      640,  360, 1280,  720, 0,                            DXGI_FORMAT_B8G8R8A8_UNORM,   false,    false,  true, 30 },
        { "no colorspace1",                   640,  360, 1280,  720, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  false,30 },
        { "letterbox dest",                   640,  360, 1280,  720, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   true,     false,  true, 30 },
        { "960x540->1920x1080",               960,  540, 1920, 1080, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 30 },
        { "1280x720->2560x1440",             1280,  720, 2560, 1440, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 30 },
        { "non-integer 640x360->1216x684",    640,  360, 1216,  684, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 30 },
        { "60fps content",                    640,  360, 1280,  720, 0,                            DXGI_FORMAT_R8G8B8A8_UNORM,   false,    false,  true, 60 },
    };

    for (const auto& c : configs)
    {
        Result res{};
        const bool completed = RunConfig(c, res);
        PrintResult(c, res, completed);
    }

    printf("\nDone.\n");
    return 0;
}
