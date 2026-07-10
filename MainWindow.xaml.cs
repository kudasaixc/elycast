using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Video;
using Microsoft.Win32;
using NAudio.Wave;

namespace Elysium_Cast_IPTV;

public partial class MainWindow : Window
{
    private enum Section { Live, Movies, Series, Local, Fav }

    private readonly IptvService _iptv = new();
    private readonly MagpieUpscalerService _magpie = new();
    private readonly MpvNativeInstaller _mpvInstaller = new();
    private IVideoBackend? _videoBackend;

    private readonly BulkObservableCollection<PlayItem> _items = new();
    private readonly ObservableCollection<SubtitleOption> _subtitleOptions = new();
    private readonly ObservableCollection<AudioOption> _audioOptions = new();
    private ListCollectionView? _view;

    private List<PlayItem> _liveItems = new();
    private List<PlayItem>? _movieItems;
    private List<PlayItem>? _seriesItems;
    private List<PlayItem> _localItems = new();

    private PlayItem? _current;
    private Section _section = Section.Live;
    private Section _sectionBeforeSettings = Section.Live;
    private bool _suppressNav;
    private bool _connected;
    private bool _isFullscreen, _m3uMode, _initializing;
    private string _selectedCategory = "";

    private readonly ObservableCollection<Profile> _profiles = new();
    private ProfileState _state = new();

    private SeriesInfo? _seriesInfo;
    private string _seriesName = "";

    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _epgTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private readonly DispatcherTimer _elySoundApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    // Audio visualizer: analysis runs on a background thread (AudioVisualEngine),
    // rendering happens every displayed frame via CompositionTarget.Rendering.
    private readonly AudioVisualEngine _audioEngine = new();
    private readonly double[] _audioSpectrumSnapshot = new double[AudioVisualEngine.Bands];
    private readonly Random _audioVisualRandom = new();
    private readonly System.Diagnostics.Stopwatch _audioVisualStopwatch = new();
    private bool _audioRenderHooked;
    private ImageSource? _audioDefaultDisc;
    private double _audioLastTickSeconds;
    private double _audioBassEnergy;
    private double _audioFullEnergy;
    private double _audioBeatPulse;
    private DispatcherTimer? _reconnectTimer;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _sectionCts;
    private CancellationTokenSource? _seriesCts;
    private CancellationTokenSource? _epgCts;
    private int _playbackGeneration;
    private bool _userSeeking;
    private bool _loadingSubtitleChoices;
    private bool _loadingAudioChoices;
    private bool _upscalerBusy;
    private bool _mpvInstallBusy;
    private bool _upscalerActive;
    private int _magpieScalingChangedMessage;

    // fullscreen restore state
    private double _prevLeft, _prevTop, _prevW, _prevH;
    private WindowState _prevState = WindowState.Normal;

    // Airspace overlay host: a transparent borderless Window owned by this one,
    // glued over VideoStage. A Popup cannot be used: WPF clamps popups to the
    // monitor edges, so it detached from the window whenever the window was
    // dragged partially off-screen.
    private Window? _overlayWindow;
    private bool _shuttingDown;
    private bool _osdVisible;

    private const string AllCategories = "Toutes les catégories";

    private sealed record SubtitleOption(int Id, string Name, string Key, bool IsAuto = false)
    {
        public override string ToString() => Name;
    }

    private sealed record AudioOption(int Id, string Name, string Key, bool IsAuto = false)
    {
        public override string ToString() => Name;
    }

