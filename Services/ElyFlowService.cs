using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace Elysium_Cast_IPTV.Services;

public sealed record ElyFlowStatus(
    bool NvidiaGpu,
    bool OpticalFlowDriver,
    bool FrucRuntime,
    string GpuName,
    string DriverVersion,
    string FrucPath,
    bool NativeDllLoaded,
    string NativePath,
    int NativeStatusCode,
    string RuntimeVersion,
    string BackendStatus,
    string UnavailableReason)
{
    /// <summary>An active FRUC session exists right now (playback running).</summary>
    public bool FrucReady => FrucCapable && NativeStatusCode == 0;

    /// <summary>
    /// Everything FRUC needs is installed (GPU, driver, runtime, native DLL).
    /// Unlike <see cref="FrucReady"/> this is true outside of playback — use it
    /// to decide whether the feature can be offered, not whether it is running.
    /// </summary>
    public bool FrucCapable => NvidiaGpu && OpticalFlowDriver && FrucRuntime && NativeDllLoaded;
}

public static class ElyFlowService
{
    private const int StatusOk = 0;
    private const int StatusNativeDllReady = 1;
    private const int StatusNvidiaDriverMissing = -10;
    private const int StatusFrucRuntimeMissing = -11;
    private const int StatusSdkAdapterNotCompiled = -12;
    private const int StatusTexturePipelineNotConnected = -15;
    private const int StatusFrucSymbolMissing = -16;
    private const int StatusFrucCreateFailed = -17;
    private const int StatusFrameResourceMissing = -18;
    private const int StatusFrucProcessFailed = -19;

    private static readonly object Sync = new();
    private static NativeApi? _native;

    private static readonly string[] FrucNames =
    {
        "NvOFFRUC64.dll",
        "NvOFFRUC.dll",
        "nvoffruc64.dll",
        "nvoffruc.dll"
    };

    public static ElyFlowStatus Probe()
    {
        var gpu = ProbeGpu();
        var opticalFlowDriverPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvofapi64.dll");
        var opticalFlowDriver = File.Exists(opticalFlowDriverPath);
        var managedFrucPath = FindFrucRuntime();
        var native = GetNativeApi();
        var nativeInfo = native?.ReadRuntimeInfo();

        var frucPath = FirstNonEmpty(nativeInfo?.FrucRuntimePath, managedFrucPath);
        var frucRuntime = !string.IsNullOrWhiteSpace(frucPath) || nativeInfo?.FrucRuntimeLoaded == true;
        var runtimeVersion = RuntimeVersionFor(frucPath, nativeInfo?.RuntimeVersion);
        var nativeStatus = nativeInfo?.StatusCode ?? (native?.Loaded == true ? StatusNativeDllReady : StatusFrucRuntimeMissing);
        var backendStatus = BuildBackendStatus(native, nativeInfo);
        var reason = BuildUnavailableReason(gpu.NvidiaGpu, opticalFlowDriver, frucRuntime, native, nativeStatus);

        return new ElyFlowStatus(
            NvidiaGpu: gpu.NvidiaGpu,
            OpticalFlowDriver: opticalFlowDriver || nativeInfo?.NvofapiLoaded == true,
            FrucRuntime: frucRuntime,
            GpuName: gpu.Name,
            DriverVersion: gpu.DriverVersion,
            FrucPath: frucPath,
            NativeDllLoaded: native?.Loaded == true,
            NativePath: native?.Path ?? "",
            NativeStatusCode: nativeStatus,
            RuntimeVersion: runtimeVersion,
            BackendStatus: backendStatus,
            UnavailableReason: reason);
    }

    private static NativeApi? GetNativeApi()
    {
        lock (Sync)
        {
            if (_native != null) return _native;
            _native = NativeApi.TryLoad(BuildNativeSearchPaths());
            return _native;
        }
    }

    private static IEnumerable<string> BuildNativeSearchPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ElyFlow.Native.dll");
        yield return Path.Combine(baseDir, "runtimes", "win-x64", "native", "ElyFlow.Native.dll");
        yield return Path.Combine(baseDir, "ElyFlow", "ElyFlow.Native.dll");

