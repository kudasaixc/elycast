#pragma once

#include "ElyFlowNative.h"
#include "NvidiaRuntimeLoader.h"

#include <string>
#include <vector>

class NvidiaFrucAdapter
{
public:
    NvidiaFrucAdapter();
    ~NvidiaFrucAdapter();

    bool IsSdkCompiled() const;
    bool AreSymbolsResolved() const;
    bool IsInitialized() const;

    ElyFlowStatusCode Probe(const NvidiaRuntimeState& runtime, std::string& status);
    ElyFlowStatusCode Initialize(const NvidiaRuntimeState& runtime, const ElyFlowConfig& config, std::string& status);
    ElyFlowStatusCode ProcessFrame(const ElyFlowFrame& previousFrame, const ElyFlowFrame& currentFrame, ElyFlowFrame& outputFrame, std::string& status);
    void Shutdown();

private:
    ElyFlowStatusCode ResolveSymbols(const NvidiaRuntimeState& runtime, std::string& status);
    ElyFlowStatusCode EnsureResourcesRegistered(const ElyFlowFrame& previousFrame, const ElyFlowFrame& currentFrame, const ElyFlowFrame& outputFrame, std::string& status);

    void* handle_ = nullptr;
    bool symbolsResolved_ = false;
    bool initialized_ = false;
    ElyFlowConfig config_{};
    std::vector<void*> registeredResources_;
};
