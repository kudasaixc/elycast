using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Bindings for the experimental ELYFLOW renderer exported by
/// ElyFlow.Native.dll (mpv render API → D3D11 interop → NvOFFRUC → swapchain).
/// Loaded manually so the search paths match ElyFlowService and only one copy
/// of the module ever lives in the process.
/// </summary>
public static class ElyFlowRendererInterop
{
    public const uint RequiredAbiVersion = 10;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct RendererState
    {
        public uint StructSize;
        public int Active;
        public int GlInterop;
        public int TexturesShared;
        public int FrucInitialized;
        public int LastFrucStatus;
        public ulong FramesRendered;
        public ulong FramesInterpolated;
        public ulong FramesPresented;
        public uint Width;
        public uint Height;
        public ulong LatePresents;
        public double SourceFps;
        public double AverageWorkMs;
        public double MaxWorkMs;
        public int VsrActive;
        public int LastVsrStatus;
        public uint VsrInputWidth;
        public uint VsrInputHeight;
        public uint VsrContentWidth;
        public uint VsrContentHeight;
        public int VsrAvailable;
        public int VsrRequested;
        public int VsrEffective;
        public int VsrLevel;
        public uint AdapterVendorId;
        public uint VsrInputFormat;
        public uint VsrOutputFormat;
        public uint VsrColorSpace;
        public ulong TargetRebuilds;
        public ulong SwapchainResizes;
        public ulong VsrFramesProcessed;
        public ulong VsrFramesBypassed;
        public ulong VideoProcessorFrames;
        public ulong PresentErrors;
        public int VideoProcessorCreated;
        public int VsrExtensionEnabled;
        public int VsrConverterActive;
        public int LastConvStatus;
        public uint VsrQueryRaw;
        public double VsrBltAvgMs;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Message;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAbiVersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PreflightFn(byte[] message, int messageSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateFn(IntPtr mpvHandle, IntPtr hwnd, int enableFruc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSourceFpsFn(double sourceFps);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureVsrFn(int enable, uint sourceWidth, uint sourceHeight);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureFrucFn(int enable);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetStateFn(ref RendererState state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyFn();

    private static readonly object Sync = new();
    private static bool _probed;
    private static PreflightFn? _preflight;
    private static GetAbiVersionFn? _getAbiVersion;
    private static CreateFn? _create;
    private static SetSourceFpsFn? _setSourceFps;
    private static ConfigureVsrFn? _configureVsr;
    private static ConfigureFrucFn? _configureFruc;
    private static GetStateFn? _getState;
    private static DestroyFn? _destroy;
    private static string _loadError = "";

    public static bool Available
    {
        get { EnsureLoaded(); return _create != null; }
    }

    public static string LoadError { get { EnsureLoaded(); return _loadError; } }

    public static int Preflight(out string message)
    {
        EnsureLoaded();
        message = "";
        if (_preflight == null) { message = _loadError; return -1; }
        var buffer = new byte[512];
        var code = _preflight(buffer, buffer.Length);
        var end = Array.IndexOf(buffer, (byte)0);
        message = Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        return code;
    }

    public static int Create(IntPtr mpvHandle, IntPtr hwnd, bool enableFruc)
    {
        EnsureLoaded();
        return _create?.Invoke(mpvHandle, hwnd, enableFruc ? 1 : 0) ?? -1;
    }

    public static RendererState GetState()
    {
        EnsureLoaded();
        var state = new RendererState { StructSize = (uint)Marshal.SizeOf<RendererState>() };
        if (_getState != null) _getState(ref state);
        return state;
    }

    public static void SetSourceFps(double sourceFps)
    {
        EnsureLoaded();
        if (double.IsFinite(sourceFps) && sourceFps >= 5 && sourceFps <= 240)
            _setSourceFps?.Invoke(sourceFps);
    }

    public static void ConfigureVsr(bool enable, uint sourceWidth, uint sourceHeight)
    {
        EnsureLoaded();
        _configureVsr?.Invoke(enable ? 1 : 0, sourceWidth, sourceHeight);
    }

    public static void ConfigureFruc(bool enable)
    {
        EnsureLoaded();
        _configureFruc?.Invoke(enable ? 1 : 0);
    }

    public static void Destroy()
    {
        EnsureLoaded();
        _destroy?.Invoke();
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_probed) return;
            _probed = true;

            var baseDir = AppContext.BaseDirectory;
            var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            var candidates = new[]
            {
                Path.Combine(baseDir, "ElyFlow.Native.dll"),
                Path.Combine(baseDir, "runtimes", "win-x64", "native", "ElyFlow.Native.dll"),
                Path.Combine(repoRoot, "native", "ElyFlow.Native", "bin", "Debug", "ElyFlow.Native.dll"),
                Path.Combine(repoRoot, "native", "ElyFlow.Native", "bin", "Release", "ElyFlow.Native.dll")
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var module = LoadLibrary(path);
                if (module == IntPtr.Zero)
                {
                    _loadError = path + " -> LoadLibrary " + Marshal.GetLastWin32Error();
                    continue;
                }

                _preflight = GetDelegate<PreflightFn>(module, "ElyFlowRenderer_Preflight");
                _getAbiVersion = GetDelegate<GetAbiVersionFn>(module, "ElyFlowRenderer_GetAbiVersion");
                _create = GetDelegate<CreateFn>(module, "ElyFlowRenderer_Create");
                _setSourceFps = GetDelegate<SetSourceFpsFn>(module, "ElyFlowRenderer_SetSourceFps");
                _configureVsr = GetDelegate<ConfigureVsrFn>(module, "ElyFlowRenderer_ConfigureVsr");
                _configureFruc = GetDelegate<ConfigureFrucFn>(module, "ElyFlowRenderer_ConfigureFruc");
                _getState = GetDelegate<GetStateFn>(module, "ElyFlowRenderer_GetState");
                _destroy = GetDelegate<DestroyFn>(module, "ElyFlowRenderer_Destroy");
                var abiVersion = _getAbiVersion?.Invoke() ?? 0;
                if (abiVersion == RequiredAbiVersion && _create != null && _setSourceFps != null && _configureVsr != null &&
                    _configureFruc != null && _getState != null && _destroy != null && _preflight != null)
                {
                    DebugConsole.Info("ELYCORE: native renderer loaded -> " + path);
                    return;
                }

                _preflight = null; _getAbiVersion = null; _create = null; _setSourceFps = null; _configureVsr = null;
                _configureFruc = null; _getState = null; _destroy = null;
                _loadError = abiVersion switch
                {
                    0 => path + $" -> ELYCORE ABI missing (DLL predates ABI v{RequiredAbiVersion}).",
                    not RequiredAbiVersion => path + $" -> ABI ELYCORE incompatible (DLL v{abiVersion}, application v{RequiredAbiVersion}).",
                    _ => path + $" -> ELYCORE ABI v{abiVersion}, but required exports are incomplete."
                };
                FreeLibrary(module);
            }

            if (string.IsNullOrEmpty(_loadError)) _loadError = "ElyFlow.Native.dll not found.";
        }
    }

    private static T? GetDelegate<T>(IntPtr module, string name) where T : Delegate
    {
        var ptr = GetProcAddress(module, name);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr hModule);
}
