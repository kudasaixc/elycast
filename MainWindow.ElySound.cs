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

    // ============ ELYSOUND+ ============
    private IEnumerable<ElySoundProfile> AllElySoundProfiles()
    {
        foreach (var p in ElySoundCatalog.BuiltIn) yield return p;
        yield return StateStore.Settings.ElySoundCustomProfile ?? ElySoundProfile.DefaultCustom();
    }

    private ElySoundProfile ActiveElySoundProfile()
    {
        var s = StateStore.Settings;
        if (string.Equals(s.ElySoundPresetId, "custom", StringComparison.OrdinalIgnoreCase))
            return s.ElySoundCustomProfile ??= ElySoundProfile.DefaultCustom();
        return ElySoundCatalog.BuiltIn.FirstOrDefault(p => p.Id.Equals(s.ElySoundPresetId, StringComparison.OrdinalIgnoreCase))
               ?? ElySoundCatalog.BuiltIn[0];
    }

    private void PopulateElySoundCombos()
    {
        _syncingElySound = true;
        try
        {
            ElySoundPresetCombo.Items.Clear();
            OsdElySoundCombo.Items.Clear();
            foreach (var profile in AllElySoundProfiles())
            {
                ElySoundPresetCombo.Items.Add(new ComboBoxItem { Tag = profile.Id, Content = profile.Name });
                OsdElySoundCombo.Items.Add(new ComboBoxItem { Tag = profile.Id, Content = profile.Name });
            }

            SelectComboItemByTag(ElySoundPresetCombo, StateStore.Settings.ElySoundPresetId);
            SelectComboItemByTag(OsdElySoundCombo, StateStore.Settings.ElySoundPresetId);
            ElySoundEnabledSwitch.IsChecked = StateStore.Settings.ElySoundEnabled;
            OsdElySoundEnableSwitch.IsChecked = StateStore.Settings.ElySoundEnabled;
            ElySoundVirtualSwitch.IsChecked = StateStore.Settings.ElySoundVirtualSurround;
            LoadElySoundEditor(ActiveElySoundProfile());
        }
        finally
        {
            _syncingElySound = false;
        }
        RefreshOsdElySoundRow();
    }

    private void ElySoundEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingElySound) return;
        CancelPendingElySound();
        var enabled = sender switch
        {
            CheckBox cb when ReferenceEquals(cb, OsdElySoundEnableSwitch) => cb.IsChecked == true,
            CheckBox cb => cb.IsChecked == true,
            _ => StateStore.Settings.ElySoundEnabled
        };

        StateStore.Settings.ElySoundEnabled = enabled;
        StateStore.Save();
        _syncingElySound = true;
        ElySoundEnabledSwitch.IsChecked = enabled;
        OsdElySoundEnableSwitch.IsChecked = enabled;
        _syncingElySound = false;
        ApplyElySoundToBackend();
        RefreshOsdElySoundRow();
    }

    private void ElySoundSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingElySound) return;
        CancelPendingElySound();
        StateStore.Settings.ElySoundVirtualSurround = ElySoundVirtualSwitch.IsChecked == true;
        StateStore.Save();
        ApplyElySoundToBackend();
    }

    private void ElySoundPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingElySound) return;
        CancelPendingElySound();
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        StateStore.Settings.ElySoundPresetId = id;
        StateStore.Save();
        _syncingElySound = true;
        SelectComboItemByTag(ElySoundPresetCombo, id);
        SelectComboItemByTag(OsdElySoundCombo, id);
        LoadElySoundEditor(ActiveElySoundProfile());
        _syncingElySound = false;
        ApplyElySoundToBackend();
    }

    private void OsdElySound_Changed(object sender, SelectionChangedEventArgs e) => ElySoundPreset_Changed(sender, e);

    private void ElySoundSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _syncingElySound || ElySoundPresetCombo == null || OsdElySoundCombo == null) return;
        UpdateElySoundValueLabels();
        var custom = ReadElySoundEditor();
        StateStore.Settings.ElySoundCustomProfile = custom;
        StateStore.Settings.ElySoundPresetId = "custom";
        _syncingElySound = true;
        SelectComboItemByTag(ElySoundPresetCombo, "custom");
        SelectComboItemByTag(OsdElySoundCombo, "custom");
        _syncingElySound = false;
        // Recreating mpv's complete lavfi graph for every pointer pixel creates
        // audible gaps and synchronous disk writes. Apply the settled value.
        _pendingElySoundProfile = custom;
        _elySoundApplyTimer.Stop();
        _elySoundApplyTimer.Start();
    }

    private ElySoundProfile? _pendingElySoundProfile;

    private void FlushPendingElySound()
    {
        _elySoundApplyTimer.Stop();
        var profile = _pendingElySoundProfile;
        _pendingElySoundProfile = null;
        if (profile == null) return;
        StateStore.Save();
        ApplyElySoundToBackend(profile);
    }

    private void CancelPendingElySound()
    {
        _elySoundApplyTimer.Stop();
        _pendingElySoundProfile = null;
    }

    private ElySoundProfile ReadElySoundEditor() => new()
    {
        Id = "custom",
        Name = "ELYSOUND+ Custom",
        Version = 2,
        Preamp = (int)Math.Round(ElySoundPreampSlider.Value),
        Bass = (int)Math.Round(ElySoundBassSlider.Value),
        LowMid = (int)Math.Round(ElySoundLowMidSlider.Value),
        Mid = (int)Math.Round(ElySoundMidSlider.Value),
        Presence = (int)Math.Round(ElySoundPresenceSlider.Value),
        Treble = (int)Math.Round(ElySoundTrebleSlider.Value),
        Clarity = (int)Math.Round(ElySoundClaritySlider.Value),
        Width = (int)Math.Round(ElySoundWidthSlider.Value),
        Compressor = (int)Math.Round(ElySoundCompressorSlider.Value),
        LimiterCeilingDb = Math.Round(ElySoundLimiterSlider.Value, 1)
    };

    private void LoadElySoundEditor(ElySoundProfile profile)
    {
        ElySoundPreampSlider.Value = profile.Preamp;
        ElySoundBassSlider.Value = profile.Bass;
        ElySoundLowMidSlider.Value = profile.LowMid;
        ElySoundMidSlider.Value = profile.Mid;
        ElySoundPresenceSlider.Value = profile.Presence;
        ElySoundTrebleSlider.Value = profile.Treble;
        ElySoundClaritySlider.Value = profile.Clarity;
        ElySoundWidthSlider.Value = profile.Width;
        ElySoundCompressorSlider.Value = profile.Compressor;
        ElySoundLimiterSlider.Value = profile.LimiterCeilingDb;
        UpdateElySoundValueLabels();
    }

    private void UpdateElySoundValueLabels()
    {
        if (ElySoundPreampValue == null || ElySoundLimiterValue == null ||
            ElySoundPreampSlider == null || ElySoundLimiterSlider == null) return;
        ElySoundPreampValue.Text = SignedDb(ElySoundPreampSlider.Value);
        if (ElySoundBassValue != null && ElySoundBassSlider != null) ElySoundBassValue.Text = SignedDb(ElySoundBassSlider.Value);
        if (ElySoundLowMidValue != null && ElySoundLowMidSlider != null) ElySoundLowMidValue.Text = SignedDb(ElySoundLowMidSlider.Value);
        if (ElySoundMidValue != null && ElySoundMidSlider != null) ElySoundMidValue.Text = SignedDb(ElySoundMidSlider.Value);
        if (ElySoundPresenceValue != null && ElySoundPresenceSlider != null) ElySoundPresenceValue.Text = SignedDb(ElySoundPresenceSlider.Value);
        if (ElySoundTrebleValue != null && ElySoundTrebleSlider != null) ElySoundTrebleValue.Text = SignedDb(ElySoundTrebleSlider.Value);
        if (ElySoundClarityValue != null && ElySoundClaritySlider != null) ElySoundClarityValue.Text = SignedDb(ElySoundClaritySlider.Value);
        if (ElySoundWidthValue != null && ElySoundWidthSlider != null) ElySoundWidthValue.Text = ((int)Math.Round(ElySoundWidthSlider.Value)).ToString(CultureInfo.InvariantCulture) + "%";
        if (ElySoundCompressorValue != null && ElySoundCompressorSlider != null) ElySoundCompressorValue.Text = ((int)Math.Round(ElySoundCompressorSlider.Value)).ToString(CultureInfo.InvariantCulture) + "%";
        ElySoundLimiterValue.Text = ElySoundLimiterSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS";
    }

    private void ApplyElySoundToBackend() => ApplyElySoundToBackend(ActiveElySoundProfile());

    private void ApplyElySoundToBackend(ElySoundProfile profile)
    {
        if (_videoBackend is not IElySoundBackend dsp) return;
        var enabled = StateStore.Settings.ElySoundEnabled;
        var result = dsp.ApplyElySound(profile, enabled, StateStore.Settings.ElySoundVirtualSurround);
        if (result.Pending)
            DebugConsole.Info("ELYSOUND+ -> en attente des paramètres audio de la piste.");
        else if (result.Applied)
            DebugConsole.Info("ELYSOUND+ -> " + profile.Name + " | " + result.Message + " | " + result.Graph);
        else if (enabled)
            DebugConsole.Warn("ELYSOUND+ -> " + result.Message);
        else
            DebugConsole.Info("ELYSOUND+ -> off (seul @elysound a été retiré)");
    }

    private void RefreshOsdElySoundRow()
    {
        if (OsdElySoundRow == null) return;
        var playing = _current != null && _videoBackend?.HasMedia == true;
        OsdElySoundRow.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SignedDb(double value)
    {
        var n = (int)Math.Round(value);
        return (n > 0 ? "+" : "") + n.ToString(CultureInfo.InvariantCulture) + " dB";
    }
}
