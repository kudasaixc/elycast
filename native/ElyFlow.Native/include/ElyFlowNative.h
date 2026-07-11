#pragma once

#include <cstdint>

#ifdef _WIN32
    #ifdef ELYFLOW_NATIVE_EXPORTS
        #define ELYFLOW_API extern "C" __declspec(dllexport)
    #else
        #define ELYFLOW_API extern "C" __declspec(dllimport)
    #endif
#else
    #define ELYFLOW_API extern "C"
#endif

enum ElyFlowEngine : int32_t
{
    ELYFLOW_ENGINE_NVIDIA_FRUC = 1
};

enum ElyFlowPixelFormat : int32_t
{
    ELYFLOW_FORMAT_UNKNOWN = 0,
    ELYFLOW_FORMAT_NV12 = 1,
    ELYFLOW_FORMAT_P010 = 2,
    ELYFLOW_FORMAT_RGBA8 = 3,
    ELYFLOW_FORMAT_BGRA8 = 4
};

enum ElyFlowStatusCode : int32_t
{
    ELYFLOW_STATUS_OK = 0,
    ELYFLOW_STATUS_NATIVE_DLL_READY = 1,
    ELYFLOW_STATUS_NVIDIA_DRIVER_MISSING = -10,
    ELYFLOW_STATUS_FRUC_RUNTIME_MISSING = -11,
    ELYFLOW_STATUS_SDK_ADAPTER_NOT_COMPILED = -12,
    ELYFLOW_STATUS_NOT_INITIALIZED = -13,
    ELYFLOW_STATUS_INVALID_ARGUMENT = -14,
    ELYFLOW_STATUS_TEXTURE_PIPELINE_NOT_CONNECTED = -15,
    ELYFLOW_STATUS_FRUC_SYMBOL_MISSING = -16,
    ELYFLOW_STATUS_FRUC_CREATE_FAILED = -17,
    ELYFLOW_STATUS_FRAME_RESOURCE_MISSING = -18,
    ELYFLOW_STATUS_FRUC_PROCESS_FAILED = -19
};

struct ElyFlowConfig
{
    uint32_t structSize;
    ElyFlowEngine engine;
    uint32_t width;
    uint32_t height;
    double sourceFps;
    double targetFps;
    double liveBufferSeconds;
    ElyFlowPixelFormat format;

    // Future D3D11 interop handles. They are intentionally opaque so the C ABI
    // stays stable and the WPF layer does not need native headers.
    void* d3d11Device;
    void* d3d11DeviceContext;
    void* d3d11Fence;
    uint32_t reserved[16];
};

struct ElyFlowFrame
{
    uint32_t structSize;
    uint64_t frameIndex;
    int64_t presentationTime100ns;
    uint32_t width;
    uint32_t height;
    ElyFlowPixelFormat format;

    // ID3D11Texture2D* and optional keyed mutex/shared-handle metadata later.
    void* texture;
    void* sharedHandle;
    uint64_t waitFenceValue;
    uint64_t signalFenceValue;
    uint64_t keyedMutexAcquire0;
    uint64_t keyedMutexAcquire1;
    uint64_t keyedMutexRelease0;
    uint64_t keyedMutexRelease1;
    uint32_t syncMode;
    uint32_t skipWarp;
    uint32_t reserved[16];
};

struct ElyFlowRuntimeInfo
{
    uint32_t structSize;
    int32_t statusCode;
    int32_t nvofapiLoaded;
    int32_t frucRuntimeLoaded;
    int32_t initialized;
    char nvofapiPath[520];
    char frucRuntimePath[520];
    char runtimeVersion[128];
    char status[1024];
};

ELYFLOW_API int32_t ElyFlow_IsRuntimeInstalled();
ELYFLOW_API int32_t ElyFlow_IsSupportedGPU();
ELYFLOW_API const char* ElyFlow_GetStatus();
ELYFLOW_API const char* ElyFlow_GetRuntimeVersion();
ELYFLOW_API int32_t ElyFlow_GetRuntimeInfo(ElyFlowRuntimeInfo* info);

ELYFLOW_API int32_t ElyFlow_Initialize(const ElyFlowConfig* config);
ELYFLOW_API int32_t ElyFlow_ProcessFrame(const ElyFlowFrame* previousFrame, const ElyFlowFrame* currentFrame, ElyFlowFrame* outputFrame);
ELYFLOW_API void ElyFlow_Shutdown();

// ============ ELYFLOW Renderer (experimental) ============
// Owns the full GPU presentation pipeline for the player:
//   mpv (vo=libmpv, render API OpenGL)
//   -> FBO backed by a D3D11 texture (WGL_NV_DX_interop2, zero copy)
//   -> ring of shared D3D11 textures (previous / current)
//   -> ElyFlow_ProcessFrame (NvOFFRUC) -> interpolated texture
//   -> DXGI swapchain Present on the player child HWND.
// Everything runs on a dedicated render thread inside this DLL.

enum ElyFlowRendererStatusCode : int32_t
{
    ELYFLOW_RENDERER_OK = 0,
    ELYFLOW_RENDERER_ALREADY_ACTIVE = -30,
    ELYFLOW_RENDERER_LIBMPV_NOT_LOADED = -31,
    ELYFLOW_RENDERER_MPV_EXPORTS_MISSING = -32,
    ELYFLOW_RENDERER_D3D11_FAILED = -33,
    ELYFLOW_RENDERER_SWAPCHAIN_FAILED = -34,
    ELYFLOW_RENDERER_WGL_FAILED = -35,
    ELYFLOW_RENDERER_INTEROP_MISSING = -36,
    ELYFLOW_RENDERER_GL_FUNCTIONS_MISSING = -37,
    ELYFLOW_RENDERER_MPV_CONTEXT_FAILED = -38,
    ELYFLOW_RENDERER_FBO_INCOMPLETE = -39,
    ELYFLOW_RENDERER_INVALID_ARGUMENT = -40
};