        // Developer layout: bin/x64/Debug/net8.0-windows -> repository root.
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        yield return Path.Combine(repoRoot, "native", "ElyFlow.Native", "bin", "Debug", "ElyFlow.Native.dll");
        yield return Path.Combine(repoRoot, "native", "ElyFlow.Native", "bin", "Release", "ElyFlow.Native.dll");
    }

    private static string FindFrucRuntime()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var dirs = new[]
        {
            baseDir,
            Path.Combine(baseDir, "runtimes", "win-x64", "native"),
            Path.Combine(baseDir, "ElyFlow"),
            Path.Combine(repoRoot, "native", "NVIDIA-Optical-Flow-SDK-5.0.7", "Optical_Flow_SDK_5.0.7", "NvOFFRUC", "NvOFFRUCSample", "bin", "win64"),
            Path.Combine(repoRoot, "Optical_Flow_SDK_5.0.7", "NvOFFRUC", "NvOFFRUCSample", "bin", "win64"),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetEnvironmentVariable("NVOF_SDK_PATH") ?? "",
            Environment.GetEnvironmentVariable("NVIDIA_OPTICAL_FLOW_SDK") ?? "",
            Environment.GetEnvironmentVariable("ELYFLOW_FRUC_PATH") ?? ""
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var candidate in FrucNames.Select(name => Path.Combine(dir, name)))
                if (File.Exists(candidate)) return candidate;

            try
            {
                var fruc = Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => Path.GetFileName(path).Contains("fruc", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(fruc)) return fruc;
            }
            catch { }
        }

        return "";
    }

    private static (bool NvidiaGpu, string Name, string DriverVersion) ProbeGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (!name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                return (true, name, obj["DriverVersion"]?.ToString() ?? "");
            }
        }
        catch { }

        return (false, "", "");
    }

    private static string RuntimeVersionFor(string path, string? nativeVersion)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return FirstNonEmpty(info.ProductVersion, info.FileVersion, nativeVersion, "unknown");
            }
            catch { }
        }

        return FirstNonEmpty(nativeVersion, "unknown");
    }

    private static string BuildBackendStatus(NativeApi? native, NativeRuntimeInfo? info)
    {
        if (native?.Loaded != true)
            return native?.LoadError ?? "ElyFlow.Native.dll absent.";
        if (info == null)
            return "ElyFlow.Native.dll chargé, diagnostic runtime indisponible.";
        return FirstNonEmpty(info.Status, "ElyFlow.Native.dll chargé.");
    }

    private static string BuildUnavailableReason(bool nvidiaGpu, bool opticalFlowDriver, bool frucRuntime, NativeApi? native, int nativeStatus)
    {
        if (!nvidiaGpu) return "Aucun GPU NVIDIA détecté.";
        if (!opticalFlowDriver) return "nvofapi64.dll absent : le driver NVIDIA Optical Flow n'est pas disponible.";
        if (native?.Loaded != true) return "ElyFlow.Native.dll absent : backend natif non chargé.";
        if (!frucRuntime) return "Runtime FRUC NVIDIA absent : ajoutez le SDK/runtime officiel contenant la DLL FRUC.";

        return nativeStatus switch
        {
            StatusOk => "",
            StatusNativeDllReady => "Backend natif charge, mais aucune session FRUC active n'est initialisee.",
            StatusSdkAdapterNotCompiled => "Runtime FRUC trouvé, mais l'adaptateur officiel SDK FRUC n'est pas encore compilé dans ElyFlow.Native.",
            StatusNvidiaDriverMissing => "Backend natif chargé, mais nvofapi64.dll n'a pas pu être chargé.",
            StatusFrucRuntimeMissing => "Backend natif chargé, mais aucune DLL FRUC officielle n'a pu être chargée.",
            StatusTexturePipelineNotConnected => "SDK FRUC compile et exports NvOFFRUC resolus ; il manque encore le pipeline D3D11 mpv -> textures -> ElyFlow.Native -> swapchain.",
            StatusFrucSymbolMissing => "NvOFFRUC.dll est chargee, mais au moins un export officiel NVIDIA est absent.",
            StatusFrucCreateFailed => "NvOFFRUCCreate a echoue : voir le diagnostic backend pour le code NVIDIA exact.",
            StatusFrameResourceMissing => "Session FRUC presente, mais les textures D3D11 entree/sortie ne sont pas encore fournies.",
            StatusFrucProcessFailed => "NvOFFRUCProcess a echoue : voir le diagnostic backend pour le code NVIDIA exact.",
            _ => "Backend FRUC indisponible : code " + nativeStatus.ToString()
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private sealed class NativeApi
    {
        private readonly ElyFlowGetRuntimeInfo _getRuntimeInfo;
        private readonly ElyFlowGetStatus _getStatus;
        private readonly ElyFlowGetRuntimeVersion _getRuntimeVersion;

        private NativeApi(IntPtr module, string path, ElyFlowGetRuntimeInfo getRuntimeInfo, ElyFlowGetStatus getStatus, ElyFlowGetRuntimeVersion getRuntimeVersion)
        {
            Module = module;
            Path = path;
            _getRuntimeInfo = getRuntimeInfo;
            _getStatus = getStatus;
            _getRuntimeVersion = getRuntimeVersion;
        }

        public bool Loaded => Module != IntPtr.Zero;
        public IntPtr Module { get; }
        public string Path { get; }
        public string LoadError { get; private init; } = "";

        public static NativeApi TryLoad(IEnumerable<string> paths)
        {
            var errors = new StringBuilder();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var module = LoadLibrary(path);
                    if (module == IntPtr.Zero)
                    {
                        errors.AppendLine(path + " -> LoadLibrary failed: " + Marshal.GetLastWin32Error());
                        continue;
                    }

                    var getRuntimeInfo = GetDelegate<ElyFlowGetRuntimeInfo>(module, "ElyFlow_GetRuntimeInfo");
                    var getStatus = GetDelegate<ElyFlowGetStatus>(module, "ElyFlow_GetStatus");
                    var getRuntimeVersion = GetDelegate<ElyFlowGetRuntimeVersion>(module, "ElyFlow_GetRuntimeVersion");
                    if (getRuntimeInfo == null || getStatus == null || getRuntimeVersion == null)
                    {
                        errors.AppendLine(path + " -> exports ELYFLOW manquants.");
                        FreeLibrary(module);
                        continue;
                    }

                    return new NativeApi(module, path, getRuntimeInfo, getStatus, getRuntimeVersion);
                }
                catch (Exception ex)
                {
                    errors.AppendLine(path + " -> " + ex.Message);
                }
            }

            return new NativeApi(IntPtr.Zero, "", MissingRuntimeInfo, MissingString, MissingString)
            {
                LoadError = errors.Length == 0 ? "ElyFlow.Native.dll introuvable dans les chemins de recherche." : errors.ToString()
            };
        }

        public NativeRuntimeInfo? ReadRuntimeInfo()
        {
            if (!Loaded) return null;
            try
            {
                var native = new ElyFlowRuntimeInfo { StructSize = (uint)Marshal.SizeOf<ElyFlowRuntimeInfo>() };
                var code = _getRuntimeInfo(ref native);
                return new NativeRuntimeInfo(
                    StatusCode: code,
                    NvofapiLoaded: native.NvofapiLoaded != 0,
                    FrucRuntimeLoaded: native.FrucRuntimeLoaded != 0,
                    Initialized: native.Initialized != 0,
                    NvofapiPath: native.NvofapiPath ?? "",
                    FrucRuntimePath: native.FrucRuntimePath ?? "",
                    RuntimeVersion: FirstNonEmpty(native.RuntimeVersion, PtrToString(_getRuntimeVersion())),
                    Status: FirstNonEmpty(native.Status, PtrToString(_getStatus())));
            }
            catch (Exception ex)
            {
                return new NativeRuntimeInfo(-1, false, false, false, "", "", "unknown", "Erreur diagnostic ElyFlow.Native : " + ex.Message);
            }
        }

        private static T? GetDelegate<T>(IntPtr module, string name) where T : Delegate
        {
            var ptr = GetProcAddress(module, name);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private static int MissingRuntimeInfo(ref ElyFlowRuntimeInfo info) => -1;

        private static IntPtr MissingString() => IntPtr.Zero;

        private static string PtrToString(IntPtr ptr) => ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    }

    private sealed record NativeRuntimeInfo(
        int StatusCode,
        bool NvofapiLoaded,
        bool FrucRuntimeLoaded,
        bool Initialized,
        string NvofapiPath,
        string FrucRuntimePath,
        string RuntimeVersion,
        string Status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ElyFlowRuntimeInfo
    {
        public uint StructSize;
        public int StatusCode;
        public int NvofapiLoaded;
        public int FrucRuntimeLoaded;
        public int Initialized;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string NvofapiPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string FrucRuntimePath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string RuntimeVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Status;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ElyFlowGetRuntimeInfo(ref ElyFlowRuntimeInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ElyFlowGetStatus();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ElyFlowGetRuntimeVersion();
}
