#include "NvidiaFrucAdapter.h"

#include <algorithm>
#include <array>
#include <sstream>

#ifndef ELYFLOW_HAS_NVOFFRUC_SDK
#define ELYFLOW_HAS_NVOFFRUC_SDK 0
#endif

#if ELYFLOW_HAS_NVOFFRUC_SDK
#include <NvOFFRUC.h>
#endif

namespace
{
#if ELYFLOW_HAS_NVOFFRUC_SDK
    PtrToFuncNvOFFRUCCreate g_create = nullptr;
    PtrToFuncNvOFFRUCRegisterResource g_registerResource = nullptr;
    PtrToFuncNvOFFRUCUnregisterResource g_unregisterResource = nullptr;
    PtrToFuncNvOFFRUCProcess g_process = nullptr;
    PtrToFuncNvOFFRUCDestroy g_destroy = nullptr;

    std::string FrucStatusToString(NvOFFRUC_STATUS status)
    {
        switch (status)
        {
        case NvOFFRUC_SUCCESS: return "NvOFFRUC_SUCCESS";
        case NvOFFRUC_ERR_NvOFFRUC_NOT_SUPPORTED: return "NvOFFRUC_ERR_NvOFFRUC_NOT_SUPPORTED";
        case NvOFFRUC_ERR_INVALID_PTR: return "NvOFFRUC_ERR_INVALID_PTR";
        case NvOFFRUC_ERR_INVALID_PARAM: return "NvOFFRUC_ERR_INVALID_PARAM";
        case NvOFFRUC_ERR_INVALID_HANDLE: return "NvOFFRUC_ERR_INVALID_HANDLE";
        case NvOFFRUC_ERR_OUT_OF_SYSTEM_MEMORY: return "NvOFFRUC_ERR_OUT_OF_SYSTEM_MEMORY";
        case NvOFFRUC_ERR_OUT_OF_VIDEO_MEMORY: return "NvOFFRUC_ERR_OUT_OF_VIDEO_MEMORY";
        case NvOFFRUC_ERR_OPENCV_NOT_AVAILABLE: return "NvOFFRUC_ERR_OPENCV_NOT_AVAILABLE";
        case NvOFFRUC_ERR_UNIMPLEMENTED: return "NvOFFRUC_ERR_UNIMPLEMENTED";
        case NvOFFRUC_ERR_OF_FAILURE: return "NvOFFRUC_ERR_OF_FAILURE";
        case NvOFFRUC_ERR_DUPLICATE_RESOURCE: return "NvOFFRUC_ERR_DUPLICATE_RESOURCE";
        case NvOFFRUC_ERR_UNREGISTERED_RESOURCE: return "NvOFFRUC_ERR_UNREGISTERED_RESOURCE";
        case NvOFFRUC_ERR_INCORRECT_API_SEQUENCE: return "NvOFFRUC_ERR_INCORRECT_API_SEQUENCE";
        case NvOFFRUC_ERR_WRITE_TODISK_FAILED: return "NvOFFRUC_ERR_WRITE_TODISK_FAILED";
        case NvOFFRUC_ERR_PIPELINE_EXECUTION_FAILURE: return "NvOFFRUC_ERR_PIPELINE_EXECUTION_FAILURE";
        case NvOFFRUC_ERR_SYNC_WRITE_FAILED: return "NvOFFRUC_ERR_SYNC_WRITE_FAILED";
        case NvOFFRUC_ERR_GENERIC: return "NvOFFRUC_ERR_GENERIC";
        default: return "NvOFFRUC_STATUS(" + std::to_string(static_cast<int>(status)) + ")";
        }
    }

    NvOFFRUCSurfaceFormat ToFrucSurfaceFormat(ElyFlowPixelFormat format)
    {
        switch (format)
        {
        case ELYFLOW_FORMAT_NV12: return NV12Surface;
        case ELYFLOW_FORMAT_RGBA8:
        case ELYFLOW_FORMAT_BGRA8: return ARGBSurface;
        default: return UndefinedSurfaceType;
        }
    }

    double ToSeconds100ns(int64_t presentationTime100ns)
    {
        return static_cast<double>(presentationTime100ns) / 10000000.0;
    }

    bool ContainsResource(const std::vector<void*>& resources, void* resource)
    {
        return std::find(resources.begin(), resources.end(), resource) != resources.end();
    }

