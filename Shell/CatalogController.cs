using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

internal enum Section { Live, Movies, Series, LocalAudio, LocalVideo, Fav }

/// <summary>
/// Navigation and catalogue presentation: section switching with lazy
/// Movies/Series loading, the flat list with its category filter and search,
/// favourites, the series drill-down panel and list zapping.
/// Local-audio grouped browsing is delegated through <see cref="MusicGroupsRequested"/>.
/// </summary>
internal sealed class CatalogController
{
    private const string AllCategories = "All categories";

    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly MediaLibrary _library;
    private readonly AudioQueue _queue;
    private readonly PlaybackController _playback;
    private readonly OsdController _osd;

    private readonly BulkObservableCollection<PlayItem> _items = new();
    private ListCollectionView? _listView;
    private string _selectedCategory = "";
    private bool _suppressNav;
    private CancellationTokenSource? _sectionCts;
    private CancellationTokenSource? _seriesCts;
    private SeriesInfo? _seriesInfo;
    private string _seriesName = "";
    private DispatcherTimer? _searchTimer;

    public CatalogController(MainWindow view, PlayerViewModel vm, PlaybackSession session, MediaLibrary library,
        AudioQueue queue, PlaybackController playback, OsdController osd)
    {
        _view = view;
        _vm = vm;
        _session = session;
        _library = library;
        _queue = queue;
        _playback = playback;
        _osd = osd;
        _playback.SeriesRequested += OpenSeries;
        _playback.HistoryChanged += UpdateResumeButton;
    }

    public Section Current { get; private set; } = Section.Live;

    /// <summary>Section to restore when the settings panel closes.</summary>
    public Section BeforeSettings { get; private set; } = Section.Live;

    /// <summary>Shared accent-insensitive, multiword search of the catalogue box.</summary>
    public MediaSearch Search { get; } = new();

    /// <summary>The local-audio section switched to grouped browsing (albums, artists...).</summary>
    public event Action? MusicGroupsRequested;

    /// <summary>The search query changed while grouped browsing is shown.</summary>
    public event Action? MusicGroupFilterRequested;

    /// <summary>Navigation leaves the current view: close the music detail panel.</summary>
    public event Action? MusicPanelCloseRequested;

    public event Action? SettingsRequested;
    public event Action? SettingsCloseRequested;

    /// <summary>The local-only shell has no IPTV account behind Live/VOD/Series.</summary>
    public event Action? SignInRequired;

