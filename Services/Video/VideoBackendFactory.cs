using System.IO;

namespace Elysium_Cast_IPTV.Services.Video;

public static class VideoBackendFactory
{
    public static IVideoBackend Create(string preferredBackend)
    {
        if (preferredBackend.Equals("mpv-gpu", StringComparison.OrdinalIgnoreCase))
        {
            var native = MpvHwndBackend.LocateNative();
            if (!string.IsNullOrWhiteSpace(native))
            {
                try
                {
                    DebugConsole.Info("Video backend -> mpv GPU HWND (" + native + ")");
                    return new MpvHwndBackend(native);
                }
                catch (Exception ex)
                {
                    DebugConsole.Exception("mpv backend unavailable; falling back to VLC", ex);
                }
            }
            else
            {
                DebugConsole.Warn("mpv backend requested, but libmpv-2.dll was not found; falling back to VLC.");
            }
        }

        // "elyflow" is the legacy tag of the same renderer, kept for old
        // persisted settings that were saved before the ELYCORE rename.
        if (preferredBackend.Equals("elycore", StringComparison.OrdinalIgnoreCase) ||
            preferredBackend.Equals("elyflow", StringComparison.OrdinalIgnoreCase))
        {
            var native = MpvHwndBackend.LocateNative();
            if (string.IsNullOrWhiteSpace(native))
            {
                DebugConsole.Warn("ELYCORE backend requested, but libmpv-2.dll was not found; falling back to VLC.");
            }
            else if (!ElyFlowRendererInterop.Available)
            {
                DebugConsole.Warn("ELYCORE backend requested, but the native renderer is unavailable (" +
                                  ElyFlowRendererInterop.LoadError + "); falling back to mpv HWND.");
                try { return new MpvHwndBackend(native); }
                catch (Exception ex) { DebugConsole.Exception("mpv backend unavailable; falling back to VLC", ex); }
            }
            else
            {
                var preflight = ElyFlowRendererInterop.Preflight(out var message);
                if (preflight != 0)
                {
                    DebugConsole.Warn($"ELYCORE preflight rejected ({preflight}): {message}; falling back to mpv HWND.");
                    try { return new MpvHwndBackend(native); }
                    catch (Exception ex) { DebugConsole.Exception("mpv backend unavailable; falling back to VLC", ex); }
                }
                else
                {
                    try
                    {
                        // FRUC (the ELYFLOW feature) starts enabled only if the
                        // user turned it on; it can be toggled live afterwards.
                        var s = StateStore.Settings;
                        var fruc = s.ElyFlowEnabled &&
                                   s.ElyFlowEngine.Equals("nvidia-fruc", StringComparison.OrdinalIgnoreCase);
                        DebugConsole.Info("Video backend -> ELYCORE Renderer (" + message +
                                          (fruc ? ", FRUC active)" : ", FRUC inactive)"));
                        return new MpvHwndBackend(native, elyCore: true, fruc: fruc);
                    }
                    catch (Exception ex) { DebugConsole.Exception("ELYCORE backend unavailable; falling back to VLC", ex); }
                }
            }
        }

        if (preferredBackend.Equals("rtx-sdk", StringComparison.OrdinalIgnoreCase))
        {
            var native = MpvHwndBackend.LocateNative();
            if (string.IsNullOrWhiteSpace(native))
            {
                DebugConsole.Warn("RTX backend requested, but libmpv-2.dll was not found; falling back to VLC.");
            }
            else
            {
                // RTX VSR needs an NVIDIA driver; without one, keep the mpv GPU
                // pipeline (still better than the VLC bitmap fallback).
                var rtx = HasNvidiaDriver();
                if (!rtx)
                    DebugConsole.Warn("RTX backend requested, but no NVIDIA driver was detected; using standard mpv GPU.");
                try
                {
                    DebugConsole.Info(rtx
                        ? "Video backend -> mpv GPU + RTX Video Super Resolution (" + native + ")"
                        : "Video backend -> mpv GPU HWND (" + native + ")");
                    return new MpvHwndBackend(native, rtxVsr: rtx);
                }
                catch (Exception ex)
                {
                    DebugConsole.Exception("RTX backend unavailable; falling back to VLC", ex);
                }
            }
        }

        DebugConsole.Info("Video backend -> VLC bitmap");
        return new VlcBackend();
    }

    // nvapi64.dll only ships with the NVIDIA driver - cheap and reliable vendor
    // probe. Whether the GPU/driver actually supports VSR (RTX 20+, ≥ 531) is
    // decided by the driver itself: unsupported setups get standard VPP scaling.
    private static bool HasNvidiaDriver()
    {
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(system, "nvapi64.dll"));
        }
        catch { return false; }
    }
}
