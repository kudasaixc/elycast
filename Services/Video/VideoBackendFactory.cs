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
                    DebugConsole.Info("Backend vidéo -> mpv GPU HWND (" + native + ")");
                    return new MpvHwndBackend(native);
                }
                catch (Exception ex)
                {
                    DebugConsole.Exception("Backend mpv indisponible, fallback VLC", ex);
                }
            }
            else
            {
                DebugConsole.Warn("Backend mpv demandé mais libmpv-2.dll introuvable, fallback VLC.");
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
                DebugConsole.Warn("Backend ELYCORE demandé mais libmpv-2.dll introuvable, fallback VLC.");
            }
            else if (!ElyFlowRendererInterop.Available)
            {
                DebugConsole.Warn("Backend ELYCORE demandé mais renderer natif indisponible (" +
                                  ElyFlowRendererInterop.LoadError + ") — fallback mpv HWND.");
                try { return new MpvHwndBackend(native); }
                catch (Exception ex) { DebugConsole.Exception("Backend mpv indisponible, fallback VLC", ex); }
            }
            else
            {
                var preflight = ElyFlowRendererInterop.Preflight(out var message);
                if (preflight != 0)
                {
                    DebugConsole.Warn($"ELYCORE: préflight refusé ({preflight}) : {message} — fallback mpv HWND.");
                    try { return new MpvHwndBackend(native); }
                    catch (Exception ex) { DebugConsole.Exception("Backend mpv indisponible, fallback VLC", ex); }
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
                        DebugConsole.Info("Backend vidéo -> ELYCORE Renderer (" + message +
                                          (fruc ? ", FRUC actif)" : ", FRUC inactif)"));
                        return new MpvHwndBackend(native, elyCore: true, fruc: fruc);
                    }
                    catch (Exception ex) { DebugConsole.Exception("Backend ELYCORE indisponible, fallback VLC", ex); }
                }
            }
        }

        if (preferredBackend.Equals("rtx-sdk", StringComparison.OrdinalIgnoreCase))
        {
            var native = MpvHwndBackend.LocateNative();
            if (string.IsNullOrWhiteSpace(native))
            {
                DebugConsole.Warn("Backend RTX demandé mais libmpv-2.dll introuvable, fallback VLC.");
            }
            else
            {
                // RTX VSR needs an NVIDIA driver; without one, keep the mpv GPU
                // pipeline (still better than the VLC bitmap fallback).
                var rtx = HasNvidiaDriver();
                if (!rtx)
                    DebugConsole.Warn("Backend RTX demandé mais aucun driver NVIDIA détecté — mpv GPU standard.");
                try
                {
                    DebugConsole.Info(rtx
                        ? "Backend vidéo -> mpv GPU + RTX Video Super Resolution (" + native + ")"
                        : "Backend vidéo -> mpv GPU HWND (" + native + ")");
                    return new MpvHwndBackend(native, rtxVsr: rtx);
                }
                catch (Exception ex)
                {
                    DebugConsole.Exception("Backend RTX indisponible, fallback VLC", ex);
                }
            }
        }

        DebugConsole.Info("Backend vidéo -> VLC bitmap");
        return new VlcBackend();
    }

    // nvapi64.dll only ships with the NVIDIA driver — cheap and reliable vendor
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
