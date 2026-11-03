using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Shell.Panels;

/// <summary>
/// ELYCOLOR picker (settings + OSD), live preview editor and custom profiles.
/// A custom profile may carry a full video pipeline and then deliberately goes
/// through the backend replacement path.
/// </summary>
internal sealed class ElyColorPanel
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly VideoPipeline _pipeline;
    private readonly UiLoadGate _gate;
    private readonly BackendHost _backendHost;
    private readonly UpscalingPanel _upscaling;
    private bool _syncing;
    private bool _previewDirty;

    public ElyColorPanel(MainWindow view, PlaybackSession session, IAppStateStore store, VideoPipeline pipeline,
        UiLoadGate gate, BackendHost backendHost, UpscalingPanel upscaling)
    {
        _view = view;
        _session = session;
        _store = store;
        _pipeline = pipeline;
        _gate = gate;
        _backendHost = backendHost;
        _upscaling = upscaling;
    }

    private Settings Settings => _store.Settings;

    public void Populate()
    {
        _syncing = true;
        try
        {
            var active = ElyColorCatalog.Active(Settings).Id;
            FillCombo(_view.ElyColorFilterCombo, active);
            FillCombo(_view.OsdElyColorCombo, active);

            _view.ElyColorCustomSelectCombo.Items.Clear();
            foreach (var filter in Settings.ElyColorCustomFilters ?? new())
                _view.ElyColorCustomSelectCombo.Items.Add(new ComboBoxItem { Tag = filter.Id, Content = LocalizationService.T(filter.Name) });
        }
        finally
        {
            _syncing = false;
        }
        RefreshOsdRow();
    }

    private void FillCombo(ComboBox combo, string selectedId)
    {
        combo.Items.Clear();
        foreach (var filter in ElyColorCatalog.All(Settings))
            combo.Items.Add(new ComboBoxItem { Tag = filter.Id, Content = LocalizationService.T(filter.Name) });
        ComboTags.Select(combo, selectedId);
    }

    /// <summary>Selection from the settings combo or the OSD quick picker.</summary>
    public void OnFilterSelected(ComboBox combo)
    {
        if (_gate.IsLoading || _syncing) return;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var filter = ElyColorCatalog.Find(Settings, id);
        if (filter == null) return;
        // No forced backend reset here: the colour properties apply live, and
        // ApplyFilter recreates the backend only when a custom filter genuinely
        // switches it. Rebuilding mpv for a colour change interrupted playback.
        LoadEditor(filter);
        ApplyFilter(filter, persist: true, includePipeline: true);
    }

    /// <summary>Console: selects a filter by id. Returns false for an unknown id.</summary>
    public bool Select(string id)
    {
        var filter = ElyColorCatalog.Find(Settings, id);
        if (filter == null) return false;
        Settings.ElyColorFilterId = filter.Id;
        _store.Save();
        Populate();
        _pipeline.ApplyElyColor();
        return true;
    }

    private void ApplyFilter(ElyColorFilter filter, bool persist, bool includePipeline, bool forceBackendReset = false)
    {
        var s = Settings;
        var backendChanged = false;

        if (includePipeline && filter.IncludeVideoPipeline)
        {
            backendChanged = !s.VideoBackend.Equals(filter.VideoBackend, StringComparison.OrdinalIgnoreCase);
            s.VideoBackend = filter.VideoBackend;
            s.UpscalerEngine = filter.UpscalerEngine;
            s.UpscaleTargetHeight = filter.UpscaleTargetHeight;
            s.UpscaleMethod = filter.UpscaleMethod;
            s.UpscaleSharpen = filter.UpscaleSharpen;
            s.VideoEffects = ShaderCatalog.NormalizeEffects(filter.VideoEffects);

            ComboTags.Select(_view.BackendCombo, s.VideoBackend);
            ComboTags.Select(_view.UpscalerEngineCombo, s.UpscalerEngine);
            ComboTags.Select(_view.UpscaleTargetCombo, s.UpscaleTargetHeight.ToString());
            ComboTags.Select(_view.UpscaleMethodCombo, s.UpscaleMethod);
            ComboTags.Select(_view.UpscaleSharpenCombo, s.UpscaleSharpen);
            _upscaling.SyncCombos();
            _upscaling.SyncEffects();
        }

        if (persist)
        {
            s.ElyColorFilterId = filter.Id;
            _store.Save();
            Populate();
            _previewDirty = false;
        }

        if (backendChanged || forceBackendReset)
        {
            _backendHost.Recreate(replayCurrent: true);
            return;
        }

        if (filter.IncludeVideoPipeline) _upscaling.Apply();
        _pipeline.ApplyElyColor(filter);
        DebugConsole.Info("ELYCOLOR -> " + filter.Name);
    }

    public void RefreshOsdRow()
    {
        if (_view.OsdElyColorRow == null) return;
        _view.OsdElyColorRow.Visibility = _session.IsShowingMedia ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Leaving settings discards an unsaved slider preview.</summary>
    public void RevertPreviewIfDirty()
    {
        if (!_previewDirty) return;
        var active = ElyColorCatalog.Active(Settings);
        LoadEditor(active);
        _pipeline.ApplyElyColor(active);
        _previewDirty = false;
    }

    // ============ EDITOR ============
    public void OnCustomSelected()
    {
        if (_gate.IsLoading || _syncing) return;
        if (_view.ElyColorCustomSelectCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var filter = Settings.ElyColorCustomFilters.FirstOrDefault(f => f.Id == id);
        if (filter != null) LoadEditor(filter);
    }

    public void OnSliderChanged()
    {
        UpdateValueLabels();
        if (_gate.IsLoading || _syncing) return;
        _previewDirty = true;
        _pipeline.ApplyElyColor(ReadEditor("ELYCOLOR Preview", includePipeline: false));
    }

    public void Capture()
    {
        var current = ReadEditor("ELYCOLOR Preview", _view.ElyColorPipelineSwitch.IsChecked == true);
        var draft = new ElyColorFilter
        {
            Name = NextName(),
            IncludeVideoPipeline = true,
            VideoBackend = Settings.VideoBackend,
            UpscalerEngine = Settings.UpscalerEngine,
            UpscaleTargetHeight = Settings.UpscaleTargetHeight,
            UpscaleMethod = Settings.UpscaleMethod,
            UpscaleSharpen = Settings.UpscaleSharpen,
            VideoEffects = Settings.VideoEffects.ToList(),
            Saturation = current.Saturation,
            Brightness = current.Brightness,
            Contrast = current.Contrast,
            Gamma = current.Gamma,
            Hue = current.Hue
        };
        LoadEditor(draft);
        _view.ElyColorCustomSelectCombo.SelectedIndex = -1;
    }

    public void Save()
    {
        var existingId = (_view.ElyColorCustomSelectCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var filter = ReadEditor(_view.ElyColorNameBox.Text.Trim(), includePipeline: _view.ElyColorPipelineSwitch.IsChecked == true);
        if (!string.IsNullOrWhiteSpace(existingId)) filter.Id = existingId;

        var list = Settings.ElyColorCustomFilters ??= new();
        var index = list.FindIndex(f => f.Id == filter.Id);
        if (index >= 0) list[index] = filter; else list.Add(filter);

        Settings.ElyColorFilterId = filter.Id;
        _store.Save();
        Populate();
        ComboTags.Select(_view.ElyColorCustomSelectCombo, filter.Id);
        ApplyFilter(filter, persist: true, includePipeline: true);
    }

    public void Delete()
    {
        if (_view.ElyColorCustomSelectCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var list = Settings.ElyColorCustomFilters ??= new();
        list.RemoveAll(f => f.Id == id);
        if (Settings.ElyColorFilterId == id)
            Settings.ElyColorFilterId = "off";
        _store.Save();
        Populate();
        var active = ElyColorCatalog.Active(Settings);
        LoadEditor(active);
        ApplyFilter(active, persist: true, includePipeline: true);
    }

    private ElyColorFilter ReadEditor(string name, bool includePipeline)
    {
        if (string.IsNullOrWhiteSpace(name)) name = NextName();
        return new ElyColorFilter
        {
            Name = name,
            IncludeVideoPipeline = includePipeline,
            VideoBackend = ComboTags.Of(_view.ElyColorBackendCombo, Settings.VideoBackend),
            UpscalerEngine = ComboTags.Of(_view.ElyColorExternalUpscalerCombo, Settings.UpscalerEngine),
            UpscaleTargetHeight = int.TryParse(ComboTags.Of(_view.ElyColorTargetCombo, Settings.UpscaleTargetHeight.ToString()), out var h) ? h : 0,
            UpscaleMethod = ComboTags.Of(_view.ElyColorMethodCombo, Settings.UpscaleMethod),
            UpscaleSharpen = ComboTags.Of(_view.ElyColorSharpenCombo, Settings.UpscaleSharpen),
            // The editor has no effect switches: a pipeline profile records the
            // effects active in the upscaling settings.
            VideoEffects = Settings.VideoEffects.ToList(),
            Saturation = (int)Math.Round(_view.ElyColorSaturationSlider.Value),
            Brightness = (int)Math.Round(_view.ElyColorBrightnessSlider.Value),
            Contrast = (int)Math.Round(_view.ElyColorContrastSlider.Value),
            Gamma = (int)Math.Round(_view.ElyColorGammaSlider.Value),
            Hue = (int)Math.Round(_view.ElyColorHueSlider.Value)
        };
    }

    public void LoadEditor() => LoadEditor(ElyColorCatalog.Active(Settings));

    private void LoadEditor(ElyColorFilter filter)
    {
        _syncing = true;
        try
        {
            _view.ElyColorNameBox.Text = filter.Name == "ELYCOLOR Off" ? NextName() : LocalizationService.T(filter.Name);
            _view.ElyColorPipelineSwitch.IsChecked = filter.IncludeVideoPipeline;
            ComboTags.Select(_view.ElyColorBackendCombo, string.IsNullOrWhiteSpace(filter.VideoBackend) ? Settings.VideoBackend : filter.VideoBackend);
            ComboTags.Select(_view.ElyColorExternalUpscalerCombo, string.IsNullOrWhiteSpace(filter.UpscalerEngine) ? Settings.UpscalerEngine : filter.UpscalerEngine);
            ComboTags.Select(_view.ElyColorTargetCombo, filter.UpscaleTargetHeight.ToString());
            ComboTags.Select(_view.ElyColorMethodCombo, string.IsNullOrWhiteSpace(filter.UpscaleMethod) ? Settings.UpscaleMethod : filter.UpscaleMethod);
            ComboTags.Select(_view.ElyColorSharpenCombo, string.IsNullOrWhiteSpace(filter.UpscaleSharpen) ? Settings.UpscaleSharpen : filter.UpscaleSharpen);
            _view.ElyColorSaturationSlider.Value = filter.Saturation;
            _view.ElyColorBrightnessSlider.Value = filter.Brightness;
            _view.ElyColorContrastSlider.Value = filter.Contrast;
            _view.ElyColorGammaSlider.Value = filter.Gamma;
            _view.ElyColorHueSlider.Value = filter.Hue;
        }
        finally
        {
            _syncing = false;
        }
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        if (_view.ElyColorSaturationValue == null) return;
        _view.ElyColorSaturationValue.Text = Signed(_view.ElyColorSaturationSlider.Value);
        _view.ElyColorBrightnessValue.Text = Signed(_view.ElyColorBrightnessSlider.Value);
        _view.ElyColorContrastValue.Text = Signed(_view.ElyColorContrastSlider.Value);
        _view.ElyColorGammaValue.Text = Signed(_view.ElyColorGammaSlider.Value);
        _view.ElyColorHueValue.Text = Signed(_view.ElyColorHueSlider.Value);
    }

    private static string Signed(double value)
    {
        var n = (int)Math.Round(value);
        return n > 0 ? "+" + n : n.ToString(CultureInfo.InvariantCulture);
    }

    private static string NextName() => "ELYCOLOR Custom " + DateTime.Now.ToString("HHmm", CultureInfo.InvariantCulture);
}
