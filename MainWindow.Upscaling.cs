using System.Windows;
using System.Windows.Controls;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{

    // ============ UPSCALING (internal mpv) ============
    private bool _syncingUpscale;
    private bool _syncingElyColor;
    private bool _syncingElySound;
    private bool _elyColorPreviewDirty;

    private void UpscaleSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingUpscale) return;
        var s = StateStore.Settings;
        if (UpscaleTargetCombo.SelectedItem is ComboBoxItem t && t.Tag is string th && int.TryParse(th, out var h))
            s.UpscaleTargetHeight = h;
        if (UpscaleMethodCombo.SelectedItem is ComboBoxItem m && m.Tag is string mm)
            s.UpscaleMethod = mm;
        if (UpscaleSharpenCombo.SelectedItem is ComboBoxItem sp && sp.Tag is string ss)
            s.UpscaleSharpen = ss;
        StateStore.Save();
        SyncUpscaleCombos();
        ApplyUpscalingToBackend();
    }

    // OSD quick selector (under the seek bar) - drives the same method setting.
    private void OsdUpscale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingUpscale) return;
        if (OsdUpscaleCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string method) return;
        StateStore.Settings.UpscaleMethod = method;
        StateStore.Save();
        SyncUpscaleCombos();
        ApplyUpscalingToBackend();
    }

    private void SyncUpscaleCombos()
    {
        _syncingUpscale = true;
        SelectComboItemByTag(UpscaleMethodCombo, StateStore.Settings.UpscaleMethod);
        SelectComboItemByTag(OsdUpscaleCombo, StateStore.Settings.UpscaleMethod);
        _syncingUpscale = false;
    }

    // Master list of every upscaling mode (id + display label).
    private static readonly (string Id, string Label)[] UpscaleCatalog =
    {
        ("none", "None"),
        ("anime4k-hq", "Anime4K: Quality"),
        ("anime4k-fast", "Anime4K: Fast"),
        ("anime4k-denoise", "Anime4K: Denoise"),
        ("anime4k-deblur", "Anime4K: Deblur"),
        ("fsrcnnx", "FSRCNNX"),
        ("fsrcnnx-hq", "FSRCNNX HQ"),
        ("fsr", "AMD FSR + CAS"),
        ("nvscaler", "NVIDIA Image Scaling"),
        ("ewa_lanczossharp", "EWA Lanczos (sharp)"),
        ("lanczos", "Lanczos"),
        ("spline36", "Spline36"),
        ("mitchell", "Mitchell"),
        ("catmull_rom", "Catmull-Rom"),
        ("bilinear", "Bilinear"),
    };

    // Fills the OSD quick-selector with only the modes the user enabled in settings.
    private void PopulateOsdUpscaleCombo()
    {
        _syncingUpscale = true;
        OsdUpscaleCombo.Items.Clear();
        var enabled = StateStore.Settings.OsdUpscaleModes ?? new();
        foreach (var (id, label) in UpscaleCatalog)
            if (enabled.Contains(id))
                OsdUpscaleCombo.Items.Add(new ComboBoxItem { Tag = id, Content = LocalizationService.T(label) });
        SelectComboItemByTag(OsdUpscaleCombo, StateStore.Settings.UpscaleMethod);
        _syncingUpscale = false;
        RefreshOsdUpscaleRow();
    }

    // The OSD quick picker only makes sense while a stream is playing AND the user
    // has enabled at least one mode in settings - never on the idle screen.
    private void RefreshOsdUpscaleRow()
    {
        bool hasModes = OsdUpscaleCombo.Items.Count > 0;
        bool playing = _current != null && _videoBackend?.HasMedia == true;
        OsdUpscaleRow.Visibility = (hasModes && playing) ? Visibility.Visible : Visibility.Collapsed;
    }

    // Builds the settings checkboxes that pick which modes show in the OSD bar.
    private void BuildOsdModesCheckboxes()
    {
        OsdModesPanel.Children.Clear();
        var enabled = StateStore.Settings.OsdUpscaleModes ?? new();
        foreach (var (id, label) in UpscaleCatalog)
        {
            var cb = new CheckBox
            {
                Content = LocalizationService.T(label),
                Tag = id,
                IsChecked = enabled.Contains(id),
                Margin = new Thickness(0, 0, 18, 8),
                MinWidth = 160,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };
            cb.Checked += OsdMode_Toggled;
            cb.Unchecked += OsdMode_Toggled;
            OsdModesPanel.Children.Add(cb);
        }
    }

    private void OsdMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not CheckBox cb || cb.Tag is not string id) return;
        var list = StateStore.Settings.OsdUpscaleModes ??= new();
        if (cb.IsChecked == true) { if (!list.Contains(id)) list.Add(id); }
        else list.Remove(id);
        StateStore.Save();
        PopulateOsdUpscaleCombo();
    }

    private readonly ShaderInstaller _shaderInstaller = new();

    // The GLSL chains (FSR, NVScaler, FSRCNNX, Anime4K + their CAS / NVSharpen
    // NVSharpen) are downloaded on demand before being applied - otherwise the
    // backend would silently skip missing files and the mode would do nothing.
    private async void ApplyUpscalingToBackend()
    {
        if (_videoBackend is not MpvHwndBackend) return;
        var s = StateStore.Settings;

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
        if (_videoBackend is MpvHwndBackend mpv)
        {
            mpv.ApplyUpscaling(s.UpscaleTargetHeight, s.UpscaleMethod, s.UpscaleSharpen);
            DebugConsole.Info($"Upscaling: target={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")}, method={s.UpscaleMethod}, sharpness={s.UpscaleSharpen}");
        }
    }
}