    template <typename T>
    T Resolve(HMODULE module, const char* name)
    {
        return reinterpret_cast<T>(GetProcAddress(module, name));
    }
#endif
}

NvidiaFrucAdapter::NvidiaFrucAdapter() = default;

NvidiaFrucAdapter::~NvidiaFrucAdapter()
{
    Shutdown();
}

bool NvidiaFrucAdapter::IsSdkCompiled() const
{
    return ELYFLOW_HAS_NVOFFRUC_SDK != 0;
}

bool NvidiaFrucAdapter::AreSymbolsResolved() const
{
    return symbolsResolved_;
}

bool NvidiaFrucAdapter::IsInitialized() const
{
    return initialized_;
}

ElyFlowStatusCode NvidiaFrucAdapter::Probe(const NvidiaRuntimeState& runtime, std::string& status)
{
    const auto resolved = ResolveSymbols(runtime, status);
    if (resolved != ELYFLOW_STATUS_OK) return resolved;
    status += "Official NVIDIA NvOFFRUC SDK adapter compiled and exports resolved. Waiting for the player D3D11 texture pipeline.\n";
    return initialized_ ? ELYFLOW_STATUS_OK : ELYFLOW_STATUS_TEXTURE_PIPELINE_NOT_CONNECTED;
}

ElyFlowStatusCode NvidiaFrucAdapter::Initialize(const NvidiaRuntimeState& runtime, const ElyFlowConfig& config, std::string& status)
{
    const auto resolved = ResolveSymbols(runtime, status);
    if (resolved != ELYFLOW_STATUS_OK) return resolved;

#if ELYFLOW_HAS_NVOFFRUC_SDK
    if (config.width == 0 || config.height == 0 || config.d3d11Device == nullptr)
    {
        status += "NvOFFRUC is available, but no ID3D11Device was provided by the player yet.\n";
        return ELYFLOW_STATUS_TEXTURE_PIPELINE_NOT_CONNECTED;
    }

    const auto surfaceFormat = ToFrucSurfaceFormat(config.format);
    if (surfaceFormat == UndefinedSurfaceType)
    {
        status += "Unsupported FRUC surface format for the current pipeline. Expected NV12 or ARGB-compatible D3D11 textures.\n";
        return ELYFLOW_STATUS_INVALID_ARGUMENT;
    }

    Shutdown();

    NvOFFRUC_CREATE_PARAM createParams{};
    createParams.uiWidth = config.width;
    createParams.uiHeight = config.height;
    createParams.pDevice = config.d3d11Device;
    createParams.eResourceType = DirectX11Resource;
    createParams.eSurfaceFormat = surfaceFormat;
    createParams.eCUDAResourceType = CudaResourceTypeUndefined;

    NvOFFRUCHandle handle = nullptr;
    const auto createStatus = g_create(&createParams, &handle);
    if (createStatus != NvOFFRUC_SUCCESS)
    {
        status += "NvOFFRUCCreate failed: " + FrucStatusToString(createStatus) + ".\n";
        handle_ = nullptr;
        initialized_ = false;
        return ELYFLOW_STATUS_FRUC_CREATE_FAILED;
    }

    handle_ = handle;
    initialized_ = true;
    config_ = config;
    status += "NvOFFRUCCreate succeeded. FRUC session is ready for registered D3D11 textures.\n";
    return ELYFLOW_STATUS_OK;
#else
    (void)config;
    status += "NvOFFRUC.h was not available at build time.\n";
    return ELYFLOW_STATUS_SDK_ADAPTER_NOT_COMPILED;
#endif
}

