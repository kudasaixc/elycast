using System.IO;
using System.Runtime.InteropServices;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>Optional control plane for the ELYCAST AudioCore+ D3D11 scene.</summary>
public static class ElyAudioCoreInterop
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSceneFn(int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PushFrameFn(
        [In] double[] bands, int count, float bass, float energy, float beat);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PushVisualFrameFn(
        ref VisualFrame frame,
        [In] LinePrimitive[] bars, int barCount,
        [In] EllipsePrimitive[] particles, int particleCount,
        [In] EllipsePrimitive[] waves, int waveCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BeatFn(float strength);
    [StructLayout(LayoutKind.Sequential)]
    public struct Settings
    {
        public uint StructSize; public int ParticleCount; public float ParticleDistance; public float Dim; public float Blur;
        public int SlowZoom, SlowPan, Parallax, Shake, VSync, TargetFps;
        public float MouseX, MouseY, CenterX, CenterY, InnerRadius, UnitScale;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public uint StructSize; public int Active; public double ActualFps; public double GpuFrameMs; public ulong Frames; public int LastError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct VisualFrame
    {
        public uint StructSize;
        public float RootWidthDip, RootHeightDip;
        public float BackgroundScale;
        public float BackgroundTranslateXDip, BackgroundTranslateYDip;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct LinePrimitive
    {
        public float X0, Y0, X1, Y1, Thickness;
        public uint Color;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct EllipsePrimitive
    {
        public float X, Y, RadiusX, RadiusY, Thickness;
        public uint Color;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetPaletteFn([In] uint[] colors, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetSettingsFn(ref Settings settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetStatsFn(ref Stats stats);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetBackgroundFn([In] byte[] pixels, uint width, uint height, uint stride);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetPointerFn(float x, float y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLayoutFn(float centerX, float centerY, float innerRadius, float unitScale);

    private static readonly object Sync = new();
    private static bool _loaded;
    private static SetSceneFn? _setScene;
    private static PushFrameFn? _pushFrame;
    private static PushVisualFrameFn? _pushVisualFrame;
    private static BeatFn? _beat;
    private static SetPaletteFn? _setPalette;
    private static SetSettingsFn? _setSettings;
    private static GetStatsFn? _getStats;
    private static SetBackgroundFn? _setBackground;
    private static SetPointerFn? _setPointer;
    private static SetLayoutFn? _setLayout;

    public static bool Available { get { EnsureLoaded(); return _setScene != null && _pushFrame != null && _pushVisualFrame != null; } }

    public static bool SetScene(bool enabled)
    {
        EnsureLoaded();
        return _setScene?.Invoke(enabled ? 1 : 0) == 0;
    }

    public static void PushAudioFrame(double[] bands, float bass, float energy, float beat)
    {
        EnsureLoaded();
        _pushFrame?.Invoke(bands, bands.Length, bass, energy, beat);
    }

    public static void PushVisualFrame(
        VisualFrame frame,
        LinePrimitive[] bars, int barCount,
        EllipsePrimitive[] particles, int particleCount,
        EllipsePrimitive[] waves, int waveCount)
    {
        EnsureLoaded();
        frame.StructSize = (uint)Marshal.SizeOf<VisualFrame>();
        _pushVisualFrame?.Invoke(ref frame,
            bars, Math.Clamp(barCount, 0, bars.Length),
            particles, Math.Clamp(particleCount, 0, particles.Length),
            waves, Math.Clamp(waveCount, 0, waves.Length));
    }
    public static void Beat(float strength) { EnsureLoaded(); _beat?.Invoke(strength); }

    public static void Configure(Settings settings, uint[] palette)
    {
        EnsureLoaded();
        settings.StructSize = (uint)Marshal.SizeOf<Settings>();
        _setSettings?.Invoke(ref settings);
        if (palette.Length > 0) _setPalette?.Invoke(palette, palette.Length);
    }

    public static Stats GetStats()
    {
        EnsureLoaded();
        var stats = new Stats { StructSize = (uint)Marshal.SizeOf<Stats>() };
        _getStats?.Invoke(ref stats);
        return stats;
    }

    public static void SetBackground(byte[] pixels, uint width, uint height, uint stride)
    {
        EnsureLoaded();
        _setBackground?.Invoke(pixels, width, height, stride);
    }
    public static void SetPointer(float x, float y) { EnsureLoaded(); _setPointer?.Invoke(x, y); }
    public static void SetLayout(float centerX, float centerY, float innerRadius, float unitScale)
    { EnsureLoaded(); _setLayout?.Invoke(centerX, centerY, innerRadius, unitScale); }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;
            var baseDir = AppContext.BaseDirectory;
            var root = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            foreach (var path in new[]
            {
                Path.Combine(baseDir, "ElyFlow.Native.dll"),
                Path.Combine(root, "native", "ElyFlow.Native", "bin", "Debug", "ElyFlow.Native.dll"),
                Path.Combine(root, "native", "ElyFlow.Native", "bin", "Release", "ElyFlow.Native.dll")
            })
            {
                if (!File.Exists(path)) continue;
                var module = LoadLibrary(path);
                if (module == IntPtr.Zero) continue;
                _setScene = Get<SetSceneFn>(module, "ElyAudioCore_SetScene");
                _pushFrame = Get<PushFrameFn>(module, "ElyAudioCore_PushAudioFrame");
                _pushVisualFrame = Get<PushVisualFrameFn>(module, "ElyAudioCore_PushVisualFrame");
                _beat = Get<BeatFn>(module, "ElyAudioCore_Beat");
                _setPalette = Get<SetPaletteFn>(module, "ElyAudioCore_SetPalette");
                _setSettings = Get<SetSettingsFn>(module, "ElyAudioCore_SetSettings");
                _getStats = Get<GetStatsFn>(module, "ElyAudioCore_GetStats");
                _setBackground = Get<SetBackgroundFn>(module, "ElyAudioCore_SetBackground");
                _setPointer = Get<SetPointerFn>(module, "ElyAudioCore_SetPointer");
                _setLayout = Get<SetLayoutFn>(module, "ElyAudioCore_SetLayout");
                if (_setScene != null && _pushFrame != null && _pushVisualFrame != null && _beat != null && _setPalette != null && _setSettings != null && _getStats != null && _setBackground != null && _setPointer != null && _setLayout != null) return;
            }
        }
    }

    private static T? Get<T>(IntPtr module, string name) where T : Delegate
    {
        var address = GetProcAddress(module, name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
