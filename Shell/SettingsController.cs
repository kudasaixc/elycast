using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.Shell.Panels;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// The settings panel: loading persisted values into every editor, general
/// switches, language, accent, audio-player options, backend choice and mpv
/// installation, reset, and panel/category navigation. Feature editors are
/// delegated to the <c>Shell/Panels</c> classes.
/// </summary>
internal sealed class SettingsController
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly UiLoadGate _gate;
    private readonly VideoPipeline _pipeline;
    private readonly PlaybackController _playback;
    private readonly AudioVisualizerController _visualizer;
    private readonly BackendHost _backendHost;
    private readonly CatalogController _catalog;
    private readonly MusicLibraryController _music;
    private readonly ConnectionController _connection;
    private readonly UpscalingPanel _upscaling;
    private readonly ElyColorPanel _elyColor;
    private readonly ElySoundPanel _elySound;
    private readonly ElyFlowPanel _elyFlow;
    private readonly MagpiePanel _magpie;
    private readonly MpvNativeInstaller _mpvInstaller;
    private readonly OsdController _osd;
    private readonly DispatcherTimer _audioSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _mpvInstallBusy;

    public SettingsController(MainWindow view, PlayerViewModel vm, PlaybackSession session, IAppStateStore store,
        UiLoadGate gate, VideoPipeline pipeline, PlaybackController playback, AudioVisualizerController visualizer,
        BackendHost backendHost, CatalogController catalog, MusicLibraryController music, ConnectionController connection,
        UpscalingPanel upscaling, ElyColorPanel elyColor, ElySoundPanel elySound, ElyFlowPanel elyFlow,
        MagpiePanel magpie, MpvNativeInstaller mpvInstaller, OsdController osd)
    {
        _osd = osd;
        _view = view;
        _vm = vm;
        _session = session;
        _store = store;
        _gate = gate;
        _pipeline = pipeline;
        _playback = playback;
        _visualizer = visualizer;
        _backendHost = backendHost;
        _catalog = catalog;
        _music = music;
        _connection = connection;
        _upscaling = upscaling;
        _elyColor = elyColor;
        _elySound = elySound;
        _elyFlow = elyFlow;
        _magpie = magpie;
        _mpvInstaller = mpvInstaller;

        _audioSaveTimer.Tick += (_, _) => { _audioSaveTimer.Stop(); _store.Save(); };
        _catalog.SettingsRequested += OpenPanel;
        _catalog.SettingsCloseRequested += ClosePanel;
        _magpie.SettingsRequested += OpenPanel;
    }

    private Settings Settings => _store.Settings;

    // ============ LOAD ============
    public void LoadIntoUi()
    {
        _gate.IsLoading = true;
        var s = Settings;
        ComboTags.Select(_view.LanguageCombo, s.Language);
        ComboTags.Select(_view.AudioBrowseCombo, s.AudioBrowseMode);
        ComboTags.Select(_view.AudioRendererCombo, s.AudioVisualizerRenderer);
        _view.AccentSwatches.ItemsSource = ThemeManager.Presets;
        _view.AccentHexBox.Text = s.AccentColor;
        _view.AudioBgSolid.IsChecked = s.AudioBackgroundMode == "solid";
        _view.AudioBgCover.IsChecked = s.AudioBackgroundMode == "cover";
        _view.AudioBgImage.IsChecked = s.AudioBackgroundMode == "image";
        ComboTags.Select(_view.AudioBackgroundCombo, s.AudioBackgroundImage);
        _view.AudioBlurSlider.Value = s.AudioBackgroundBlur;
        _view.AudioDimSlider.Value = s.AudioBackgroundDim * 100;
        _view.AudioSlowZoomSwitch.IsChecked = s.AudioBackgroundSlowZoom;
        _view.AudioSlowPanSwitch.IsChecked = s.AudioBackgroundSlowPan;
        _view.AudioParallaxSwitch.IsChecked = s.AudioBackgroundMouseParallax;
        _view.AudioParallaxIntensitySlider.Value = s.AudioBackgroundParallaxIntensity * 100;
        _view.AudioParallaxIntensitySlider.IsEnabled = s.AudioBackgroundMouseParallax;
        _view.AudioParallaxIntensityValue.Text = $"{s.AudioBackgroundParallaxIntensity * 100:0}%";
        _view.AudioAutoPaletteSwitch.IsChecked = s.AudioPaletteAutomatic;
        _view.AudioAdaptiveParticlesSwitch.IsChecked = s.AudioParticleAdaptiveColors;
        _view.AudioShakeSwitch.IsChecked = s.AudioVisualizerShake;
        _view.AudioVSyncSwitch.IsChecked = s.AudioVisualizerVSync;
        ComboTags.Select(_view.AudioFpsCombo, s.AudioVisualizerTargetFps.ToString());
        _view.AudioParticleCountSlider.Value = s.AudioParticleCount;
        _view.AudioParticleDistanceSlider.Value = s.AudioParticleDistance * 100;
        _view.AudioBackgroundCombo.IsEnabled = true;
        _view.StatsSwitch.IsChecked = s.ShowStats;
        _view.ReconnectSwitch.IsChecked = s.AutoReconnect;
        _view.RememberVolSwitch.IsChecked = s.RememberVolume;
        _view.ZapSwitch.IsChecked = s.ZapWithArrows;
        _view.ConfirmExitSwitch.IsChecked = s.ConfirmExit;
        _view.FormatTs.IsChecked = s.LiveStreamFormat != "m3u8";
        _view.FormatM3u.IsChecked = s.LiveStreamFormat == "m3u8";
        ComboTags.Select(_view.BackendCombo, s.VideoBackend);
        UpdateMpvStatus();
        ComboTags.Select(_view.UpscalerEngineCombo, s.UpscalerEngine);
        ComboTags.Select(_view.UpscaleTargetCombo, s.UpscaleTargetHeight.ToString());
        ComboTags.Select(_view.UpscaleMethodCombo, s.UpscaleMethod);
        ComboTags.Select(_view.UpscaleSharpenCombo, s.UpscaleSharpen);
        _upscaling.BuildOsdModeCheckboxes();
        _upscaling.BuildEffectCheckboxes();
        _upscaling.PopulateOsdCombo();
        _elyColor.Populate();
        _elySound.Populate();
        _elyFlow.Load();
        _elyColor.LoadEditor();
        _magpie.Load();
        // 0 is a legitimate persisted choice (start muted); only repair values
        // that pre-date validation and fall outside the slider range.
        if (s.DefaultVolume < 0 || s.DefaultVolume > 100) s.DefaultVolume = 75;
        _view.DefaultVolSlider.Value = s.DefaultVolume;
        _view.VolumeSlider.Value = s.DefaultVolume;
        _vm.IsStatsEnabled = s.ShowStats;
        _playback.ResetTrackChoices();
        _magpie.UpdateStatus();
        ShowCategory("appearance");
        _gate.IsLoading = false;
        _visualizer.ApplySettings();
        _visualizer.RefreshRendererStatus();
    }

    // ============ GENERAL ============
    public void OnLanguageChanged()
    {
        if (_gate.IsLoading || _view.LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        language = LocalizationService.Normalize(language);
        Settings.Language = language;
        LocalizationService.SetLanguage(language);
        _store.Save();

        // Only re-render texts. Re-running the onboarding preferences here used
        // to reconnect the auto-connect profile (or re-enter the local shell)
        // in the middle of playback.
        _connection.RefreshWelcome();
        _vm.RefreshLanguage();
        _catalog.ShowSection(_catalog.Current);
        _upscaling.BuildOsdModeCheckboxes();
        _upscaling.BuildEffectCheckboxes();
        _upscaling.PopulateOsdCombo();
        UpdateMpvStatus();
        _magpie.UpdateStatus();
        _elyFlow.UpdateGate();
        _visualizer.RefreshRendererStatus();
        _playback.UpdateStats();
        if (_session.Current != null)
            _vm.NowPlayingSubtitle = PlaybackText.NowPlayingSubtitle(_session.Current);
        _music.RefreshOpenPanel();
    }

    public void OnGeneralSettingChanged(object sender)
    {
        if (_gate.IsLoading) return;
        var s = Settings;
        s.AutoReconnect = _view.ReconnectSwitch.IsChecked == true;
        s.RememberVolume = _view.RememberVolSwitch.IsChecked == true;
        s.ZapWithArrows = _view.ZapSwitch.IsChecked == true;
        s.ConfirmExit = _view.ConfirmExitSwitch.IsChecked == true;
        s.LiveStreamFormat = _view.FormatM3u.IsChecked == true ? "m3u8" : "ts";
        if (ReferenceEquals(sender, _view.StatsSwitch)) _playback.SetStatsVisible(_view.StatsSwitch.IsChecked == true);
        _store.Save();
    }

    public void OnDefaultVolumeChanged()
    {
        if (_gate.IsLoading) return;
        Settings.DefaultVolume = (int)_view.DefaultVolSlider.Value;
        _store.Save();
    }

    public void ApplyAccent(string hex, bool updateBox)
    {
        ThemeManager.Apply(hex);
        if (updateBox) _view.AccentHexBox.Text = hex;
        Settings.AccentColor = hex;
        _store.Save();
    }

    public void OpenDataFolder()
    {
        try { Process.Start("explorer.exe", _store.FolderPath); }
        catch (Exception ex) { DebugConsole.Exception("Could not open the data folder", ex); }
    }

    public void Reset()
    {
        var oldBackend = Settings.VideoBackend;
        _store.Current.Settings = new Settings();
        LocalizationService.SetLanguage(Settings.Language);
        ThemeManager.Apply(Settings.AccentColor);
        LoadIntoUi();
        _store.Save();
        if (!string.Equals(oldBackend, Settings.VideoBackend, StringComparison.OrdinalIgnoreCase))
            _backendHost.Recreate(replayCurrent: true);
        else
            _pipeline.ApplyAll();
    }

    // ============ AUDIO PLAYER ============
    public void OnAudioVisualizerSettingChanged()
    {
        if (_gate.IsLoading || _view.AudioBgSolid == null) return;
        var s = Settings;
        s.AudioBackgroundMode = _view.AudioBgCover.IsChecked == true ? "cover" : _view.AudioBgImage.IsChecked == true ? "image" : "solid";
        _view.AudioBackgroundCombo.IsEnabled = true;
        s.AudioBackgroundImage = ComboTags.Of(_view.AudioBackgroundCombo, "sunset");
        s.AudioBackgroundBlur = _view.AudioBlurSlider.Value;
        s.AudioBackgroundDim = _view.AudioDimSlider.Value / 100.0;
        s.AudioBackgroundSlowZoom = _view.AudioSlowZoomSwitch.IsChecked == true;
        s.AudioBackgroundSlowPan = _view.AudioSlowPanSwitch.IsChecked == true;
        s.AudioBackgroundMouseParallax = _view.AudioParallaxSwitch.IsChecked == true;
        s.AudioBackgroundParallaxIntensity = _view.AudioParallaxIntensitySlider.Value / 100.0;
        _view.AudioParallaxIntensitySlider.IsEnabled = s.AudioBackgroundMouseParallax;
        _view.AudioParallaxIntensityValue.Text = $"{_view.AudioParallaxIntensitySlider.Value:0}%";
        s.AudioPaletteAutomatic = _view.AudioAutoPaletteSwitch.IsChecked == true;
        s.AudioParticleAdaptiveColors = _view.AudioAdaptiveParticlesSwitch.IsChecked == true;
        s.AudioVisualizerShake = _view.AudioShakeSwitch.IsChecked == true;
        s.AudioVisualizerVSync = _view.AudioVSyncSwitch.IsChecked == true;
        s.AudioVisualizerTargetFps = int.TryParse(ComboTags.Of(_view.AudioFpsCombo, "60"), out var fps) ? fps : 60;
        s.AudioParticleCount = (int)_view.AudioParticleCountSlider.Value;
        s.AudioParticleDistance = _view.AudioParticleDistanceSlider.Value / 100.0;
        // Apply live (cheap: pushes config to the native scene), but DON'T write to
        // disk on every tick - StateStore.Save() encrypts (DPAPI) and does an atomic
        // file write, which stalls the UI thread while dragging a slider and makes the
        // thumb stutter/jump. Coalesce the persist to fire once the drag settles.
        _visualizer.ApplySettings();
        _audioSaveTimer.Stop();
        _audioSaveTimer.Start();
    }

    public void OnAudioRendererChanged()
    {
        if (_gate.IsLoading || _view.AudioRendererCombo?.SelectedItem is not ComboBoxItem { Tag: string renderer }) return;
        Settings.AudioVisualizerRenderer = renderer;
        _store.Save();
        _visualizer.ApplyRendererSelection(showFeedback: true);
    }

    // ============ VIDEO BACKEND ============
    public void OnBackendChanged()
    {
        if (_gate.IsLoading) return;
        if (_view.BackendCombo.SelectedItem is not ComboBoxItem { Tag: string backend }) return;
        if (string.Equals(Settings.VideoBackend, backend, StringComparison.OrdinalIgnoreCase)) return;
        SelectBackend(backend);
    }

    /// <summary>Persists a backend choice and replaces the running backend.</summary>
    public void SelectBackend(string backend)
    {
        Settings.VideoBackend = backend;
        _store.Save();
        ComboTags.Select(_view.BackendCombo, backend);
        UpdateMpvStatus();
        _elyFlow.UpdateGate();
        _backendHost.Recreate(replayCurrent: true);
    }

    public void OnUpscalerEngineChanged()
    {
        if (_gate.IsLoading) return;
        _magpie.OnEngineChanged();
    }

    public async Task InstallMpvAsync()
    {
        if (_mpvInstallBusy) return;
        _mpvInstallBusy = true;
        _view.InstallMpvBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => _view.MpvStatusText.Text = LocalizationService.T(msg));
            var dll = await _mpvInstaller.InstallLatestAsync(progress);
            _view.MpvStatusText.Text = LocalizationService.T("mpv installed");
            DebugConsole.Success("libmpv installed: " + dll);

            Settings.VideoBackend = "mpv-gpu";
            ComboTags.Select(_view.BackendCombo, "mpv-gpu");
            _store.Save();
            _backendHost.Recreate(replayCurrent: true);
        }
        catch (Exception ex)
        {
            _view.MpvStatusText.Text = LocalizationService.T("installation failed");
            DebugConsole.Error("mpv: " + ex.Message);
        }
        finally
        {
            _view.InstallMpvBtn.IsEnabled = true;
            _mpvInstallBusy = false;
            UpdateMpvStatus();
        }
    }

    private void UpdateMpvStatus()
    {
        if (_view.MpvStatusText == null) return;
        var dll = _mpvInstaller.Locate();
        _view.MpvStatusText.Text = LocalizationService.T(string.IsNullOrWhiteSpace(dll) ? "mpv not installed" : "mpv installed");
        _view.InstallMpvBtn.Content = LocalizationService.T(string.IsNullOrWhiteSpace(dll) ? "Install mpv" : "Reinstall");
    }

    // ============ PANEL ============
    public void OpenPanel()
    {
        _view.SettingsPanel.Visibility = Visibility.Visible;
        _osd.OnPanelOpened(_view.SettingsPanel);
        ShowCategory("appearance");
        _magpie.UpdateStatus();
    }

    public void ShowCategory(string category)
    {
        if (_view.SettingsAppearancePanel == null) return;

        _view.SettingsAppearancePanel.Visibility = category == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsAudioPlayerPanel.Visibility = category == "audio-player" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsPlaybackPanel.Visibility = category == "playback" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsUpscalingPanel.Visibility = category == "upscaling" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsElyColorPanel.Visibility = category == "elycolor" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsElySoundPanel.Visibility = category == "elysound" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsElyFlowPanel.Visibility = category == "elyflow" ? Visibility.Visible : Visibility.Collapsed;
        _view.SettingsSystemPanel.Visibility = category == "system" ? Visibility.Visible : Visibility.Collapsed;

        MarkCategory(_view.SettingsAppearanceBtn, category == "appearance");
        MarkCategory(_view.SettingsAudioPlayerBtn, category == "audio-player");
        MarkCategory(_view.SettingsPlaybackBtn, category == "playback");
        MarkCategory(_view.SettingsUpscalingBtn, category == "upscaling");
        MarkCategory(_view.SettingsElyColorBtn, category == "elycolor");
        MarkCategory(_view.SettingsElySoundBtn, category == "elysound");
        MarkCategory(_view.SettingsElyFlowBtn, category == "elyflow");
        MarkCategory(_view.SettingsSystemBtn, category == "system");
    }

    private static void MarkCategory(Button button, bool selected)
    {
        button.Opacity = selected ? 1.0 : 0.62;
        button.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
    }

    public void ClosePanel()
    {
        _elyColor.RevertPreviewIfDirty();
        _view.SettingsPanel.Visibility = Visibility.Collapsed;
        _osd.UpdateCloseButton();
    }

    /// <summary>The ✕ button / Escape: closes every side panel.</summary>
    public void CloseAllPanels()
    {
        var wasSettings = _view.SettingsPanel.Visibility == Visibility.Visible;
        ClosePanel();
        _catalog.CloseSeriesPanel();
        _music.ClosePanel();
        _view.VideoStage.Cursor = Cursors.Arrow;
        // Restore the nav highlight to the section the user was on before opening
        // settings, without rebuilding the list.
        if (wasSettings) _catalog.SelectNav(_catalog.BeforeSettings);
    }
}
