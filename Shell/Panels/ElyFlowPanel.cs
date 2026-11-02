using System.Globalization;
using System.Windows;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Pipeline;

namespace Elysium_Cast_IPTV.Shell.Panels;

/// <summary>
/// ELYFLOW/ELYCORE controls: target cadence, live buffer, VSR/FRUC gating and
/// the runtime diagnostic text. Native truth counters stay outside the window.
/// </summary>
internal sealed class ElyFlowPanel
{
    private readonly MainWindow _view;
    private readonly IAppStateStore _store;
    private readonly VideoPipeline _pipeline;
    private readonly UiLoadGate _gate;

    public ElyFlowPanel(MainWindow view, IAppStateStore store, VideoPipeline pipeline, UiLoadGate gate)
    {
        _view = view;
        _store = store;
        _pipeline = pipeline;
        _gate = gate;
        _pipeline.ElyFlowApplied += UpdateStatus;
    }

    private Settings Settings => _store.Settings;
    private bool IsElyCore => string.Equals(Settings.VideoBackend, "elycore", StringComparison.OrdinalIgnoreCase);

    public void Load()
    {
        var s = Settings;
        _view.ElyFlowEnabledSwitch.IsChecked = s.ElyFlowEnabled;
        _view.ElyFlowRtxVsrSwitch.IsChecked = s.ElyFlowRtxVsrEnabled;
        ComboTags.Select(_view.ElyFlowEngineCombo, s.ElyFlowEngine);
        ComboTags.Select(_view.ElyFlowTargetCombo, s.ElyFlowTargetFps);
        UpdateTargetAvailability();
        _view.ElyFlowLiveBufferSlider.Value = Math.Clamp(s.ElyFlowLiveBufferSeconds, 0.5, 5.0);
        UpdateGate();
        UpdateStatus();
        UpdateBufferLabel();
    }

    /// <summary>
    /// ELYFLOW can be enabled only when ELYCORE is the selected video backend;
    /// otherwise the switch remains locked with an explanatory hint.
    /// </summary>
    public void UpdateGate()
    {
        var elyCore = IsElyCore;
        _view.ElyFlowEnabledSwitch.IsEnabled = elyCore;
        _view.ElyFlowRtxVsrSwitch.IsEnabled = elyCore;
        _view.ElyFlowGateHint.Visibility = elyCore ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A switch or combo changed: persist, then apply live.</summary>
    public void OnSettingChanged()
    {
        if (_gate.IsLoading) return;
        SaveFromUi();
        UpdateTargetAvailability();
        _pipeline.ApplyElyFlow();
    }

    public void OnBufferChanged()
    {
        UpdateBufferLabel();
        if (_gate.IsLoading) return;
        SaveFromUi();
    }

    /// <summary>Console: toggles FRUC; refused unless ELYCORE is selected.</summary>
    public string SetEnabled(bool enabled)
    {
        if (!IsElyCore) return "ELYFLOW requires the ELYCORE backend (type 'backend elycore').";
        Settings.ElyFlowEnabled = enabled;
        _store.Save();
        Load();
        _pipeline.ApplyElyFlow();
        return "ELYFLOW " + (enabled ? "enabled." : "disabled.");
    }

    /// <summary>Console: toggles RTX VSR.</summary>
    public void SetVsr(bool enabled)
    {
        Settings.ElyFlowRtxVsrEnabled = enabled;
        _store.Save();
        Load();
        _pipeline.ApplyElyFlow();
    }

    private void SaveFromUi()
    {
        var s = Settings;
        s.ElyFlowEnabled = _view.ElyFlowEnabledSwitch.IsChecked == true;
        s.ElyFlowRtxVsrEnabled = _view.ElyFlowRtxVsrSwitch.IsChecked == true;
        s.ElyFlowEngine = ComboTags.Of(_view.ElyFlowEngineCombo, s.ElyFlowEngine);
        s.ElyFlowTargetFps = ComboTags.Of(_view.ElyFlowTargetCombo, s.ElyFlowTargetFps);
        s.ElyFlowLiveBufferSeconds = Math.Round(_view.ElyFlowLiveBufferSlider.Value, 1);
        _store.Save();
        UpdateStatus();
    }

    private void UpdateBufferLabel()
    {
        if (_view.ElyFlowLiveBufferValue == null || _view.ElyFlowLiveBufferSlider == null) return;
        _view.ElyFlowLiveBufferValue.Text = _view.ElyFlowLiveBufferSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }

    private void UpdateTargetAvailability()
    {
        var nativeFruc = ComboTags.Of(_view.ElyFlowEngineCombo, "nvidia-fruc") == "nvidia-fruc";
        var elyCore = IsElyCore;
        _view.ElyFlowRtxVsrSwitch.IsEnabled = elyCore;
        _view.ElyFlowRtxVsrSwitch.ToolTip = LocalizationService.T(elyCore
            ? "Improve video detail, with or without motion smoothing."
            : "Select ELYCORE to use this setting.");
        _view.ElyFlowTargetCombo.IsEnabled = !nativeFruc;
        _view.ElyFlowTargetCombo.ToolTip = LocalizationService.T(nativeFruc
            ? "Motion smoothing doubles the source frame rate."
            : "Choose a rate supported by your display. This mode does not create intermediate images.");
        _view.ElyFlowTargetLabel.Opacity = nativeFruc ? 0.55 : 1.0;
    }

    public void UpdateStatus()
    {
        var st = ElyFlowService.Probe();
        var gpu = st.NvidiaGpu ? st.GpuName : LocalizationService.T("NVIDIA not detected");
        var of = LocalizationService.T(st.OpticalFlowDriver ? "Optical Flow driver OK" : "nvofapi64.dll missing");
        var fruc = st.FrucRuntime ? LocalizationService.T("FRUC runtime OK: ") + st.FrucPath : LocalizationService.T("FRUC runtime missing (NvOFFRUC64.dll)");
        var native = st.NativeDllLoaded ? LocalizationService.T("ElyFlow.Native OK: ") + st.NativePath : LocalizationService.T("ElyFlow.Native missing");
        var version = string.IsNullOrWhiteSpace(st.RuntimeVersion) ? "unknown" : st.RuntimeVersion;
        var reason = string.IsNullOrWhiteSpace(st.UnavailableReason) ? LocalizationService.T("Available") : LocalizationService.T(st.UnavailableReason);
        _view.ElyFlowStatusText.Text =
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
}