    public MainWindow()
    {
        _initializing = true;
        InitializeComponent();
        _initializing = false;
        SubtitleCombo.ItemsSource = _subtitleOptions;
        AudioCombo.ItemsSource = _audioOptions;
        Loaded += OnLoaded;
        Activated += OnWindowActivated;
        KeyDown += OnKeyDown;
        _statsTimer.Tick += (_, _) => UpdateStats();
        _epgTimer.Tick += (_, _) => UpdateEpgProgress();
        _progressTimer.Tick += (_, _) => UpdateSeek();
        _osdTimer.Tick += (_, _) => HideOsd();
        _elySoundApplyTimer.Tick += (_, _) => FlushPendingElySound();

        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _userSeeking = true));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { SeekToSlider(); _userSeeking = false; }));
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => SeekToSlider()), true);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AnimateLoginIn();
        InitVideoBackend();
        InitMagpieMessages();
        LoadProfiles();
        RegisterConsoleCommands();
        LoadSettingsIntoUi();
        LoadLocalLibrary();
        StartDiagnosticPlaybackIfRequested();

        // Keep the airspace overlay (owned window) glued to the video area as the
        // window moves or resizes. An owned window follows the main window
        // anywhere — including partially off-screen — minimises/restores with it
        // and never floats above other applications, so no activation juggling
        // is needed.
        LocationChanged += (_, _) => ReanchorOverlay();
        SizeChanged += (_, _) => { UpdateOsdSafeArea(); RefreshOverlayLayout(); };
        DpiChanged += (_, _) => RefreshOverlayLayout();
        StateChanged += (_, _) => { UpdateOsdSafeArea(); UpdateOverlayVisibility(); RefreshOverlayLayout(); };
    }

    private void StartDiagnosticPlaybackIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            DebugConsole.Warn("Diagnostic playback: fichier introuvable.");
            return;
        }

        // Opt-in test hook used by automated renderer smoke/performance tests.
        // A short dispatcher delay lets backend creation settle before opening
        // the media.
        _connected = true;
        GoToPlayer();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_videoBackend == null) return;
            _current = null;
            DebugConsole.Info("Diagnostic playback -> fichier local.");
            _videoBackend.Volume = (int)VolumeSlider.Value;
            _videoBackend.Play(path);
            _ = LogDiagnosticPlaybackStatsAsync(_videoBackend);
        };
        timer.Start();
    }

    private async Task LogDiagnosticPlaybackStatsAsync(IVideoBackend expectedBackend)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_videoBackend, expectedBackend)) return;
            var stats = expectedBackend.GetStats();
            DebugConsole.Info(string.Create(CultureInfo.InvariantCulture,
                $"Diagnostic renderer — source={stats.SourceWidth}x{stats.SourceHeight}, sortie={stats.OutputWidth}x{stats.OutputHeight}, FPS={stats.Fps:0.###}, présentées={stats.DisplayedFrames}, perdues={stats.DroppedFrames}, pipeline={stats.Shaders}"));
        });
    }

    // ============ AIRSPACE OVERLAY ============
    private Window EnsureOverlayWindow()
    {
        if (_overlayWindow != null) return _overlayWindow;

        // Detach the overlay content from its collapsed XAML mount so the owned
        // window can adopt it. Every style/brush it uses lives in App.xaml, so
        // StaticResource (parse-time) and DynamicResource (App fallback) both
        // keep working after the reparent.
        OverlayMount.Child = null;

        var overlay = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Content = OverlayRoot
        };

        // The overlay must receive mouse input without ever becoming the
        // foreground window. Otherwise clicking an OSD control deactivates the
        // owner, bounces the HWND z-order and can make the video flash black.
        overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(overlay).Handle;
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                new IntPtr(exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(OverlayWindowHook);
        };

        // Keyboard shortcuts must keep working when a click gave the overlay
        // window the focus (it is a separate HWND, keys do not bubble to us).
        overlay.KeyDown += OnKeyDown;

        // Alt-F4 on the overlay must not tear it down while the app lives on.
        overlay.Closing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true;
            overlay.Hide();
        };

        _overlayWindow = overlay;
        return overlay;
    }

    private bool SyncOverlaySize()
    {
        if (_overlayWindow == null) return false;
        if (VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0) return false;

        var width = Math.Ceiling(VideoStage.ActualWidth);
        var height = Math.Ceiling(VideoStage.ActualHeight);

        _overlayWindow.Width = width;
        _overlayWindow.Height = height;
        OverlayRoot.Width = width;
        OverlayRoot.Height = height;
        return true;
    }

    private void UpdateOsdSafeArea()
    {
        OsdBottomChrome.Padding = new Thickness(20, 28, 20, 16);
        OsdBottomChrome.Margin = new Thickness(0);

        var offset = GetVideoBottomLetterboxDip();
        if (WindowState == WindowState.Maximized && !_isFullscreen)
            offset += Math.Max(GetTaskbarBottomReserveDip(), 56);

        OsdBottomOffset.Y = -Math.Min(offset, Math.Max(0, VideoStage.ActualHeight - 96));
    }

    private double GetTaskbarBottomReserveDip()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return 56;

            var monitor = MonitorFromWindow(hwnd, 2 /*NEAREST*/);
            if (monitor == IntPtr.Zero) return 56;

            var mi = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref mi)) return 56;

            var source = HwndSource.FromHwnd(hwnd);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var monitorBottom = transform.Transform(new Point(0, mi.rcMonitor.Bottom)).Y;
            var workBottom = transform.Transform(new Point(0, mi.rcWork.Bottom)).Y;

            return Math.Clamp(monitorBottom - workBottom, 0, 96);
        }
        catch { return 56; }
    }

    private double GetVideoBottomLetterboxDip()
    {
        if (_videoBackend?.HasMedia != true) return 0;
        if (VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0) return 0;

        try
        {
            var stats = _videoBackend.GetStats();
            var videoWidth = stats.OutputWidth > 0 ? stats.OutputWidth : stats.SourceWidth;
            var videoHeight = stats.OutputHeight > 0 ? stats.OutputHeight : stats.SourceHeight;
            if (videoWidth <= 0 || videoHeight <= 0) return 0;

            var videoAspect = (double)videoWidth / videoHeight;
            var stageAspect = VideoStage.ActualWidth / VideoStage.ActualHeight;
            if (videoAspect <= stageAspect) return 0;

            var displayedHeight = VideoStage.ActualWidth / videoAspect;
            return Math.Clamp((VideoStage.ActualHeight - displayedHeight) / 2, 0, VideoStage.ActualHeight / 2);
        }
        catch { return 0; }
    }

    // Position the overlay window exactly over VideoStage, in screen DIPs
    // (PointToScreen returns device pixels, so convert via the visual's DPI).
    // A window — unlike a popup — is free to extend past the screen edges.
    private void ReanchorOverlay()
    {
        if (_overlayWindow is not { IsVisible: true }) return;
        if (VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;

        try
        {
            var deviceTopLeft = VideoStage.PointToScreen(new Point(0, 0));
            var dipTopLeft = source.CompositionTarget.TransformFromDevice.Transform(deviceTopLeft);
            _overlayWindow.Left = dipTopLeft.X;
            _overlayWindow.Top = dipTopLeft.Y;
        }
        catch { /* visual not connected yet */ }
    }

    private void UpdateOverlayVisibility()
    {
        if (_overlayWindow == null) return;
        if (!_connected || WindowState == WindowState.Minimized)
        {
            if (_overlayWindow.IsVisible) _overlayWindow.Hide();
            return;
        }
        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
            RefreshOverlayLayout();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // Keep the layered overlay alive across focus changes. Hide/Show here
        // forces DWM to rebuild composition around the child swapchain and was
        // itself one of the remaining sources of black flashes.
        UpdateOverlayVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsCursorOverVideoStage()) ShowOsd();
        }));
    }

    private nint OverlayWindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    // Re-sync the overlay window to VideoStage after a resize / maximize / DPI
    // change: once now, and once more after the pending layout pass so the final
    // VideoStage bounds are picked up.
    private void RefreshOverlayLayout()
    {
        if (_overlayWindow is not { IsVisible: true }) return;
        UpdateOsdSafeArea();
        SyncOverlaySize();
        ReanchorOverlay();

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_overlayWindow is not { IsVisible: true }) return;
            UpdateOsdSafeArea();
            SyncOverlaySize();
            ReanchorOverlay();
        }));
    }

    // ============ VIDEO BACKEND ============
    private void InitVideoBackend()
    {
        if (_videoBackend != null) return;

        DebugConsole.Step($"Backend: création via factory (préférence='{StateStore.Settings.VideoBackend}')…");
        var backend = VideoBackendFactory.Create(StateStore.Settings.VideoBackend);
        _videoBackend = backend;

        DebugConsole.Step($"Backend: rattachement de la surface vidéo ({backend.Name}) au Visual Tree…");
        VideoHost.Content = backend.View;
        if (backend.View is MpvHwndHost nativeHost)
        {
            // HwndHost owns its own native input queue; relay it explicitly so
            // entering the picture reveals the OSD on the very first movement.
            nativeHost.PointerActivity += OnVideoSurfacePointerActivity;
            nativeHost.PointerLeft += OnVideoSurfacePointerLeft;
        }
        backend.Volume = (int)VolumeSlider.Value;

        DebugConsole.Step("Backend: rattachement des événements…");
        backend.Playing += OnBackendPlaying;
        backend.Failed += OnBackendFailed;
        backend.Ended += OnBackendEnded;
        backend.Paused += OnBackendPaused;

        ApplyUpscalingToBackend();
        ApplyElyColorToBackend();
        ApplyElySoundToBackend();
        ApplyElyFlowToBackend();

        // ApplyElyColorToBackend can legitimately replace the backend (a
        // filter may carry a full video pipeline) and RecreateVideoBackend
        // clears the field synchronously before scheduling the new instance.
        // Never dereference that transient null during application startup.
        if (!ReferenceEquals(_videoBackend, backend))
        {
            DebugConsole.Info($"Backend: transition depuis {backend.Name} programmée.");
            return;
        }

        DebugConsole.Success($"Backend: initialisé ({backend.Name}).");
    }

    private void DetachBackendEvents(IVideoBackend backend)
    {
        backend.Playing -= OnBackendPlaying;
        backend.Failed -= OnBackendFailed;
        backend.Ended -= OnBackendEnded;
        backend.Paused -= OnBackendPaused;
    }

    private void OnBackendPlaying() => Dispatcher.Invoke(() =>
    {
        var generation = _playbackGeneration;
        HideOverlay(); SetPlayIcon(false); ShowOsd();
        RefreshOsdSafeAreaSoon();
        RefreshSubtitleTracksSoon(generation);
        RefreshAudioTracksSoon(generation);
        RefreshOsdUpscaleRow();
        RefreshOsdElyColorRow();
        RefreshOsdElySoundRow();
        DebugConsole.Success($"Lecture en cours : {_current?.Name}");
    });

    private void OnBackendFailed(string message) => Dispatcher.Invoke(() =>
    {
        if (!_connected) return;
        ShowOverlay(message, spinning: false);
        TopSubtitle.Text = "Erreur";
        DebugConsole.Error($"Échec de lecture : {_current?.Name} ({message})");
        TryAutoReconnect();
    });

    private void OnBackendEnded() => Dispatcher.Invoke(() =>
    {
        Interlocked.Increment(ref _playbackGeneration);
        CancelEpgRequest();
        SetPlayIcon(true);
        TopSubtitle.Text = "Terminé";
        if (_current != null && IsAudioOnlyItem(_current)) HideAudioVisualizer();
    });

    private void OnBackendPaused() => Dispatcher.Invoke(() => { SetPlayIcon(true); ShowOsd(); });

    private async void RefreshOsdSafeAreaSoon()
    {
        await Task.Delay(700);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateOsdSafeArea);
            return;
        }
        UpdateOsdSafeArea();
    }

    // ============ PROFILES (login) ============
    private void LoadProfiles()
    {
        _profiles.Clear();
        foreach (var p in ProfileStore.Load()) _profiles.Add(p);
        ProfilesItems.ItemsSource = _profiles;
        ProfilesSection.Visibility = _profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Profile p) return;
        if (p.Kind == ProfileKind.Xtream)
        {
            SwitchTab(false);
            UrlBox.Text = p.Url; UserBox.Text = p.Username;
            PassBox.Password = ProfileStore.Unprotect(p.ProtectedPassword);
        }
        else { SwitchTab(true); M3uPathBox.Text = p.M3uPath; }
        Connect_Click(this, new RoutedEventArgs());
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Profile p) return;
        _profiles.Remove(p);
        ProfileStore.Save(_profiles.ToList());
        ProfilesSection.Visibility = _profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveCurrentProfileIfRequested()
    {
        if (SaveProfileCheck.IsChecked != true) return;
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
            name = _m3uMode ? "Playlist M3U" : (UserBox.Text.Trim() + " @ " + UrlBox.Text.Trim());

        var profile = _m3uMode
            ? new Profile { Name = name, Kind = ProfileKind.M3u, M3uPath = M3uPathBox.Text.Trim() }
            : new Profile
            {
                Name = name, Kind = ProfileKind.Xtream, Url = UrlBox.Text.Trim(),
                Username = UserBox.Text.Trim(), ProtectedPassword = ProfileStore.Protect(PassBox.Password)
            };
        var existing = _profiles.FirstOrDefault(p => p.Name == name);
        if (existing != null) _profiles.Remove(existing);
        _profiles.Add(profile);
        ProfileStore.Save(_profiles.ToList());
    }

    // ============ TABS ============
    private void XtreamTab_Click(object sender, RoutedEventArgs e) => SwitchTab(false);
    private void M3uTab_Click(object sender, RoutedEventArgs e) => SwitchTab(true);

    private void SwitchTab(bool m3u)
    {
        _m3uMode = m3u;
        XtreamPanel.Visibility = m3u ? Visibility.Collapsed : Visibility.Visible;
        M3uPanel.Visibility = m3u ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(TabHighlight, m3u ? 1 : 0);
        XtreamTabBtn.Foreground = m3u ? (Brush)FindResource("MutedBrush") : Brushes.White;
        M3uTabBtn.Foreground = m3u ? Brushes.White : (Brush)FindResource("MutedBrush");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une playlist M3U",
            Filter = "Playlists M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) M3uPathBox.Text = dlg.FileName;
    }

    private void SaveProfile_Toggle(object sender, RoutedEventArgs e) =>
        ProfileNamePanel.Visibility = SaveProfileCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ============ CONNECT ============
    private void AnimateLoginIn()
    {
        LoginView.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        LoginCardT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(550)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        _connectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        var ct = cts.Token;
        SetConnecting(true);
        StatusText.Text = "";
        try
        {
            List<Channel> channels;
            if (_m3uMode)
            {
                var path = M3uPathBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path)) { StatusText.Text = "Indique un fichier ou une URL M3U."; SetConnecting(false); return; }
                (_, channels) = await _iptv.LoadM3uAsync(path, ct);
            }
            else
            {
                var url = UrlBox.Text.Trim(); var user = UserBox.Text.Trim(); var pass = PassBox.Password;
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                { StatusText.Text = "Merci de remplir l'URL, l'utilisateur et le mot de passe."; SetConnecting(false); return; }
                (_, channels) = await _iptv.ConnectAsync(url, user, pass, ct);
            }

            ct.ThrowIfCancellationRequested();

            if (channels.Count == 0) { StatusText.Text = "Connexion réussie mais aucune chaîne trouvée."; SetConnecting(false); return; }

            _state = StateStore.ForProfile(_iptv.ProfileKey);
            _liveItems = channels.Select(PlayItem.FromChannel).ToList();
            _movieItems = null; _seriesItems = null;
            MarkFavorites(_liveItems);

            SaveCurrentProfileIfRequested();
            _connected = true;
            NavLive.IsChecked = true;
            ShowSection(Section.Live);
            UpdateResumeButton();
            GoToPlayer();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DebugConsole.Info("Connexion annulée.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Échec de la connexion : " + ex.Message;
            DebugConsole.Error("Connexion : " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
                SetConnecting(false);
            }
            cts.Dispose();
        }
    }

    private void SetConnecting(bool busy)
    {
        ConnectBtn.IsEnabled = !busy;
        ConnectLabel.Text = busy ? "Connexion…" : "Se connecter";
        LoginIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        LoginSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) StartSpin(LoginSpinner); else StopSpin(LoginSpinner);
    }

    private void GoToPlayer()
    {
        PlayerView.Visibility = Visibility.Visible;
        // Show the airspace overlay (OSD/menus) now that the player is visible.
        // Defer so VideoStage has been laid out and has a real size to anchor to.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var overlay = EnsureOverlayWindow();
            UpdateOsdSafeArea();
            SyncOverlaySize();
            overlay.Show();
            SyncOverlaySize();
            ReanchorOverlay();
        }));
        var outFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        outFade.Completed += (_, _) => LoginView.Visibility = Visibility.Collapsed;
        LoginView.BeginAnimation(OpacityProperty, outFade);
        PlayerView.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            { BeginTime = TimeSpan.FromMilliseconds(160), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _connectCts?.Cancel();
        _sectionCts?.Cancel();
        _seriesCts?.Cancel();
        CancelEpgRequest();
        Interlocked.Increment(ref _playbackGeneration);
        _reconnectTimer?.Stop();
        try { _videoBackend?.Stop(); } catch { }
        _statsTimer.Stop(); _epgTimer.Stop(); _progressTimer.Stop();
        HideAudioVisualizer();
        _connected = false;
        _current = null;
        _overlayWindow?.Hide();
        ShowOverlay("Sélectionne une chaîne pour lancer la lecture", spinning: false);
        TopTitle.Text = "Aucune lecture"; TopSubtitle.Text = "En attente…";
        SeekArea.Visibility = Visibility.Collapsed;
        SkipBackBtn.Visibility = Visibility.Collapsed;
        SkipFwdBtn.Visibility = Visibility.Collapsed;
        EpgProgress.Visibility = Visibility.Collapsed;
        LoadProfiles();

        LoginView.Visibility = Visibility.Visible;
        LoginView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        var outFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        outFade.Completed += (_, _) => PlayerView.Visibility = Visibility.Collapsed;
        PlayerView.BeginAnimation(OpacityProperty, outFade);
    }

    // ============ NAV / SECTIONS ============
    private async void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!_connected || _suppressNav) return;
        if (NavSettings.IsChecked == true) { _sectionBeforeSettings = _section; OpenSettingsPanel(); return; }
        CloseSettingsPanel();
        CloseSeriesPanel();

        _sectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sectionCts = cts;
        try
        {
        if (NavMovies.IsChecked == true) await ShowSectionAsync(Section.Movies, cts.Token);
        else if (NavSeries.IsChecked == true) await ShowSectionAsync(Section.Series, cts.Token);
        else if (NavLocal.IsChecked == true) ShowSection(Section.Local);
        else if (NavFav.IsChecked == true) ShowSection(Section.Fav);
        else ShowSection(Section.Live);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_sectionCts, cts)) _sectionCts = null;
            cts.Dispose();
        }
    }

    private async Task ShowSectionAsync(Section s, CancellationToken ct)
    {
        if (s == Section.Movies && _movieItems == null)
        {
            SectionTitle.Text = "Films…";
            try { _movieItems = (await _iptv.GetVodAsync(ct)).Select(PlayItem.FromVod).ToList(); MarkFavorites(_movieItems); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { DebugConsole.Error("VOD : " + ex.Message); _movieItems = new(); }
        }
        else if (s == Section.Series && _seriesItems == null)
        {
            SectionTitle.Text = "Séries…";
            try { _seriesItems = (await _iptv.GetSeriesAsync(ct)).Select(PlayItem.FromSeries).ToList(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { DebugConsole.Error("Séries : " + ex.Message); _seriesItems = new(); }
        }
        ct.ThrowIfCancellationRequested();
        ShowSection(s);
    }

    private void ShowSection(Section s)
    {
        _section = s;
        List<PlayItem> source = s switch
        {
            Section.Live => _liveItems,
            Section.Movies => _movieItems ?? new(),
            Section.Series => _seriesItems ?? new(),
            Section.Local => _localItems,
            Section.Fav => GetFavorites(),
            _ => _liveItems
        };
        SectionTitle.Text = s switch
        {
            Section.Local => "Local",
            Section.Live => "Chaînes", Section.Movies => "Films",
            Section.Series => "Séries", Section.Fav => "Favoris", _ => ""
        };

        LocalActionsPanel.Visibility = s == Section.Local ? Visibility.Visible : Visibility.Collapsed;
        UpdateResumeButton();
        _items.Reset(source);
        _view = new ListCollectionView(_items) { Filter = FilterItem };
        ItemList.ItemsSource = _view;
        BuildCategoryFilter(source);
        UpdateCount();
    }

    private List<PlayItem> GetFavorites() { MarkFavorites(_state.Favorites); return _state.Favorites.ToList(); }

    // ============ LOCAL LIBRARY ============
    private void LoadLocalLibrary()
    {
        _localItems = (StateStore.Current.LocalLibrary ?? new())
            .Where(item => item.Kind == PlayItemKind.Local && !string.IsNullOrWhiteSpace(item.DirectUrl))
            .GroupBy(item => item.DirectUrl!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        MarkFavorites(_localItems);
    }

    private void SaveLocalLibrary()
    {
        StateStore.Current.LocalLibrary = _localItems
            .Where(item => item.Kind == PlayItemKind.Local && !string.IsNullOrWhiteSpace(item.DirectUrl))
            .ToList();
        StateStore.Save();
    }

    private void AddLocalFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Ajouter des fichiers locaux",
            Multiselect = true,
            Filter = "Médias vidéo/audio|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg;*.flv;*.mp3;*.flac;*.wav;*.aac;*.m4a;*.ogg;*.opus;*.wma;*.aiff;*.ape|Tous les fichiers|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var existing = _localItems.Select(i => i.DirectUrl ?? i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in dlg.FileNames.Where(File.Exists))
        {
            if (!existing.Add(file)) continue;
            _localItems.Add(PlayItem.FromLocalFile(file));
        }

        SaveLocalLibrary();
        MarkFavorites(_localItems);
        ShowSection(Section.Local);
    }

    private void RemoveLocalFile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not PlayItem item || item.Kind != PlayItemKind.Local) return;

        _localItems.RemoveAll(i => i.SameAs(item));
        var fav = _state.Favorites.FirstOrDefault(f => f.SameAs(item));
        if (fav != null) _state.Favorites.Remove(fav);
        if (_current?.SameAs(item) == true)
        {
            try { _videoBackend?.Stop(); } catch { }
            HideAudioVisualizer();
            _current = null;
            ShowOverlay("Fichier local retire.", spinning: false);
        }

        SaveLocalLibrary();
        StateStore.Save();
        ShowSection(Section.Local);
    }

    private void BuildCategoryFilter(List<PlayItem> source)
    {
        var cats = source.Select(c => c.CategoryName).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct().OrderBy(c => c).ToList();
        var items = new List<string> { AllCategories };
        items.AddRange(cats);
        CategoryCombo.ItemsSource = items;
        CategoryCombo.SelectedIndex = 0;
        _selectedCategory = AllCategories;
    }

    private bool FilterItem(object o)
    {
        if (o is not PlayItem c) return false;
        if (_selectedCategory != AllCategories && !string.IsNullOrEmpty(_selectedCategory)
            && !string.Equals(c.CategoryName, _selectedCategory, StringComparison.Ordinal)) return false;
        var q = SearchBox.Text;
        return string.IsNullOrWhiteSpace(q) || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        _selectedCategory = CategoryCombo.SelectedItem as string ?? AllCategories;
        _view?.Refresh(); UpdateCount();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _view?.Refresh(); UpdateCount(); }
    private void UpdateCount() => CountText.Text = (_view?.Count ?? 0).ToString();

    private void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is not PlayItem item) return;
        if (item.Kind == PlayItemKind.Series) OpenSeries(item);
        else Play(item);
    }

    // ============ FAVOURITES ============
    private void MarkFavorites(IEnumerable<PlayItem> list)
    {
        var set = _state.Favorites.Select(f => f.Kind + ":" + f.Id).ToHashSet();
        foreach (var it in list) it.IsFavorite = set.Contains(it.Kind + ":" + it.Id);
    }

    private void Fav_Toggle(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PlayItem item) return;
        var existing = _state.Favorites.FirstOrDefault(f => f.SameAs(item));
        if (item.IsFavorite && existing == null)
        {
            _state.Favorites.Add(new PlayItem
            {
                Kind = item.Kind, Id = item.Id, Name = item.Name, Icon = item.Icon,
                Ext = item.Ext, CategoryName = item.CategoryName, DirectUrl = item.DirectUrl, IsFavorite = true
            });
        }
        else if (!item.IsFavorite && existing != null)
        {
            _state.Favorites.Remove(existing);
            if (_section == Section.Fav) { _items.Remove(item); UpdateCount(); }
        }
        StateStore.Save();
    }

    // ============ SERIES DRILL-DOWN ============
    private async void OpenSeries(PlayItem series)
    {
        _seriesCts?.Cancel();
        var cts = new CancellationTokenSource();
        _seriesCts = cts;
        _seriesName = series.Name;
        SeriesPanelTitle.Text = series.Name;
        EpisodeList.ItemsSource = null;
        SeasonCombo.ItemsSource = null;
        SeriesPanel.Visibility = Visibility.Visible;
        CloseOverlayBtn.Visibility = Visibility.Visible;
        HideOsd(force: true);
        VideoStage.Cursor = Cursors.Arrow;
        FadeIn(SeriesPanel);
        UpdatePanelsVideo();
        try
        {
            _seriesInfo = await _iptv.GetSeriesInfoAsync(series.Id, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var seasons = _seriesInfo.Episodes.Keys.OrderBy(k => int.TryParse(k, out var n) ? n : 0).ToList();
            SeasonCombo.ItemsSource = seasons.Select(s => $"Saison {s}").ToList();
            if (seasons.Count > 0) SeasonCombo.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DebugConsole.Error("Infos série : " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_seriesCts, cts)) _seriesCts = null;
            cts.Dispose();
        }
    }

    private void Season_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_seriesInfo == null || SeasonCombo.SelectedIndex < 0) return;
        var key = _seriesInfo.Episodes.Keys.OrderBy(k => int.TryParse(k, out var n) ? n : 0).ElementAtOrDefault(SeasonCombo.SelectedIndex);
        if (key != null && _seriesInfo.Episodes.TryGetValue(key, out var eps)) EpisodeList.ItemsSource = eps;
    }

    private void Episode_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EpisodeList.SelectedItem is not Episode ep) return;
        Play(PlayItem.FromEpisode(ep, _seriesName));
        CloseSeriesPanel();
    }

    private void CloseSeries_Click(object sender, RoutedEventArgs e) => CloseSeriesPanel();
    private void CloseSeriesPanel()
    {
        _seriesCts?.Cancel();
        SeriesPanel.Visibility = Visibility.Collapsed;
        UpdateCloseOverlayButton();
        UpdatePanelsVideo();
    }

    // With the WriteableBitmap renderer there is no airspace, so panels compose
    // over the video natively — nothing to toggle.
    private void UpdatePanelsVideo() { }

    // ============ AUDIO VISUALIZER ============
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "alac", "aiff", "ape"
    };

    private bool IsAudioOnlyItem(PlayItem item)
    {
        if (item.Kind != PlayItemKind.Local) return false;
        var ext = item.Ext;
        if (string.IsNullOrWhiteSpace(ext))
            ext = System.IO.Path.GetExtension(item.DirectUrl ?? item.Id).TrimStart('.');
        return AudioExtensions.Contains(ext);
    }

    private void SetAudioVisualizer(PlayItem item)
    {
        if (IsAudioOnlyItem(item))
        {
            _audioEngine.Start(item.DirectUrl ?? item.Id);
            AudioVisualizerTitle.Text = item.Name;
            _audioDefaultDisc ??= AudioDiscBrush.ImageSource;
            ApplyAudioMetadata(item);
            AudioVisualizerLayer.Visibility = Visibility.Visible;
            _audioVisualStopwatch.Restart();
            _audioLastTickSeconds = 0;
            _audioBeatPulse = 0;

            // Render in lockstep with the display (144 Hz on a 144 Hz screen)
            // instead of a jittery 30 Hz DispatcherTimer.
            if (!_audioRenderHooked)
            {
                CompositionTarget.Rendering += OnAudioVisualFrame;
                _audioRenderHooked = true;
            }
        }
        else
        {
            HideAudioVisualizer();
        }
    }

    private void HideAudioVisualizer()
    {
        if (_audioRenderHooked)
        {
            CompositionTarget.Rendering -= OnAudioVisualFrame;
            _audioRenderHooked = false;
        }
        AudioVisualizerLayer.Visibility = Visibility.Collapsed;
        _audioVisualStopwatch.Reset();
        _audioEngine.Stop();
        AudioVisualizerShake.X = 0;
        AudioVisualizerShake.Y = 0;
        AudioVisualizerScale.ScaleX = 1;
        AudioVisualizerScale.ScaleY = 1;
        AudioCenterScale.ScaleX = 1;
        AudioCenterScale.ScaleY = 1;
        AudioSurface.ResetScene();
        ResetAudioMetadataUi();
        _audioBassEnergy = 0;
        _audioFullEnergy = 0;
        _audioBeatPulse = 0;
    }

    // Embedded tags (title / artist / album / cover): when present, the layout
    // splits — visualizer on the left, big cover with the credits on the
    // right. The centre disc ALWAYS keeps the Elycast Audio artwork.
    private void ApplyAudioMetadata(PlayItem item)
    {
        string? title = null, artist = null, album = null;
        ImageSource? cover = null;

        try
        {
            using var tagFile = TagLib.File.Create(item.DirectUrl ?? item.Id);
            var tag = tagFile.Tag;
            title = string.IsNullOrWhiteSpace(tag.Title) ? null : tag.Title.Trim();
            artist = string.IsNullOrWhiteSpace(tag.JoinedPerformers) ? null : tag.JoinedPerformers.Trim();
            album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album.Trim();

            var picture = tag.Pictures.FirstOrDefault(p => p.Data?.Data is { Length: > 0 });
            if (picture != null)
            {
                using var stream = new MemoryStream(picture.Data.Data);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.DecodePixelWidth = 1024;
                image.EndInit();
                image.Freeze();
                cover = image;
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Métadonnées audio illisibles : " + ex.Message);
        }

        if (cover == null && title == null && artist == null)
        {
            ResetAudioMetadataUi();
            return;
        }

        AudioMetaTitle.Text = title ?? item.Name;
        AudioMetaArtist.Text = artist ?? "";
        AudioMetaArtist.Visibility = artist == null ? Visibility.Collapsed : Visibility.Visible;
        AudioMetaAlbum.Text = album ?? "";
        AudioMetaAlbum.Visibility = album == null ? Visibility.Collapsed : Visibility.Visible;
        AudioCoverBrush.ImageSource = cover ?? _audioDefaultDisc;
        AudioMetaColumn.Width = new GridLength(1, GridUnitType.Star);
        AudioMetaPanel.Visibility = Visibility.Visible;
        AudioTitleBlock.Visibility = Visibility.Collapsed;
        DebugConsole.Info($"Métadonnées audio : {title ?? item.Name}" +
                          (artist != null ? $" — {artist}" : "") +
                          (cover != null ? " (pochette intégrée)" : " (sans pochette)"));
    }

    private void ResetAudioMetadataUi()
    {
        AudioMetaPanel.Visibility = Visibility.Collapsed;
        AudioMetaColumn.Width = new GridLength(0);
        AudioTitleBlock.Visibility = Visibility.Visible;
        AudioCoverBrush.ImageSource = null;
    }

    // One call per displayed frame: feed the engine the player state, pull the
    // latest analysis snapshot, and drive the surface + the light transforms.
    private void OnAudioVisualFrame(object? sender, EventArgs e)
    {
        if (AudioVisualizerLayer.Visibility != Visibility.Visible) return;

        var now = _audioVisualStopwatch.Elapsed.TotalSeconds;
        var dt = now - _audioLastTickSeconds;
        if (dt < 0.0005) return; // Rendering can fire twice for one frame.
        dt = Math.Min(dt, 0.1);
        _audioLastTickSeconds = now;

        // The UI thread owns the backend — the engine thread never touches mpv.
        _audioEngine.UpdatePlayerState(_videoBackend?.PositionMs ?? 0, _videoBackend?.IsPlaying == true);

        _audioEngine.ReadSnapshot(_audioSpectrumSnapshot, out _audioBassEnergy, out _audioFullEnergy);
        while (_audioEngine.TryDequeueBeat(out var strength))
        {
            _audioBeatPulse = Math.Max(_audioBeatPulse, 0.55 + strength * 0.45);
            AudioSurface.Beat(strength);
        }
        _audioBeatPulse *= Math.Pow(0.04, dt); // ≈ ×0.90 per 33 ms, frame-rate independent.

        AudioSurface.Advance(dt, _audioSpectrumSnapshot, _audioBassEnergy, _audioFullEnergy, _audioBeatPulse);

        AudioOuterRingRotate.Angle = (AudioOuterRingRotate.Angle + (12 + _audioBassEnergy * 80) * dt) % 360;
        AudioGlowRing.Opacity = 0.35 + _audioBassEnergy * 0.55;
        AudioVisualizerScale.ScaleX = AudioVisualizerScale.ScaleY = 1.0 + _audioFullEnergy * 0.02;
        AudioCenterScale.ScaleX = AudioCenterScale.ScaleY = 1.0 + _audioBeatPulse * 0.085;

        // Gentle: strong shakes exposed the layer edge (light seams at the
        // bottom); the background no longer moves at all, and the content
        // barely does.
        var shake = Math.Max(0, _audioBeatPulse - 0.6) * 5;
        AudioVisualizerShake.X = (_audioVisualRandom.NextDouble() - 0.5) * shake;
        AudioVisualizerShake.Y = (_audioVisualRandom.NextDouble() - 0.5) * shake;
    }



    // ============ PLAYBACK ============
    private void Play(PlayItem item)
    {
        if (_videoBackend == null) return;
        var generation = Interlocked.Increment(ref _playbackGeneration);
        CancelEpgRequest();
        _current = item;
        TopTitle.Text = item.Name;
        TopSubtitle.Text = string.IsNullOrWhiteSpace(item.CategoryName) ? item.KindLabel : $"{item.KindLabel} · {item.CategoryName}";
        EpgProgress.Visibility = Visibility.Collapsed;
        ResetSubtitleChoices();
        ResetAudioChoices();
        ShowOverlay("Chargement…", spinning: true);
        ShowOsd();

        // seek bar + skip buttons only make sense for finite media (films / episodes)
        bool seekable = item.Kind is PlayItemKind.Movie or PlayItemKind.Episode or PlayItemKind.Local;
        SeekArea.Visibility = seekable ? Visibility.Visible : Visibility.Collapsed;
        SkipBackBtn.Visibility = Visibility.Collapsed;
        SkipFwdBtn.Visibility = Visibility.Collapsed;
        if (seekable)
        {
            SeekSlider.Value = 0; CurTime.Text = "0:00"; TotTime.Text = "0:00";
            _progressTimer.Start();
        }
        else _progressTimer.Stop();

        try
        {
            var url = _iptv.GetStreamUrl(item);
            DebugConsole.Info($"Play [{item.KindLabel}] {item.Name}");
            _videoBackend.Volume = (int)VolumeSlider.Value;
            _videoBackend.Play(url);
            SetPlayIcon(false);
            SetAudioVisualizer(item);
            ApplyElySoundToBackend();
            ApplyElyFlowToBackend();
            RefreshOsdElySoundRow();
        }
        catch (Exception ex)
        {
            DebugConsole.Exception($"Échec du lancement de la lecture : {item.Name}", ex);
            ShowOverlay("Impossible de lancer la lecture.", spinning: false);
            TopSubtitle.Text = "Erreur";
            HideAudioVisualizer();
            return;
        }

        _state.LastPlayed = item;
        StateStore.Save();
        UpdateResumeButton();

        if (StatsOverlay.Visibility == Visibility.Visible) _statsTimer.Start();
        if (item.Kind == PlayItemKind.Live && _iptv.IsXtream)
        {
            var cts = new CancellationTokenSource();
            _epgCts = cts;
            _ = LoadEpgAsync(item.Id, generation, cts.Token);
        }
        else { _epgTimer.Stop(); EpgProgress.Visibility = Visibility.Collapsed; }
    }

    private void UpdateResumeButton()
    {
        if (_section == Section.Local)
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (_state.LastPlayed != null)
        {
            ResumeText.Text = "Reprendre — " + _state.LastPlayed.Name;
            ResumeButton.Visibility = Visibility.Visible;
        }
        else ResumeButton.Visibility = Visibility.Collapsed;
    }

    private void Resume_Click(object sender, RoutedEventArgs e) { if (_state.LastPlayed != null) Play(_state.LastPlayed); }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_videoBackend == null) return;
        if (_videoBackend.IsPlaying)
        {
            _videoBackend.Pause();
            SetPlayIcon(true);
            // Audio visualizer: the engine sees IsPlaying=false and lets the
            // spectrum settle — rendering keeps running, no freeze needed.
        }
        else if (_videoBackend.HasMedia)
        {
            _videoBackend.Resume();
            SetPlayIcon(false);
            if (_current != null && IsAudioOnlyItem(_current))
                AudioVisualizerLayer.Visibility = Visibility.Visible;
        }
        else if (ItemList.SelectedItem is PlayItem c && c.Kind != PlayItemKind.Series) Play(c);
        else if (_current != null) Play(_current);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _playbackGeneration);
        CancelEpgRequest();
        _videoBackend?.Stop(); _statsTimer.Stop(); _epgTimer.Stop(); _progressTimer.Stop();
        _videoBackend?.Clear();
        HideAudioVisualizer();
        SetPlayIcon(true);
        ShowOverlay("Lecture arrêtée", spinning: false);
        TopSubtitle.Text = "Arrêté";
        EpgProgress.Visibility = Visibility.Collapsed;
        SeekArea.Visibility = Visibility.Collapsed;
        OsdUpscaleRow.Visibility = Visibility.Collapsed;
        RefreshOsdElySoundRow();
        SkipBackBtn.Visibility = Visibility.Collapsed;
        SkipFwdBtn.Visibility = Visibility.Collapsed;
        ResetSubtitleChoices();
        ResetAudioChoices();
        ShowOsd();
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoBackend != null) _videoBackend.Volume = (int)e.NewValue;
        if (VolumePct != null) VolumePct.Text = $"{(int)e.NewValue}%";
        if (_connected && StateStore.Settings.RememberVolume) { StateStore.Settings.DefaultVolume = (int)e.NewValue; }
    }

    private void TryAutoReconnect()
    {
        if (!_connected || !StateStore.Settings.AutoReconnect || _current == null) return;
        var generation = _playbackGeneration;
        _reconnectTimer?.Stop();
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer!.Stop();
            if (_connected && generation == _playbackGeneration && _current != null)
            {
                DebugConsole.Warn("Reconnexion auto…");
                Play(_current);
            }
        };
        _reconnectTimer.Start();
    }

    // ============ ZAPPING ============
    private void Zap(int dir)
    {
        if (_view == null || _view.Count == 0) return;
        var idx = ItemList.SelectedIndex;
        idx = idx < 0 ? 0 : idx + dir;
        if (idx < 0) idx = _view.Count - 1;
        if (idx >= _view.Count) idx = 0;
        ItemList.SelectedIndex = idx;
        ItemList.ScrollIntoView(ItemList.SelectedItem);
        if (ItemList.SelectedItem is PlayItem p && p.Kind != PlayItemKind.Series) Play(p);
    }

    // ============ EPG ============
    private async Task LoadEpgAsync(string streamId, int generation, CancellationToken ct)
    {
        try
        {
            var list = await _iptv.GetShortEpgAsync(streamId, ct: ct);
            if (ct.IsCancellationRequested || generation != _playbackGeneration || _current?.Id != streamId) return;
            if (list.Count == 0) { EpgProgress.Visibility = Visibility.Collapsed; return; }
            var now = list.FirstOrDefault(x => x.Start <= DateTime.Now && DateTime.Now < x.End) ?? list[0];
            var next = list.SkipWhile(x => x != now).Skip(1).FirstOrDefault();
            TopSubtitle.Text = next != null
                ? $"● {now.Title}   ·   ensuite : {next.Title}"
                : $"● {now.Title}";
            EpgProgress.Tag = now;
            EpgProgress.Value = now.ProgressPercent;
            EpgProgress.Visibility = Visibility.Visible;
            _epgTimer.Start();
        }
        catch (OperationCanceledException) { }
    }

    private void CancelEpgRequest()
    {
        _epgCts?.Cancel();
        _epgCts?.Dispose();
        _epgCts = null;
        _epgTimer.Stop();
    }

    private void UpdateEpgProgress()
    {
        if (EpgProgress.Tag is EpgEntry now) EpgProgress.Value = now.ProgressPercent;
    }

    // ============ SEEK (VOD / episodes) ============
    private void UpdateSeek()
    {
        if (_videoBackend == null) return;
        var len = _videoBackend.LengthMs;       // ms
        var pos = _videoBackend.PositionMs;     // ms
        if (len <= 0) { TotTime.Text = "—"; UpdateSkipButtons(0, 0); return; }

        if (!_userSeeking)
        {
            SeekSlider.Maximum = len / 1000.0;
            SeekSlider.Value = Math.Clamp(pos / 1000.0, 0, SeekSlider.Maximum);
        }
        CurTime.Text = Fmt(pos);
        TotTime.Text = Fmt(len);
        UpdateSkipButtons(len, pos);
    }

    private void SeekToSlider()
    {
        if (_videoBackend == null || _videoBackend.LengthMs <= 0) return;
        _videoBackend.PositionMs = (long)(SeekSlider.Value * 1000);
        CurTime.Text = Fmt(_videoBackend.PositionMs);
    }

    private void SeekRelative(long deltaMs)
    {
        if (_videoBackend == null || _videoBackend.LengthMs <= 0) return;
        var t = Math.Clamp(_videoBackend.PositionMs + deltaMs, 0, _videoBackend.LengthMs);
        _videoBackend.PositionMs = t;
        SeekSlider.Value = t / 1000.0;
        CurTime.Text = Fmt(t);
        UpdateSkipButtons(_videoBackend.LengthMs, t);
    }

    private void SkipBack_Click(object sender, RoutedEventArgs e) => SeekRelative(-30_000);
    private void SkipFwd_Click(object sender, RoutedEventArgs e) => SeekRelative(30_000);

    private void UpdateSkipButtons(long lengthMs, long positionMs)
    {
        if (SeekArea.Visibility != Visibility.Visible || lengthMs <= 0)
        {
            SkipBackBtn.Visibility = Visibility.Collapsed;
            SkipFwdBtn.Visibility = Visibility.Collapsed;
            return;
        }

        // Avoid the annoying trap at the beginning: no "-30" button until it can do useful work.
        SkipBackBtn.Visibility = positionMs >= 30_000 ? Visibility.Visible : Visibility.Collapsed;
        SkipFwdBtn.Visibility = positionMs <= lengthMs - 30_000 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ============ SUBTITLES ============
    private void ResetSubtitleChoices()
    {
        _loadingSubtitleChoices = true;
        _subtitleOptions.Clear();
        _subtitleOptions.Add(new SubtitleOption(int.MinValue, "Sous-titres", "auto", IsAuto: true));
        _subtitleOptions.Add(new SubtitleOption(-1, "Désactivés", "off"));
        SubtitleCombo.SelectedIndex = 0;
        SubtitleCombo.Visibility = Visibility.Collapsed;
        _loadingSubtitleChoices = false;
    }

    private async void RefreshSubtitleTracksSoon(int generation)
    {
        await Task.Delay(1200);
        if (generation != _playbackGeneration || _current == null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshSubtitleTracks);
            return;
        }
        RefreshSubtitleTracks();
    }

    private void RefreshSubtitleTracks()
    {
        if (_videoBackend == null) return;

        _loadingSubtitleChoices = true;
        _subtitleOptions.Clear();
        _subtitleOptions.Add(new SubtitleOption(int.MinValue, "Sous-titres", "auto", IsAuto: true));
        _subtitleOptions.Add(new SubtitleOption(-1, "Désactivés", "off"));

        try
        {
            foreach (var track in _videoBackend.GetSubtitleTracks())
            {
                var name = string.IsNullOrWhiteSpace(track.Name) ? $"Piste {track.Id}" : track.Name;
                if (_subtitleOptions.Any(x => x.Id == track.Id)) continue;
                _subtitleOptions.Add(new SubtitleOption(track.Id, name, "name:" + name));
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Sous-titres indisponibles : " + ex.Message);
        }

        var preferred = StateStore.Settings.PreferredSubtitle;
        var selected =
            _subtitleOptions.FirstOrDefault(x => x.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
            _subtitleOptions.FirstOrDefault(x => preferred.StartsWith("name:", StringComparison.OrdinalIgnoreCase) &&
                                                 x.Name.Contains(preferred[5..], StringComparison.OrdinalIgnoreCase)) ??
            _subtitleOptions.FirstOrDefault();
        SubtitleCombo.SelectedItem = selected;
        SubtitleCombo.Visibility = _subtitleOptions.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        _loadingSubtitleChoices = false;

        if (selected is { IsAuto: false }) ApplySubtitle(selected, persist: false);
    }

    private void Subtitle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSubtitleChoices || SubtitleCombo.SelectedItem is not SubtitleOption opt) return;
        ApplySubtitle(opt, persist: true);
    }

    private void ApplySubtitle(SubtitleOption opt, bool persist)
    {
        try
        {
            if (_videoBackend != null && !opt.IsAuto) _videoBackend.SetSubtitleTrack(opt.Id);
            if (persist)
            {
                StateStore.Settings.PreferredSubtitle = opt.Key;
                StateStore.Save();
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Sélection sous-titres impossible : " + ex.Message);
        }
    }

    // ============ AUDIO TRACKS ============
    private void ResetAudioChoices()
    {
        _loadingAudioChoices = true;
        _audioOptions.Clear();
        _audioOptions.Add(new AudioOption(int.MinValue, "Piste audio", "auto", IsAuto: true));
        AudioCombo.SelectedIndex = 0;
        AudioCombo.Visibility = Visibility.Collapsed;
        _loadingAudioChoices = false;
    }

    private async void RefreshAudioTracksSoon(int generation)
    {
        await Task.Delay(1200);
        if (generation != _playbackGeneration || _current == null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshAudioTracks);
            return;
        }
        RefreshAudioTracks();
    }

    private void RefreshAudioTracks()
    {
        if (_videoBackend == null) return;

        _loadingAudioChoices = true;
        _audioOptions.Clear();
        _audioOptions.Add(new AudioOption(int.MinValue, "Piste audio", "auto", IsAuto: true));

        try
        {
            foreach (var track in _videoBackend.GetAudioTracks())
            {
                var name = string.IsNullOrWhiteSpace(track.Name) ? $"Piste {track.Id}" : track.Name;
                if (_audioOptions.Any(x => x.Id == track.Id)) continue;
                _audioOptions.Add(new AudioOption(track.Id, name, "name:" + name));
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Pistes audio indisponibles : " + ex.Message);
        }

        var preferred = StateStore.Settings.PreferredAudio;
        var selected =
            _audioOptions.FirstOrDefault(x => x.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
            _audioOptions.FirstOrDefault(x => preferred.StartsWith("name:", StringComparison.OrdinalIgnoreCase) &&
                                              x.Name.Contains(preferred[5..], StringComparison.OrdinalIgnoreCase)) ??
            _audioOptions.FirstOrDefault();
        AudioCombo.SelectedItem = selected;
        AudioCombo.Visibility = _audioOptions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loadingAudioChoices = false;

        if (selected is { IsAuto: false }) ApplyAudio(selected, persist: false);
    }

    private void Audio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioChoices || AudioCombo.SelectedItem is not AudioOption opt) return;
        ApplyAudio(opt, persist: true);
    }

    private void ApplyAudio(AudioOption opt, bool persist)
    {
        try
        {
            if (_videoBackend != null && !opt.IsAuto) _videoBackend.SetAudioTrack(opt.Id);
            if (persist)
            {
                StateStore.Settings.PreferredAudio = opt.Key;
                StateStore.Save();
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Sélection audio impossible : " + ex.Message);
        }
    }

    // ============ STATS ============
    private void StatsBtn_Click(object sender, RoutedEventArgs e) => SetStatsVisible(StatsOverlay.Visibility != Visibility.Visible);

    private void SetStatsVisible(bool on)
    {
        StatsOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StatsSwitch.IsChecked = on;
        StateStore.Settings.ShowStats = on; StateStore.Save();
        if (on && _videoBackend?.IsPlaying == true) _statsTimer.Start(); else _statsTimer.Stop();
    }

    private void UpdateStats()
    {
        if (_videoBackend == null || !_videoBackend.HasMedia) { StatsText.Text = "—"; return; }
        try
        {
            // Audio mode: video stats are meaningless (0x0) — show the stream
            // parameters and the live analysis of the visualizer instead.
            if (_current != null && IsAudioOnlyItem(_current))
            {
                StatsText.Text = BuildAudioStats();
                return;
            }

            var st = _videoBackend.GetStats();

            // Upscale factor: output (display) vs source (decoded) height.
            var ratio = st.SourceHeight > 0 ? (double)st.OutputHeight / st.SourceHeight : 0;
            string upscale;
            if (ratio <= 0) upscale = "—";
            else if (ratio > 1.02) upscale = $"▲ UPSCALE x{ratio:0.0#}  ({st.Scaler})";
            else if (ratio < 0.98) upscale = $"▼ downscale x{ratio:0.0#}  ({st.Scaler})";
            else upscale = $"1:1 (natif, {st.Scaler})";

            // The AI chain either upscales (display > source) or falls back to
            // its CAS/NVSharpen sharpening pass — make that visible.
            var shaders = string.IsNullOrEmpty(st.Shaders)
                ? "aucun (scaler mpv)"
                : st.Shaders + (ratio > 1.02 ? "  [upscale IA actif]" : "  [netteté seule, affichage ≤ source]");

            StatsText.Text =
                $"Backend    : {st.Backend}\n" +
                $"Décodage   : {st.Hwdec}\n" +
                $"Source     : {st.SourceWidth}x{st.SourceHeight}\n" +
                $"Sortie     : {st.OutputWidth}x{st.OutputHeight}\n" +
                $"Upscaling  : {upscale}\n" +
                $"Shaders IA : {shaders}\n" +
                $"ELYCOLOR   : {ActiveElyColorFilter().Name}\n" +
                $"FPS        : {st.Fps:0.0}\n" +
                $"Bitrate    : {st.BitrateKbps:0} kb/s\n" +
                $"Images vues: {st.DisplayedFrames}\n" +
                $"Perdues    : {st.DroppedFrames}\n" +
                $"État       : {st.State}";
        }
        catch { StatsText.Text = "stats indisponibles"; }
    }

    private string BuildAudioStats()
    {
        string codec = "—", rate = "—", channels = "—", bitrate = "—";
        if (_videoBackend is MpvHwndBackend mpv)
        {
            var c = mpv.GetOption("audio-codec-name");
            if (!string.IsNullOrWhiteSpace(c)) codec = c.ToUpperInvariant();
            var sr = mpv.GetOption("audio-params/samplerate");
            if (double.TryParse(sr, NumberStyles.Any, CultureInfo.InvariantCulture, out var hz))
                rate = $"{hz / 1000.0:0.#} kHz";
            var ch = mpv.GetOption("audio-params/channel-count");
            if (!string.IsNullOrWhiteSpace(ch)) channels = ch;
            var br = mpv.GetOption("audio-bitrate");
            if (double.TryParse(br, NumberStyles.Any, CultureInfo.InvariantCulture, out var bps) && bps > 0)
                bitrate = $"{bps / 1000.0:0} kb/s";
        }

        static string Gauge(double v)
        {
            var filled = (int)Math.Round(Math.Clamp(v, 0, 1) * 10);
            return new string('▮', filled) + new string('▯', 10 - filled) + $" {v * 100:0}%";
        }

        var bpm = _audioEngine.EstimatedBpm > 0 ? $"≈ {_audioEngine.EstimatedBpm:0}" : "détection…";
        var analysis = _audioEngine.HasAnalysis ? "FFT temps réel (thread dédié)" : "indisponible (format)";

        return
            $"Backend    : {_videoBackend?.Name}\n" +
            $"Codec      : {codec}\n" +
            $"Échantill. : {rate}\n" +
            $"Canaux     : {channels}\n" +
            $"Bitrate    : {bitrate}\n" +
            $"Analyse    : {analysis}\n" +
            $"Basses     : {Gauge(_audioBassEnergy)}\n" +
            $"Énergie    : {Gauge(_audioFullEnergy)}\n" +
            $"BPM        : {bpm}\n" +
            $"État       : {(_videoBackend?.IsPlaying == true ? "Lecture" : "Pause")}";
    }

    // ============ UPSCALING (mpv interne) ============
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

    // OSD quick selector (under the seek bar) — drives the same method setting.
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
        ("none", "Aucun"),
        ("anime4k-hq", "Anime4K — Qualité"),
        ("anime4k-fast", "Anime4K — Rapide"),
        ("anime4k-denoise", "Anime4K — Débruitage"),
        ("anime4k-deblur", "Anime4K — Déflou"),
        ("fsrcnnx", "FSRCNNX"),
        ("fsrcnnx-hq", "FSRCNNX HQ"),
        ("fsr", "AMD FSR + CAS"),
        ("nvscaler", "NVIDIA Image Scaling"),
        ("ewa_lanczossharp", "EWA Lanczos (net)"),
        ("lanczos", "Lanczos"),
        ("spline36", "Spline36"),
        ("mitchell", "Mitchell"),
        ("catmull_rom", "Catmull-Rom"),
        ("bilinear", "Bilinéaire"),
    };

    // Fills the OSD quick-selector with only the modes the user enabled in settings.
    private void PopulateOsdUpscaleCombo()
    {
        _syncingUpscale = true;
        OsdUpscaleCombo.Items.Clear();
        var enabled = StateStore.Settings.OsdUpscaleModes ?? new();
        foreach (var (id, label) in UpscaleCatalog)
            if (enabled.Contains(id))
                OsdUpscaleCombo.Items.Add(new ComboBoxItem { Tag = id, Content = label });
        SelectComboItemByTag(OsdUpscaleCombo, StateStore.Settings.UpscaleMethod);
        _syncingUpscale = false;
        RefreshOsdUpscaleRow();
    }

    // The OSD quick picker only makes sense while a stream is playing AND the user
    // has enabled at least one mode in settings — never on the idle screen.
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
                Content = label,
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

    // The GLSL chains (FSR, NVScaler, FSRCNNX, Anime4K + leurs compagnons CAS /
    // NVSharpen) are downloaded on demand before being applied — otherwise the
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
                DebugConsole.Step($"Upscaling: préparation des shaders ({string.Join(", ", missing)})…");
                await _shaderInstaller.EnsureAsync(s.UpscaleMethod, s.UpscaleSharpen);
            }
            catch (Exception ex)
            {
                DebugConsole.Warn("Upscaling: shaders indisponibles (" + ex.Message + ") — repli sur le scaler mpv.");
            }
        }

        // The backend (or the settings) may have changed during the download.
        if (_videoBackend is MpvHwndBackend mpv)
        {
            mpv.ApplyUpscaling(s.UpscaleTargetHeight, s.UpscaleMethod, s.UpscaleSharpen);
            DebugConsole.Info($"Upscaling -> cible={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")}, méthode={s.UpscaleMethod}, netteté={s.UpscaleSharpen}");
        }
    }

    // ============ ELYSOUND+ ============
    private static readonly ElySoundProfile[] BuiltInElySoundProfiles =
    {
        new() { Id = "cinema", Name = "ELYSOUND+ Cinema", Preamp = -4, Bass = 4, LowMid = -1, Mid = 3, Presence = 3, Treble = 1, Clarity = 2, Width = 10, Compressor = 12, Limiter = 88 },
        new() { Id = "music", Name = "ELYSOUND+ Music", Preamp = -3, Bass = 3, LowMid = 0, Mid = 0, Presence = 2, Treble = 2, Clarity = 2, Width = 8, Compressor = 5, Limiter = 90 },
        new() { Id = "anime", Name = "ELYSOUND+ Anime", Preamp = -3, Bass = 2, LowMid = -1, Mid = 3, Presence = 4, Treble = 3, Clarity = 3, Width = 7, Compressor = 8, Limiter = 89 },
        new() { Id = "voice", Name = "ELYSOUND+ Voix", Preamp = -2, Bass = -3, LowMid = -1, Mid = 4, Presence = 4, Treble = 0, Clarity = 1, Width = 0, Compressor = 16, Limiter = 88 },
        new() { Id = "night", Name = "ELYSOUND+ Nuit", Preamp = -5, Bass = -4, LowMid = 0, Mid = 3, Presence = 2, Treble = -2, Clarity = 0, Width = 0, Compressor = 28, Limiter = 80 },
        new() { Id = "bass", Name = "ELYSOUND+ Bass Boost", Preamp = -5, Bass = 8, LowMid = 2, Mid = -1, Presence = 1, Treble = 0, Clarity = 1, Width = 5, Compressor = 9, Limiter = 86 },
        new() { Id = "horror", Name = "ELYSOUND+ Horror", Preamp = -5, Bass = 5, LowMid = 2, Mid = 1, Presence = 4, Treble = 2, Clarity = 2, Width = 12, Compressor = 8, Limiter = 87 },
        new() { Id = "sport", Name = "ELYSOUND+ Sport", Preamp = -3, Bass = 2, LowMid = -2, Mid = 4, Presence = 3, Treble = 1, Clarity = 1, Width = 3, Compressor = 12, Limiter = 88 },
    };

    private IEnumerable<ElySoundProfile> AllElySoundProfiles()
    {
        foreach (var p in BuiltInElySoundProfiles) yield return p;
        yield return StateStore.Settings.ElySoundCustomProfile ?? ElySoundProfile.DefaultCustom();
    }

    private ElySoundProfile ActiveElySoundProfile()
    {
        var s = StateStore.Settings;
        if (string.Equals(s.ElySoundPresetId, "custom", StringComparison.OrdinalIgnoreCase))
            return s.ElySoundCustomProfile ??= ElySoundProfile.DefaultCustom();
        return BuiltInElySoundProfiles.FirstOrDefault(p => p.Id.Equals(s.ElySoundPresetId, StringComparison.OrdinalIgnoreCase))
               ?? BuiltInElySoundProfiles[0];
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
        Preamp = (int)Math.Round(ElySoundPreampSlider.Value),
        Bass = (int)Math.Round(ElySoundBassSlider.Value),
        LowMid = (int)Math.Round(ElySoundLowMidSlider.Value),
        Mid = (int)Math.Round(ElySoundMidSlider.Value),
        Presence = (int)Math.Round(ElySoundPresenceSlider.Value),
        Treble = (int)Math.Round(ElySoundTrebleSlider.Value),
        Clarity = (int)Math.Round(ElySoundClaritySlider.Value),
        Width = (int)Math.Round(ElySoundWidthSlider.Value),
        Compressor = (int)Math.Round(ElySoundCompressorSlider.Value),
        Limiter = (int)Math.Round(ElySoundLimiterSlider.Value)
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
        ElySoundLimiterSlider.Value = profile.Limiter;
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
        if (ElySoundWidthValue != null && ElySoundWidthSlider != null) ElySoundWidthValue.Text = ((int)Math.Round(ElySoundWidthSlider.Value)).ToString(CultureInfo.InvariantCulture);
        if (ElySoundCompressorValue != null && ElySoundCompressorSlider != null) ElySoundCompressorValue.Text = ((int)Math.Round(ElySoundCompressorSlider.Value)).ToString(CultureInfo.InvariantCulture);
        ElySoundLimiterValue.Text = ((int)Math.Round(ElySoundLimiterSlider.Value)).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private void ApplyElySoundToBackend() => ApplyElySoundToBackend(ActiveElySoundProfile());

    private void ApplyElySoundToBackend(ElySoundProfile profile)
    {
        if (_videoBackend is not MpvHwndBackend mpv) return;
        if (!StateStore.Settings.ElySoundEnabled)
        {
            mpv.SetOption("af", "");
            DebugConsole.Info("ELYSOUND+ -> off");
            return;
        }

        var channels = mpv.GetOption("audio-params/channel-count");
        var stereo = int.TryParse(channels, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count == 2;
        var graph = BuildElySoundLavfi(profile, StateStore.Settings.ElySoundVirtualSurround, stereo);
        if (!mpv.SetOption("af", "lavfi=[" + graph + "]"))
        {
            DebugConsole.Warn("ELYSOUND+ non appliqué : mpv a refusé le graphe audio.");
            return;
        }

        var stereoNote = !stereo && (profile.Width > 0 || StateStore.Settings.ElySoundVirtualSurround)
            ? " (élargissement stéréo ignoré : piste non stéréo)"
            : "";
        DebugConsole.Info("ELYSOUND+ -> " + profile.Name +
                          (StateStore.Settings.ElySoundVirtualSurround && stereo ? " + virtualiseur surround" : "") + stereoNote);
    }

    private static string BuildElySoundLavfi(ElySoundProfile p, bool surround, bool stereo)
    {
        var filters = new List<string>
        {
            "volume=volume=" + Db(p.Preamp),
            Eq(64, p.Bass * 0.42, 1.05),
            Eq(140, p.Bass * 0.26 + p.LowMid * 0.18, 1.0),
            Eq(320, p.LowMid * 0.32, 1.1),
            Eq(950, p.Mid * 0.34, 1.05),
            Eq(2600, p.Presence * 0.34, 1.0),
            Eq(5200, p.Treble * 0.32 + p.Clarity * 0.10, 1.0),
            Eq(9500, p.Treble * 0.22 + p.Clarity * 0.14, 1.15)
        };

        if (stereo && surround)
        {
            filters.Add("stereowiden=delay=14:feedback=0.08:crossfeed=0.16:drymix=0.92");
            filters.Add("extrastereo=m=" + F(1.06 + Math.Clamp(p.Width, 0, 60) / 180.0) + ":c=0");
        }
        else if (stereo && p.Width > 0)
        {
            filters.Add("extrastereo=m=" + F(1.0 + Math.Clamp(p.Width, 0, 60) / 150.0) + ":c=0");
        }

        if (p.Compressor > 0)
        {
            var ratio = 1.0 + Math.Clamp(p.Compressor, 0, 60) / 18.0;
            // acompressor expects a positive linear amplitude (not a dB value).
            var thresholdDb = -14 - Math.Clamp(p.Compressor, 0, 60) * 0.22;
            var threshold = Math.Clamp(Math.Pow(10, thresholdDb / 20.0), 0.00097563, 1.0);
            filters.Add("acompressor=threshold=" + F(threshold) + ":ratio=" + F(ratio) + ":attack=8:release=140:makeup=1");
        }

        filters.Add("alimiter=limit=" + F(Math.Clamp(p.Limiter, 70, 100) / 100.0));
        return string.Join(",", filters);

        static string Eq(int frequency, double gain, double width) =>
            "equalizer=f=" + frequency.ToString(CultureInfo.InvariantCulture) +
            ":t=q:w=" + F(width) +
            ":g=" + F(Math.Clamp(gain, -12, 12));
        static string Db(double db) => F(Math.Clamp(db, -12, 6)) + "dB";
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
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

    // ============ ELYFLOW ============
    private void LoadElyFlowIntoUi()
    {
        var s = StateStore.Settings;
        ElyFlowEnabledSwitch.IsChecked = s.ElyFlowEnabled;
        ElyFlowRtxVsrSwitch.IsChecked = s.ElyFlowRtxVsrEnabled;
        SelectComboItemByTag(ElyFlowEngineCombo, s.ElyFlowEngine);
        SelectComboItemByTag(ElyFlowTargetCombo, s.ElyFlowTargetFps);
        UpdateElyFlowTargetAvailability();
        ElyFlowLiveBufferSlider.Value = Math.Clamp(s.ElyFlowLiveBufferSeconds, 0.5, 5.0);
        UpdateElyFlowGate();
        UpdateElyFlowStatus();
        UpdateElyFlowBufferLabel();
    }

    /// <summary>
    /// ELYFLOW n'est activable que lorsque le renderer ELYCORE est le backend
    /// vidéo choisi : le switch est verrouillé (avec un indice) sinon.
    /// </summary>
    private void UpdateElyFlowGate()
    {
        if (ElyFlowEnabledSwitch == null) return;
        var elyCore = string.Equals(StateStore.Settings.VideoBackend, "elycore", StringComparison.OrdinalIgnoreCase);
        ElyFlowEnabledSwitch.IsEnabled = elyCore;
        if (ElyFlowGateHint != null)
            ElyFlowGateHint.Visibility = elyCore ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ElyFlowSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SaveElyFlowFromUi();
        UpdateElyFlowTargetAvailability();
        ApplyElyFlowToBackend();
    }

    private void ElyFlowCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        SaveElyFlowFromUi();
        UpdateElyFlowTargetAvailability();
        ApplyElyFlowToBackend();
    }

    private void ElyFlowSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ElyFlowLiveBufferValue != null) UpdateElyFlowBufferLabel();
        if (_initializing) return;
        SaveElyFlowFromUi();
    }

    private void SaveElyFlowFromUi()
    {
        var s = StateStore.Settings;
        s.ElyFlowEnabled = ElyFlowEnabledSwitch.IsChecked == true;
        s.ElyFlowRtxVsrEnabled = ElyFlowRtxVsrSwitch.IsChecked == true;
        s.ElyFlowEngine = TagOf(ElyFlowEngineCombo, s.ElyFlowEngine);
        s.ElyFlowTargetFps = TagOf(ElyFlowTargetCombo, s.ElyFlowTargetFps);
        s.ElyFlowLiveBufferSeconds = Math.Round(ElyFlowLiveBufferSlider.Value, 1);
        StateStore.Save();
        UpdateElyFlowStatus();
    }

    private void UpdateElyFlowBufferLabel()
    {
        if (ElyFlowLiveBufferValue == null || ElyFlowLiveBufferSlider == null) return;
        ElyFlowLiveBufferValue.Text = ElyFlowLiveBufferSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }

    private void UpdateElyFlowTargetAvailability()
    {
        if (ElyFlowTargetCombo == null || ElyFlowEngineCombo == null) return;
        var nativeFruc = TagOf(ElyFlowEngineCombo, "nvidia-fruc") == "nvidia-fruc";
        if (ElyFlowRtxVsrSwitch != null)
            ElyFlowRtxVsrSwitch.IsEnabled = nativeFruc;
        ElyFlowTargetCombo.IsEnabled = !nativeFruc;
        ElyFlowTargetCombo.ToolTip = nativeFruc
            ? "Le runtime NVIDIA FRUC est un interpolateur ×2. La cadence effective dépend de la source."
            : "Cadence demandée à mpv display-resample.";
        if (ElyFlowTargetLabel != null)
            ElyFlowTargetLabel.Opacity = nativeFruc ? 0.55 : 1.0;
    }

    private void UpdateElyFlowStatus()
    {
        if (ElyFlowStatusText == null) return;
        var st = ElyFlowService.Probe();
        var gpu = st.NvidiaGpu ? st.GpuName : "NVIDIA non détecté";
        var of = st.OpticalFlowDriver ? "driver Optical Flow OK" : "nvofapi64.dll absent";
        var fruc = st.FrucRuntime ? "runtime FRUC OK : " + st.FrucPath : "runtime FRUC absent (NvOFFRUC64.dll)";
        var native = st.NativeDllLoaded ? "ElyFlow.Native OK : " + st.NativePath : "ElyFlow.Native absent";
        var version = string.IsNullOrWhiteSpace(st.RuntimeVersion) ? "unknown" : st.RuntimeVersion;
        var reason = string.IsNullOrWhiteSpace(st.UnavailableReason) ? "Disponible" : st.UnavailableReason;
        ElyFlowStatusText.Text =
            $"GPU : {gpu}\n" +
            $"Driver : {st.DriverVersion}\n" +
            $"Optical Flow : {of}\n" +
            $"Runtime FRUC : {fruc}\n" +
            $"Version runtime : {version}\n" +
            $"Backend natif : {native}\n" +
            $"Code backend : {st.NativeStatusCode}\n" +
            $"Raison : {reason}\n" +
            $"Statut : {st.BackendStatus}";
    }

    private void ApplyElyFlowToBackend()
    {
        if (_videoBackend is not MpvHwndBackend mpv) return;
        var s = StateStore.Settings;

        // ELYFLOW (FRUC + VSR natif) vit exclusivement dans le renderer
        // ELYCORE. Sur les autres backends la fonctionnalité est inapplicable
        // et le backend choisi par l'utilisateur n'est JAMAIS remplacé ici —
        // l'ancien basculement automatique rtx-sdk -> renderer natif recréait
        // le pipeline en pleine lecture (écran noir) et écrasait le choix fait
        // dans les réglages.
        if (!mpv.IsElyCoreRenderer)
        {
            ResetMpvFramePacing(mpv);
            return;
        }

        // Never enable mpv interpolation on top of the native renderer: FRUC
        // already presents one generated frame between every source frame.
        var frucWanted = s.ElyFlowEnabled &&
                         s.ElyFlowEngine.Equals("nvidia-fruc", StringComparison.OrdinalIgnoreCase);
        mpv.ConfigureElyCoreFruc(frucWanted);
        mpv.ConfigureElyCoreVsr(s.ElyFlowEnabled && s.ElyFlowRtxVsrEnabled);

        if (s.ElyFlowEnabled &&
            s.ElyFlowEngine.Equals("mpv-pacing", StringComparison.OrdinalIgnoreCase))
        {
            var effectiveTarget = ApplyMpvFramePacing(mpv, s.ElyFlowTargetFps, s.ElyFlowLiveBufferSeconds);
            DebugConsole.Info($"ELYFLOW mpv pacing -> {effectiveTarget} fps effectifs");
            return;
        }

        ResetMpvFramePacing(mpv);
        if (frucWanted)
        {
            ApplyMpvLiveBuffer(mpv, s.ElyFlowLiveBufferSeconds);
            DebugConsole.Info("ELYFLOW NVIDIA FRUC -> actif sur le renderer ELYCORE.");
        }
        else
        {
            DebugConsole.Info("ELYFLOW -> off (renderer ELYCORE conservé, FRUC arrêté à chaud).");
        }
        UpdateElyFlowStatus();
    }

    private static string ApplyMpvFramePacing(MpvHwndBackend mpv, string targetFps, double bufferSeconds)
    {
        var normalized = NormalizeFps(targetFps);
        var requested = normalized == "60000/1001"
            ? 60000.0 / 1001.0
            : double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 60.0;
        var refresh = mpv.GetDisplayRefreshRate();
        var effective = refresh > 0 ? Math.Min(requested, refresh) : Math.Min(requested, 120.0);
        var effectiveText = effective.ToString("0.###", CultureInfo.InvariantCulture);
        if (effective + 0.5 < requested)
            DebugConsole.Info($"ELYFLOW mpv: cible {requested:0.###} plafonnée au moniteur ({refresh:0.###} Hz).");

        mpv.SetOption("video-sync", "display-resample");
        mpv.SetOption("interpolation", "yes");
        mpv.SetOption("tscale", "oversample");
        mpv.SetOption("override-display-fps", effectiveText);
        ApplyMpvLiveBuffer(mpv, bufferSeconds);
        return effectiveText;
    }

    private static void ResetMpvFramePacing(MpvHwndBackend mpv)
    {
        mpv.SetOption("video-sync", "audio");
        mpv.SetOption("interpolation", "no");
        mpv.SetOption("tscale", "oversample");
        // Numeric mpv option: an empty string is rejected with
        // MPV_ERROR_PROPERTY_FORMAT (-9). Zero restores auto detection.
        mpv.SetOption("override-display-fps", "0");
        mpv.SetOption("cache", "auto");
        mpv.SetOption("cache-secs", "0");
        mpv.SetOption("cache-pause-wait", "1");
    }

    private static void ApplyMpvLiveBuffer(MpvHwndBackend mpv, double bufferSeconds)
    {
        var seconds = Math.Clamp(bufferSeconds, 0.5, 5.0).ToString("0.0", CultureInfo.InvariantCulture);
        mpv.SetOption("cache", "yes");
        mpv.SetOption("cache-secs", seconds);
        mpv.SetOption("cache-pause-wait", Math.Min(Math.Max(bufferSeconds, 0.5), 1.5).ToString("0.0", CultureInfo.InvariantCulture));
    }

    private static string NormalizeFps(string fps) => fps switch
    {
        "60000/1001" => "60000/1001",
        "59.94" => "60000/1001",
        _ => double.TryParse(fps, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v.ToString("0.###", CultureInfo.InvariantCulture)
            : "60"
    };

    // ============ ELYCOLOR ============
    // mpv equalizer values, range -100..100. Guiding principles: brightness is
    // a black-floor offset (washes the image — avoid, prefer gamma), contrast
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
            // aplats; the hue shift tinted skin — gone. Slight gamma lift
            // keeps line-art detail in dark scenes.
            Id = "elycolor-anime",
            Name = "ELYCOLOR Animé Vif",
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
            Name = "ELYCOLOR Film Éclatant",
            IncludeVideoPipeline = false,
            Saturation = 12,
            Brightness = 0,
            Contrast = 8,
            Gamma = -2
        },
        new()
        {
            // Cold desaturated dread. The old contrast 25 / gamma -12 /
            // brightness -7 trio crushed everything the genre lives on —
            // horror needs *readable* shadows to be scary.
            Id = "elycolor-horror",
            Name = "ELYCOLOR Horreur",
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
            Name = "ELYCOLOR Noir Dense",
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
            Name = "ELYCOLOR Doux Confort",
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
                ElyColorCustomSelectCombo.Items.Add(new ComboBoxItem { Tag = filter.Id, Content = filter.Name });
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
            combo.Items.Add(new ComboBoxItem { Tag = filter.Id, Content = filter.Name });
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
            ElyColorNameBox.Text = filter.Name == "ELYCOLOR Off" ? NextElyColorName() : filter.Name;
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

    // ============ SETTINGS ============
    private void LoadSettingsIntoUi()
    {
        _initializing = true;
        var s = StateStore.Settings;
        AccentSwatches.ItemsSource = ThemeManager.Presets;
        AccentHexBox.Text = s.AccentColor;
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
            var progress = new Progress<string>(msg => MpvStatusText.Text = msg);
            var dll = await _mpvInstaller.InstallLatestAsync(progress);
            MpvStatusText.Text = "mpv installé";
            DebugConsole.Success("libmpv installé : " + dll);

            StateStore.Settings.VideoBackend = "mpv-gpu";
            SelectComboItemByTag(BackendCombo, "mpv-gpu");
            StateStore.Save();
            RecreateVideoBackend(replayCurrent: true);
        }
        catch (Exception ex)
        {
            MpvStatusText.Text = "installation impossible";
            DebugConsole.Error("mpv : " + ex.Message);
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
        MpvStatusText.Text = string.IsNullOrWhiteSpace(dll) ? "mpv non installé" : "mpv installé";
        InstallMpvBtn.Content = string.IsNullOrWhiteSpace(dll) ? "Installer mpv" : "Réinstaller";
    }

    private void RecreateVideoBackend(bool replayCurrent)
    {
        DebugConsole.Step("Backend: changement demandé, démontage de l'ancien backend…");

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
            // 1. Stop playback so no new native frames are decoded or rendered.
            try { DebugConsole.Step("Backend: arrêt de la lecture…"); old.Stop(); }
            catch (Exception ex) { DebugConsole.Exception("Backend: échec de l'arrêt", ex); }

            // 2. Detach all managed event handlers from the OLD backend so late
            //    Playing/Failed/Ended/Paused events cannot reach the UI.
            try { DebugConsole.Step("Backend: détachement des événements…"); DetachBackendEvents(old); }
            catch (Exception ex) { DebugConsole.Exception("Backend: échec du détachement des événements", ex); }

            // 3. Dispose the OLD backend WHILE its video surface is still in the
            //    Visual Tree. The mpv render context (OpenGL) must be freed while
            //    its GL context is still alive — Dispose() internally detaches the
            //    native render callback before freeing libmpv.
            try { DebugConsole.Step("Backend: disposition de l'ancien backend…"); old.Dispose(); }
            catch (Exception ex) { DebugConsole.Exception("Backend: échec de la disposition", ex); }

            // 4. Only now remove the surface from the Visual Tree. This triggers
            //    the GL/D3D context teardown, which is safe because no native
            //    handle still references it.
            try { DebugConsole.Step("Backend: retrait de la surface vidéo du Visual Tree…"); VideoHost.Content = null; }
            catch (Exception ex) { DebugConsole.Exception("Backend: échec du retrait de la surface", ex); }
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
                DebugConsole.Step("Backend: création du nouveau backend…");
                InitVideoBackend();
                if (_videoBackend != null) _videoBackend.Volume = volume;
                UpdateStats();
                DebugConsole.Success("Backend: nouveau backend prêt.");
            }
            catch (Exception ex)
            {
                DebugConsole.Exception("Backend: échec de création du nouveau backend", ex);
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
        SettingsPlaybackPanel.Visibility = category == "playback" ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpscalingPanel.Visibility = category == "upscaling" ? Visibility.Visible : Visibility.Collapsed;
        SettingsElyColorPanel.Visibility = category == "elycolor" ? Visibility.Visible : Visibility.Collapsed;
        SettingsElySoundPanel.Visibility = category == "elysound" ? Visibility.Visible : Visibility.Collapsed;
        SettingsElyFlowPanel.Visibility = category == "elyflow" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSystemPanel.Visibility = category == "system" ? Visibility.Visible : Visibility.Collapsed;

        MarkSettingsCategory(SettingsAppearanceBtn, category == "appearance");
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
            case Section.Local: NavLocal.IsChecked = true; break;
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
            Title = "Choisir Magpie.exe"
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
            var progress = new Progress<string>(msg => UpscalerStatusText.Text = msg);
            var exe = await _magpie.InstallLatestAsync(progress);
            MagpiePathBox.Text = exe;
            UpdateUpscalerStatus();
            DebugConsole.Success("Magpie installé : " + exe);
        }
        catch (Exception ex)
        {
            UpscalerStatusText.Text = "Installation Magpie impossible";
            DebugConsole.Error("Magpie : " + ex.Message);
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
            UpscalerStatusText.Text = "Installe Magpie d'abord";
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
                throw new InvalidOperationException("Magpie n'a pas pu démarrer.");

            // Let Magpie initialize its hidden window/tray state before sending the hotkey.
            await Task.Delay(250);
            var hwnd = MagpieUpscalerService.HwndFor(this);
            await _magpie.ActivateScalingAsync(hwnd, StateStore.Settings.MagpieHotkey);
            UpscalerStatusText.Text = _upscalerActive
                ? $"Arrêt demandé ({UpscalerEngineLabel()})"
                : $"Activation demandée ({UpscalerEngineLabel()})";
            await Task.Delay(900);
            RefreshMagpieActiveState();
            DebugConsole.Success("Toggle upscaling Magpie / " + UpscalerEngineLabel());
        }
        catch (Exception ex)
        {
            UpscalerStatusText.Text = "Activation Magpie impossible";
            DebugConsole.Error("Magpie : " + ex.Message);
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
            ? "Upscaling externe désactivé"
            : _upscalerActive
                ? $"{UpscalerEngineLabel()} actif"
                : located != null
                    ? $"{UpscalerEngineLabel()} prêt"
                : "Magpie requis pour l'upscaling GPU";
    }

    private void RefreshMagpieActiveState(bool updateText = true)
    {
        _upscalerActive = _magpie.FindScalingWindow() != 0;
        if (UpscalerBtn?.Content is TextBlock text)
            text.Text = _upscalerActive ? "ON" : "FSR";
        if (updateText && UpscalerStatusText != null && StateStore.Settings.UpscalerEngine != "off")
            UpscalerStatusText.Text = _upscalerActive ? $"{UpscalerEngineLabel()} actif" : $"{UpscalerEngineLabel()} prêt";
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
            // stats box for the duration of the drag (no popup reopen — that path is
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
            // (maximize, Win+arrow snap, DPI) — keep the overlay glued to the window.
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
        _ => "désactivé"
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

    // ============ FULLSCREEN / KEYS ============
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            UpdateOsdSafeArea();
            _videoBackend?.SetFullscreen(true);
            // remember windowed bounds
            _prevState = WindowState;
            _prevLeft = Left; _prevTop = Top; _prevW = Width; _prevH = Height;

            // hide chrome
            NavRail.Visibility = Visibility.Collapsed;
            Sidebar.Visibility = Visibility.Collapsed;
            NavColumn.Width = new GridLength(0);
            SidebarColumn.Width = new GridLength(0);
            TitleRow.Height = new GridLength(0);
            TitleBar.Visibility = Visibility.Collapsed;
            VideoArea.Margin = new Thickness(0);

            // cover the whole monitor (incl. taskbar)
            WindowState = WindowState.Normal;
            var r = GetMonitorRectDip();
            if (r.HasValue) { Left = r.Value.X; Top = r.Value.Y; Width = r.Value.Width; Height = r.Value.Height; }
            else WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = false;
            if (_overlayWindow != null) _overlayWindow.Topmost = false;
            UpdateOsdSafeArea();
        }
        else
        {
            UpdateOsdSafeArea();
            _videoBackend?.SetFullscreen(false);
            Topmost = false;
            if (_overlayWindow != null) _overlayWindow.Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            NavRail.Visibility = Visibility.Visible;
            Sidebar.Visibility = Visibility.Visible;
            NavColumn.Width = new GridLength(76);
            SidebarColumn.Width = new GridLength(330);
            TitleRow.Height = new GridLength(44);
            TitleBar.Visibility = Visibility.Visible;
            VideoArea.Margin = new Thickness(7, 0, 14, 14);

            if (_prevState == WindowState.Maximized) WindowState = WindowState.Maximized;
            else { WindowState = WindowState.Normal; Left = _prevLeft; Top = _prevTop; Width = _prevW; Height = _prevH; }
            UpdateOsdSafeArea();
            VideoStage.Cursor = Cursors.Arrow;
        }
    }

    // ============ OSD (auto-hide controls) ============
    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (IsPanelOpen()) return;
        ShowOsd();
    }
    private void Stage_MouseLeave(object sender, MouseEventArgs e) => SchedulePointerLeaveCheck();

    private void OnVideoSurfacePointerActivity()
    {
        if (Dispatcher.CheckAccess()) ShowOsd();
        else Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ShowOsd));
    }

    private void OnVideoSurfacePointerLeft()
    {
        if (Dispatcher.CheckAccess()) SchedulePointerLeaveCheck();
        else Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(SchedulePointerLeaveCheck));
    }

    private void SchedulePointerLeaveCheck()
    {
        // Crossing between the child HWND and its WPF overlay emits a native
        // MouseLeave even though the pointer never left the player. Recheck the
        // real screen position after Windows has completed the HWND transition.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsCursorOverVideoStage() && _videoBackend?.IsPlaying == true)
                HideOsd();
        }));
    }

    private bool IsCursorOverVideoStage()
    {
        if (!VideoStage.IsVisible || VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0)
            return false;
        try
        {
            if (!GetCursorPos(out var cursor)) return false;
            var point = VideoStage.PointFromScreen(new Point(cursor.X, cursor.Y));
            return point.X >= 0 && point.Y >= 0 &&
                   point.X < VideoStage.ActualWidth && point.Y < VideoStage.ActualHeight;
        }
        catch { return false; }
    }

    private bool IsPanelOpen() =>
        SettingsPanel.Visibility == Visibility.Visible || SeriesPanel.Visibility == Visibility.Visible;

    private void ShowOsd()
    {
        if (IsPanelOpen()) return;
        UpdateOsdSafeArea();
        if (!_osdVisible)
        {
            _osdVisible = true;
            Osd.IsHitTestVisible = true;
            Osd.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
        }
        VideoStage.Cursor = Cursors.Arrow;
        _osdTimer.Stop(); _osdTimer.Start();
    }

    private void HideOsd() => HideOsd(force: false);

    private void HideOsd(bool force)
    {
        _osdTimer.Stop();
        // keep the controls visible whenever we're not actively playing
        if (!force && (_videoBackend == null || !_videoBackend.IsPlaying)) return;
        if (!force && Osd.IsMouseOver) { _osdTimer.Start(); return; }
        if (!_osdVisible) return;
        _osdVisible = false;
        Osd.IsHitTestVisible = false;
        Osd.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        VideoStage.Cursor = IsPanelOpen() ? Cursors.Arrow : Cursors.None;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo mi);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt point);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public Rect rcMonitor; public Rect rcWork; public uint dwFlags; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointInt { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private System.Windows.Rect? GetMonitorRectDip()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var mon = MonitorFromWindow(hwnd, 2 /*NEAREST*/);
            var mi = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(mon, ref mi)) return null;
            var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tl = m.Transform(new Point(mi.rcMonitor.Left, mi.rcMonitor.Top));
            var br = m.Transform(new Point(mi.rcMonitor.Right, mi.rcMonitor.Bottom));
            return new System.Windows.Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
        }
        catch { return null; }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsPanelOpen()) { CloseOverlay_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape && _isFullscreen) { ToggleFullscreen(); return; }
        if (e.Key == Key.F11) { ToggleFullscreen(); return; }
        if (!PlayerView.IsVisible || SettingsPanel.Visibility == Visibility.Visible) return;
        if (SearchBox.IsKeyboardFocusWithin) return;

        if (e.Key == Key.Space) { PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (SeekArea.Visibility == Visibility.Visible && e.Key == Key.Left) { SeekRelative(-30_000); ShowOsd(); e.Handled = true; }
        else if (SeekArea.Visibility == Visibility.Visible && e.Key == Key.Right) { SeekRelative(30_000); ShowOsd(); e.Handled = true; }
        else if (StateStore.Settings.ZapWithArrows && e.Key == Key.Up) { Zap(-1); e.Handled = true; }
        else if (StateStore.Settings.ZapWithArrows && e.Key == Key.Down) { Zap(1); e.Handled = true; }
        else if (e.Key == Key.Enter && ItemList.SelectedItem is PlayItem c)
        {
            if (c.Kind == PlayItemKind.Series) OpenSeries(c); else Play(c);
        }
    }

    // ============ OVERLAY / SPINNER ============
    private void ShowOverlay(string text, bool spinning)
    {
        VideoOverlay.Visibility = Visibility.Visible;
        OverlayText.Text = text;
        BigSpinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        IdlePlay.Visibility = spinning ? Visibility.Collapsed : Visibility.Visible;
        if (spinning) StartSpin(BigSpinner); else StopSpin(BigSpinner);
    }

    private void HideOverlay() { StopSpin(BigSpinner); VideoOverlay.Visibility = Visibility.Collapsed; }

    private void SetPlayIcon(bool showPlay) =>
        PlayPauseIcon.Data = showPlay
            ? Geometry.Parse("M 5,3 L 12,8 L 5,13 Z")
            : Geometry.Parse("M 4,3 L 6.5,3 L 6.5,13 L 4,13 Z M 9.5,3 L 12,3 L 12,13 L 9.5,13 Z");

    private static void StartSpin(UIElement target)
    {
        if (target.RenderTransform is not RotateTransform rt)
        {
            rt = new RotateTransform(); target.RenderTransform = rt; target.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        rt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private static void StopSpin(UIElement target)
    {
        if (target.RenderTransform is RotateTransform rt) rt.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void PassBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        PassPlaceholder.Visibility = string.IsNullOrEmpty(PassBox.Password) ? Visibility.Visible : Visibility.Collapsed;

    // ============ CONSOLE COMMANDS ============
    private void Console_Click(object sender, RoutedEventArgs e) => DebugConsole.Toggle();

    private void RegisterConsoleCommands()
    {
        DebugConsole.RegisterCommand("channels", "Nombre de chaînes chargées",
            _ => $"{_liveItems.Count} chaînes · {_movieItems?.Count ?? 0} films · {_seriesItems?.Count ?? 0} séries");
        DebugConsole.RegisterCommand("favorites", "Liste les favoris",
            _ => _state.Favorites.Count == 0 ? "Aucun favori." : string.Join("\n", _state.Favorites.Select(f => $"  ★ {f.Name} ({f.KindLabel})")));
        DebugConsole.RegisterCommand("categories", "Liste les catégories de la section active",
            _ => string.Join("\n", _items.Select(c => c.CategoryName).Distinct().OrderBy(c => c)));
        DebugConsole.RegisterCommand("search", "search <texte>", args =>
        {
            var q = string.Join(' ', args);
            Dispatcher.Invoke(() => SearchBox.Text = q);
            return $"Filtre : '{q}'";
        });
        DebugConsole.RegisterCommand("play", "play <n> — joue le n-ième élément visible", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var n)) return "Usage : play <index>";
            return Dispatcher.Invoke(() =>
            {
                if (_view == null || n < 1 || n > _view.Count) return $"Hors limites (1..{_view?.Count ?? 0}).";
                if (_view.GetItemAt(n - 1) is PlayItem c && c.Kind != PlayItemKind.Series) { Play(c); return $"Lecture : {c.Name}"; }
                return "Élément non jouable.";
            });
        });
        DebugConsole.RegisterCommand("accent", "accent <#hex> — change la couleur d'accent", args =>
        {
            if (args.Length == 0) return "Usage : accent #FF8B5CF6";
            Dispatcher.Invoke(() => { ThemeManager.Apply(args[0]); AccentHexBox.Text = args[0]; StateStore.Settings.AccentColor = args[0]; StateStore.Save(); });
            return "Accent appliqué : " + args[0];
        });
        DebugConsole.RegisterCommand("stats", "stats on | off", args =>
        {
            var on = args.FirstOrDefault()?.ToLowerInvariant() != "off";
            Dispatcher.Invoke(() => SetStatsVisible(on));
            return $"Stats {(on ? "activées" : "désactivées")}.";
        });
        DebugConsole.RegisterCommand("mpv", "mpv <propriété> [valeur] — lit/écrit une propriété mpv (ex: mpv vf, mpv hwdec-current)", args =>
        {
            if (args.Length == 0) return "Usage : mpv <propriété> [valeur]";
            return Dispatcher.Invoke(() =>
            {
                if (_videoBackend is not MpvHwndBackend mpv) return "Backend mpv inactif.";
                var name = args[0];
                if (args.Length == 1)
                {
                    var value = mpv.GetOption(name);
                    return string.IsNullOrEmpty(value) ? $"{name} = (vide)" : $"{name} = {value}";
                }
                var newValue = string.Join(' ', args.Skip(1));
                mpv.SetOption(name, newValue);
                return $"{name} <- {newValue}";
            });
        });
        DebugConsole.RegisterCommand("ui", "ui show | hide", args =>
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "show";
            Dispatcher.Invoke(() => Visibility = mode == "hide" ? Visibility.Hidden : Visibility.Visible);
            return $"Fenêtre {(mode == "hide" ? "masquée" : "affichée")}.";
        });
        DebugConsole.RegisterCommand("mpv", "mpv <prop> [valeur] — lit/règle une option mpv à chaud (ex: mpv scale bilinear)", args =>
        {
            if (_videoBackend is not MpvHwndBackend mpv) return "Backend mpv (HWND) non actif.";
            if (args.Length == 0) return "Usage : mpv <prop> [valeur]  ·  ex: mpv scale ewa_lanczossharp | mpv sharpen 1.0";
            var prop = args[0];
            if (args.Length == 1)
            {
                string val = "";
                Dispatcher.Invoke(() => val = mpv.GetOption(prop));
                return $"{prop} = {val}";
            }
            var value = string.Join(" ", args.Skip(1));
            Dispatcher.Invoke(() => mpv.SetOption(prop, value));
            return $"{prop} -> {value}";
        });
        DebugConsole.RegisterCommand("playfile", "playfile [chemin] — lit un fichier local (diagnostic renderer)", args =>
        {
            // No arg -> a known-good local MP4 that ships with Windows. This rules
            // out Xtream / IPTV / network: if it crashes the same way, the fault
            // is the renderer itself, not the streams.
            var path = args.Length > 0
                ? string.Join(" ", args)
                : @"C:\Windows\SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4";

            var isUrl = path.Contains("://", StringComparison.Ordinal);
            if (!isUrl && !File.Exists(path)) return "Fichier introuvable : " + path;

            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null) { DebugConsole.Error("playfile: aucun backend vidéo."); return; }
                _current = null;
                ShowOverlay("Chargement (fichier local)…", spinning: true);
                DebugConsole.Info("playfile -> " + path);
                _videoBackend.Volume = (int)VolumeSlider.Value;
                _videoBackend.Play(path);
                SetPlayIcon(false);
            });
            return "Lecture du fichier local demandée : " + path;
        });
    }

    // ============ WINDOW CHROME ============
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (StateStore.Settings.ConfirmExit && _connected)
        {
            var r = MessageBox.Show("Quitter ElyCast ?", "ElyCast", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        // Let the owned overlay window close with us instead of cancelling.
        _shuttingDown = true;
        try { _audioEngine.Dispose(); }
        catch (Exception ex) { DebugConsole.Exception("Fermeture : arrêt de l'analyse audio", ex); }
        FlushPendingElySound();
        base.OnClosing(e);
        try { StateStore.Save(); } catch (Exception ex) { DebugConsole.Exception("Fermeture: échec de l'enregistrement de l'état", ex); }

        var backend = _videoBackend;
        _videoBackend = null;
        if (backend != null)
        {
            try { DebugConsole.Step("Fermeture: arrêt de la lecture…"); backend.Stop(); }
            catch (Exception ex) { DebugConsole.Exception("Fermeture: échec de l'arrêt", ex); }
            try { DetachBackendEvents(backend); } catch (Exception ex) { DebugConsole.Exception("Fermeture: échec du détachement des événements", ex); }
            try { DebugConsole.Step("Fermeture: disposition du backend…"); backend.Dispose(); }
            catch (Exception ex) { DebugConsole.Exception("Fermeture: échec de la disposition", ex); }
        }
    }
}
