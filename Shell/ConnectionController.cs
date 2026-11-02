using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Sign-in screen: saved profiles, Xtream/M3U tabs, connection, the local-only
/// shell, onboarding auto-connect and sign-out. A connection is published to
/// the catalogue only after its own request succeeded.
/// </summary>
internal sealed class ConnectionController
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly MediaLibrary _library;
    private readonly IAppStateStore _store;
    private readonly IProfileStore _profiles;
    private readonly PlaybackController _playback;
    private readonly CatalogController _catalog;
    private readonly MusicLibraryController _music;
    private readonly OverlayWindowHost _overlay;
    private CancellationTokenSource? _connectCts;
    private bool _m3uMode;

    public ConnectionController(MainWindow view, PlayerViewModel vm, PlaybackSession session, MediaLibrary library,
        IAppStateStore store, IProfileStore profiles, PlaybackController playback, CatalogController catalog, MusicLibraryController music,
        OverlayWindowHost overlay)
    {
        _view = view;
        _vm = vm;
        _session = session;
        _library = library;
        _store = store;
        _profiles = profiles;
        _playback = playback;
        _catalog = catalog;
        _music = music;
        _overlay = overlay;
        _catalog.SignInRequired += SignOut;
    }

    public ObservableCollection<Profile> Profiles { get; } = new();

    // ============ PROFILES ============
    public void LoadProfiles()
    {
        Profiles.Clear();
        if (!(_store.SuppressSaves && Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_CLEAN") == "1"))
            foreach (var p in _profiles.Load()) Profiles.Add(p);
        _view.ProfilesItems.ItemsSource = Profiles;
        _view.ProfilesSection.Visibility = Profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Fills the login form from <paramref name="profile"/> and connects.</summary>
    public void ConnectWith(Profile profile)
    {
        FillForm(profile);
        ConnectAsync().Forget("Connection");
    }

    private void FillForm(Profile profile)
    {
        if (profile.Kind == ProfileKind.Xtream)
        {
            SwitchTab(m3u: false);
            _view.UrlBox.Text = profile.Url;
            _view.UserBox.Text = profile.Username;
            _view.PassBox.Password = _profiles.Unprotect(profile.ProtectedPassword);
        }
        else
        {
            SwitchTab(m3u: true);
            _view.M3uPathBox.Text = profile.M3uPath;
        }
    }

    public void DeleteProfile(Profile profile)
    {
        Profiles.Remove(profile);
        if (_store.Settings.AutoConnectProfile == profile.Name)
        {
            _store.Settings.AutoConnectProfile = "";
            _store.Save();
        }
        _profiles.Save(Profiles.ToList());
        _view.ProfilesSection.Visibility = Profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveCurrentProfileIfRequested()
    {
        if (_view.SaveProfileCheck.IsChecked != true) return;
        var name = _view.ProfileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
            name = _m3uMode ? "Playlist M3U" : (_view.UserBox.Text.Trim() + " @ " + _view.UrlBox.Text.Trim());

        var profile = _m3uMode
            ? new Profile { Name = name, Kind = ProfileKind.M3u, M3uPath = _view.M3uPathBox.Text.Trim() }
            : new Profile
            {
                Name = name, Kind = ProfileKind.Xtream, Url = _view.UrlBox.Text.Trim(),
                Username = _view.UserBox.Text.Trim(), ProtectedPassword = _profiles.Protect(_view.PassBox.Password)
            };
        var existing = Profiles.FirstOrDefault(p => p.Name == name);
        if (existing != null) Profiles.Remove(existing);
        Profiles.Add(profile);
        _profiles.Save(Profiles.ToList());
    }

    // ============ FORM ============
    public void SwitchTab(bool m3u)
    {
        _m3uMode = m3u;
        _view.XtreamPanel.Visibility = m3u ? Visibility.Collapsed : Visibility.Visible;
        _view.M3uPanel.Visibility = m3u ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_view.TabHighlight, m3u ? 1 : 0);
        _view.XtreamTabBtn.Foreground = m3u ? (Brush)_view.FindResource("MutedBrush") : Brushes.White;
        _view.M3uTabBtn.Foreground = m3u ? Brushes.White : (Brush)_view.FindResource("MutedBrush");
    }

    public void BrowsePlaylist()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.T("Choose an M3U playlist"),
            Filter = LocalizationService.T("M3U playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*")
        };
        if (dlg.ShowDialog() == true) _view.M3uPathBox.Text = dlg.FileName;
    }

    public void OnSaveProfileToggled() =>
        _view.ProfileNamePanel.Visibility = _view.SaveProfileCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    public void OnPasswordChanged() =>
        _view.PassPlaceholder.Visibility = string.IsNullOrEmpty(_view.PassBox.Password) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Personalised welcome title chosen during onboarding.</summary>
    public void RefreshWelcome()
    {
        var name = _store.Settings.UserDisplayName;
        if (!string.IsNullOrWhiteSpace(name))
            _view.WelcomeTitle.Text = LocalizationService.Format("Welcome {0} 👋", name);
    }

    // ============ CONNECT ============
    public void AnimateLoginIn()
    {
        _view.LoginView.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _view.LoginCardT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(550)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public async Task ConnectAsync()
    {
        _connectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        var ct = cts.Token;
        _vm.IsConnecting = true;
        _vm.LoginStatus = "";
        try
        {
            var candidate = new IptvService();
            List<Channel> channels;
            if (_m3uMode)
            {
                var path = _view.M3uPathBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path)) { _vm.LoginStatus = LocalizationService.T("Enter an M3U file or URL."); _vm.IsConnecting = false; return; }
                (_, channels) = await candidate.LoadM3uAsync(path, ct);
            }
            else
            {
                var url = _view.UrlBox.Text.Trim(); var user = _view.UserBox.Text.Trim(); var pass = _view.PassBox.Password;
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                { _vm.LoginStatus = LocalizationService.T("Enter the URL, username, and password."); _vm.IsConnecting = false; return; }
                (_, channels) = await candidate.ConnectAsync(url, user, pass, ct);
            }

            ct.ThrowIfCancellationRequested();

            if (channels.Count == 0 && !candidate.IsXtream) { _vm.LoginStatus = LocalizationService.T("Connection succeeded, but no channels were found."); return; }

            _library.OpenProfile(candidate, channels.Select(PlayItem.FromChannel));

            SaveCurrentProfileIfRequested();
            _session.Connected = true;
            _view.NavLive.IsChecked = true;
            _catalog.ShowSection(Section.Live);
            _catalog.UpdateResumeButton();
            GoToPlayer();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DebugConsole.Info("Connection canceled.");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || _session.ShuttingDown) return;
            _vm.LoginStatus = LocalizationService.T("Connection failed: ") + ex.Message;
            DebugConsole.Error("Connection: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
                _vm.IsConnecting = false;
            }
            cts.Dispose();
        }
    }

    public void CancelConnect() => _connectCts?.Cancel();

    public void GoToPlayer()
    {
        _view.PlayerView.Visibility = Visibility.Visible;
        // Show the airspace overlay (OSD/menus) now that the player is visible.
        _overlay.ShowDeferred();
        var outFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        outFade.Completed += (_, _) => { if (_session.Connected) _view.LoginView.Visibility = Visibility.Collapsed; };
        _view.LoginView.BeginAnimation(UIElement.OpacityProperty, outFade);
        _view.PlayerView.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            { BeginTime = TimeSpan.FromMilliseconds(160), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ============ ONBOARDING / LOCAL-ONLY ============
    /// <summary>
    /// Applies the choices made in the first-run wizard: personalised welcome,
    /// preselected connection tab, and direct entry into the local library for
    /// the "local only" mode (no IPTV account required).
    /// </summary>
    public void ApplyOnboardingPreferences()
    {
        var s = _store.Settings;
        RefreshWelcome();

        switch (s.PreferredConnection)
        {
            case "m3u":
                SwitchTab(m3u: true);
                break;
            case "local":
                EnterLocalOnlyMode();
                return;
        }

        // Credentials entered during onboarding are stored as a normal saved
        // profile; connect with it right away instead of showing the login.
        if (string.IsNullOrWhiteSpace(s.AutoConnectProfile)) return;
        var profile = Profiles.FirstOrDefault(p => p.Name == s.AutoConnectProfile);
        if (profile == null)
        {
            // The profile was deleted: stop trying at every launch.
            s.AutoConnectProfile = "";
            _store.Save();
            return;
        }

        DebugConsole.Info(LocalizationService.Format("Connecting automatically with profile “{0}”...", profile.Name));
        ConnectWith(profile);
    }

    /// <summary>Local-only mode: straight to the local library, no IPTV login.</summary>
    public void EnterLocalOnlyMode()
    {
        DebugConsole.Info("Local-only mode: opening the library directly.");
        _connectCts?.Cancel();
        _catalog.CancelPendingLoads();
        _library.OpenLocalOnly();
        _session.Connected = true;
        _catalog.SelectNav(Section.LocalAudio);
        _catalog.ShowSection(Section.LocalAudio);
        _vm.ShowOverlay("Add local files, then start playback", spinning: false);
        GoToPlayer();
    }

    // ============ SIGN-OUT ============
    public void SignOut()
    {
        _catalog.CloseSeriesPanel();
        _music.ClosePanel();
        _connectCts?.Cancel();
        _catalog.CancelPendingLoads();
        _playback.ResetForSignOut();
        _session.Connected = false;
        _library.CloseSource();
        _overlay.Hide();
        LoadProfiles();

        _view.LoginView.Visibility = Visibility.Visible;
        _view.LoginView.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        var outFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        outFade.Completed += (_, _) => { if (!_session.Connected) _view.PlayerView.Visibility = Visibility.Collapsed; };
        _view.PlayerView.BeginAnimation(UIElement.OpacityProperty, outFade);
    }
}
