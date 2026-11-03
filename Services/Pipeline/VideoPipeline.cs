using System.Globalization;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Pipeline;

/// <summary>
/// Applies the persisted picture/sound pipeline (internal upscaling, ELYCOLOR,
/// ELYSOUND+, ELYFLOW) to whichever backend currently owns playback. Backends
/// that do not support a stage are left untouched.
/// </summary>
public sealed class VideoPipeline
{
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly ShaderInstaller _shaderInstaller;

    public VideoPipeline(PlaybackSession session, IAppStateStore store, ShaderInstaller shaderInstaller)
    {
        _session = session;
        _store = store;
        _shaderInstaller = shaderInstaller;
    }

    /// <summary>Raised after ELYFLOW was (re)applied on ELYCORE so diagnostics can refresh.</summary>
    public event Action? ElyFlowApplied;

    private Settings Settings => _store.Settings;

    /// <summary>Every stage, in the order a freshly created backend expects them.</summary>
    public void ApplyAll()
    {
        ApplyUpscalingAsync().Forget("Upscaling: applying the pipeline");
        ApplyElyColor();
        ApplyElySound();
        ApplyElyFlow();
    }

    // ============ UPSCALING (internal mpv) ============
    // The GLSL chains (FSR, NVScaler, FSRCNNX, Anime4K + their CAS / NVSharpen
    // passes) are downloaded on demand before being applied - otherwise the
    // backend would silently skip missing files and the mode would do nothing.
    public async Task ApplyUpscalingAsync()
    {
        if (_session.Backend is not MpvHwndBackend) return;
        var s = Settings;

        var missing = ShaderCatalog.MissingFor(s.UpscaleMethod, s.UpscaleSharpen);
        if (missing.Count > 0)
        {
            try
            {
                DebugConsole.Step($"Upscaling: preparing shaders ({string.Join(", ", missing)})...");
                await _shaderInstaller.EnsureAsync(s.UpscaleMethod, s.UpscaleSharpen);
            }
            catch (Exception ex)
            {
                DebugConsole.Warn("Upscaling: shaders unavailable (" + ex.Message + "); falling back to the mpv scaler.");
            }
        }

        // The backend (or the settings) may have changed during the download.
        if (_session.Backend is MpvHwndBackend mpv)
        {
            mpv.ApplyUpscaling(s.UpscaleTargetHeight, s.UpscaleMethod, s.UpscaleSharpen, s.VideoEffects);
            DebugConsole.Info($"Upscaling: target={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")}, method={s.UpscaleMethod}, sharpness={s.UpscaleSharpen}, effects={(s.VideoEffects.Count == 0 ? "none" : string.Join("+", s.VideoEffects))}");
        }
    }

    // ============ ELYCOLOR ============
    public void ApplyElyColor() => ApplyElyColor(ElyColorCatalog.Active(Settings));

    public void ApplyElyColor(ElyColorFilter filter)
    {
        if (_session.Backend is not MpvHwndBackend mpv) return;

        mpv.SetOption("saturation", filter.Saturation.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("brightness", filter.Brightness.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("contrast", filter.Contrast.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("gamma", filter.Gamma.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("hue", filter.Hue.ToString(CultureInfo.InvariantCulture));
    }

    // ============ ELYSOUND+ ============
    public void ApplyElySound() => ApplyElySound(ElySoundProfiles.Active(Settings));

    public void ApplyElySound(ElySoundProfile profile)
    {
        if (_session.Backend is not IElySoundBackend dsp) return;
        var enabled = Settings.ElySoundEnabled;
        var result = dsp.ApplyElySound(profile, enabled, Settings.ElySoundVirtualSurround);
        if (result.Pending)
            DebugConsole.Info("ELYSOUND+: waiting for the track's audio parameters.");
        else if (result.Applied)
            DebugConsole.Info("ELYSOUND+ -> " + LocalizationService.T(profile.Name) + " | " + LocalizationService.T(result.Message) + " | " + result.Graph);
        else if (enabled)
            DebugConsole.Warn("ELYSOUND+ -> " + LocalizationService.T(result.Message));
        else
            DebugConsole.Info("ELYSOUND+ off (only @elysound was removed)");
    }

    // ============ ELYFLOW ============
    public void ApplyElyFlow()
    {
        if (_session.Backend is not MpvHwndBackend mpv) return;
        var s = Settings;

        // ELYFLOW (FRUC + native VSR) lives exclusively in ELYCORE. Other
        // backends are never replaced here: the former automatic rtx-sdk to
        // native-renderer switch rebuilt the pipeline during playback, caused a
        // black screen, and overwrote the user's explicit selection.
        if (!mpv.IsElyCoreRenderer)
        {
            ResetMpvFramePacing(mpv);
            return;
        }

        // Never enable mpv interpolation on top of the native renderer: FRUC
        // already presents one generated frame between every source frame.
        var frucWanted = s.ElyFlowEnabled &&
                         s.ElyFlowEngine.Equals("nvidia-fruc", StringComparison.OrdinalIgnoreCase);
        mpv.ConfigureElyCoreFruc(frucWanted);
        mpv.ConfigureElyCoreVsr(s.ElyFlowRtxVsrEnabled);

        if (s.ElyFlowEnabled &&
            s.ElyFlowEngine.Equals("mpv-pacing", StringComparison.OrdinalIgnoreCase))
        {
            var effectiveTarget = ApplyMpvFramePacing(mpv, s.ElyFlowTargetFps, s.ElyFlowLiveBufferSeconds);
            DebugConsole.Info($"ELYFLOW mpv pacing: {effectiveTarget} effective fps");
            return;
        }

        ResetMpvFramePacing(mpv);
        if (frucWanted)
        {
            ApplyMpvLiveBuffer(mpv, s.ElyFlowLiveBufferSeconds);
            DebugConsole.Info("ELYFLOW NVIDIA FRUC active on the ELYCORE renderer.");
        }
        else
        {
            DebugConsole.Info("ELYFLOW off (ELYCORE retained, FRUC stopped live).");
        }
        ElyFlowApplied?.Invoke();
    }

    private static string ApplyMpvFramePacing(MpvHwndBackend mpv, string targetFps, double bufferSeconds)
    {
        var normalized = NormalizeFps(targetFps);
        var requested = normalized == "60000/1001"
            ? 60000.0 / 1001.0
            : double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 60.0;
        var refresh = mpv.GetDisplayRefreshRate();
        var effective = refresh > 0 ? Math.Min(requested, refresh) : Math.Min(requested, 120.0);
        var effectiveText = effective.ToString("0.###", CultureInfo.InvariantCulture);
        if (effective + 0.5 < requested)
            DebugConsole.Info($"ELYFLOW mpv: target {requested:0.###} capped to the display ({refresh:0.###} Hz).");

        mpv.SetOption("video-sync", "display-resample");
        mpv.SetOption("interpolation", "yes");
        mpv.SetOption("tscale", "oversample");
        mpv.SetOption("override-display-fps", effectiveText);
        ApplyMpvLiveBuffer(mpv, bufferSeconds);
        return effectiveText;
    }

    private static void ResetMpvFramePacing(MpvHwndBackend mpv)
    {
        mpv.SetOption("video-sync", "audio");
        mpv.SetOption("interpolation", "no");
        mpv.SetOption("tscale", "oversample");
        // Numeric mpv option: an empty string is rejected with
        // MPV_ERROR_PROPERTY_FORMAT (-9). Zero restores auto detection.
        mpv.SetOption("override-display-fps", "0");
        mpv.SetOption("cache", "auto");
        mpv.SetOption("cache-secs", "0");
        mpv.SetOption("cache-pause-wait", "1");
    }

    private static void ApplyMpvLiveBuffer(MpvHwndBackend mpv, double bufferSeconds)
    {
        var seconds = Math.Clamp(bufferSeconds, 0.5, 5.0).ToString("0.0", CultureInfo.InvariantCulture);
        mpv.SetOption("cache", "yes");
        mpv.SetOption("cache-secs", seconds);
        mpv.SetOption("cache-pause-wait", Math.Min(Math.Max(bufferSeconds, 0.5), 1.5).ToString("0.0", CultureInfo.InvariantCulture));
    }

    public static string NormalizeFps(string fps) => fps switch
    {
        "60000/1001" => "60000/1001",
        "59.94" => "60000/1001",
        _ => double.TryParse(fps, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v.ToString("0.###", CultureInfo.InvariantCulture)
            : "60"
    };
}
