using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{

    // ============ ELYFLOW ============
    private void LoadElyFlowIntoUi()
    {
        var s = StateStore.Settings;
        ElyFlowEnabledSwitch.IsChecked = s.ElyFlowEnabled;
        ElyFlowRtxVsrSwitch.IsChecked = s.ElyFlowRtxVsrEnabled;
        SelectComboItemByTag(ElyFlowEngineCombo, s.ElyFlowEngine);
        SelectComboItemByTag(ElyFlowTargetCombo, s.ElyFlowTargetFps);
        UpdateElyFlowTargetAvailability();
        ElyFlowLiveBufferSlider.Value = Math.Clamp(s.ElyFlowLiveBufferSeconds, 0.5, 5.0);
        UpdateElyFlowGate();
        UpdateElyFlowStatus();
        UpdateElyFlowBufferLabel();
    }

    /// <summary>
    /// ELYFLOW can be enabled only when ELYCORE is the selected video backend;
    /// otherwise the switch remains locked with an explanatory hint.
    /// </summary>
    private void UpdateElyFlowGate()
    {
        if (ElyFlowEnabledSwitch == null) return;
        var elyCore = string.Equals(StateStore.Settings.VideoBackend, "elycore", StringComparison.OrdinalIgnoreCase);
        ElyFlowEnabledSwitch.IsEnabled = elyCore;
        if (ElyFlowRtxVsrSwitch != null)
            ElyFlowRtxVsrSwitch.IsEnabled = elyCore;
        if (ElyFlowGateHint != null)
            ElyFlowGateHint.Visibility = elyCore ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ElyFlowSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SaveElyFlowFromUi();
        UpdateElyFlowTargetAvailability();
        ApplyElyFlowToBackend();
    }

    private void ElyFlowCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        SaveElyFlowFromUi();
        UpdateElyFlowTargetAvailability();
        ApplyElyFlowToBackend();
    }

    private void ElyFlowSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ElyFlowLiveBufferValue != null) UpdateElyFlowBufferLabel();
        if (_initializing) return;
        SaveElyFlowFromUi();
    }

    private void SaveElyFlowFromUi()
    {
        var s = StateStore.Settings;
        s.ElyFlowEnabled = ElyFlowEnabledSwitch.IsChecked == true;
        s.ElyFlowRtxVsrEnabled = ElyFlowRtxVsrSwitch.IsChecked == true;
        s.ElyFlowEngine = TagOf(ElyFlowEngineCombo, s.ElyFlowEngine);
        s.ElyFlowTargetFps = TagOf(ElyFlowTargetCombo, s.ElyFlowTargetFps);
        s.ElyFlowLiveBufferSeconds = Math.Round(ElyFlowLiveBufferSlider.Value, 1);
        StateStore.Save();
        UpdateElyFlowStatus();
    }

    private void UpdateElyFlowBufferLabel()
    {
        if (ElyFlowLiveBufferValue == null || ElyFlowLiveBufferSlider == null) return;
        ElyFlowLiveBufferValue.Text = ElyFlowLiveBufferSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }

    private void UpdateElyFlowTargetAvailability()
    {
        if (ElyFlowTargetCombo == null || ElyFlowEngineCombo == null) return;
        var nativeFruc = TagOf(ElyFlowEngineCombo, "nvidia-fruc") == "nvidia-fruc";
        if (ElyFlowRtxVsrSwitch != null)
        {
            var elyCore = string.Equals(StateStore.Settings.VideoBackend, "elycore", StringComparison.OrdinalIgnoreCase);
            ElyFlowRtxVsrSwitch.IsEnabled = elyCore;
            ElyFlowRtxVsrSwitch.ToolTip = LocalizationService.T(elyCore
                ? "Native ELYCORE RTX VSR pass, independent from FRUC interpolation."
                : "Native RTX VSR requires the ELYCORE backend.");
        }
        ElyFlowTargetCombo.IsEnabled = !nativeFruc;
        ElyFlowTargetCombo.ToolTip = LocalizationService.T(nativeFruc
            ? "The NVIDIA FRUC runtime is a 2x interpolator. Effective frame rate depends on the source."
            : "Frame rate requested from mpv display-resample.");
        if (ElyFlowTargetLabel != null)
            ElyFlowTargetLabel.Opacity = nativeFruc ? 0.55 : 1.0;
    }

    private void UpdateElyFlowStatus()
    {
        if (ElyFlowStatusText == null) return;
        var st = ElyFlowService.Probe();
        var gpu = st.NvidiaGpu ? st.GpuName : LocalizationService.T("NVIDIA not detected");
        var of = LocalizationService.T(st.OpticalFlowDriver ? "Optical Flow driver OK" : "nvofapi64.dll missing");
        var fruc = st.FrucRuntime ? LocalizationService.T("FRUC runtime OK: ") + st.FrucPath : LocalizationService.T("FRUC runtime missing (NvOFFRUC64.dll)");
        var native = st.NativeDllLoaded ? LocalizationService.T("ElyFlow.Native OK: ") + st.NativePath : LocalizationService.T("ElyFlow.Native missing");
        var version = string.IsNullOrWhiteSpace(st.RuntimeVersion) ? "unknown" : st.RuntimeVersion;
        var reason = string.IsNullOrWhiteSpace(st.UnavailableReason) ? LocalizationService.T("Available") : LocalizationService.T(st.UnavailableReason);
        ElyFlowStatusText.Text =
            $"GPU : {gpu}\n" +
            $"{LocalizationService.T("Driver")} : {st.DriverVersion}\n" +
            $"Optical Flow : {of}\n" +
            $"Runtime FRUC : {fruc}\n" +
            $"{LocalizationService.T("Runtime version")} : {version}\n" +
            $"{LocalizationService.T("Native backend")} : {native}\n" +
            $"{LocalizationService.T("Backend code")} : {st.NativeStatusCode}\n" +
            $"{LocalizationService.T("Reason")} : {reason}\n" +
            $"{LocalizationService.T("Status")} : {LocalizationService.T(st.BackendStatus)}";
    }

    private void ApplyElyFlowToBackend()
    {
        if (_videoBackend is not MpvHwndBackend mpv) return;
        var s = StateStore.Settings;

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
        UpdateElyFlowStatus();
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

    private static string NormalizeFps(string fps) => fps switch
    {
        "60000/1001" => "60000/1001",
        "59.94" => "60000/1001",
        _ => double.TryParse(fps, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v.ToString("0.###", CultureInfo.InvariantCulture)
            : "60"
    };
}
