using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;

namespace Elysium_Cast_IPTV.Shell.Panels;

/// <summary>
/// ELYSOUND+ preset/editor/OSD synchronisation. Slider edits are debounced so
/// the labelled filter receives runtime commands for the settled value
/// instead of one per pointer pixel. Topology and command policy stay in
/// <c>Services/Audio/ElySoundController</c>.
/// </summary>
internal sealed class ElySoundPanel
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly VideoPipeline _pipeline;
    private readonly UiLoadGate _gate;
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private ElySoundProfile? _pending;
    private bool _syncing;

    public ElySoundPanel(MainWindow view, PlaybackSession session, IAppStateStore store, VideoPipeline pipeline, UiLoadGate gate)
    {
        _view = view;
        _session = session;
        _store = store;
        _pipeline = pipeline;
        _gate = gate;
        _applyTimer.Tick += (_, _) => FlushPending();
    }

    private Settings Settings => _store.Settings;

    public void Populate()
    {
        _syncing = true;
        try
        {
            _view.ElySoundPresetCombo.Items.Clear();
            _view.OsdElySoundCombo.Items.Clear();
            foreach (var profile in ElySoundProfiles.All(Settings))
            {
                _view.ElySoundPresetCombo.Items.Add(new ComboBoxItem { Tag = profile.Id, Content = LocalizationService.T(profile.Name) });
                _view.OsdElySoundCombo.Items.Add(new ComboBoxItem { Tag = profile.Id, Content = LocalizationService.T(profile.Name) });
            }

            ComboTags.Select(_view.ElySoundPresetCombo, Settings.ElySoundPresetId);
            ComboTags.Select(_view.OsdElySoundCombo, Settings.ElySoundPresetId);
            _view.ElySoundEnabledSwitch.IsChecked = Settings.ElySoundEnabled;
            _view.OsdElySoundEnableSwitch.IsChecked = Settings.ElySoundEnabled;
            _view.ElySoundVirtualSwitch.IsChecked = Settings.ElySoundVirtualSurround;
            LoadEditor(ElySoundProfiles.Active(Settings));
        }
        finally
        {
            _syncing = false;
        }
        RefreshOsdRow();
    }

    /// <summary>Enable switch from settings or the OSD; both stay in sync.</summary>
    public void OnEnabledChanged(CheckBox source)
    {
        if (_gate.IsLoading || _syncing) return;
        CancelPending();
        SetEnabled(source.IsChecked == true);
    }

    public void SetEnabled(bool enabled)
    {
        Settings.ElySoundEnabled = enabled;
        _store.Save();
        _syncing = true;
        _view.ElySoundEnabledSwitch.IsChecked = enabled;
        _view.OsdElySoundEnableSwitch.IsChecked = enabled;
        _syncing = false;
        _pipeline.ApplyElySound();
        RefreshOsdRow();
    }

    public void OnVirtualSurroundChanged()
    {
        if (_gate.IsLoading || _syncing) return;
        CancelPending();
        Settings.ElySoundVirtualSurround = _view.ElySoundVirtualSwitch.IsChecked == true;
        _store.Save();
        _pipeline.ApplyElySound();
    }

    /// <summary>Preset chosen in the settings combo or the OSD combo.</summary>
    public void OnPresetSelected(ComboBox combo)
    {
        if (_gate.IsLoading || _syncing) return;
        CancelPending();
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        SelectPreset(id);
    }

    /// <summary>Selects a preset; returns false for an unknown id (console).</summary>
    public bool SelectPreset(string id)
    {
        if (!ElySoundProfiles.All(Settings).Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return false;
        Settings.ElySoundPresetId = id;
        _store.Save();
        _syncing = true;
        ComboTags.Select(_view.ElySoundPresetCombo, id);
        ComboTags.Select(_view.OsdElySoundCombo, id);
        LoadEditor(ElySoundProfiles.Active(Settings));
        _syncing = false;
        _pipeline.ApplyElySound();
        return true;
    }

    public void OnSliderChanged()
    {
        if (_gate.IsLoading || _syncing || _view.ElySoundPresetCombo == null || _view.OsdElySoundCombo == null) return;
        UpdateValueLabels();
        var custom = ReadEditor();
        Settings.ElySoundCustomProfile = custom;
        Settings.ElySoundPresetId = "custom";
        _syncing = true;
        ComboTags.Select(_view.ElySoundPresetCombo, "custom");
        ComboTags.Select(_view.OsdElySoundCombo, "custom");
        _syncing = false;
        // Recreating mpv's complete lavfi graph for every pointer pixel creates
        // audible gaps and synchronous disk writes. Apply the settled value.
        _pending = custom;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    public void FlushPending()
    {
        _applyTimer.Stop();
        var profile = _pending;
        _pending = null;
        if (profile == null) return;
        _store.Save();
        _pipeline.ApplyElySound(profile);
    }

    private void CancelPending()
    {
        _applyTimer.Stop();
        _pending = null;
    }

    private ElySoundProfile ReadEditor() => new()
    {
        Id = "custom",
        Name = "ELYSOUND+ Custom",
        Version = 2,
        Preamp = (int)Math.Round(_view.ElySoundPreampSlider.Value),
        Bass = (int)Math.Round(_view.ElySoundBassSlider.Value),
        LowMid = (int)Math.Round(_view.ElySoundLowMidSlider.Value),
        Mid = (int)Math.Round(_view.ElySoundMidSlider.Value),
        Presence = (int)Math.Round(_view.ElySoundPresenceSlider.Value),
        Treble = (int)Math.Round(_view.ElySoundTrebleSlider.Value),
        Clarity = (int)Math.Round(_view.ElySoundClaritySlider.Value),
        Width = (int)Math.Round(_view.ElySoundWidthSlider.Value),
        Compressor = (int)Math.Round(_view.ElySoundCompressorSlider.Value),
        LimiterCeilingDb = Math.Round(_view.ElySoundLimiterSlider.Value, 1)
    };

    private void LoadEditor(ElySoundProfile profile)
    {
        _view.ElySoundPreampSlider.Value = profile.Preamp;
        _view.ElySoundBassSlider.Value = profile.Bass;
        _view.ElySoundLowMidSlider.Value = profile.LowMid;
        _view.ElySoundMidSlider.Value = profile.Mid;
        _view.ElySoundPresenceSlider.Value = profile.Presence;
        _view.ElySoundTrebleSlider.Value = profile.Treble;
        _view.ElySoundClaritySlider.Value = profile.Clarity;
        _view.ElySoundWidthSlider.Value = profile.Width;
        _view.ElySoundCompressorSlider.Value = profile.Compressor;
        _view.ElySoundLimiterSlider.Value = profile.LimiterCeilingDb;
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        var v = _view;
        v.ElySoundPreampValue.Text = SignedDb(v.ElySoundPreampSlider.Value);
        v.ElySoundBassValue.Text = SignedDb(v.ElySoundBassSlider.Value);
        v.ElySoundLowMidValue.Text = SignedDb(v.ElySoundLowMidSlider.Value);
        v.ElySoundMidValue.Text = SignedDb(v.ElySoundMidSlider.Value);
        v.ElySoundPresenceValue.Text = SignedDb(v.ElySoundPresenceSlider.Value);
        v.ElySoundTrebleValue.Text = SignedDb(v.ElySoundTrebleSlider.Value);
        v.ElySoundClarityValue.Text = SignedDb(v.ElySoundClaritySlider.Value);
        v.ElySoundWidthValue.Text = ((int)Math.Round(v.ElySoundWidthSlider.Value)).ToString(CultureInfo.InvariantCulture) + "%";
        v.ElySoundCompressorValue.Text = ((int)Math.Round(v.ElySoundCompressorSlider.Value)).ToString(CultureInfo.InvariantCulture) + "%";
        v.ElySoundLimiterValue.Text = v.ElySoundLimiterSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS";
    }

    public void RefreshOsdRow()
    {
        if (_view.OsdElySoundRow == null) return;
        _view.OsdElySoundRow.Visibility = _session.IsShowingMedia ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SignedDb(double value)
    {
        var n = (int)Math.Round(value);
        return (n > 0 ? "+" : "") + n.ToString(CultureInfo.InvariantCulture) + " dB";
    }
}
