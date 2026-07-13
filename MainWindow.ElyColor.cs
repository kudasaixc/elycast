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

    // ============ ELYCOLOR ============
    // mpv equalizer values, range -100..100. Guiding principles: brightness is
    // a black-floor offset (washes the image - avoid, prefer gamma), contrast
    // above ~+18 combined with negative gamma crushes shadow detail, hue
    // shifts tint skin very fast (±2 max on live-action), and every look must
    // stay watchable over a full film, not just on a demo scene.
    private static readonly ElyColorFilter[] BuiltInElyColorFilters =
    {
        new()
        {
            Id = "off",
            Name = "ELYCOLOR Off",
            IncludeVideoPipeline = false
        },
        new()
        {
            // Flat anime masters take saturation well, but +28 bled the cel
            // aplats; the hue shift tinted skin - gone. Slight gamma lift
            // keeps line-art detail in dark scenes.
            Id = "elycolor-anime",
            Name = "ELYCOLOR Vivid Anime",
            IncludeVideoPipeline = false,
            Saturation = 22,
            Brightness = 0,
            Contrast = 10,
            Gamma = -2
        },
        new()
        {
            // Subtle pop for live-action: skin must stay natural, so about
            // half the anime boost, and no black-floor lift.
            Id = "elycolor-film",
            Name = "ELYCOLOR Vivid Film",
            IncludeVideoPipeline = false,
            Saturation = 12,
            Brightness = 0,
            Contrast = 8,
            Gamma = -2
        },
        new()
        {
            // Cold desaturated dread. The old contrast 25 / gamma -12 /
            // brightness -7 trio crushed everything the genre lives on -
            // horror needs *readable* shadows to be scary.
            Id = "elycolor-horror",
            Name = "ELYCOLOR Horror",
            IncludeVideoPipeline = false,
            Saturation = -22,
            Brightness = -3,
            Contrast = 15,
            Gamma = -6,
            Hue = -5
        },
        new()
        {
            // Punchy grass and kits without neon turf; brightness lift
            // removed (washed the pitch on day games).
            Id = "elycolor-sport",
            Name = "ELYCOLOR Sport Live",
            IncludeVideoPipeline = false,
            Saturation = 15,
            Brightness = 0,
            Contrast = 8,
            Gamma = 0
        },
        new()
        {
            // True black & white: silver-print look = strong contrast but
            // neutral floor, and a light gamma dip for dense blacks that
            // still hold texture.
            Id = "elycolor-noir",
            Name = "ELYCOLOR Deep Noir",
            IncludeVideoPipeline = false,
            Saturation = -100,
            Brightness = 0,
            Contrast = 22,
            Gamma = -4
        },
        new()
        {
            // Low-fatigue evening look: softened contrast, raised gamma to
            // open shadows, near-neutral colour.
            Id = "elycolor-soft",
            Name = "ELYCOLOR Soft Comfort",
            IncludeVideoPipeline = false,
            Saturation = 3,
            Brightness = 0,
            Contrast = -6,
            Gamma = 6
        }
    };

    private IEnumerable<ElyColorFilter> AllElyColorFilters() =>
        BuiltInElyColorFilters.Concat(StateStore.Settings.ElyColorCustomFilters ?? new());

    private ElyColorFilter ActiveElyColorFilter() =>
        AllElyColorFilters().FirstOrDefault(f => f.Id.Equals(StateStore.Settings.ElyColorFilterId, StringComparison.OrdinalIgnoreCase))
        ?? BuiltInElyColorFilters[0];

    private void PopulateElyColorCombos()
    {
        _syncingElyColor = true;
        try
        {
            var active = ActiveElyColorFilter().Id;
            FillElyColorCombo(ElyColorFilterCombo, active);
            FillElyColorCombo(OsdElyColorCombo, active);

            ElyColorCustomSelectCombo.Items.Clear();
            foreach (var filter in StateStore.Settings.ElyColorCustomFilters ?? new())
                ElyColorCustomSelectCombo.Items.Add(new ComboBoxItem { Tag = filter.Id, Content = LocalizationService.T(filter.Name) });
        }
        finally
        {
            _syncingElyColor = false;
        }
        RefreshOsdElyColorRow();
    }

    private void FillElyColorCombo(ComboBox combo, string selectedId)
    {
        combo.Items.Clear();
        foreach (var filter in AllElyColorFilters())
            combo.Items.Add(new ComboBoxItem { Tag = filter.Id, Content = LocalizationService.T(filter.Name) });
        SelectComboItemByTag(combo, selectedId);
    }

    private void ElyColorFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingElyColor) return;
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var filter = AllElyColorFilters().FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (filter == null) return;
        LoadElyColorEditor(filter);
        ApplyElyColorFilter(filter, persist: true, includePipeline: true);
    }

    private void OsdElyColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingElyColor) return;
        if (OsdElyColorCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var filter = AllElyColorFilters().FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (filter == null) return;
        // No forced backend reset here: the colour properties apply live, and
        // ApplyElyColorFilter already recreates the backend when a custom
        // filter genuinely switches it. Rebuilding mpv for a colour change
        // interrupted playback for nothing.
        LoadElyColorEditor(filter);
        ApplyElyColorFilter(filter, persist: true, includePipeline: true);
    }

    private void ApplyElyColorFilter(ElyColorFilter filter, bool persist, bool includePipeline, bool forceBackendReset = false)
    {
        var s = StateStore.Settings;
        var backendChanged = false;

        if (includePipeline && filter.IncludeVideoPipeline)
        {
            backendChanged = !s.VideoBackend.Equals(filter.VideoBackend, StringComparison.OrdinalIgnoreCase);
            s.VideoBackend = filter.VideoBackend;
            s.UpscalerEngine = filter.UpscalerEngine;
            s.UpscaleTargetHeight = filter.UpscaleTargetHeight;
            s.UpscaleMethod = filter.UpscaleMethod;
            s.UpscaleSharpen = filter.UpscaleSharpen;

            SelectComboItemByTag(BackendCombo, s.VideoBackend);
            SelectComboItemByTag(UpscalerEngineCombo, s.UpscalerEngine);
            SelectComboItemByTag(UpscaleTargetCombo, s.UpscaleTargetHeight.ToString());
            SelectComboItemByTag(UpscaleMethodCombo, s.UpscaleMethod);
            SelectComboItemByTag(UpscaleSharpenCombo, s.UpscaleSharpen);
            SyncUpscaleCombos();
        }

        if (persist)
        {
            s.ElyColorFilterId = filter.Id;
            StateStore.Save();
            PopulateElyColorCombos();
            _elyColorPreviewDirty = false;
        }

        if (backendChanged || forceBackendReset)
        {
            RecreateVideoBackend(replayCurrent: true);
            return;
        }

        if (filter.IncludeVideoPipeline) ApplyUpscalingToBackend();
        ApplyElyColorToBackend(filter);
        DebugConsole.Info("ELYCOLOR -> " + filter.Name);
    }

    private void ApplyElyColorToBackend() => ApplyElyColorToBackend(ActiveElyColorFilter());

    private void ApplyElyColorToBackend(ElyColorFilter filter)
    {
        if (_videoBackend is not MpvHwndBackend mpv) return;

        mpv.SetOption("saturation", filter.Saturation.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("brightness", filter.Brightness.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("contrast", filter.Contrast.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("gamma", filter.Gamma.ToString(CultureInfo.InvariantCulture));
        mpv.SetOption("hue", filter.Hue.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshOsdElyColorRow()
    {
        if (OsdElyColorRow == null) return;
        var playing = _current != null && _videoBackend?.HasMedia == true;
        OsdElyColorRow.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ElyColorCustom_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingElyColor) return;
        if (ElyColorCustomSelectCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var filter = StateStore.Settings.ElyColorCustomFilters.FirstOrDefault(f => f.Id == id);
        if (filter != null) LoadElyColorEditor(filter);
    }

    private void ElyColorSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateElyColorValueLabels();
        if (_initializing || _syncingElyColor) return;
        _elyColorPreviewDirty = true;
        ApplyElyColorToBackend(ReadElyColorEditor("ELYCOLOR Preview", includePipeline: false));
    }

    private void CaptureElyColor_Click(object sender, RoutedEventArgs e)
    {
        var current = ReadElyColorEditor("ELYCOLOR Preview", ElyColorPipelineSwitch.IsChecked == true);
        var draft = new ElyColorFilter
        {
            Name = NextElyColorName(),
            IncludeVideoPipeline = true,
            VideoBackend = StateStore.Settings.VideoBackend,
            UpscalerEngine = StateStore.Settings.UpscalerEngine,
            UpscaleTargetHeight = StateStore.Settings.UpscaleTargetHeight,
            UpscaleMethod = StateStore.Settings.UpscaleMethod,
            UpscaleSharpen = StateStore.Settings.UpscaleSharpen,
            Saturation = current.Saturation,
            Brightness = current.Brightness,
            Contrast = current.Contrast,
            Gamma = current.Gamma,
            Hue = current.Hue
        };
        LoadElyColorEditor(draft);
        ElyColorCustomSelectCombo.SelectedIndex = -1;
    }

    private void SaveElyColor_Click(object sender, RoutedEventArgs e)
    {
        var existingId = (ElyColorCustomSelectCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var filter = ReadElyColorEditor(ElyColorNameBox.Text.Trim(), includePipeline: ElyColorPipelineSwitch.IsChecked == true);
        if (!string.IsNullOrWhiteSpace(existingId)) filter.Id = existingId;

        var list = StateStore.Settings.ElyColorCustomFilters ??= new();
        var index = list.FindIndex(f => f.Id == filter.Id);
        if (index >= 0) list[index] = filter; else list.Add(filter);

        StateStore.Settings.ElyColorFilterId = filter.Id;
        StateStore.Save();
        PopulateElyColorCombos();
        SelectComboItemByTag(ElyColorCustomSelectCombo, filter.Id);
        ApplyElyColorFilter(filter, persist: true, includePipeline: true);
    }

    private void DeleteElyColor_Click(object sender, RoutedEventArgs e)
    {
        if (ElyColorCustomSelectCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        var list = StateStore.Settings.ElyColorCustomFilters ??= new();
        list.RemoveAll(f => f.Id == id);
        if (StateStore.Settings.ElyColorFilterId == id)
            StateStore.Settings.ElyColorFilterId = "off";
        StateStore.Save();
        PopulateElyColorCombos();
        LoadElyColorEditor(ActiveElyColorFilter());
        ApplyElyColorFilter(ActiveElyColorFilter(), persist: true, includePipeline: true);
    }

    private ElyColorFilter ReadElyColorEditor(string name, bool includePipeline)
    {
        if (string.IsNullOrWhiteSpace(name)) name = NextElyColorName();
        return new ElyColorFilter
        {
            Name = name,
            IncludeVideoPipeline = includePipeline,
            VideoBackend = TagOf(ElyColorBackendCombo, StateStore.Settings.VideoBackend),
            UpscalerEngine = TagOf(ElyColorExternalUpscalerCombo, StateStore.Settings.UpscalerEngine),
            UpscaleTargetHeight = int.TryParse(TagOf(ElyColorTargetCombo, StateStore.Settings.UpscaleTargetHeight.ToString()), out var h) ? h : 0,
            UpscaleMethod = TagOf(ElyColorMethodCombo, StateStore.Settings.UpscaleMethod),
            UpscaleSharpen = TagOf(ElyColorSharpenCombo, StateStore.Settings.UpscaleSharpen),
            Saturation = (int)Math.Round(ElyColorSaturationSlider.Value),
            Brightness = (int)Math.Round(ElyColorBrightnessSlider.Value),
            Contrast = (int)Math.Round(ElyColorContrastSlider.Value),
            Gamma = (int)Math.Round(ElyColorGammaSlider.Value),
            Hue = (int)Math.Round(ElyColorHueSlider.Value)
        };
    }

    private void LoadElyColorEditor(ElyColorFilter filter)
    {
        _syncingElyColor = true;
        try
        {
            ElyColorNameBox.Text = filter.Name == "ELYCOLOR Off" ? NextElyColorName() : LocalizationService.T(filter.Name);
            ElyColorPipelineSwitch.IsChecked = filter.IncludeVideoPipeline;
            SelectComboItemByTag(ElyColorBackendCombo, string.IsNullOrWhiteSpace(filter.VideoBackend) ? StateStore.Settings.VideoBackend : filter.VideoBackend);
            SelectComboItemByTag(ElyColorExternalUpscalerCombo, string.IsNullOrWhiteSpace(filter.UpscalerEngine) ? StateStore.Settings.UpscalerEngine : filter.UpscalerEngine);
            SelectComboItemByTag(ElyColorTargetCombo, filter.UpscaleTargetHeight.ToString());
            SelectComboItemByTag(ElyColorMethodCombo, string.IsNullOrWhiteSpace(filter.UpscaleMethod) ? StateStore.Settings.UpscaleMethod : filter.UpscaleMethod);
            SelectComboItemByTag(ElyColorSharpenCombo, string.IsNullOrWhiteSpace(filter.UpscaleSharpen) ? StateStore.Settings.UpscaleSharpen : filter.UpscaleSharpen);
            ElyColorSaturationSlider.Value = filter.Saturation;
            ElyColorBrightnessSlider.Value = filter.Brightness;
            ElyColorContrastSlider.Value = filter.Contrast;
            ElyColorGammaSlider.Value = filter.Gamma;
            ElyColorHueSlider.Value = filter.Hue;
        }
        finally
        {
            _syncingElyColor = false;
        }
        UpdateElyColorValueLabels();
    }

    private void UpdateElyColorValueLabels()
    {
        if (ElyColorSaturationValue == null) return;
        ElyColorSaturationValue.Text = Signed(ElyColorSaturationSlider.Value);
        ElyColorBrightnessValue.Text = Signed(ElyColorBrightnessSlider.Value);
        ElyColorContrastValue.Text = Signed(ElyColorContrastSlider.Value);
        ElyColorGammaValue.Text = Signed(ElyColorGammaSlider.Value);
        ElyColorHueValue.Text = Signed(ElyColorHueSlider.Value);
    }

    private static string Signed(double value)
    {
        var n = (int)Math.Round(value);
        return n > 0 ? "+" + n : n.ToString(CultureInfo.InvariantCulture);
    }

    private static string TagOf(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;

    private static string NextElyColorName() => "ELYCOLOR Custom " + DateTime.Now.ToString("HHmm", CultureInfo.InvariantCulture);
}