ElyFlowStatusCode NvidiaFrucAdapter::ProcessFrame(const ElyFlowFrame& previousFrame, const ElyFlowFrame& currentFrame, ElyFlowFrame& outputFrame, std::string& status)
{
#if ELYFLOW_HAS_NVOFFRUC_SDK
    if (!initialized_ || !handle_)
    {
        status += "NvOFFRUCProcess rejected: no active FRUC session.\n";
        return ELYFLOW_STATUS_NOT_INITIALIZED;
    }

    if (!currentFrame.texture || !outputFrame.texture)
    {
        status += "NvOFFRUCProcess rejected: missing D3D11 input or output texture.\n";
        return ELYFLOW_STATUS_FRAME_RESOURCE_MISSING;
    }

    const auto resourceStatus = EnsureResourcesRegistered(previousFrame, currentFrame, outputFrame, status);
    if (resourceStatus != ELYFLOW_STATUS_OK) return resourceStatus;

    bool repeated = false;
    NvOFFRUC_PROCESS_IN_PARAMS inParams{};
    inParams.stFrameDataInput.pFrame = currentFrame.texture;
    inParams.stFrameDataInput.nTimeStamp = ToSeconds100ns(currentFrame.presentationTime100ns);
    inParams.bSkipWarp = currentFrame.skipWarp ? 1u : 0u;

    if (currentFrame.syncMode == 2)
    {
        inParams.uSyncWait.MutexAcquireKey.uiKeyForRenderTextureAcquire = currentFrame.keyedMutexAcquire0;
        inParams.uSyncWait.MutexAcquireKey.uiKeyForInterpTextureAcquire = currentFrame.keyedMutexAcquire1;
    }
    else
    {
        inParams.uSyncWait.FenceWaitValue.uiFenceValueToWaitOn = currentFrame.waitFenceValue;
    }

    NvOFFRUC_PROCESS_OUT_PARAMS outParams{};
    outParams.stFrameDataOutput.pFrame = outputFrame.texture;
    outParams.stFrameDataOutput.nTimeStamp = ToSeconds100ns(outputFrame.presentationTime100ns);
    outParams.stFrameDataOutput.bHasFrameRepetitionOccurred = &repeated;

    if (outputFrame.syncMode == 2)
    {
        outParams.uSyncSignal.MutexReleaseKey.uiKeyForRenderTextureRelease = outputFrame.keyedMutexRelease0;
        outParams.uSyncSignal.MutexReleaseKey.uiKeyForInterpolateRelease = outputFrame.keyedMutexRelease1;
    }
    else
    {
        outParams.uSyncSignal.FenceSignalValue.uiFenceValueToSignalOn = outputFrame.signalFenceValue;
    }

    const auto processStatus = g_process(static_cast<NvOFFRUCHandle>(handle_), &inParams, &outParams);
    if (processStatus != NvOFFRUC_SUCCESS)
    {
        status += "NvOFFRUCProcess failed: " + FrucStatusToString(processStatus) + ".\n";
        return ELYFLOW_STATUS_FRUC_PROCESS_FAILED;
    }

    status += repeated ? "NvOFFRUCProcess OK. FRUC repeated the frame for quality protection.\n" : "NvOFFRUCProcess OK.\n";
    return ELYFLOW_STATUS_OK;
#else
    (void)previousFrame;
    (void)currentFrame;
    (void)outputFrame;
    status += "NvOFFRUC.h was not available at build time.\n";
    return ELYFLOW_STATUS_SDK_ADAPTER_NOT_COMPILED;
#endif
}

void NvidiaFrucAdapter::Shutdown()
{
#if ELYFLOW_HAS_NVOFFRUC_SDK
    if (handle_ && g_unregisterResource && !registeredResources_.empty())
    {
        NvOFFRUC_UNREGISTER_RESOURCE_PARAM unregisterParam{};
        unregisterParam.uiCount = static_cast<uint32_t>(std::min<size_t>(registeredResources_.size(), NvOFFRUC_MAX_RESOURCE));
        for (uint32_t i = 0; i < unregisterParam.uiCount; ++i)
            unregisterParam.pArrResource[i] = registeredResources_[i];
        g_unregisterResource(static_cast<NvOFFRUCHandle>(handle_), &unregisterParam);
    }

    if (handle_ && g_destroy)
        g_destroy(static_cast<NvOFFRUCHandle>(handle_));
#endif

    handle_ = nullptr;
    initialized_ = false;
    config_ = {};
    registeredResources_.clear();
}

