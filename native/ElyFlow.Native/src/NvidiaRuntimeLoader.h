#pragma once

#include <string>
#include <vector>
#include <filesystem>
#include <windows.h>

struct NvidiaRuntimeState
{
    bool attempted = false;
    bool nvofapiLoaded = false;
    bool frucLoaded = false;
    HMODULE nvofapiModule = nullptr;
    HMODULE frucModule = nullptr;
    std::wstring nvofapiPath;
    std::wstring frucPath;
    std::string runtimeVersion = "unknown";
    std::string status;
    std::vector<HMODULE> modules;
};

class NvidiaRuntimeLoader
{
public:
    static NvidiaRuntimeLoader& Instance();

    const NvidiaRuntimeState& EnsureLoaded();
    void Shutdown();

private:
    NvidiaRuntimeLoader() = default;
    ~NvidiaRuntimeLoader();

    NvidiaRuntimeLoader(const NvidiaRuntimeLoader&) = delete;
    NvidiaRuntimeLoader& operator=(const NvidiaRuntimeLoader&) = delete;

    HMODULE TryLoadByName(const wchar_t* name, std::wstring& loadedPath);
    HMODULE TryLoadFromPath(const std::wstring& path);
    std::vector<std::wstring> BuildSearchDirectories() const;
    std::vector<std::wstring> FindFrucCandidates(const std::vector<std::wstring>& dirs) const;
    static void AddSdkSearchDirectories(std::vector<std::wstring>& dirs, const std::filesystem::path& root);
    static std::wstring ModulePath(HMODULE module);
    static std::string Narrow(const std::wstring& value);
    static std::wstring EnvironmentVariable(const wchar_t* name);

    NvidiaRuntimeState state_;
};