    public string AudioBrowseMode => (_view.AudioBrowseCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "albums";
    public bool IsAudioGroupMode => AudioBrowseMode is "albums" or "artists" or "genres" or "playlists";
    public bool IsLocalSection => Current is Section.LocalAudio or Section.LocalVideo;

    /// <summary>Items currently visible in the flat list, in display order.</summary>
    public IReadOnlyList<PlayItem>? VisibleItems => _listView?.Cast<PlayItem>().ToList();

    /// <summary>The selected list row when it can be played directly.</summary>
    public PlayItem? SelectedPlayable =>
        _view.ItemList.Visibility == Visibility.Visible && _view.ItemList.SelectedItem is PlayItem c && c.Kind != PlayItemKind.Series
            ? c : null;

    // ============ NAVIGATION ============
    public async Task OnNavigationAsync(RadioButton button)
    {
        if (!_session.Connected || _suppressNav) return;
        if (button is not { IsChecked: true, Tag: string target }) return;
        _sectionCts?.Cancel();
        if (target == "Settings") { BeforeSettings = Current; SettingsRequested?.Invoke(); return; }

        // Local-only mode sets Connected so the local library can use the
        // normal player shell, but it has no IPTV service behind Live/VOD/Series.
        // Never turn that absence into a misleading cached "0 item" catalogue.
        if (string.IsNullOrEmpty(_library.Source.ProfileKey) && target is "Live" or "Movies" or "Series")
        {
            SignInRequired?.Invoke();
            _vm.LoginStatus = LocalizationService.T("Connect an IPTV profile to access Live TV, Movies, and Series.");
            return;
        }
        SettingsCloseRequested?.Invoke();
        CloseSeriesPanel();
        MusicPanelCloseRequested?.Invoke();

        _sectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sectionCts = cts;
        try
        {
            switch (target)
            {
                case "Movies": await ShowSectionAsync(Section.Movies, cts.Token); break;
                case "Series": await ShowSectionAsync(Section.Series, cts.Token); break;
                case "LocalAudio": ShowSection(Section.LocalAudio); break;
                case "LocalVideo": ShowSection(Section.LocalVideo); break;
                case "Fav": ShowSection(Section.Fav); break;
                default: ShowSection(Section.Live); break;
            }
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
        var source = _library.Source;
        if (s == Section.Movies && _library.MovieItems == null)
        {
            _vm.SectionTitle = LocalizationService.T("Movies...");
            try
            {
                var loaded = (await source.GetVodAsync(ct)).Select(PlayItem.FromVod).ToList();
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(source, _library.Source) || _session.ShuttingDown) return;
                _library.MovieItems = loaded;
                _library.MarkFavorites(loaded);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || !ReferenceEquals(source, _library.Source) || _session.ShuttingDown) return;
                DebugConsole.Error("VOD: " + ex.Message);
                _vm.SectionTitle = LocalizationService.T("Movies unavailable: try again");
                return;
            }
        }
        else if (s == Section.Series && _library.SeriesItems == null)
        {
            _vm.SectionTitle = LocalizationService.T("Series...");
            try
            {
                var loaded = (await source.GetSeriesAsync(ct)).Select(PlayItem.FromSeries).ToList();
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(source, _library.Source) || _session.ShuttingDown) return;
                _library.SeriesItems = loaded;
                _library.MarkFavorites(loaded);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || !ReferenceEquals(source, _library.Source) || _session.ShuttingDown) return;
                DebugConsole.Error("Series: " + ex.Message);
                _vm.SectionTitle = LocalizationService.T("Series unavailable: try again");
                return;
            }
        }
        ct.ThrowIfCancellationRequested();
        ShowSection(s);
    }

    public void ShowSection(Section s)
    {
        Current = s;
        _selectedCategory = AllCategories;
        List<PlayItem> source = s switch
        {
            Section.Live => _library.LiveItems,
            Section.Movies => _library.MovieItems ?? new(),
            Section.Series => _library.SeriesItems ?? new(),
            Section.LocalAudio => _library.AudioSectionItems(),
            Section.LocalVideo => _library.LocalVideo,
            Section.Fav => _library.GetFavorites(),
            _ => _library.LiveItems
        };
        _vm.SectionTitle = LocalizationService.T(s switch
        {
            Section.LocalAudio => "Local music",
            Section.LocalVideo => "Local videos",
            Section.Live => "Channels", Section.Movies => "Movies",
            Section.Series => "Series", Section.Fav => "Favorites", _ => ""
        });

        _view.LocalActionsPanel.Visibility = IsLocalSection ? Visibility.Visible : Visibility.Collapsed;
        _view.AudioLibraryActions.Visibility = s == Section.LocalAudio ? Visibility.Visible : Visibility.Collapsed;
        _view.VideoLibraryActions.Visibility = s == Section.LocalVideo ? Visibility.Visible : Visibility.Collapsed;

        // Local audio browses through grouped covers (albums / artists / genres /
        // playlists); the flat PlayItem list only serves "all tracks" and the
        // other sections.
        var groupMode = s == Section.LocalAudio && IsAudioGroupMode;
        _view.MusicGroupList.Visibility = groupMode ? Visibility.Visible : Visibility.Collapsed;
        _view.ItemList.Visibility = groupMode ? Visibility.Collapsed : Visibility.Visible;
        _view.CategoryCombo.Visibility = groupMode ? Visibility.Collapsed : Visibility.Visible;
        _view.PlaylistCreateRow.Visibility = s == Section.LocalAudio && AudioBrowseMode == "playlists"
            ? Visibility.Visible : Visibility.Collapsed;
        _view.RemoveLocalButton.Visibility = s == Section.LocalVideo || (s == Section.LocalAudio && !groupMode)
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateResumeButton();
        if (groupMode)
        {
            MusicGroupsRequested?.Invoke();
            return;
        }
        _items.Reset(source);
        _listView = new ListCollectionView(_items) { Filter = FilterItem };
        _view.ItemList.ItemsSource = _listView;
        BuildCategoryFilter(source);
        UpdateCount();
    }

    /// <summary>Checks the nav button of <paramref name="section"/> without reloading the list.</summary>
    public void SelectNav(Section section)
    {
        _suppressNav = true;
        switch (section)
        {
            case Section.Movies: _view.NavMovies.IsChecked = true; break;
            case Section.Series: _view.NavSeries.IsChecked = true; break;
            case Section.LocalAudio: _view.NavLocalAudio.IsChecked = true; break;
            case Section.LocalVideo: _view.NavLocalVideo.IsChecked = true; break;
            case Section.Fav: _view.NavFav.IsChecked = true; break;
            default: _view.NavLive.IsChecked = true; break;
        }
        _suppressNav = false;
    }

    public void CancelPendingLoads()
    {
        _sectionCts?.Cancel();
        _seriesCts?.Cancel();
    }

    public void UpdateResumeButton()
    {
        if (IsLocalSection)
        {
            _vm.IsResumeVisible = false;
            return;
        }

        if (_library.Profile.LastPlayed != null)
        {
            _vm.ResumeText = LocalizationService.T("Resume: ") + _library.Profile.LastPlayed.Name;
            _vm.IsResumeVisible = true;
        }
        else _vm.IsResumeVisible = false;
    }

    public void Resume()
    {
        if (_library.Profile.LastPlayed != null) _playback.Play(_library.Profile.LastPlayed);
    }

    // ============ LIST / FILTER / SEARCH ============
    private void BuildCategoryFilter(List<PlayItem> source)
    {
        var cats = source.Select(c => c.CategoryName).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct().OrderBy(c => c).ToList();
        var items = new List<string> { LocalizationService.T(AllCategories) };
        items.AddRange(cats);
        _view.CategoryCombo.ItemsSource = items;
        _view.CategoryCombo.SelectedIndex = 0;
        _selectedCategory = AllCategories;
    }

    private bool FilterItem(object o)
    {
        if (o is not PlayItem c) return false;
        if (_selectedCategory != AllCategories && !string.IsNullOrEmpty(_selectedCategory)
            && !string.Equals(c.CategoryName, _selectedCategory, StringComparison.Ordinal)) return false;
        return Search.Matches(c.Name, c.Artist, c.Album, c.CategoryName);
    }

    public void OnCategoryChanged()
    {
        var selected = _view.CategoryCombo.SelectedItem as string;
        _selectedCategory = string.Equals(selected, LocalizationService.T(AllCategories), StringComparison.Ordinal)
            ? AllCategories : selected ?? AllCategories;
        _listView?.Refresh(); UpdateCount();
    }

    /// <summary>Debounced (160 ms) so typing does not refilter thousands of channels per key.</summary>
    public void OnSearchTextChanged()
    {
        if (_searchTimer == null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            timer.Tick += (_, _) => { timer.Stop(); RefreshSearch(); };
            _searchTimer = timer;
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>Applies the search box immediately (console and tests).</summary>
    public void RefreshSearch()
    {
        Search.SetQuery(_view.SearchBox.Text);
        if (Current == Section.LocalAudio && IsAudioGroupMode) { MusicGroupFilterRequested?.Invoke(); return; }
        _listView?.Refresh(); UpdateCount();
    }

    public void StopSearchTimer() => _searchTimer?.Stop();

    private void UpdateCount() => _vm.ItemCount = (_listView?.Count ?? 0).ToString();

    /// <summary>Plays (or opens) the selected row of the flat list.</summary>
    public void ActivateSelected()
    {
        if (_view.ItemList.SelectedItem is not PlayItem item) return;
        if (item.Kind == PlayItemKind.Series) OpenSeries(item);
        else
        {
            // Launched from the flat list: playback advances through the library.
            if (MediaKinds.IsAudioOnly(item)) PrepareVisibleAudioPlayback(item);
            _playback.Play(item);
        }
    }

    /// <summary>Automatic continuation will follow the visible audio list from <paramref name="track"/>.</summary>
    public void PrepareVisibleAudioPlayback(PlayItem track)
    {
        var visible = _listView?.Cast<PlayItem>().Where(MediaKinds.IsAudioOnly).ToList() ?? _library.LocalAudio.ToList();
        _queue.StartFrom(visible, track);
    }

    /// <summary>Previous/next row of the flat list (wrapping), playing it.</summary>
    public void ZapList(int direction)
    {
        if (_listView == null || _listView.Count == 0) return;
        var idx = _view.ItemList.SelectedIndex;
        idx = idx < 0 ? 0 : idx + direction;
        if (idx < 0) idx = _listView.Count - 1;
        if (idx >= _listView.Count) idx = 0;
        _view.ItemList.SelectedIndex = idx;
        _view.ItemList.ScrollIntoView(_view.ItemList.SelectedItem);
        if (_view.ItemList.SelectedItem is PlayItem p && p.Kind != PlayItemKind.Series) _playback.Play(p);
    }

    /// <summary>Console helper: plays the nth visible item (1-based).</summary>
    public string PlayVisible(int n)
    {
        if (_listView == null || n < 1 || n > _listView.Count) return $"Out of range (1..{_listView?.Count ?? 0}).";
        if (_listView.GetItemAt(n - 1) is PlayItem c && c.Kind != PlayItemKind.Series) { _playback.Play(c); return $"Playing: {c.Name}"; }
        return "Item is not playable.";
    }

    public IEnumerable<string> VisibleCategories() =>
        _items.Select(c => c.CategoryName).Distinct().OrderBy(c => c);

    // ============ FAVOURITES ============
    public void OnFavoriteToggled(PlayItem item)
    {
        var removed = _library.CommitFavoriteToggle(item);
        if (removed && Current == Section.Fav) { _items.Remove(item); UpdateCount(); }
    }

    // ============ SERIES DRILL-DOWN ============
    public void OpenSeries(PlayItem series) => OpenSeriesAsync(series).Forget("Series details");

    private async Task OpenSeriesAsync(PlayItem series)
    {
        _seriesCts?.Cancel();
        var cts = new CancellationTokenSource();
        _seriesCts = cts;
        _seriesName = series.Name;
        _view.SeriesPanelTitle.Text = series.Name;
        _view.EpisodeList.ItemsSource = null;
        _view.SeasonCombo.ItemsSource = null;
        _view.SeriesPanel.Visibility = Visibility.Visible;
        _osd.OnPanelOpened(_view.SeriesPanel);
        try
        {
            var info = await _library.Source.GetSeriesInfoAsync(series.Id, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_session.ShuttingDown) return;
            _seriesInfo = info;
            var seasons = SortedSeasons(info);
            _view.SeasonCombo.ItemsSource = seasons.Select(s => LocalizationService.Format("Season {0}", s)).ToList();
            if (seasons.Count > 0) _view.SeasonCombo.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && !_session.ShuttingDown)
            {
                DebugConsole.Error("Series details: " + ex.Message);
                _view.SeriesPanelTitle.Text = LocalizationService.T("Could not load episodes. Open the series to retry.");
            }
        }
        finally
        {
            if (ReferenceEquals(_seriesCts, cts)) _seriesCts = null;
            cts.Dispose();
        }
    }

    private static List<string> SortedSeasons(SeriesInfo info) =>
        info.Episodes.Keys.OrderBy(k => int.TryParse(k, out var n) ? n : 0).ToList();

    public void OnSeasonChanged()
    {
        if (_seriesInfo == null || _view.SeasonCombo.SelectedIndex < 0) return;
        var key = SortedSeasons(_seriesInfo).ElementAtOrDefault(_view.SeasonCombo.SelectedIndex);
        if (key != null && _seriesInfo.Episodes.TryGetValue(key, out var eps)) _view.EpisodeList.ItemsSource = eps;
    }

    public void PlaySelectedEpisode()
    {
        if (_view.EpisodeList.SelectedItem is not Episode ep) return;
        _playback.Play(PlayItem.FromEpisode(ep, _seriesName));
        CloseSeriesPanel();
    }

    public void CloseSeriesPanel()
    {
        _seriesCts?.Cancel();
        _view.SeriesPanel.Visibility = Visibility.Collapsed;
        _osd.UpdateCloseButton();
    }
}