ElyFlowStatusCode NvidiaFrucAdapter::ResolveSymbols(const NvidiaRuntimeState& runtime, std::string& status)
{
#if ELYFLOW_HAS_NVOFFRUC_SDK
    if (!runtime.frucModule)
    {
        status += "NvOFFRUC runtime module is not loaded.\n";
        return ELYFLOW_STATUS_FRUC_RUNTIME_MISSING;
    }

    if (symbolsResolved_) return ELYFLOW_STATUS_OK;

    g_create = Resolve<PtrToFuncNvOFFRUCCreate>(runtime.frucModule, CreateProcName);
    g_registerResource = Resolve<PtrToFuncNvOFFRUCRegisterResource>(runtime.frucModule, RegisterResourceProcName);
    g_unregisterResource = Resolve<PtrToFuncNvOFFRUCUnregisterResource>(runtime.frucModule, UnregisterResourceProcName);
    g_process = Resolve<PtrToFuncNvOFFRUCProcess>(runtime.frucModule, ProcessProcName);
    g_destroy = Resolve<PtrToFuncNvOFFRUCDestroy>(runtime.frucModule, DestroyProcName);

    symbolsResolved_ = g_create && g_registerResource && g_unregisterResource && g_process && g_destroy;
    if (!symbolsResolved_)
    {
        status += "NvOFFRUC.dll loaded, but one or more official exports are missing: ";
        status += g_create ? "" : "NvOFFRUCCreate ";
        status += g_registerResource ? "" : "NvOFFRUCRegisterResource ";
        status += g_unregisterResource ? "" : "NvOFFRUCUnregisterResource ";
        status += g_process ? "" : "NvOFFRUCProcess ";
        status += g_destroy ? "" : "NvOFFRUCDestroy ";
        status += "\n";
        return ELYFLOW_STATUS_FRUC_SYMBOL_MISSING;
    }

    status += "NvOFFRUC official exports resolved via GetProcAddress.\n";
    return ELYFLOW_STATUS_OK;
#else
    (void)runtime;
    status += "NvOFFRUC.h was not found when ElyFlow.Native was built.\n";
    return ELYFLOW_STATUS_SDK_ADAPTER_NOT_COMPILED;
#endif
}

ElyFlowStatusCode NvidiaFrucAdapter::EnsureResourcesRegistered(const ElyFlowFrame& previousFrame, const ElyFlowFrame& currentFrame, const ElyFlowFrame& outputFrame, std::string& status)
{
#if ELYFLOW_HAS_NVOFFRUC_SDK
    std::array<void*, 3> required = { previousFrame.texture, currentFrame.texture, outputFrame.texture };
    if (!required[0] || !required[1] || !required[2])
    {
        status += "NvOFFRUC requires at least previous, current, and output D3D11 resources before processing.\n";
        return ELYFLOW_STATUS_FRAME_RESOURCE_MISSING;
    }

    std::vector<void*> toRegister;
    for (auto* resource : required)
    {
        if (resource && !ContainsResource(registeredResources_, resource) && !ContainsResource(toRegister, resource))
            toRegister.push_back(resource);
    }

    if (toRegister.empty()) return ELYFLOW_STATUS_OK;
    if (registeredResources_.size() + toRegister.size() > NvOFFRUC_MAX_RESOURCE)
    {
        status += "NvOFFRUC resource pool is full; the adapter currently supports NVIDIA's documented maximum of 10 resources per session.\n";
        return ELYFLOW_STATUS_INVALID_ARGUMENT;
    }

    NvOFFRUC_REGISTER_RESOURCE_PARAM registerParam{};
    registerParam.uiCount = static_cast<uint32_t>(toRegister.size());
    registerParam.pD3D11FenceObj = config_.d3d11Fence;
    for (uint32_t i = 0; i < registerParam.uiCount; ++i)
        registerParam.pArrResource[i] = toRegister[i];

    const auto registerStatus = g_registerResource(static_cast<NvOFFRUCHandle>(handle_), &registerParam);
    if (registerStatus != NvOFFRUC_SUCCESS)
    {
        status += "NvOFFRUCRegisterResource failed: " + FrucStatusToString(registerStatus) + ".\n";
        return ELYFLOW_STATUS_FRUC_PROCESS_FAILED;
    }

    registeredResources_.insert(registeredResources_.end(), toRegister.begin(), toRegister.end());
    status += "Registered D3D11 resources with NvOFFRUC.\n";
    return ELYFLOW_STATUS_OK;
#else
    (void)previousFrame;
    (void)currentFrame;
    (void)outputFrame;
    status += "NvOFFRUC.h was not available at build time.\n";
    return ELYFLOW_STATUS_SDK_ADAPTER_NOT_COMPILED;
#endif
}
