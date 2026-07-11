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
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.ElySmart;
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
    private readonly WindowsMediaTransportService _mediaTransport;
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
        _mediaTransport = new WindowsMediaTransportService(
            play: () => Dispatcher.BeginInvoke(() => ResumeFromSystemControls()),
            pause: () => Dispatcher.BeginInvoke(() => PauseFromSystemControls()),
            next: () => Dispatcher.BeginInvoke(() => Zap(1)),
            previous: () => Dispatcher.BeginInvoke(() => Zap(-1)));
        SourceInitialized += (_, _) => _mediaTransport.Initialize(new WindowInteropHelper(this).Handle);
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
        ApplyOnboardingPreferences();
        StartDiagnosticPlaybackIfRequested();
        InitializeElySmart();

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

    /// <summary>
    /// Applies the choices made in the first-run wizard: personalised welcome,
    /// preselected connection tab, and direct entry into the local library for
    /// the "local only" mode (no IPTV account required).
    /// </summary>
    private void ApplyOnboardingPreferences()
    {
        var s = StateStore.Settings;
        if (!string.IsNullOrWhiteSpace(s.UserDisplayName))
            WelcomeTitle.Text = $"Bienvenue {s.UserDisplayName} 👋";

        switch (s.PreferredConnection)
        {
            case "m3u":
                SwitchTab(true);
                break;
            case "local":
                EnterLocalOnlyMode();
                return;
        }

        // Credentials entered during onboarding are stored as a normal saved
        // profile; connect with it right away instead of showing the login.
        if (!string.IsNullOrWhiteSpace(s.AutoConnectProfile))
        {
            var profile = _profiles.FirstOrDefault(p => p.Name == s.AutoConnectProfile);
            if (profile == null)
            {
                // The profile was deleted: stop trying at every launch.
                s.AutoConnectProfile = "";
                StateStore.Save();
                return;
            }

            DebugConsole.Info("Connexion automatique avec le profil « " + profile.Name + " »…");
            if (profile.Kind == ProfileKind.Xtream)
            {
                SwitchTab(false);
                UrlBox.Text = profile.Url;
                UserBox.Text = profile.Username;
                PassBox.Password = ProfileStore.Unprotect(profile.ProtectedPassword);
            }
            else
            {
                SwitchTab(true);
                M3uPathBox.Text = profile.M3uPath;
            }
            Connect_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Local-only mode: straight to the local library, no IPTV login.</summary>
    private void EnterLocalOnlyMode()
    {
        DebugConsole.Info("Mode local uniquement : ouverture directe de la bibliothèque.");
        _connected = true;
        _state = StateStore.ForProfile("local-only");
        MarkFavorites(_localItems);
        _suppressNav = true;
        NavLocal.IsChecked = true;
        _suppressNav = false;
        ShowSection(Section.Local);
        ShowOverlay("Ajoute des fichiers locaux puis lance la lecture", spinning: false);
        GoToPlayer();
    }

    private void LocalOnly_Click(object sender, RoutedEventArgs e) => EnterLocalOnlyMode();

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
            DebugConsole.Info("Diagnostic playback -> fichier local.");
            var item = PlayItem.FromLocalFile(path);
            Play(item, persistHistory: false);
            StartDiagnosticElySoundSweepIfRequested();
            StartDiagnosticPipelineSweepIfRequested();
            _ = LogDiagnosticPlaybackStatsAsync(_videoBackend);
        };
        timer.Start();
    }

    private async void StartDiagnosticPipelineSweepIfRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_PIPELINE_SWEEP"), "1", StringComparison.Ordinal))
            return;
        if (_videoBackend is not MpvHwndBackend { IsElyCoreRenderer: true } backend) return;

        await Task.Delay(1800);
        static string Snapshot(MpvHwndBackend current, ElyFlowRendererInterop.RendererState state) =>
            current.GetElyCoreVsrAuditSnapshot() + Environment.NewLine +
            $"ELYCORE target rebuilds: {state.TargetRebuilds}{Environment.NewLine}" +
            $"ELYCORE FRUC effective: {state.FrucInitialized != 0}{Environment.NewLine}" +
            $"ELYCORE frames presented: {state.FramesPresented}";

        DebugConsole.Info("Diagnostic pipeline initial:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(false);
        await Task.Delay(700);
        DebugConsole.Info("Diagnostic pipeline VSR off:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(true);
        await Task.Delay(900);
        DebugConsole.Info("Diagnostic pipeline VSR on:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(false);
        await Task.Delay(700);
        DebugConsole.Info("Diagnostic pipeline VSR off #2:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(true);
        await Task.Delay(900);
        DebugConsole.Info("Diagnostic pipeline VSR on #2:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreFruc(false);
        await Task.Delay(500);
        DebugConsole.Info("Diagnostic pipeline FRUC off:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreFruc(true);
        await Task.Delay(3600);
        DebugConsole.Info("Diagnostic pipeline VSR+FRUC:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
    }

    private async void StartDiagnosticElySoundSweepIfRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_ELYSOUND_SWEEP"), "1", StringComparison.Ordinal))
            return;
        if (_videoBackend is not IElySoundBackend dsp) return;

        await Task.Delay(1200);
        foreach (var profile in ElySoundCatalog.BuiltIn)
        {
            if (_videoBackend is not IElySoundBackend current || !ReferenceEquals(current, dsp)) return;
            var before = _videoBackend.PositionMs;
            var stopwatch = Stopwatch.StartNew();
            var result = dsp.ApplyElySound(profile, enabled: true, virtualSurround: true);
            stopwatch.Stop();
            var after = _videoBackend.PositionMs;
            DebugConsole.Info($"Diagnostic ELYSOUND+ {profile.Id}: applied={result.Applied}, pending={result.Pending}, " +
                              $"commande={stopwatch.Elapsed.TotalMilliseconds:0.0} ms, position {before}->{after} ms (delta={after - before} ms), {result.Message}");
            await Task.Delay(350);
        }

        if (_videoBackend?.HasMedia == true)
        {
            _videoBackend.Pause();
            var pausedAt = _videoBackend.PositionMs;
            await Task.Delay(300);
            var pausedApply = dsp.ApplyElySound(ElySoundCatalog.BuiltIn[1], enabled: true, virtualSurround: false);
            var pausedAfter = _videoBackend.PositionMs;
            DebugConsole.Info($"Diagnostic ELYSOUND+ pause: applied={pausedApply.Applied}, position {pausedAt}->{pausedAfter} ms");
            _videoBackend.Resume();
            await Task.Delay(300);
            var beforeSeek = _videoBackend.PositionMs;
            _videoBackend.SeekRelative(1000);
            await Task.Delay(300);
            DebugConsole.Info($"Diagnostic lecture pause/reprise/seek: avant={beforeSeek} ms, après={_videoBackend.PositionMs} ms");
        }
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
        try { _videoBackend?.Stop(PlaybackEndReason.Teardown); } catch { }
        _statsTimer.Stop(); _epgTimer.Stop(); _progressTimer.Stop();
        HideAudioVisualizer();
        _mediaTransport.Clear();
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
        try { _mediaTransport.Dispose(); } catch { }
        try { DisposeElySmart(); } catch { }
        FlushPendingElySound();
        base.OnClosing(e);
        try { StateStore.Save(); } catch (Exception ex) { DebugConsole.Exception("Fermeture: échec de l'enregistrement de l'état", ex); }

        var backend = _videoBackend;
        _videoBackend = null;
        if (backend != null)
        {
            try { DetachBackendEvents(backend); } catch (Exception ex) { DebugConsole.Exception("Fermeture: échec du détachement des événements", ex); }
            try { DebugConsole.Step("Fermeture: arrêt de la lecture…"); backend.Stop(PlaybackEndReason.Teardown); }
            catch (Exception ex) { DebugConsole.Exception("Fermeture: échec de l'arrêt", ex); }
            try { DebugConsole.Step("Fermeture: disposition du backend…"); backend.Dispose(); }
            catch (Exception ex) { DebugConsole.Exception("Fermeture: échec de la disposition", ex); }
        }
    }
}
