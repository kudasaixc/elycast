using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Video;
using Microsoft.Win32;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{

    // ============ SETTINGS ============
    private void LoadSettingsIntoUi()
    {
        _initializing = true;
        var s = StateStore.Settings;
        SelectComboItemByTag(LanguageCombo, s.Language);
        SelectComboItemByTag(AudioBrowseCombo, s.AudioBrowseMode);
        SelectComboItemByTag(AudioRendererCombo, s.AudioVisualizerRenderer);
        AccentSwatches.ItemsSource = ThemeManager.Presets;
        AccentHexBox.Text = s.AccentColor;
        AudioBgSolid.IsChecked = s.AudioBackgroundMode == "solid";
        AudioBgCover.IsChecked = s.AudioBackgroundMode == "cover";
        AudioBgImage.IsChecked = s.AudioBackgroundMode == "image";
        SelectComboItemByTag(AudioBackgroundCombo, s.AudioBackgroundImage);
        AudioBlurSlider.Value = s.AudioBackgroundBlur;
        AudioDimSlider.Value = s.AudioBackgroundDim * 100;
        AudioSlowZoomSwitch.IsChecked = s.AudioBackgroundSlowZoom;
        AudioSlowPanSwitch.IsChecked = s.AudioBackgroundSlowPan;
        AudioParallaxSwitch.IsChecked = s.AudioBackgroundMouseParallax;
        AudioParallaxIntensitySlider.Value = s.AudioBackgroundParallaxIntensity * 100;
        AudioParallaxIntensitySlider.IsEnabled = s.AudioBackgroundMouseParallax;
        AudioParallaxIntensityValue.Text = $"{s.AudioBackgroundParallaxIntensity * 100:0}%";
        AudioAutoPaletteSwitch.IsChecked = s.AudioPaletteAutomatic;
        AudioAdaptiveParticlesSwitch.IsChecked = s.AudioParticleAdaptiveColors;
        AudioShakeSwitch.IsChecked = s.AudioVisualizerShake;
        AudioVSyncSwitch.IsChecked = s.AudioVisualizerVSync;
        SelectComboItemByTag(AudioFpsCombo, s.AudioVisualizerTargetFps.ToString());
        AudioParticleCountSlider.Value = s.AudioParticleCount;
        AudioParticleDistanceSlider.Value = s.AudioParticleDistance * 100;
        AudioBackgroundCombo.IsEnabled = true;
        ElySmartAutoSwitch.IsChecked = s.ElySmartAutoOptimizeDecorative;
        ElySmartNotificationsSwitch.IsChecked = s.ElySmartNotificationsEnabled;
        SelectComboItemByTag(ElySmartProfileCombo, s.ElySmartWorkload);
        StatsSwitch.IsChecked = s.ShowStats;
        ReconnectSwitch.IsChecked = s.AutoReconnect;
        RememberVolSwitch.IsChecked = s.RememberVolume;
        ZapSwitch.IsChecked = s.ZapWithArrows;
        ConfirmExitSwitch.IsChecked = s.ConfirmExit;
        FormatTs.IsChecked = s.LiveStreamFormat != "m3u8";
        FormatM3u.IsChecked = s.LiveStreamFormat == "m3u8";
        SelectComboItemByTag(BackendCombo, s.VideoBackend);
        UpdateMpvStatus();
        SelectComboItemByTag(UpscalerEngineCombo, s.UpscalerEngine);
        SelectComboItemByTag(UpscaleTargetCombo, s.UpscaleTargetHeight.ToString());
        SelectComboItemByTag(UpscaleMethodCombo, s.UpscaleMethod);
        SelectComboItemByTag(UpscaleSharpenCombo, s.UpscaleSharpen);
        BuildOsdModesCheckboxes();
        PopulateOsdUpscaleCombo();
        PopulateElyColorCombos();
        PopulateElySoundCombos();
        PopulateElyColorCombos();
        PopulateElySoundCombos();
        LoadElyFlowIntoUi();
        LoadElyColorEditor(ActiveElyColorFilter());
        var magpiePath = _magpie.Locate() ?? "";
        MagpiePathBox.Text = magpiePath;
        if (!string.Equals(s.MagpiePath, magpiePath, StringComparison.OrdinalIgnoreCase))
        {
            s.MagpiePath = magpiePath;
            StateStore.Save();
        }
        // 0 is a legitimate persisted choice (start muted); only repair values
        // that pre-date validation and fall outside the slider range.
        if (s.DefaultVolume < 0 || s.DefaultVolume > 100) s.DefaultVolume = 75;
        DefaultVolSlider.Value = s.DefaultVolume;
        BootSlider.Value = s.BootSeconds;
        VolumeSlider.Value = s.DefaultVolume;
        StatsOverlay.Visibility = s.ShowStats ? Visibility.Visible : Visibility.Collapsed;
        ResetSubtitleChoices();
        ResetAudioChoices();
        UpdateUpscalerStatus();
        ShowSettingsCategory("appearance");
        _initializing = false;
        ApplyAudioVisualizerSettings();
        RefreshAudioRendererStatus();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        language = LocalizationService.Normalize(language);
        StateStore.Settings.Language = language;
        LocalizationService.SetLanguage(language);
        StateStore.Save();

        ApplyOnboardingPreferences();
        ShowSection(_section);
        BuildOsdModesCheckboxes();
        PopulateOsdUpscaleCombo();
        UpdateMpvStatus();
        UpdateUpscalerStatus();
        UpdateElyFlowGate();
        RefreshAudioRendererStatus();
        UpdateStats();
        if (_elySmartReport != null) ShowElySmartReport(_elySmartReport);
        if (_current != null)
        {
            var kind = LocalizationService.T(_current.KindLabel);
            TopSubtitle.Text = string.IsNullOrWhiteSpace(_current.CategoryName) ? kind : $"{kind} · {_current.CategoryName}";
        }
        if (_openMusicGroup != null) OpenMusicGroup(_openMusicGroup);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var s = StateStore.Settings;
        s.AutoReconnect = ReconnectSwitch.IsChecked == true;
        s.RememberVolume = RememberVolSwitch.IsChecked == true;
        s.ZapWithArrows = ZapSwitch.IsChecked == true;
        s.ConfirmExit = ConfirmExitSwitch.IsChecked == true;
        s.LiveStreamFormat = FormatM3u.IsChecked == true ? "m3u8" : "ts";
        if (ReferenceEquals(sender, StatsSwitch)) SetStatsVisible(StatsSwitch.IsChecked == true);
        StateStore.Save();
    }

    private void AudioVisualizerSetting_Changed(object sender, RoutedEventArgs e) => SaveAudioVisualizerSettings();
    private void AudioVisualizerSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveAudioVisualizerSettings();

    private void AudioRenderer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || AudioRendererCombo?.SelectedItem is not ComboBoxItem { Tag: string renderer }) return;
        StateStore.Settings.AudioVisualizerRenderer = renderer;
        StateStore.Save();
        ApplyAudioRendererSelection(showFeedback: true);
    }

    private void SaveAudioVisualizerSettings()
    {
        if (_initializing || AudioBgSolid == null) return;
        var s = StateStore.Settings;
        s.AudioBackgroundMode = AudioBgCover.IsChecked == true ? "cover" : AudioBgImage.IsChecked == true ? "image" : "solid";
        AudioBackgroundCombo.IsEnabled = true;
        s.AudioBackgroundImage = TagOf(AudioBackgroundCombo, "sunset");
        s.AudioBackgroundBlur = AudioBlurSlider.Value;
        s.AudioBackgroundDim = AudioDimSlider.Value / 100.0;
        s.AudioBackgroundSlowZoom = AudioSlowZoomSwitch.IsChecked == true;
        s.AudioBackgroundSlowPan = AudioSlowPanSwitch.IsChecked == true;
        s.AudioBackgroundMouseParallax = AudioParallaxSwitch.IsChecked == true;
        s.AudioBackgroundParallaxIntensity = AudioParallaxIntensitySlider.Value / 100.0;
        AudioParallaxIntensitySlider.IsEnabled = s.AudioBackgroundMouseParallax;
        AudioParallaxIntensityValue.Text = $"{AudioParallaxIntensitySlider.Value:0}%";
        s.AudioPaletteAutomatic = AudioAutoPaletteSwitch.IsChecked == true;
        s.AudioParticleAdaptiveColors = AudioAdaptiveParticlesSwitch.IsChecked == true;
        s.AudioVisualizerShake = AudioShakeSwitch.IsChecked == true;
        s.AudioVisualizerVSync = AudioVSyncSwitch.IsChecked == true;
        s.AudioVisualizerTargetFps = int.TryParse(TagOf(AudioFpsCombo, "60"), out var fps) ? fps : 60;
        s.AudioParticleCount = (int)AudioParticleCountSlider.Value;
        s.AudioParticleDistance = AudioParticleDistanceSlider.Value / 100.0;
        // Apply live (cheap: pushes config to the native scene), but DON'T write to
        // disk on every tick - StateStore.Save() encrypts (DPAPI) and does an atomic
        // file write, which stalls the UI thread while dragging a slider and makes the
        // thumb stutter/jump. Coalesce the persist to fire once the drag settles.
        ApplyAudioVisualizerSettings();
        QueueAudioSettingsSave();
    }

    private readonly DispatcherTimer _audioSettingsSaveTimer =
        new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _audioSaveHooked;

    private void QueueAudioSettingsSave()
    {
        if (!_audioSaveHooked)
        {
            _audioSettingsSaveTimer.Tick += (_, _) => { _audioSettingsSaveTimer.Stop(); StateStore.Save(); };
            _audioSaveHooked = true;
        }
        _audioSettingsSaveTimer.Stop();
        _audioSettingsSaveTimer.Start();
    }

    private void UpscalerSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (UpscalerEngineCombo.SelectedItem is ComboBoxItem item && item.Tag is string engine)
        {
            StateStore.Settings.UpscalerEngine = engine;
            StateStore.Save();
            UpdateUpscalerStatus();
        }
    }

    private void BackendSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (BackendCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string backend) return;
        if (string.Equals(StateStore.Settings.VideoBackend, backend, StringComparison.OrdinalIgnoreCase)) return;

        StateStore.Settings.VideoBackend = backend;
        StateStore.Save();
        UpdateMpvStatus();
        UpdateElyFlowGate();
        RecreateVideoBackend(replayCurrent: true);
    }

    private async void InstallMpv_Click(object sender, RoutedEventArgs e)
    {
        if (_mpvInstallBusy) return;
        _mpvInstallBusy = true;
        InstallMpvBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => MpvStatusText.Text = LocalizationService.T(msg));
            var dll = await _mpvInstaller.InstallLatestAsync(progress);
            MpvStatusText.Text = LocalizationService.T("mpv installed");
            DebugConsole.Success("libmpv installed: " + dll);

            StateStore.Settings.VideoBackend = "mpv-gpu";
            SelectComboItemByTag(BackendCombo, "mpv-gpu");
            StateStore.Save();
            RecreateVideoBackend(replayCurrent: true);
        }
        catch (Exception ex)
        {
            MpvStatusText.Text = LocalizationService.T("installation failed");
            DebugConsole.Error("mpv: " + ex.Message);
        }
        finally
        {
            InstallMpvBtn.IsEnabled = true;
            _mpvInstallBusy = false;
            UpdateMpvStatus();
        }
    }

    private void UpdateMpvStatus()
    {
        if (MpvStatusText == null) return;
        var dll = _mpvInstaller.Locate();
        MpvStatusText.Text = LocalizationService.T(string.IsNullOrWhiteSpace(dll) ? "mpv not installed" : "mpv installed");
        InstallMpvBtn.Content = LocalizationService.T(string.IsNullOrWhiteSpace(dll) ? "Install mpv" : "Reinstall");
    }

    private void RecreateVideoBackend(bool replayCurrent)
    {
        DebugConsole.Step("Backend change requested: tearing down the old backend...");

        var current = _current;
        var hadMedia = _videoBackend?.HasMedia == true;
        var resumePosition = _videoBackend?.PositionMs ?? 0;
        var volume = (int)VolumeSlider.Value;

        var old = _videoBackend;
        // Mark the field null immediately so timers/callbacks firing during the
        // teardown no longer touch the backend we are about to destroy.
        _videoBackend = null;

        if (old != null)
        {
            // 1. Detach all managed event handlers from the OLD backend so late
            //    Playing/Failed/Ended/Paused events cannot reach the UI.
            try { DebugConsole.Step("Backend: detaching events..."); DetachBackendEvents(old); }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to detach events", ex); }

            // 2. Stop playback so no new native frames are decoded or rendered.
            try { DebugConsole.Step("Backend: stopping playback..."); old.Stop(PlaybackEndReason.Replaced); }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to stop playback", ex); }

            // 3. Dispose the OLD backend WHILE its video surface is still in the
            //    Visual Tree. The mpv render context (OpenGL) must be freed while
            //    its GL context is still alive - Dispose() internally detaches the
            //    native render callback before freeing libmpv.
            try { DebugConsole.Step("Backend: disposing the old backend..."); old.Dispose(); }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to dispose the old backend", ex); }

            // 4. Only now remove the surface from the Visual Tree. This triggers
            //    the GL/D3D context teardown, which is safe because no native
            //    handle still references it.
            try { DebugConsole.Step("Backend: removing the video surface from the visual tree..."); VideoHost.Content = null; }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to remove the video surface", ex); }
        }
        else
        {
            VideoHost.Content = null;
        }

        ResetSubtitleChoices();
        ResetAudioChoices();

        // 5. Wait one WPF Dispatcher cycle so the Unloaded handlers and native
        //    resource frees complete before a new backend is created.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                DebugConsole.Step("Backend: creating the new backend...");
                InitVideoBackend();
                if (_videoBackend != null) _videoBackend.Volume = volume;
                UpdateStats();
                DebugConsole.Success("Backend: new backend ready.");
            }
            catch (Exception ex)
            {
                DebugConsole.Exception("Backend: failed to create the new backend", ex);
                return;
            }

            if (!replayCurrent || !hadMedia || current == null) return;

            Play(current);
            if (resumePosition <= 2500 || current.Kind == PlayItemKind.Live) return;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_current == current && _videoBackend?.LengthMs > resumePosition)
                    _videoBackend.PositionMs = resumePosition;
            };
            timer.Start();
        }));
    }

    private void SliderSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        StateStore.Settings.DefaultVolume = (int)DefaultVolSlider.Value;
        StateStore.Settings.BootSeconds = Math.Round(BootSlider.Value, 1);
        StateStore.Save();
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string hex) return;
        ThemeManager.Apply(hex);
        AccentHexBox.Text = hex;
        StateStore.Settings.AccentColor = hex; StateStore.Save();
    }

    private void ApplyHex_Click(object sender, RoutedEventArgs e)
    {
        var hex = AccentHexBox.Text.Trim();
        ThemeManager.Apply(hex);
        StateStore.Settings.AccentColor = hex; StateStore.Save();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", StateStore.FolderPath); } catch { }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var oldBackend = StateStore.Settings.VideoBackend;
        StateStore.Current.Settings = new Settings();
        LocalizationService.SetLanguage(StateStore.Settings.Language);
        ThemeManager.Apply(StateStore.Settings.AccentColor);
        LoadSettingsIntoUi();
        StateStore.Save();
        if (!string.Equals(oldBackend, StateStore.Settings.VideoBackend, StringComparison.OrdinalIgnoreCase))
            RecreateVideoBackend(replayCurrent: true);
        else
        {
            ApplyUpscalingToBackend();
            ApplyElyColorToBackend();
            ApplyElySoundToBackend();
            ApplyElyFlowToBackend();
        }
    }

    private void OpenSettingsPanel()
    {
        SettingsPanel.Visibility = Visibility.Visible;
        CloseOverlayBtn.Visibility = Visibility.Visible;
        HideOsd(force: true);
        VideoStage.Cursor = Cursors.Arrow;
        FadeIn(SettingsPanel);
        ShowSettingsCategory("appearance");
        UpdateUpscalerStatus();
        UpdatePanelsVideo();
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string category })
            ShowSettingsCategory(category);
    }

    private void ShowSettingsCategory(string category)
    {
        if (SettingsAppearancePanel == null) return;

        SettingsAppearancePanel.Visibility = category == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAudioPlayerPanel.Visibility = category == "audio-player" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPlaybackPanel.Visibility = category == "playback" ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpscalingPanel.Visibility = category == "upscaling" ? Visibility.Visible : Visibility.Collapsed;
        SettingsElyColorPanel.Visibility = category == "elycolor" ? Visibility.Visible : Visibility.Collapsed;
        SettingsElySoundPanel.Visibility = category == "elysound" ? Visibility.Visible : Visibility.Collapsed;
        SettingsElyFlowPanel.Visibility = category == "elyflow" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSystemPanel.Visibility = category == "system" ? Visibility.Visible : Visibility.Collapsed;

        MarkSettingsCategory(SettingsAppearanceBtn, category == "appearance");
        MarkSettingsCategory(SettingsAudioPlayerBtn, category == "audio-player");
        MarkSettingsCategory(SettingsPlaybackBtn, category == "playback");
        MarkSettingsCategory(SettingsUpscalingBtn, category == "upscaling");
        MarkSettingsCategory(SettingsElyColorBtn, category == "elycolor");
        MarkSettingsCategory(SettingsElySoundBtn, category == "elysound");
        MarkSettingsCategory(SettingsElyFlowBtn, category == "elyflow");
        MarkSettingsCategory(SettingsSystemBtn, category == "system");
    }

    private void MarkSettingsCategory(Button button, bool selected)
    {
        button.Opacity = selected ? 1.0 : 0.62;
        button.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
    }

    private void CloseSettingsPanel()
    {
        if (_elyColorPreviewDirty)
        {
            var active = ActiveElyColorFilter();
            LoadElyColorEditor(active);
            ApplyElyColorToBackend(active);
            _elyColorPreviewDirty = false;
        }
        SettingsPanel.Visibility = Visibility.Collapsed;
        UpdateCloseOverlayButton();
        UpdatePanelsVideo();
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e)
    {
        var wasSettings = SettingsPanel.Visibility == Visibility.Visible;
        CloseSettingsPanel();
        CloseSeriesPanel();
        CloseMusicPanel();
        VideoStage.Cursor = Cursors.Arrow;
        // Restore the nav highlight to the section the user was on before opening
        // settings, without rebuilding the list (no reset).
        if (wasSettings) RestoreSectionNav();
    }

    // Re-check the nav button matching the current section without triggering a
    // reload (guarded by _suppressNav).
    private void RestoreSectionNav()
    {
        _suppressNav = true;
        switch (_sectionBeforeSettings)
        {
            case Section.Movies: NavMovies.IsChecked = true; break;
            case Section.Series: NavSeries.IsChecked = true; break;
            case Section.LocalAudio: NavLocalAudio.IsChecked = true; break;
            case Section.LocalVideo: NavLocalVideo.IsChecked = true; break;
            case Section.Fav: NavFav.IsChecked = true; break;
            default: NavLive.IsChecked = true; break;
        }
        _suppressNav = false;
    }

    private void UpdateCloseOverlayButton()
    {
        CloseOverlayBtn.Visibility = IsPanelOpen() ? Visibility.Visible : Visibility.Collapsed;
        if (IsPanelOpen()) VideoStage.Cursor = Cursors.Arrow;
    }

    private void BrowseMagpie_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Magpie.exe|Magpie.exe|Applications (*.exe)|*.exe",
            Title = LocalizationService.T("Choose Magpie.exe")
        };
        if (dlg.ShowDialog(this) != true) return;

        MagpiePathBox.Text = dlg.FileName;
        StateStore.Settings.MagpiePath = dlg.FileName;
        StateStore.Save();
        UpdateUpscalerStatus();
    }

    private async void InstallMagpie_Click(object sender, RoutedEventArgs e)
    {
        if (_upscalerBusy) return;
        _upscalerBusy = true;
        InstallMagpieBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => UpscalerStatusText.Text = LocalizationService.T(msg));
            var exe = await _magpie.InstallLatestAsync(progress);
            MagpiePathBox.Text = exe;
            UpdateUpscalerStatus();
            DebugConsole.Success("Magpie installed: " + exe);
        }
        catch (Exception ex)
        {
            UpscalerStatusText.Text = LocalizationService.T("Magpie installation failed");
            DebugConsole.Error("Magpie: " + ex.Message);
        }
        finally
        {
            InstallMagpieBtn.IsEnabled = true;
            _upscalerBusy = false;
        }
    }

    private async void ActivateUpscaler_Click(object sender, RoutedEventArgs e) => await ActivateExternalUpscalerAsync();
    private async void UpscalerBtn_Click(object sender, RoutedEventArgs e) => await ActivateExternalUpscalerAsync();

    private async Task ActivateExternalUpscalerAsync()
    {
        if (_upscalerBusy) return;
        if (StateStore.Settings.UpscalerEngine == "off")
        {
            StateStore.Settings.UpscalerEngine = "magpie-fsr";
            SelectComboItemByTag(UpscalerEngineCombo, "magpie-fsr");
            StateStore.Save();
        }

        var path = MagpiePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = _magpie.Locate() ?? "";
            MagpiePathBox.Text = path;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            UpscalerStatusText.Text = LocalizationService.T("Install Magpie first");
            OpenSettingsPanel();
            return;
        }

        _upscalerBusy = true;
        try
        {
            StateStore.Settings.MagpiePath = path;
            StateStore.Save();
            _magpie.ConfigureForEngine(StateStore.Settings.UpscalerEngine);
            if (!await _magpie.EnsureRunningAsync(path))
                throw new InvalidOperationException(LocalizationService.T("Magpie could not start."));

            // Let Magpie initialize its hidden window/tray state before sending the hotkey.
            await Task.Delay(250);
            var hwnd = MagpieUpscalerService.HwndFor(this);
            await _magpie.ActivateScalingAsync(hwnd, StateStore.Settings.MagpieHotkey);
            UpscalerStatusText.Text = _upscalerActive
                ? LocalizationService.Format("Stop requested ({0})", UpscalerEngineLabel())
                : LocalizationService.Format("Activation requested ({0})", UpscalerEngineLabel());
            await Task.Delay(900);
            RefreshMagpieActiveState();
            DebugConsole.Success("Toggle upscaling Magpie / " + UpscalerEngineLabel());
        }
        catch (Exception ex)
        {
            UpscalerStatusText.Text = LocalizationService.T("Magpie activation failed");
            DebugConsole.Error("Magpie: " + ex.Message);
        }
        finally
        {
            _upscalerBusy = false;
        }
    }

    private void UpdateUpscalerStatus()
    {
        if (UpscalerStatusText == null) return;
        var located = _magpie.Locate();
        if (!string.IsNullOrWhiteSpace(located) && string.IsNullOrWhiteSpace(MagpiePathBox.Text))
            MagpiePathBox.Text = located;

        RefreshMagpieActiveState(updateText: false);
        UpscalerStatusText.Text = StateStore.Settings.UpscalerEngine == "off"
            ? LocalizationService.T("External upscaling disabled")
            : _upscalerActive
                ? LocalizationService.Format("{0} active", UpscalerEngineLabel())
                : located != null
                    ? LocalizationService.Format("{0} ready", UpscalerEngineLabel())
                : LocalizationService.T("Magpie is required for GPU upscaling");
    }

    private void RefreshMagpieActiveState(bool updateText = true)
    {
        _upscalerActive = _magpie.FindScalingWindow() != 0;
        if (UpscalerBtn?.Content is TextBlock text)
            text.Text = _upscalerActive ? "ON" : "FSR";
        if (updateText && UpscalerStatusText != null && StateStore.Settings.UpscalerEngine != "off")
            UpscalerStatusText.Text = _upscalerActive
                ? LocalizationService.Format("{0} active", UpscalerEngineLabel())
                : LocalizationService.Format("{0} ready", UpscalerEngineLabel());
    }

    private void InitMagpieMessages()
    {
        var hwnd = MagpieUpscalerService.HwndFor(this);
        if (hwnd == 0) return;
        _magpieScalingChangedMessage = RegisterWindowMessage("MagpieScalingChanged");
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private bool _statsHiddenForMove;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Hook the window's OWN HWND so the maximize constraint always applies
        // (the Magpie hook may never attach if Magpie isn't present).
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(MaximizeHook);
    }

    private nint MaximizeHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO && TryConstrainMaximizedBounds(hwnd, lParam))
        {
            handled = true;
        }
        else if (msg == WM_ENTERSIZEMOVE)
        {
            // During Windows' modal move/size loop a top-level Popup lags a frame or two
            // behind the window. The OSD controls are auto-hidden while dragging, but the
            // always-on STATS box stays visible and so visibly "detaches". Hide just the
            // stats box for the duration of the drag (no popup reopen - that path is
            // fragile over the mpv render surface and was crashing the app).
            _statsHiddenForMove = StatsOverlay.Visibility == Visibility.Visible;
            if (_statsHiddenForMove) StatsOverlay.Visibility = Visibility.Collapsed;
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            // Drag/snap finished: re-glue the overlay and bring the stats box back.
            ReanchorOverlay();
            if (_statsHiddenForMove)
            {
                _statsHiddenForMove = false;
                StatsOverlay.Visibility = Visibility.Visible;
            }
        }
        else if (msg == WM_WINDOWPOSCHANGED)
        {
            // Fires continuously during the move loop and for non-modal changes
            // (maximize, Win+arrow snap, DPI) - keep the overlay glued to the window.
            ReanchorOverlay();
        }
        return IntPtr.Zero;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _magpieScalingChangedMessage)
        {
            _upscalerActive = wParam != 0;
            RefreshMagpieActiveState();
        }
        else if (msg == WM_GETMINMAXINFO)
        {
            // WindowStyle=None makes a maximized window overflow the screen by the
            // resize border, pushing the bottom OSD controls under the taskbar.
            // Constrain the maximized bounds to the monitor work area.
            handled = TryConstrainMaximizedBounds(hwnd, lParam);
        }
        return 0;
    }

    private static bool TryConstrainMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, 2 /*NEAREST*/);
            if (monitor == IntPtr.Zero) return false;

            var mi = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref mi)) return false;

            var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
            mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
            mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
            mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
            mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
            return true;
        }
        catch { return false; }
    }

    private string UpscalerEngineLabel() => StateStore.Settings.UpscalerEngine switch
    {
        "magpie-fsrcnnx" => "Magpie FSRCNNX",
        "magpie-anime4k" => "Magpie Anime4K",
        "magpie-fsr" => "Magpie FSR",
        _ => LocalizationService.T("Disabled")
    };

    private static void SelectComboItemByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static void FadeIn(UIElement el) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
}
