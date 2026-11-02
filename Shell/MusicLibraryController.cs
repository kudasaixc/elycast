using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.ViewModels;
using Microsoft.Win32;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Local library commands: folder/file/drag-and-drop import, grouped music
/// browsing with its detail panel, playlists, queue commands, context menus,
/// the metadata editor and library removal (files on disk are never deleted).
/// Playlist/queue commands are never offered for local video.
/// </summary>
internal sealed class MusicLibraryController
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly MediaLibrary _library;
    private readonly AudioQueue _queue;
    private readonly IAppStateStore _store;
    private readonly PlaybackController _playback;
    private readonly OsdController _osd;
    private readonly CatalogController _catalog;
    private readonly LocalLibraryService _importer;
    private readonly UiLoadGate _gate;

    private List<MusicGroup> _groups = new();
    private MusicGroup? _openGroup;
    private MusicGroup? _menuTargetGroup;
    private ListCollectionView? _trackView;
    private CancellationTokenSource? _importCts;
    private readonly MediaSearch _trackSearch = new();

    public MusicLibraryController(MainWindow view, PlayerViewModel vm, PlaybackSession session, MediaLibrary library,
        AudioQueue queue, IAppStateStore store, PlaybackController playback, OsdController osd,
        CatalogController catalog, LocalLibraryService importer, UiLoadGate gate)
    {
        _gate = gate;
        _view = view;
        _vm = vm;
        _session = session;
        _library = library;
        _queue = queue;
        _store = store;
        _playback = playback;
        _osd = osd;
        _catalog = catalog;
        _importer = importer;

        _catalog.MusicGroupsRequested += RebuildGroups;
        _catalog.MusicGroupFilterRequested += ApplyGroupFilter;
        _catalog.MusicPanelCloseRequested += ClosePanel;
    }

    // ============ STARTUP ============
    /// <summary>
    /// Shows the persisted library immediately, then refreshes TagLib metadata
    /// off the UI thread and merges it back.
    /// </summary>
    public async Task LoadAsync()
    {
        _library.LoadLocal();
        var snapshot = _library.LocalAudio.ToList();
        List<PlayItem> enriched;
        try { enriched = await Task.Run(() => snapshot.Select(LocalLibraryService.EnrichAudioItem).ToList()); }
        catch (Exception ex) { DebugConsole.Exception("Library metadata refresh failed", ex); return; }
        if (_session.ShuttingDown) return;
        _library.MergeEnrichedAudio(enriched);
        if (_session.Connected && _catalog.Current == Section.LocalAudio) _catalog.ShowSection(Section.LocalAudio);
    }

    // ============ IMPORT ============
    public async Task ImportFolderAsync()
    {
        var audio = _catalog.Current == Section.LocalAudio;
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.T(audio ? "Import a music folder" : "Import a video folder"),
            Multiselect = false
        };
        if (dialog.ShowDialog(_view) != true) return;
        await ImportSingleKindAsync((progress, ct) => _importer.ImportFolderAsync(dialog.FolderName, audio, progress, ct), audio);
    }

    public async Task ImportFilesAsync()
    {
        var audio = _catalog.Current == Section.LocalAudio;
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.T(audio ? "Import music files" : "Import video files"),
            Multiselect = true,
            Filter = audio ? LocalLibraryService.AudioFileFilter : LocalLibraryService.VideoFileFilter
        };
        if (dialog.ShowDialog(_view) != true) return;
        await ImportSingleKindAsync((progress, ct) => _importer.ImportFilesAsync(dialog.FileNames, audio, progress, ct), audio);
    }

    // Throttled UI updates: the parallel import fires one report per file, but
    // we only refresh the button label a few times per second.
    private IProgress<int> MakeImportProgress(CancellationTokenSource operation)
    {
        var lastTick = 0L;
        return new Progress<int>(count =>
        {
            if (!ReferenceEquals(_importCts, operation) || operation.IsCancellationRequested || _session.ShuttingDown) return;
            var now = Environment.TickCount64;
            if (now - lastTick < 120) return;
            lastTick = now;
            _view.LocalImportButton.Content = LocalizationService.Format("Scanning... {0} file(s)", count);
        });
    }

    private void SetImportBusy(bool busy)
    {
        _view.LocalImportButton.IsEnabled = !busy;
        _view.LocalImportFilesButton.IsEnabled = !busy;
        _view.LocalImportButton.Content = LocalizationService.T(busy ? "Scanning..." : "Import folder");
    }

    private async Task ImportSingleKindAsync(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<PlayItem>>> import, bool audio)
    {
        if (_importCts != null) return;
        using var operation = new CancellationTokenSource();
        _importCts = operation;
        SetImportBusy(true);
        try
        {
            var imported = await import(MakeImportProgress(operation), operation.Token);
            if (_session.ShuttingDown) return;
            var added = _library.ApplyImported(imported, audio);
            _library.SaveLocal();
            if (_session.Connected && _catalog.Current == (audio ? Section.LocalAudio : Section.LocalVideo)) _catalog.ShowSection(_catalog.Current);
            _vm.ShowOverlay(LocalizationService.Format("{0} file(s) imported", added), spinning: false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DebugConsole.Exception("Local import failed", ex);
            _vm.ShowOverlay("Could not import these items", spinning: false);
        }
        finally
        {
            _importCts = null;
            if (!_session.ShuttingDown) SetImportBusy(false);
        }
    }

    public void CancelImport() => _importCts?.Cancel();

    // ============ DRAG & DROP ============
    public void OnDragOver(DragEventArgs e)
    {
        var accepted = _importCts == null && _session.Connected && _catalog.IsLocalSection
            && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    public async Task OnDropAsync(DragEventArgs e)
    {
        if (_importCts != null || !_session.Connected || !_catalog.IsLocalSection) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        e.Handled = true;

        using var operation = new CancellationTokenSource();
        _importCts = operation;
        var originalSection = _catalog.Current;
        SetImportBusy(true);
        try
        {
            var files = await Task.Run(() => LocalLibraryService.DiscoverFiles(dropped, operation.Token).ToList(), operation.Token);
            var audioFiles = files.Where(LocalLibraryService.IsAudio).ToList();
            var videoFiles = files.Where(LocalLibraryService.IsVideo).ToList();
            if (files.Count == 0) { _vm.ShowOverlay("No recognized audio or video files", spinning: false); return; }
            var progress = MakeImportProgress(operation);
            var added = 0;
            if (audioFiles.Count > 0)
                added += _library.ApplyImported(await _importer.ImportFilesAsync(audioFiles, true, progress, operation.Token), true);
            if (videoFiles.Count > 0)
                added += _library.ApplyImported(await _importer.ImportFilesAsync(videoFiles, false, progress, operation.Token), false);
            operation.Token.ThrowIfCancellationRequested();
            if (_session.ShuttingDown) return;
            _library.SaveLocal();

            // A completed import must not undo navigation made during scanning.
            if (_session.Connected && _catalog.Current == originalSection) _catalog.ShowSection(_catalog.Current);
            _vm.ShowOverlay(LocalizationService.Format("{0} file(s) imported", added), spinning: false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DebugConsole.Exception("Drag-and-drop import failed", ex);
            _vm.ShowOverlay("Could not import these items", spinning: false);
        }
        finally
        {
            _importCts = null;
            if (!_session.ShuttingDown) SetImportBusy(false);
        }
    }

    // ============ BROWSE MODES / GROUPS ============
    public void OnBrowseModeChanged()
    {
        if (!_gate.IsLoading)
        {
            _store.Settings.AudioBrowseMode = _catalog.AudioBrowseMode;
            _store.Save();
        }
        if (!_session.Connected || _catalog.Current != Section.LocalAudio) return;
        ClosePanel();
        _catalog.ShowSection(Section.LocalAudio);
    }

    private void RebuildGroups()
    {
        _groups = LocalLibraryService.BuildGroups(_catalog.AudioBrowseMode, _library.LocalAudio, _store.Current.LocalPlaylists);
        LoadGroupCovers(_groups);
        ApplyGroupFilter();
    }

    private void ApplyGroupFilter()
    {
        var search = _catalog.Search;
        var query = _view.SearchBox.Text?.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _groups
            : _groups.Where(g => search.Matches(g.Name, g.Subtitle)
                || g.Tracks.Any(t => search.Matches(t.Name, t.Artist, t.Album, g.Name))).ToList();
        _view.MusicGroupList.ItemsSource = filtered;
        if (_catalog.Current == Section.LocalAudio && _catalog.IsAudioGroupMode)
            _vm.ItemCount = filtered.Count.ToString();
    }

    // The group cover is the first embedded artwork found among its tracks.
    // Decoded off-thread and memoised in CoverArtCache, so rebuilds are cheap.
    private void LoadGroupCovers(List<MusicGroup> groups)
    {
        var snapshot = groups.Select(g => (Group: g, Paths: g.Tracks.Select(LocalLibraryService.PathOf).ToList())).ToList();
        Task.Run(() =>
        {
            foreach (var (group, paths) in snapshot)
            {
                foreach (var path in paths)
                {
                    var cover = CoverArtCache.GetOrDecode(path);
                    if (cover == null) continue;
                    _view.Dispatcher.BeginInvoke(() => group.Cover = cover);
                    break;
                }
            }
        }).Forget("Album cover decoding");
    }

    // ============ DETAIL PANEL ============
    public void OnGroupListClick(MouseButtonEventArgs e)
    {
        if (FindListItem(e.OriginalSource)?.DataContext is MusicGroup group)
            OpenGroup(group);
    }

    public static ListBoxItem? FindListItem(object? origin)
    {
        var current = origin as DependencyObject;
        while (current != null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        return current as ListBoxItem;
    }

    private void OpenGroup(MusicGroup group)
    {
        _openGroup = group;
        _view.MusicPanelKind.Text = LocalizationService.T(group.KindLabel).ToUpperInvariant();
        _view.MusicPanelTitle.Text = group.Name;
        var redundantSubtitle = group.Kind is MusicGroupKind.Genre or MusicGroupKind.Playlist;
        _view.MusicPanelSubtitle.Text = redundantSubtitle ? "" : group.Subtitle;
        _view.MusicPanelSubtitle.Visibility = redundantSubtitle || string.IsNullOrWhiteSpace(group.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _view.MusicPanelDetail.Text = LocalizationService.T(group.DetailLine);
        _view.MusicPanelCover.Source = group.Cover;
        _view.MusicPanelInitial.Text = group.Initial;
        _view.MusicTrackSearch.Text = "";
        RefreshTrackView(group);

        _view.MusicPanel.Visibility = Visibility.Visible;
        _osd.OnPanelOpened(_view.MusicPanel);
    }

    /// <summary>Re-renders the open panel (language switch).</summary>
    public void RefreshOpenPanel()
    {
        if (_openGroup != null) OpenGroup(_openGroup);
    }

    private bool TrackFilter(object obj) =>
        obj is PlayItem track && _trackSearch.Matches(track.Name, track.Artist, track.Album);

    public void OnTrackSearchChanged()
    {
        _trackSearch.SetQuery(_view.MusicTrackSearch.Text);
        _trackView?.Refresh();
    }

    public void ClosePanel()
    {
        if (_view.MusicPanel == null) return;
        _openGroup = null;
        _view.MusicPanel.Visibility = Visibility.Collapsed;
        _view.MusicGroupList.SelectedItem = null;
        _osd.UpdateCloseButton();
    }

    /// <summary>Group targeted by a panel button or a sidebar context menu action.</summary>
    private MusicGroup? ContextGroup(object? sender)
    {
        if (sender is MenuItem && _menuTargetGroup is { } menuTarget)
        {
            _menuTargetGroup = null;
            return menuTarget;
        }
        return (sender as FrameworkElement)?.DataContext as MusicGroup
            ?? (_view.MusicPanel.Visibility == Visibility.Visible ? _openGroup : _view.MusicGroupList.SelectedItem as MusicGroup);
    }

    public void PlayGroup(object sender, bool shuffle)
    {
        if (ContextGroup(sender) is not { } group) return;
        var first = _queue.StartGroup(group.Tracks, shuffle);
        if (first == null) return;
        ClosePanel();
        _playback.Play(first);
    }

    public void QueueGroup(object sender)
    {
        if (ContextGroup(sender) is not { } group) return;
        var added = _queue.EnqueueRange(group.Tracks);
        _vm.ShowOverlay(LocalizationService.Format("{0} track(s) added to the queue", added), spinning: false);
    }

    public void PlaySelectedTrack()
    {
        if (_view.MusicTrackList.SelectedItem is not PlayItem track) return;
        PlayTrackFromPanel(track);
    }

    public void PlayTrack(object sender)
    {
        if (ContextAudioItem(sender) is { } track) PlayTrackFromPanel(track);
    }

    private void PlayTrackFromPanel(PlayItem track)
    {
        var context = _trackView?.Cast<PlayItem>().ToList() ?? _openGroup?.Tracks.ToList();
        _queue.StartFrom(context, track);
        ClosePanel();
        _playback.Play(track);
    }

    // ============ PLAYLISTS ============
    public void CreatePlaylist()
    {
        var name = _view.PlaylistNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_store.Current.LocalPlaylists.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            _vm.ShowOverlay("A playlist with this name already exists.", spinning: false);
            _view.PlaylistNameBox.Focus(); _view.PlaylistNameBox.SelectAll(); return;
        }
        _store.Current.LocalPlaylists.Add(new LocalPlaylist { Name = name });
        _view.PlaylistNameBox.Clear();
        _store.Save();
        if (_catalog.Current == Section.LocalAudio && _catalog.IsAudioGroupMode) RebuildGroups();
    }

    public void DeletePlaylist(object sender)
    {
        if (ContextGroup(sender) is not { Playlist: { } playlist }) return;
        _store.Current.LocalPlaylists.Remove(playlist);
        _store.Save();
        ClosePanel();
        if (_catalog.Current == Section.LocalAudio && _catalog.IsAudioGroupMode) RebuildGroups();
    }

    public void OnGroupMenuOpened()
    {
        _menuTargetGroup = _view.MusicGroupList.SelectedItem as MusicGroup;
        var isPlaylist = _menuTargetGroup is { Kind: MusicGroupKind.Playlist };
        _view.ContextRemoveMusicGroup.IsEnabled = _menuTargetGroup?.Tracks.Count > 0;
        _view.ContextRemoveMusicGroup.Header = _menuTargetGroup is { } group
            ? LocalizationService.Format("Remove {0} track(s) from the library", group.Tracks.Count)
            : LocalizationService.T("Remove from library");
        _view.ContextPlaylistSeparator.Visibility = isPlaylist ? Visibility.Visible : Visibility.Collapsed;
        _view.ContextDeletePlaylist.Visibility = isPlaylist ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RemoveGroup(object sender)
    {
        if (ContextGroup(sender) is not { Tracks.Count: > 0 } group) return;
        var tracks = group.Tracks.ToList();
        var answer = MessageBox.Show(_view,
            LocalizationService.Format("Remove {0} track(s) from “{1}” in the local library?\n\nThe files will remain on disk.", tracks.Count, group.Name),
            LocalizationService.T("Remove from library"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        ClosePanel();
        RemoveLocalItems(tracks);
    }

    private void AddTrackToPlaylist(PlayItem track, LocalPlaylist playlist)
    {
        var path = LocalLibraryService.PathOf(track);
        if (!playlist.TrackPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) playlist.TrackPaths.Add(path);
        _store.Save();
        if (_catalog.Current == Section.LocalAudio && _catalog.AudioBrowseMode == "playlists") RebuildGroups();
        _vm.ShowOverlay(LocalizationService.Format("Added to “{0}”", playlist.Name), spinning: false);
    }

    // "Add to playlist" submenu, rebuilt each time the menu opens.
    private void PopulatePlaylistMenu(MenuItem menu, PlayItem? track)
    {
        menu.Items.Clear();
        var playlists = _store.Current.LocalPlaylists;
        menu.IsEnabled = playlists.Count > 0 && track != null;
        if (!menu.IsEnabled)
        {
            menu.Items.Add(new MenuItem { Header = LocalizationService.T("No playlists. Create one under Playlists."), IsEnabled = false });
            return;
        }
        foreach (var playlist in playlists)
        {
            var item = new MenuItem { Header = playlist.Name };
            var target = playlist;
            item.Click += (_, _) => { if (track != null) AddTrackToPlaylist(track, target); };
            menu.Items.Add(item);
        }
    }

    public void RemoveFromPlaylist(object sender)
    {
        if (_openGroup is not { Playlist: { } playlist } group || ContextAudioItem(sender) is not { } track) return;
        var path = LocalLibraryService.PathOf(track);
        playlist.TrackPaths.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        _store.Save();
        group.Tracks.RemoveAll(t => t.SameAs(track));
        RefreshTrackView(group);
        _view.MusicPanelDetail.Text = LocalizationService.T(group.DetailLine);
        RebuildGroups();
    }

    // ============ CONTEXT MENUS ============
    private void RefreshTrackView(MusicGroup group)
    {
        for (var i = 0; i < group.Tracks.Count; i++)
            group.Tracks[i].DisplayTrackNumberLabel = group.Kind == MusicGroupKind.Playlist ? (i + 1).ToString() : group.Tracks[i].TrackNumberLabel;
        _trackView = new ListCollectionView(group.Tracks) { Filter = TrackFilter };
        _view.MusicTrackList.ItemsSource = _trackView;
    }

    private PlayItem? ContextAudioItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as PlayItem
        ?? (_view.MusicPanel.Visibility == Visibility.Visible ? _view.MusicTrackList.SelectedItem as PlayItem : null)
        ?? _view.ItemList.SelectedItem as PlayItem;

    public void OnLocalItemMenuOpened()
    {
        var item = _view.ItemList.SelectedItem as PlayItem;
        var audio = item != null && MediaKinds.IsAudioOnly(item);
        _view.ContextPlayNext.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        _view.ContextAddQueue.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        _view.ContextAddPlaylist.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        _view.ContextEditMeta.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        _view.ContextRemoveLibrary.Visibility = item?.Kind == PlayItemKind.Local ? Visibility.Visible : Visibility.Collapsed;
        if (audio) PopulatePlaylistMenu(_view.ContextAddPlaylist, item);
    }

    public void OnTrackMenuOpened()
    {
        PopulatePlaylistMenu(_view.TrackAddPlaylist, _view.MusicTrackList.SelectedItem as PlayItem);
        _view.TrackRemoveFromPlaylist.Visibility = _openGroup is { Kind: MusicGroupKind.Playlist }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public void PlayNow(object sender)
    {
        if (ContextAudioItem(sender) is not { } item) return;
        if (item.Kind == PlayItemKind.Series) { _catalog.OpenSeries(item); return; }
        if (MediaKinds.IsAudioOnly(item)) _catalog.PrepareVisibleAudioPlayback(item);
        _playback.Play(item);
    }

    public void PlayNext(object sender)
    {
        if (ContextAudioItem(sender) is not { } item || !MediaKinds.IsAudioOnly(item)) return;
        _queue.EnqueueNext(item);
    }

    public void AddToQueue(object sender)
    {
        if (ContextAudioItem(sender) is not { } item || !MediaKinds.IsAudioOnly(item)) return;
        _queue.Enqueue(item);
    }

    public void RemoveTrack(object sender)
    {
        if (ContextAudioItem(sender) is not { } item || item.Kind != PlayItemKind.Local) return;
        RemoveLocalItems([item]);
        if (_openGroup is { } group)
        {
            group.Tracks.RemoveAll(t => t.SameAs(item));
            if (group.Tracks.Count == 0) ClosePanel();
            else
            {
                RefreshTrackView(group);
                _view.MusicPanelDetail.Text = LocalizationService.T(group.DetailLine);
            }
        }
    }

    // ============ LIBRARY REMOVAL ============
    public void RemoveSelectedListItem()
    {
        if (_view.ItemList.SelectedItem is not PlayItem item || item.Kind != PlayItemKind.Local) return;
        RemoveLocalItems([item]);
    }

    public void ResetAudioLibrary()
    {
        if (_library.LocalAudio.Count == 0)
        {
            _vm.ShowOverlay("The audio library is already empty", spinning: false);
            return;
        }
        var answer = MessageBox.Show(_view,
            LocalizationService.Format("Remove {0} track(s) from the audio library?\nThe files will not be deleted from disk.", _library.LocalAudio.Count),
            LocalizationService.T("Reset library"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        ClosePanel();
        RemoveLocalItems(_library.LocalAudio.ToList());
    }

    /// <summary>
    /// Single-track and grouped removals share this path so favourites,
    /// playlists, queue and playback context cannot retain removed tracks.
    /// </summary>
    private void RemoveLocalItems(IEnumerable<PlayItem> items)
    {
        var matches = _library.RemoveLocal(items);
        if (matches == null) return;
        _queue.RemoveWhere(matches);
        if (_session.Current != null && matches(_session.Current)) _playback.StopRemovedMedia();
        _library.SaveLocal();
        _catalog.ShowSection(_catalog.Current);
    }

    // ============ METADATA EDITOR ============
    public void EditMetadata(object sender)
    {
        if (ContextAudioItem(sender) is not { } item || !MediaKinds.IsAudioOnly(item)) return;
        var path = LocalLibraryService.PathOf(item);
        if (!File.Exists(path))
        {
            _vm.ShowOverlay("File not found on disk.", spinning: false);
            return;
        }

        var metadata = AudioMetadataReader.Read(path, item.Name);
        var editor = new MetadataEditorWindow(path, metadata) { Owner = _view };
        if (editor.ShowDialog() != true || editor.Result is not { } edit) return;

        if (_session.Current?.SameAs(item) == true) _playback.ReleaseCurrentFile();

        try
        {
            AudioMetadataWriter.Write(path, edit);
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("Could not write metadata", ex);
            _vm.ShowOverlay("Could not write metadata (is the file locked?)", spinning: false);
            return;
        }

        LocalLibraryService.RefreshFromFile(item);
        _library.SynchronizeFavoriteCopy(item);
        _library.SaveLocal();
        DebugConsole.Info($"Metadata saved: {item.Name}");
        _vm.ShowOverlay("Metadata saved", spinning: false);

        if (_catalog.Current is Section.LocalAudio or Section.Fav)
        {
            if (_catalog.Current == Section.Fav) { _catalog.ShowSection(Section.Fav); return; }
            var reopen = _openGroup?.Name;
            _catalog.ShowSection(Section.LocalAudio);
            if (reopen != null && _catalog.IsAudioGroupMode)
            {
                var group = _groups.FirstOrDefault(g =>
                    string.Equals(g.Name, reopen, StringComparison.CurrentCultureIgnoreCase))
                    ?? _groups.FirstOrDefault(g => g.Tracks.Any(t => t.SameAs(item)));
                if (group != null) OpenGroup(group);
                else ClosePanel();
            }
        }
    }
}
