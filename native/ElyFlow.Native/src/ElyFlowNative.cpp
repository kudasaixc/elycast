#include "ElyFlowNative.h"
#include "NvidiaFrucAdapter.h"
#include "NvidiaRuntimeLoader.h"

#include <algorithm>
#include <cstring>
#include <mutex>
#include <string>

namespace
{
    std::mutex g_mutex;
    bool g_initialized = false;
    ElyFlowConfig g_config{};
    NvidiaFrucAdapter g_frucAdapter;
    std::string g_status = "ElyFlow.Native loaded. NVIDIA runtime not probed yet.";

    void CopyString(char* dest, size_t destSize, const std::string& src)
    {
        if (!dest || destSize == 0) return;
        const auto count = std::min(destSize - 1, src.size());
        std::memcpy(dest, src.data(), count);
        dest[count] = '\0';
    }

    std::string NarrowPath(const std::wstring& value)
    {
        if (value.empty()) return {};
        const int needed = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (needed <= 0) return {};
        std::string result(static_cast<size_t>(needed - 1), '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), needed, nullptr, nullptr);
        return result;
    }

    ElyFlowStatusCode CurrentRuntimeStatus(const NvidiaRuntimeState& rt, std::string* adapterStatus = nullptr)
    {
        if (!rt.nvofapiLoaded) return ELYFLOW_STATUS_NVIDIA_DRIVER_MISSING;
        if (!rt.frucLoaded) return ELYFLOW_STATUS_FRUC_RUNTIME_MISSING;
        std::string status;
        const auto code = g_frucAdapter.Probe(rt, status);
        if (adapterStatus) *adapterStatus = status;
        return code;
    }
}

int32_t ElyFlow_IsRuntimeInstalled()
{
    const auto& rt = NvidiaRuntimeLoader::Instance().EnsureLoaded();
    return rt.frucLoaded ? 1 : 0;
}

int32_t ElyFlow_IsSupportedGPU()
{
    const auto& rt = NvidiaRuntimeLoader::Instance().EnsureLoaded();
    return rt.nvofapiLoaded ? 1 : 0;
}

const char* ElyFlow_GetStatus()
{
    std::lock_guard lock(g_mutex);
    const auto& rt = NvidiaRuntimeLoader::Instance().EnsureLoaded();
    std::string adapterStatus;
    CurrentRuntimeStatus(rt, &adapterStatus);
    g_status = rt.status;
    g_status += adapterStatus;
    if (g_frucAdapter.IsInitialized()) g_status += "ElyFlow FRUC session initialized.\n";
    else g_status += "ElyFlow FRUC session not initialized.\n";
    return g_status.c_str();
}

const char* ElyFlow_GetRuntimeVersion()
{
    const auto& rt = NvidiaRuntimeLoader::Instance().EnsureLoaded();
    return rt.runtimeVersion.c_str();
}

int32_t ElyFlow_GetRuntimeInfo(ElyFlowRuntimeInfo* info)
{
    if (!info || info->structSize < sizeof(ElyFlowRuntimeInfo))
        return ELYFLOW_STATUS_INVALID_ARGUMENT;

    const auto& rt = NvidiaRuntimeLoader::Instance().EnsureLoaded();
    std::lock_guard lock(g_mutex);

    std::string adapterStatus;
    info->statusCode = CurrentRuntimeStatus(rt, &adapterStatus);
    info->nvofapiLoaded = rt.nvofapiLoaded ? 1 : 0;
    info->frucRuntimeLoaded = rt.frucLoaded ? 1 : 0;
    info->initialized = g_frucAdapter.IsInitialized() ? 1 : 0;
    CopyString(info->nvofapiPath, sizeof(info->nvofapiPath), NarrowPath(rt.nvofapiPath));
    CopyString(info->frucRuntimePath, sizeof(info->frucRuntimePath), NarrowPath(rt.frucPath));
    CopyString(info->runtimeVersion, sizeof(info->runtimeVersion), rt.runtimeVersion);
    CopyString(info->status, sizeof(info->status), rt.status + adapterStatus + (g_frucAdapter.IsInitialized() ? "Initialized.\n" : "Not initialized.\n"));
    return info->statusCode;
}

int32_t ElyFlow_Initialize(const ElyFlowConfig* config)
{
    if (!config || config->structSize < sizeof(ElyFlowConfig))
    {
        std::lock_guard lock(g_mutex);
        g_status = "Invalid ElyFlowConfig.";
        return ELYFLOW_STATUS_INVALID_ARGUMENT;
    }

    const auto& rt = NvidiaRuntimeLoader::Instance().EnsureLoaded();
    if (!rt.nvofapiLoaded)
    {
        std::lock_guard lock(g_mutex);
        g_initialized = false;
        g_status = rt.status;
        return ELYFLOW_STATUS_NVIDIA_DRIVER_MISSING;
    }

    if (!rt.frucLoaded)
    {
        std::lock_guard lock(g_mutex);
        g_initialized = false;
        g_status = rt.status;
        return ELYFLOW_STATUS_FRUC_RUNTIME_MISSING;
    }

    std::lock_guard lock(g_mutex);
    std::string adapterStatus = rt.status;
    const auto result = g_frucAdapter.Initialize(rt, *config, adapterStatus);
    g_config = *config;
    g_initialized = g_frucAdapter.IsInitialized();
    g_status = adapterStatus;
    return result;
}

int32_t ElyFlow_ProcessFrame(const ElyFlowFrame* previousFrame, const ElyFlowFrame* currentFrame, ElyFlowFrame* outputFrame)
{
    if (!previousFrame || !currentFrame || !outputFrame)
        return ELYFLOW_STATUS_INVALID_ARGUMENT;

    std::lock_guard lock(g_mutex);
    g_status.clear();
    const auto result = g_frucAdapter.ProcessFrame(*previousFrame, *currentFrame, *outputFrame, g_status);
    g_initialized = g_frucAdapter.IsInitialized();
    return result;
}

void ElyFlow_Shutdown()
{
    std::lock_guard lock(g_mutex);
    g_frucAdapter.Shutdown();
    g_initialized = false;
    g_config = {};
    g_status = "ElyFlow session shut down.";
}