struct ElyFlowRendererState
{
    uint32_t structSize;
    int32_t active;             // render thread alive and mpv context created
    int32_t glInterop;          // WGL_NV_DX_interop2 opened on the D3D11 device
    int32_t texturesShared;     // mpv currently renders into the shared D3D11 texture
    int32_t frucInitialized;    // NvOFFRUCCreate succeeded for the current size
    int32_t lastFrucStatus;     // ElyFlowStatusCode of the last ProcessFrame
    uint64_t framesRendered;    // frames produced by mpv
    uint64_t framesInterpolated;// frames produced by NvOFFRUCProcess
    uint64_t framesPresented;   // swapchain Present calls
    uint32_t width;
    uint32_t height;
    uint64_t latePresents;      // FRUC work exceeded the half-frame budget
    double sourceFps;           // authoritative cadence read from mpv
    double averageWorkMs;       // mpv + FRUC + first Present, rolling average
    double maxWorkMs;           // worst work duration for the current session
    int32_t vsrActive;          // native D3D11 RTX Video Super Resolution active
    int32_t lastVsrStatus;      // HRESULT from setup/Blt, 0 on success
    uint32_t vsrInputWidth;
    uint32_t vsrInputHeight;
    uint32_t vsrContentWidth;
    uint32_t vsrContentHeight;
    int32_t vsrAvailable;       // NVIDIA extension reports this GPU as capable
    int32_t vsrRequested;       // managed control plane requested native VSR
    int32_t vsrEffective;       // driver reports VSR in use for this processor
    int32_t vsrLevel;           // NVIDIA driver quality level (0..4), when valid
    uint32_t adapterVendorId;   // DXGI vendor (0x10DE = NVIDIA)
    uint32_t vsrInputFormat;    // DXGI_FORMAT numeric value
    uint32_t vsrOutputFormat;   // DXGI_FORMAT numeric value
    uint32_t vsrColorSpace;     // DXGI_COLOR_SPACE_TYPE numeric value
    uint64_t targetRebuilds;    // render-target topology rebuilds (VSR/size/layout)
    uint64_t swapchainResizes;  // actual ResizeBuffers calls
    uint64_t vsrFramesProcessed;// successful VideoProcessorBlt frames while VSR effective
    uint64_t vsrFramesBypassed; // source frames for which RTX VSR was not effective
    uint64_t videoProcessorFrames;// successful D3D11 VideoProcessorBlt frames (generic or RTX)
    uint64_t presentErrors;     // failed swapchain Present calls
    int32_t videoProcessorCreated;
    int32_t vsrExtensionEnabled;
    int32_t vsrConverterActive; // GPU RGB->NV12 conversion pass feeding the VP
    int32_t lastConvStatus;     // HRESULT of the RGB->NV12 conversion Blt
    uint32_t vsrQueryRaw;       // raw NVIDIA GetStreamExtension payload (last query)
    double vsrBltAvgMs;         // GPU time of the VSR Blt (timestamp queries, EWMA)
    char adapterName[128];
    char driverVersion[64];
    char message[512];
};

constexpr uint32_t ELYFLOW_RENDERER_ABI_VERSION = 5;
ELYFLOW_API uint32_t ElyFlowRenderer_GetAbiVersion();
// Checks D3D11 + WGL_NV_DX_interop2 availability without mpv (cheap, safe).
ELYFLOW_API int32_t ElyFlowRenderer_Preflight(char* message, int32_t messageSize);
// mpvHandle = mpv_handle* (client API), hwnd = target child window.
ELYFLOW_API int32_t ElyFlowRenderer_Create(void* mpvHandle, void* hwnd, int32_t enableFruc);
// Supplies the decoded media cadence from the managed mpv client. Keeping this
// outside the render callback avoids a blocking mpv client call on that thread.
ELYFLOW_API void ElyFlowRenderer_SetSourceFps(double sourceFps);
ELYFLOW_API void ElyFlowRenderer_ConfigureVsr(int32_t enable, uint32_t sourceWidth, uint32_t sourceHeight);
// Toggles NVIDIA FRUC interpolation at runtime. The render thread creates or
// destroys the FRUC session at the next frame without tearing down the
// pipeline, so playback is never interrupted by the switch.
ELYFLOW_API void ElyFlowRenderer_ConfigureFruc(int32_t enable);
ELYFLOW_API int32_t ElyFlowRenderer_GetState(ElyFlowRendererState* state);
ELYFLOW_API void ElyFlowRenderer_Destroy();

// Windows System Media Transport Controls (SMTC) bridge. The native ABI keeps
// WinRT projection dependencies out of the managed WPF application.
using ElyMediaTransportCommand = void(__stdcall*)(int32_t command, void* context);
ELYFLOW_API void* ElyMediaTransport_Create(void* hwnd, ElyMediaTransportCommand callback, void* context);
ELYFLOW_API void ElyMediaTransport_Destroy(void* instance);
ELYFLOW_API int32_t ElyMediaTransport_SetMedia(void* instance, const wchar_t* title, const wchar_t* artist,
                                               const wchar_t* album, const wchar_t* artworkPath);
ELYFLOW_API void ElyMediaTransport_SetState(void* instance, int32_t hasMedia, int32_t playing);
