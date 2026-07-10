#include "NvidiaRuntimeLoader.h"

#include <algorithm>
#include <cwctype>
#include <filesystem>
#include <sstream>

namespace fs = std::filesystem;

extern "C" IMAGE_DOS_HEADER __ImageBase;

NvidiaRuntimeLoader& NvidiaRuntimeLoader::Instance()
{
    static NvidiaRuntimeLoader loader;
    return loader;
}

NvidiaRuntimeLoader::~NvidiaRuntimeLoader()
{
    Shutdown();
}

const NvidiaRuntimeState& NvidiaRuntimeLoader::EnsureLoaded()
{
    if (state_.attempted) return state_;
    state_.attempted = true;

    std::wstringstream status;

    std::wstring nvofPath;
    if (HMODULE nvof = TryLoadByName(L"nvofapi64.dll", nvofPath))
    {
        state_.nvofapiLoaded = true;
        state_.nvofapiModule = nvof;
        state_.nvofapiPath = nvofPath;
        state_.modules.push_back(nvof);
        status << L"nvofapi64.dll loaded: " << nvofPath << L"\n";
    }
    else
    {
        status << L"nvofapi64.dll not found or failed to load.\n";
    }

    const auto dirs = BuildSearchDirectories();
    const auto frucCandidates = FindFrucCandidates(dirs);
    for (const auto& candidate : frucCandidates)
    {
        if (HMODULE fruc = TryLoadFromPath(candidate))
        {
            state_.frucLoaded = true;
            state_.frucModule = fruc;
            state_.frucPath = candidate;
            state_.modules.push_back(fruc);
            status << L"FRUC runtime loaded: " << candidate << L"\n";
            break;
        }
        else
        {
            status << L"FRUC candidate failed: " << candidate << L" (GetLastError=" << GetLastError() << L")\n";
        }
    }

    if (!state_.frucLoaded)
        status << L"No FRUC runtime DLL found. Expected an official NVIDIA Optical Flow / FRUC runtime DLL in the app, SDK, or system search paths.\n";

    state_.status = Narrow(status.str());
    return state_;
}

void NvidiaRuntimeLoader::Shutdown()
{
    for (auto it = state_.modules.rbegin(); it != state_.modules.rend(); ++it)
    {
        if (*it) FreeLibrary(*it);
    }
    state_.modules.clear();
    state_.nvofapiLoaded = false;
    state_.frucLoaded = false;
    state_.nvofapiModule = nullptr;
    state_.frucModule = nullptr;
    state_.attempted = false;
}

HMODULE NvidiaRuntimeLoader::TryLoadByName(const wchar_t* name, std::wstring& loadedPath)
{
    HMODULE module = LoadLibraryW(name);
    if (module) loadedPath = ModulePath(module);
    return module;
}

HMODULE NvidiaRuntimeLoader::TryLoadFromPath(const std::wstring& path)
{
    auto previous = EnvironmentVariable(L"PATH");
    const auto dir = fs::path(path).parent_path().wstring();
    std::wstring nextPath = dir;
    if (!previous.empty())
    {
        nextPath += L";";
        nextPath += previous;
    }

    SetEnvironmentVariableW(L"PATH", nextPath.c_str());
    SetDllDirectoryW(dir.c_str());
    HMODULE module = LoadLibraryW(path.c_str());
    SetDllDirectoryW(nullptr);
    SetEnvironmentVariableW(L"PATH", previous.empty() ? nullptr : previous.c_str());
    return module;
}

