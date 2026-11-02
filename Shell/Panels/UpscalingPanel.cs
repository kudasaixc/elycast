using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;

namespace Elysium_Cast_IPTV.Shell.Panels;

/// <summary>Internal mpv upscaling settings and the OSD quick selector.</summary>
internal sealed class UpscalingPanel
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly VideoPipeline _pipeline;
    private readonly UiLoadGate _gate;
    private bool _syncing;

    public UpscalingPanel(MainWindow view, PlaybackSession session, IAppStateStore store, VideoPipeline pipeline, UiLoadGate gate)
    {
        _view = view;
        _gate = gate;
        _session = session;
        _store = store;
        _pipeline = pipeline;
    }

    private Settings Settings => _store.Settings;

    public void OnSettingChanged()
    {
        if (_gate.IsLoading || _syncing) return;
        var s = Settings;
        if (_view.UpscaleTargetCombo.SelectedItem is ComboBoxItem t && t.Tag is string th && int.TryParse(th, out var h))
            s.UpscaleTargetHeight = h;
        if (_view.UpscaleMethodCombo.SelectedItem is ComboBoxItem m && m.Tag is string mm)
            s.UpscaleMethod = mm;
        if (_view.UpscaleSharpenCombo.SelectedItem is ComboBoxItem sp && sp.Tag is string ss)
            s.UpscaleSharpen = ss;
        _store.Save();
        SyncCombos();
        Apply();
    }

    // OSD quick selector (under the seek bar) - drives the same method setting.
    public void OnOsdSelectionChanged()
    {
        if (_gate.IsLoading || _syncing) return;
        if (_view.OsdUpscaleCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string method) return;
        SetMethod(method);
    }

    public void SetMethod(string method)
    {
        Settings.UpscaleMethod = method;
        _store.Save();
        SyncCombos();
        Apply();
    }

    public void Apply() => _pipeline.ApplyUpscalingAsync().Forget("Upscaling");

    public void SyncCombos()
    {
        _syncing = true;
        ComboTags.Select(_view.UpscaleMethodCombo, Settings.UpscaleMethod);
        ComboTags.Select(_view.OsdUpscaleCombo, Settings.UpscaleMethod);
        _syncing = false;
    }

    // Fills the OSD quick-selector with only the modes the user enabled in settings.
    public void PopulateOsdCombo()
    {
        _syncing = true;
        _view.OsdUpscaleCombo.Items.Clear();
        var enabled = Settings.OsdUpscaleModes ?? new();
        foreach (var (id, label) in UpscaleCatalog.Modes)
            if (enabled.Contains(id))
                _view.OsdUpscaleCombo.Items.Add(new ComboBoxItem { Tag = id, Content = LocalizationService.T(label) });
        ComboTags.Select(_view.OsdUpscaleCombo, Settings.UpscaleMethod);
        _syncing = false;
        RefreshOsdRow();
    }

    // The OSD quick picker only makes sense while a stream is playing AND the user
    // has enabled at least one mode in settings - never on the idle screen.
    public void RefreshOsdRow()
    {
        var hasModes = _view.OsdUpscaleCombo.Items.Count > 0;
        _view.OsdUpscaleRow.Visibility = hasModes && _session.IsShowingMedia ? Visibility.Visible : Visibility.Collapsed;
    }

    // Builds the settings checkboxes that pick which modes show in the OSD bar.
    public void BuildOsdModeCheckboxes()
    {
        _view.OsdModesPanel.Children.Clear();
        var enabled = Settings.OsdUpscaleModes ?? new();
        foreach (var (id, label) in UpscaleCatalog.Modes)
        {
            var cb = new CheckBox
            {
                Content = LocalizationService.T(label),
                Tag = id,
                IsChecked = enabled.Contains(id),
                Margin = new Thickness(0, 0, 18, 8),
                MinWidth = 160,
            };
            cb.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            cb.Checked += OnOsdModeToggled;
            cb.Unchecked += OnOsdModeToggled;
            _view.OsdModesPanel.Children.Add(cb);
        }
    }

    private void OnOsdModeToggled(object sender, RoutedEventArgs e)
    {
        if (_gate.IsLoading) return;
        if (sender is not CheckBox cb || cb.Tag is not string id) return;
        var list = Settings.OsdUpscaleModes ??= new();
        if (cb.IsChecked == true) { if (!list.Contains(id)) list.Add(id); }
        else list.Remove(id);
        _store.Save();
        PopulateOsdCombo();
    }
}
