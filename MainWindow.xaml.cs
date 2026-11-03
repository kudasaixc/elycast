using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Shell;
using Elysium_Cast_IPTV.Shell.Panels;

namespace Elysium_Cast_IPTV;

/// <summary>
/// The player shell view. It composes the feature controllers (see
/// <c>Shell/</c>), routes XAML events to them, handles keyboard/pointer input
/// that spans features, and orders the shutdown. It holds no feature state.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellServices _services;
    private readonly OsdController _osd;
    private readonly OverlayWindowHost _overlay;
    private readonly AudioVisualizerController _visualizer;
    private readonly PlaybackController _playback;
    private readonly BackendHost _backendHost;
    private readonly CatalogController _catalog;
    private readonly MusicLibraryController _music;
    private readonly ConnectionController _connection;
    private readonly ClipExportController _clip;
    private readonly UpscalingPanel _upscaling;
    private readonly ElyColorPanel _elyColor;
    private readonly ElySoundPanel _elySound;
    private readonly ElyFlowPanel _elyFlow;
    private readonly ElySmartPanel _elySmart;
    private readonly MagpiePanel _magpie;
    private readonly SettingsController _settings;
    private readonly WindowChromeController _chrome;
    private readonly PlayerConsoleCommands _console;
    private readonly DiagnosticRunner _diagnostics;
    private readonly WindowsMediaTransportService _mediaTransport;
    private readonly bool _composed;

    public MainWindow(ShellServices services)
    {
        _services = services;
        var vm = services.ViewModel;
        var session = services.Session;
        var store = services.Store;

        InitializeComponent();
        LocalizationService.Attach(this);
        // OverlayRoot is later reparented into the airspace overlay window; it
        // carries its own DataContext so its bindings survive the move.
        DataContext = vm;
        OverlayRoot.DataContext = vm;

        _mediaTransport = new WindowsMediaTransportService(
            play: () => Dispatcher.BeginInvoke(ResumeFromSystemControls),
            pause: () => Dispatcher.BeginInvoke(PauseFromSystemControls),
            next: () => Dispatcher.BeginInvoke(() => Zap(1)),
            previous: () => Dispatcher.BeginInvoke(() => Zap(-1)));
        SourceInitialized += (_, _) => _mediaTransport.Initialize(new WindowInteropHelper(this).Handle);

        var gate = new UiLoadGate();
        _osd = new OsdController(this, vm, session);
        _overlay = new OverlayWindowHost(this, session, _osd, OnKeyDown);
        _visualizer = new AudioVisualizerController(this, vm, session, store, _osd, services.AudioEngine, _mediaTransport);
        _playback = new PlaybackController(this, vm, session, services.Library, services.Queue, store, services.Pipeline,
            _visualizer, _osd, _mediaTransport);
        _backendHost = new BackendHost(this, session, store, services.Pipeline, _osd, _playback);
        _catalog = new CatalogController(this, vm, session, services.Library, services.Queue, _playback, _osd);
        _music = new MusicLibraryController(this, vm, session, services.Library, services.Queue, store, _playback, _osd,
            _catalog, services.LocalLibrary, gate);
        _clip = new ClipExportController(this, session, services.Library, store, services.Shaders, services.ClipExporter, _playback);
        _connection = new ConnectionController(this, vm, session, services.Library, store, services.Profiles, _playback, _catalog, _music, _overlay);
        _upscaling = new UpscalingPanel(this, session, store, services.Pipeline, gate);
        _elyColor = new ElyColorPanel(this, session, store, services.Pipeline, gate, _backendHost, _upscaling);
        _elySound = new ElySoundPanel(this, session, store, services.Pipeline, gate);
        _elyFlow = new ElyFlowPanel(this, store, services.Pipeline, gate);
        _elySmart = new ElySmartPanel(this, session, store, _visualizer, services.Benchmark, services.PerformanceHistory,
            services.HealthAnalyzer, services.Notifications, services.Optimizer);
        _magpie = new MagpiePanel(this, store, services.Magpie);
        _settings = new SettingsController(this, vm, session, store, gate, services.Pipeline, _playback, _visualizer,
            _backendHost, _catalog, _music, _connection, _upscaling, _elyColor, _elySound, _elyFlow, _elySmart, _magpie,
            services.MpvInstaller, _osd);
        _chrome = new WindowChromeController(this, vm, session, _osd, _overlay);
        _console = new PlayerConsoleCommands(Dispatcher, session, services.Library, store, _catalog, _playback, _connection,
            _settings, _upscaling, _elyColor, _elySound, _elyFlow, _magpie, _chrome, Zap,
            visibility => Visibility = visibility, text => SearchBox.Text = text,
            () => (int)VolumeSlider.Value, volume => VolumeSlider.Value = volume);
        _diagnostics = new DiagnosticRunner(Dispatcher, session, _playback, _connection);

        _backendHost.Playing += _playback.OnBackendPlaying;
        _backendHost.Failed += _playback.OnBackendFailed;
        _backendHost.Ended += _playback.OnBackendEnded;
        _backendHost.Paused += _playback.OnBackendPaused;
        _backendHost.SurfaceClicked += OnNativeSurfaceClicked;
        _playback.MediaStateChanged += () =>
        {
            _upscaling.RefreshOsdRow();
            _elyColor.RefreshOsdRow();
            _elySound.RefreshOsdRow();
        };

        _composed = true;
        Loaded += OnLoaded;
        Activated += (_, _) => _overlay.OnOwnerActivated();
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _connection.AnimateLoginIn();
        _backendHost.Initialize();
        _magpie.HookWindowMessages();
        _connection.LoadProfiles();
        _console.Register();
        _settings.LoadIntoUi();
        _music.LoadAsync().Forget("Local library load");
        _connection.ApplyOnboardingPreferences();
        _diagnostics.StartIfRequested();
        _elySmart.Initialize();

        // Keep the airspace overlay (owned window) glued to the video area as the
        // window moves or resizes. An owned window follows the main window
        // anywhere - including partially off-screen - minimises/restores with it
        // and never floats above other applications, so no activation juggling
        // is needed.
        LocationChanged += (_, _) => _overlay.Reanchor();
        SizeChanged += (_, _) => { _osd.UpdateSafeArea(); _overlay.RefreshLayout(); };
        DpiChanged += (_, _) => _overlay.RefreshLayout();
        StateChanged += (_, _) => { _osd.UpdateSafeArea(); _overlay.UpdateVisibility(); _overlay.RefreshLayout(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _chrome.Attach();
    }

    // ============ CROSS-FEATURE INPUT ============
    private void Zap(int direction)
    {
        if (!_playback.TryZapAudio(direction)) _catalog.ZapList(direction);
    }

    private void ResumeFromSystemControls() => _playback.ResumeFromSystemControls();
    private void PauseFromSystemControls() => _playback.PauseFromSystemControls();

    private void TogglePlayPause() => _playback.PlayPause(_catalog.SelectedPlayable);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _osd.IsPanelOpen) { _settings.CloseAllPanels(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _clip.IsOpen) { _clip.Close(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _chrome.IsFullscreen) { _chrome.ToggleFullscreen(); return; }
        if (e.Key == Key.F11) { _chrome.ToggleFullscreen(); return; }
        if (!PlayerView.IsVisible || SettingsPanel.Visibility == Visibility.Visible) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            var search = MusicPanel.Visibility == Visibility.Visible ? MusicTrackSearch : SearchBox;
            search.Focus(); search.SelectAll(); e.Handled = true; return;
        }
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox || Keyboard.Modifiers != ModifierKeys.None) return;

        var seekable = SeekArea.Visibility == Visibility.Visible;
        if (e.Key == Key.Space) { TogglePlayPause(); e.Handled = true; }
        else if (seekable && e.Key == Key.Left) { _playback.SeekRelative(-15_000); _osd.Show(); e.Handled = true; }
        else if (seekable && e.Key == Key.Right) { _playback.SeekRelative(15_000); _osd.Show(); e.Handled = true; }
        else if (_services.Store.Settings.ZapWithArrows && e.Key == Key.Up) { Zap(-1); e.Handled = true; }
        else if (_services.Store.Settings.ZapWithArrows && e.Key == Key.Down) { Zap(1); e.Handled = true; }
        else if (e.Key == Key.Enter && ItemList.Visibility == Visibility.Visible && ItemList.SelectedItem is PlayItem)
        {
            _catalog.ActivateSelected();
            e.Handled = true;
        }
    }

    // Click-to-pause in the middle of the picture (WPF overlay hit).
    private void OverlayRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_services.Session.HasMedia || _osd.IsPanelOpen) return;
        if (IsInteractive(e.OriginalSource as DependencyObject) || _clip.Contains(e.OriginalSource as DependencyObject)) return;
        if (!OsdController.IsInPlayPauseZone(e.GetPosition(OverlayRoot), OverlayRoot.ActualWidth, OverlayRoot.ActualHeight)) return;
        ClickToPause();
        e.Handled = true;
    }

    // Same gesture when the native surface (AudioCore+ / video swapchain)
    // captured the click instead of the WPF overlay; VideoStage coordinates.
    private void OnNativeSurfaceClicked()
    {
        if (!_services.Session.HasMedia || _osd.IsPanelOpen) return;
        if (_osd.IsVisible && _osd.IsCursorOverOsdControls()) return;
        if (NativeMethods.CursorScreenPosition() is not { } cursor) return;
        try
        {
            var p = VideoStage.PointFromScreen(cursor);
            if (!OsdController.IsInPlayPauseZone(p, VideoStage.ActualWidth, VideoStage.ActualHeight)) return;
        }
        catch (InvalidOperationException) { return; }
        ClickToPause();
    }

    private void ClickToPause()
    {
        var wasPlaying = _services.Session.IsPlaying;
        TogglePlayPause();
        _osd.ShowCenterTransportFeedback(showPause: wasPlaying);
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
            // ComboBoxItem is hosted in a separate Popup visual tree, so walking
            // upward from it never reaches the owning ComboBox.
            if (current is ButtonBase or Slider or ComboBox or ComboBoxItem or TextBox) return true;
        return false;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_osd.IsPanelOpen) return;
        _osd.Show();
    }

    private void Stage_MouseLeave(object sender, MouseEventArgs e) => _osd.SchedulePointerLeaveCheck();

    // ============ WINDOW ============
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => _chrome.ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _chrome.ToggleFullscreen();
    private void Console_Click(object sender, RoutedEventArgs e) => DebugConsole.Toggle();

    protected override void OnClosing(CancelEventArgs e)
    {
        var session = _services.Session;
        if (_services.Store.Settings.ConfirmExit && session.Connected)
        {
            var r = MessageBox.Show(LocalizationService.T("Quit ElyCast?"), "ElyCast", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        // Let the owned overlay window close with us instead of cancelling.
        session.ShuttingDown = true;
        _connection.CancelConnect();
        _catalog.CancelPendingLoads();
        _music.CancelImport();
        _clip.Shutdown();
        _playback.Shutdown();
        _catalog.StopSearchTimer();
        _osd.StopTimer();
        session.NextGeneration();
        _visualizer.Hide();
        Shutdown("stopping audio analysis", _visualizer.Dispose);
        Shutdown("releasing the Windows media controls", _mediaTransport.Dispose);
        Shutdown("stopping ELYSMART", _elySmart.Dispose);
        _elySound.FlushPending();
        base.OnClosing(e);
        Shutdown("failed to save state", _services.Store.Save);
        _backendHost.DisposeBackend();
    }

    private static void Shutdown(string step, Action action)
    {
        try { action(); }
        catch (Exception ex) { DebugConsole.Exception("Shutdown: " + step, ex); }
    }

    // ============ SIGN-IN ============
    private void XtreamTab_Click(object sender, RoutedEventArgs e) => _connection.SwitchTab(m3u: false);
    private void M3uTab_Click(object sender, RoutedEventArgs e) => _connection.SwitchTab(m3u: true);
    private void Browse_Click(object sender, RoutedEventArgs e) => _connection.BrowsePlaylist();
    private void SaveProfile_Toggle(object sender, RoutedEventArgs e) { if (_composed) _connection.OnSaveProfileToggled(); }
    private void PassBox_PasswordChanged(object sender, RoutedEventArgs e) { if (_composed) _connection.OnPasswordChanged(); }
    private void Connect_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_connection.ConnectAsync, "Connection");
    private void LocalOnly_Click(object sender, RoutedEventArgs e) => _connection.EnterLocalOnlyMode();
    private void Disconnect_Click(object sender, RoutedEventArgs e) => _connection.SignOut();

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Profile p) _connection.ConnectWith(p);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Profile p) _connection.DeleteProfile(p);
    }

    // ============ CATALOGUE ============
    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (_composed && sender is RadioButton button)
            AsyncExtensions.FireAndLog(() => _catalog.OnNavigationAsync(button), "Navigation");
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _catalog.OnCategoryChanged(); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_composed) _catalog.OnSearchTextChanged(); }
    private void ItemList_DoubleClick(object sender, MouseButtonEventArgs e) => _catalog.ActivateSelected();
    private void Resume_Click(object sender, RoutedEventArgs e) => _catalog.Resume();
    private void Season_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _catalog.OnSeasonChanged(); }
    private void Episode_DoubleClick(object sender, MouseButtonEventArgs e) => _catalog.PlaySelectedEpisode();
    private void CloseSeries_Click(object sender, RoutedEventArgs e) => _catalog.CloseSeriesPanel();

    private void Fav_Toggle(object sender, RoutedEventArgs e)
    {
        if (_composed && ((FrameworkElement)sender).DataContext is PlayItem item) _catalog.OnFavoriteToggled(item);
    }

    // ============ LOCAL LIBRARY ============
    private void ImportLocalFolder_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_music.ImportFolderAsync, "Local import");
    private void ImportLocalFiles_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_music.ImportFilesAsync, "Local import");
    private void LocalList_DragOver(object sender, DragEventArgs e) => _music.OnDragOver(e);
    private void LocalList_Drop(object sender, DragEventArgs e) => AsyncExtensions.FireAndLog(() => _music.OnDropAsync(e), "Drag-and-drop import");
    private void RemoveLocalFile_Click(object sender, RoutedEventArgs e) => _music.RemoveSelectedListItem();
    private void ResetAudioLibrary_Click(object sender, RoutedEventArgs e) => _music.ResetAudioLibrary();
    private void AudioBrowse_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _music.OnBrowseModeChanged(); }
    private void MusicGroupList_Click(object sender, MouseButtonEventArgs e) => _music.OnGroupListClick(e);
    private void MusicTrackSearch_TextChanged(object sender, TextChangedEventArgs e) { if (_composed) _music.OnTrackSearchChanged(); }
    private void CloseMusic_Click(object sender, RoutedEventArgs e) => _music.ClosePanel();
    private void GroupPlay_Click(object sender, RoutedEventArgs e) => _music.PlayGroup(sender, shuffle: false);
    private void GroupShuffle_Click(object sender, RoutedEventArgs e) => _music.PlayGroup(sender, shuffle: true);
    private void GroupQueue_Click(object sender, RoutedEventArgs e) => _music.QueueGroup(sender);
    private void MusicTrack_DoubleClick(object sender, MouseButtonEventArgs e) => _music.PlaySelectedTrack();
    private void MusicTrackPlay_Click(object sender, RoutedEventArgs e) => _music.PlayTrack(sender);
    private void PlaylistNameBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) _music.CreatePlaylist(); }
    private void CreatePlaylist_Click(object sender, RoutedEventArgs e) => _music.CreatePlaylist();
    private void DeletePlaylist_Click(object sender, RoutedEventArgs e) => _music.DeletePlaylist(sender);
    private void MusicGroupMenu_Opened(object sender, RoutedEventArgs e) => _music.OnGroupMenuOpened();
    private void RemoveMusicGroup_Click(object sender, RoutedEventArgs e) => _music.RemoveGroup(sender);
    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e) => _music.RemoveFromPlaylist(sender);
    private void LocalItemMenu_Opened(object sender, RoutedEventArgs e) => _music.OnLocalItemMenuOpened();
    private void MusicTrackMenu_Opened(object sender, RoutedEventArgs e) => _music.OnTrackMenuOpened();
    private void PlayNow_Click(object sender, RoutedEventArgs e) => _music.PlayNow(sender);
    private void PlayNext_Click(object sender, RoutedEventArgs e) => _music.PlayNext(sender);
    private void AddToQueue_Click(object sender, RoutedEventArgs e) => _music.AddToQueue(sender);
    private void RemoveTrack_Click(object sender, RoutedEventArgs e) => _music.RemoveTrack(sender);
    private void EditMetadata_Click(object sender, RoutedEventArgs e) => _music.EditMetadata(sender);
    private void ClearQueue_Click(object sender, RoutedEventArgs e) => _services.Queue.ClearManual();
    private void Shuffle_Checked(object sender, RoutedEventArgs e) { if (_composed) _services.Queue.Shuffle = ShuffleToggle.IsChecked == true; }
    private void Repeat_Checked(object sender, RoutedEventArgs e) { if (_composed) _services.Queue.Repeat = RepeatToggle.IsChecked == true; }

    private void ItemList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (MusicLibraryController.FindListItem(e.OriginalSource) is { } row) row.IsSelected = true;
    }

    // ============ PLAYER ============
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void Stop_Click(object sender, RoutedEventArgs e) => _playback.Stop();
    private void PreviousMedia_Click(object sender, RoutedEventArgs e) => Zap(-1);
    private void NextMedia_Click(object sender, RoutedEventArgs e) => Zap(1);
    private void SkipBack_Click(object sender, RoutedEventArgs e) => _playback.SeekRelative(-15_000);
    private void SkipFwd_Click(object sender, RoutedEventArgs e) => _playback.SeekRelative(15_000);
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_composed) _playback.OnVolumeChanged(e.NewValue); }
    private void Subtitle_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _playback.OnSubtitleSelectionChanged(); }
    private void Audio_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _playback.OnAudioSelectionChanged(); }
    private void StatsBtn_Click(object sender, RoutedEventArgs e) => _playback.ToggleStats();
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => _settings.CloseAllPanels();

    // ============ CLIP EXPORT ============
    private void ClipBtn_Click(object sender, RoutedEventArgs e) => _clip.Toggle();
    private void ClipClose_Click(object sender, RoutedEventArgs e) => _clip.Close();
    private void ClipMarkStart_Click(object sender, RoutedEventArgs e) => _clip.MarkStart();
    private void ClipMarkEnd_Click(object sender, RoutedEventArgs e) => _clip.MarkEnd();
    private void ClipRange_Changed(object sender, TextChangedEventArgs e) { if (_composed) _clip.OnRangeTextChanged(); }
    private void ClipWatermark_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _clip.OnWatermarkPositionChanged(); }
    private void ClipExport_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_clip.ExportRangeAsync, "Clip export");
    private void ClipRecord_Click(object sender, RoutedEventArgs e) => _clip.ToggleRecording();
    private void ClipCancel_Click(object sender, RoutedEventArgs e) => _clip.Cancel();
    private void ClipOpenFolder_Click(object sender, RoutedEventArgs e) => _clip.OpenFolder();

    // ============ SETTINGS ============
    private void Language_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _settings.OnLanguageChanged(); }
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (_composed) _settings.OnGeneralSettingChanged(sender); }
    private void SliderSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_composed) _settings.OnDefaultVolumeChanged(); }
    private void AudioVisualizerSetting_Changed(object sender, RoutedEventArgs e) { if (_composed) _settings.OnAudioVisualizerSettingChanged(); }
    private void AudioVisualizerSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_composed) _settings.OnAudioVisualizerSettingChanged(); }
    private void AudioRenderer_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _settings.OnAudioRendererChanged(); }
    private void BackendSetting_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _settings.OnBackendChanged(); }
    private void UpscalerSetting_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _settings.OnUpscalerEngineChanged(); }
    private void InstallMpv_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_settings.InstallMpvAsync, "mpv installation");
    private void ResetSettings_Click(object sender, RoutedEventArgs e) => _settings.Reset();
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _settings.OpenDataFolder();
    private void ApplyHex_Click(object sender, RoutedEventArgs e) => _settings.ApplyAccent(AccentHexBox.Text.Trim(), updateBox: false);

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string hex) _settings.ApplyAccent(hex, updateBox: true);
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string category }) _settings.ShowCategory(category);
    }

    private void UpscaleSetting_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _upscaling.OnSettingChanged(); }
    private void OsdUpscale_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _upscaling.OnOsdSelectionChanged(); }

    private void ElyColorFilter_Changed(object sender, SelectionChangedEventArgs e) { if (_composed && sender is ComboBox combo) _elyColor.OnFilterSelected(combo); }
    private void OsdElyColor_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _elyColor.OnFilterSelected(OsdElyColorCombo); }
    private void ElyColorCustom_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _elyColor.OnCustomSelected(); }
    private void ElyColorSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_composed) _elyColor.OnSliderChanged(); }
    private void CaptureElyColor_Click(object sender, RoutedEventArgs e) => _elyColor.Capture();
    private void SaveElyColor_Click(object sender, RoutedEventArgs e) => _elyColor.Save();
    private void DeleteElyColor_Click(object sender, RoutedEventArgs e) => _elyColor.Delete();

    private void ElySoundEnabled_Changed(object sender, RoutedEventArgs e) { if (_composed && sender is CheckBox box) _elySound.OnEnabledChanged(box); }
    private void ElySoundSetting_Changed(object sender, RoutedEventArgs e) { if (_composed) _elySound.OnVirtualSurroundChanged(); }
    private void ElySoundPreset_Changed(object sender, SelectionChangedEventArgs e) { if (_composed && sender is ComboBox combo) _elySound.OnPresetSelected(combo); }
    private void OsdElySound_Changed(object sender, SelectionChangedEventArgs e) => ElySoundPreset_Changed(sender, e);
    private void ElySoundSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_composed) _elySound.OnSliderChanged(); }

    private void ElyFlowSetting_Changed(object sender, RoutedEventArgs e) { if (_composed) _elyFlow.OnSettingChanged(); }
    private void ElyFlowCombo_Changed(object sender, SelectionChangedEventArgs e) { if (_composed) _elyFlow.OnSettingChanged(); }
    private void ElyFlowSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_composed) _elyFlow.OnBufferChanged(); }

    private void ElySmartRun_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_elySmart.RunBenchmarkAsync, "ELYSMART benchmark");
    private void ElySmartCancel_Click(object sender, RoutedEventArgs e) => _elySmart.CancelBenchmark();
    private void ElySmartAutoSwitch_Click(object sender, RoutedEventArgs e) => _elySmart.OnAutoOptimizeToggled();
    private void ElySmartNotificationsSwitch_Click(object sender, RoutedEventArgs e) => _elySmart.OnNotificationsToggled();
    private void ElySmartApply_Click(object sender, RoutedEventArgs e) => _elySmart.ApplyRecommendation();
    private void ElySmartExport_Click(object sender, RoutedEventArgs e) => _elySmart.ExportReport();

    private void BrowseMagpie_Click(object sender, RoutedEventArgs e) => _magpie.Browse();
    private void InstallMagpie_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_magpie.InstallAsync, "Magpie installation");
    private void ActivateUpscaler_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_magpie.ToggleScalingAsync, "Magpie toggle");
    private void UpscalerBtn_Click(object sender, RoutedEventArgs e) => AsyncExtensions.FireAndLog(_magpie.ToggleScalingAsync, "Magpie toggle");
}