std::vector<std::wstring> NvidiaRuntimeLoader::BuildSearchDirectories() const
{
    std::vector<std::wstring> dirs;

    wchar_t modulePath[MAX_PATH]{};
    if (GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), modulePath, MAX_PATH) > 0)
    {
        auto base = fs::path(modulePath).parent_path();
        dirs.push_back(base.wstring());
        dirs.push_back((base / L"ElyFlow").wstring());
        dirs.push_back((base / L"runtimes" / L"win-x64" / L"native").wstring());

        for (auto cursor = base; !cursor.empty(); cursor = cursor.parent_path())
        {
            AddSdkSearchDirectories(dirs, cursor);
            if (cursor == cursor.root_path()) break;
        }
    }

    wchar_t systemDir[MAX_PATH]{};
    if (GetSystemDirectoryW(systemDir, MAX_PATH) > 0)
        dirs.push_back(systemDir);

    for (const auto* env : { L"NVOF_SDK_PATH", L"NVIDIA_OPTICAL_FLOW_SDK", L"ELYFLOW_FRUC_PATH" })
    {
        auto value = EnvironmentVariable(env);
        if (!value.empty()) dirs.push_back(value);
    }

    std::sort(dirs.begin(), dirs.end());
    dirs.erase(std::unique(dirs.begin(), dirs.end()), dirs.end());
    return dirs;
}

std::vector<std::wstring> NvidiaRuntimeLoader::FindFrucCandidates(const std::vector<std::wstring>& dirs) const
{
    std::vector<std::wstring> candidates;
    const std::vector<std::wstring> knownNames =
    {
        L"NvOFFRUC64.dll",
        L"NvOFFRUC.dll",
        L"nvoffruc64.dll",
        L"nvoffruc.dll"
    };

    for (const auto& dir : dirs)
    {
        std::error_code ec;
        if (!fs::exists(dir, ec)) continue;

        for (const auto& name : knownNames)
        {
            auto path = fs::path(dir) / name;
            if (fs::exists(path, ec)) candidates.push_back(path.wstring());
        }

        for (const auto& entry : fs::directory_iterator(dir, fs::directory_options::skip_permission_denied, ec))
        {
            if (ec || !entry.is_regular_file(ec)) continue;
            auto path = entry.path();
            auto file = path.filename().wstring();
            auto lower = file;
            std::transform(lower.begin(), lower.end(), lower.begin(), ::towlower);
            if (lower.find(L"fruc") != std::wstring::npos && lower.ends_with(L".dll"))
                candidates.push_back(path.wstring());
        }
    }

    std::sort(candidates.begin(), candidates.end());
    candidates.erase(std::unique(candidates.begin(), candidates.end()), candidates.end());
    return candidates;
}

void NvidiaRuntimeLoader::AddSdkSearchDirectories(std::vector<std::wstring>& dirs, const fs::path& root)
{
    const std::vector<fs::path> sdkRoots =
    {
        root,
        root / L"native",
        root / L"Optical_Flow_SDK_5.0.7",
        root / L"NVIDIA-Optical-Flow-SDK-5.0.7" / L"Optical_Flow_SDK_5.0.7",
        root / L"native" / L"NVIDIA-Optical-Flow-SDK-5.0.7" / L"Optical_Flow_SDK_5.0.7"
    };

    for (const auto& sdk : sdkRoots)
    {
        dirs.push_back((sdk / L"NvOFFRUC" / L"NvOFFRUCSample" / L"bin" / L"win64").wstring());
        dirs.push_back((sdk / L"NvOFFRUC" / L"bin" / L"win64").wstring());
        dirs.push_back((sdk / L"NvOFFRUC").wstring());
    }
}

std::wstring NvidiaRuntimeLoader::ModulePath(HMODULE module)
{
    wchar_t buffer[MAX_PATH]{};
    if (GetModuleFileNameW(module, buffer, MAX_PATH) == 0) return {};
    return buffer;
}

std::string NvidiaRuntimeLoader::Narrow(const std::wstring& value)
{
    if (value.empty()) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string result(static_cast<size_t>(needed - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), needed, nullptr, nullptr);
    return result;
}

std::wstring NvidiaRuntimeLoader::EnvironmentVariable(const wchar_t* name)
{
    const DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
    if (needed == 0) return {};
    std::wstring value(needed, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
    if (written == 0) return {};
    value.resize(written);
    return value;
}
