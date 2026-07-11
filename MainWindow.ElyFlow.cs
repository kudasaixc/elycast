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
    /// ELYFLOW n'est activable que lorsque le renderer ELYCORE est le backend
    /// vidéo choisi : le switch est verrouillé (avec un indice) sinon.
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
            ElyFlowRtxVsrSwitch.ToolTip = elyCore
                ? "Passe RTX VSR native ELYCORE, indépendante de l'interpolation FRUC."
                : "RTX VSR natif nécessite le backend ELYCORE.";
        }
        ElyFlowTargetCombo.IsEnabled = !nativeFruc;
        ElyFlowTargetCombo.ToolTip = nativeFruc
            ? "Le runtime NVIDIA FRUC est un interpolateur ×2. La cadence effective dépend de la source."
            : "Cadence demandée à mpv display-resample.";
        if (ElyFlowTargetLabel != null)
            ElyFlowTargetLabel.Opacity = nativeFruc ? 0.55 : 1.0;
    }

    private void UpdateElyFlowStatus()
    {
        if (ElyFlowStatusText == null) return;
        var st = ElyFlowService.Probe();
        var gpu = st.NvidiaGpu ? st.GpuName : "NVIDIA non détecté";
        var of = st.OpticalFlowDriver ? "driver Optical Flow OK" : "nvofapi64.dll absent";
        var fruc = st.FrucRuntime ? "runtime FRUC OK : " + st.FrucPath : "runtime FRUC absent (NvOFFRUC64.dll)";
        var native = st.NativeDllLoaded ? "ElyFlow.Native OK : " + st.NativePath : "ElyFlow.Native absent";
        var version = string.IsNullOrWhiteSpace(st.RuntimeVersion) ? "unknown" : st.RuntimeVersion;
        var reason = string.IsNullOrWhiteSpace(st.UnavailableReason) ? "Disponible" : st.UnavailableReason;
        ElyFlowStatusText.Text =
            $"GPU : {gpu}\n" +
            $"Driver : {st.DriverVersion}\n" +
            $"Optical Flow : {of}\n" +
            $"Runtime FRUC : {fruc}\n" +
            $"Version runtime : {version}\n" +
            $"Backend natif : {native}\n" +
            $"Code backend : {st.NativeStatusCode}\n" +
            $"Raison : {reason}\n" +
            $"Statut : {st.BackendStatus}";
    }

    private void ApplyElyFlowToBackend()
    {
        if (_videoBackend is not MpvHwndBackend mpv) return;
        var s = StateStore.Settings;

        // ELYFLOW (FRUC + VSR natif) vit exclusivement dans le renderer
        // ELYCORE. Sur les autres backends la fonctionnalité est inapplicable
        // et le backend choisi par l'utilisateur n'est JAMAIS remplacé ici —
        // l'ancien basculement automatique rtx-sdk -> renderer natif recréait
        // le pipeline en pleine lecture (écran noir) et écrasait le choix fait
        // dans les réglages.
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
            DebugConsole.Info($"ELYFLOW mpv pacing -> {effectiveTarget} fps effectifs");
            return;
        }

        ResetMpvFramePacing(mpv);
        if (frucWanted)
        {
            ApplyMpvLiveBuffer(mpv, s.ElyFlowLiveBufferSeconds);
            DebugConsole.Info("ELYFLOW NVIDIA FRUC -> actif sur le renderer ELYCORE.");
        }
        else
        {
            DebugConsole.Info("ELYFLOW -> off (renderer ELYCORE conservé, FRUC arrêté à chaud).");
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
            DebugConsole.Info($"ELYFLOW mpv: cible {requested:0.###} plafonnée au moniteur ({refresh:0.###} Hz).");

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
