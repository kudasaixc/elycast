using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Video;
using Microsoft.Win32;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{
    private List<MusicGroup> _musicGroups = new();
    private MusicGroup? _openMusicGroup;
    private MusicGroup? _menuTargetGroup;
    // Tracks the list playback should advance through (the group the current
    // song was launched from). Null = the whole flat library.
    private List<PlayItem>? _audioPlayContext;
    private int _audioAutoIndex = -1;
    private bool _audioPlayingManualQueue;

    private string AudioBrowseMode => (AudioBrowseCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "albums";
    private bool IsAudioGroupMode => AudioBrowseMode is "albums" or "artists" or "genres" or "playlists";

    private static List<PlayItem> DeduplicateLocal(IEnumerable<PlayItem>? items) => (items ?? [])
        .Where(item => item.Kind == PlayItemKind.Local && !string.IsNullOrWhiteSpace(item.DirectUrl ?? item.Id))
        .GroupBy(LocalLibraryService.PathOf, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToList();

    private List<PlayItem> GetAudioSectionItems()
    {
        foreach (var track in _localAudioItems)
            track.CategoryName = track.Artist ?? track.AlbumArtist ?? "Artiste inconnu";
        return _localAudioItems;
    }

    private List<PlayItem> GetAudioPlaybackSource() =>
        _audioPlayContext is { Count: > 0 } context ? context : _localAudioItems;

    // ============ IMPORT ============
    private async void ImportLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        var audio = _section == Section.LocalAudio;
        var dialog = new OpenFolderDialog
        {
            Title = audio ? "Importer un dossier de musique" : "Importer un dossier de vidéos",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        LocalImportButton.IsEnabled = false;
        LocalImportButton.Content = "Analyse en cours…";
        try
        {
            // Throttled UI updates: the parallel import fires one report per file,
            // but we only refresh the button label a few times per second.
            var lastTick = 0L;
            var progress = new Progress<int>(count =>
            {
                var now = Environment.TickCount64;
                if (now - lastTick < 120) return;
                lastTick = now;
                LocalImportButton.Content = $"Analyse… {count} fichier(s)";
            });
            var imported = await _localLibrary.ImportFolderAsync(dialog.FolderName, audio, progress);
            var target = audio ? _localAudioItems : _localVideoItems;
            var before = target.Count;
            LocalLibraryService.MergeInto(target, imported);
            MarkFavorites(target);
            SaveLocalLibrary();
            ShowSection(audio ? Section.LocalAudio : Section.LocalVideo);
            ShowOverlay($"{target.Count - before} fichier(s) importé(s)", spinning: false);
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("Import du dossier local impossible", ex);
            ShowOverlay("Impossible d’importer ce dossier", spinning: false);
        }
        finally
        {
            LocalImportButton.IsEnabled = true;
            LocalImportButton.Content = "Importer un dossier";
        }
    }

    // ============ BROWSE MODES / GROUPS ============
    private void AudioBrowse_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            StateStore.Settings.AudioBrowseMode = AudioBrowseMode;
            StateStore.Save();
        }
        if (!_connected || _section != Section.LocalAudio) return;
        CloseMusicPanel();
        ShowSection(Section.LocalAudio);
    }

    private void RebuildMusicGroups()
    {
        _musicGroups = LocalLibraryService.BuildGroups(AudioBrowseMode, _localAudioItems, StateStore.Current.LocalPlaylists);
        LoadGroupCoversAsync(_musicGroups);
        ApplyMusicGroupFilter();
    }

    private void ApplyMusicGroupFilter()
    {
        var query = SearchBox.Text?.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _musicGroups
            : _musicGroups.Where(g => g.Matches(query)).ToList();
        MusicGroupList.ItemsSource = filtered;
        if (_section == Section.LocalAudio && IsAudioGroupMode)
            CountText.Text = filtered.Count.ToString();
    }

    // The group cover is the first embedded artwork found among its tracks.
    // Decoded off-thread and memoised in CoverArtCache, so rebuilds are cheap.
    private void LoadGroupCoversAsync(List<MusicGroup> groups)
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
                    Dispatcher.BeginInvoke(() => group.Cover = cover);
                    break;
                }
            }
        });
    }

    // ============ DETAIL PANEL ============
    private void MusicGroupList_Click(object sender, MouseButtonEventArgs e)
    {
        if (FindListItem(e.OriginalSource)?.DataContext is MusicGroup group)
            OpenMusicGroup(group);
    }

    private static ListBoxItem? FindListItem(object? origin)
    {
        DependencyObject? current = origin as DependencyObject;
        while (current != null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        return current as ListBoxItem;
    }

    private void OpenMusicGroup(MusicGroup group)
    {
        _openMusicGroup = group;
        MusicPanelKind.Text = group.KindLabel.ToUpperInvariant();
        MusicPanelTitle.Text = group.Name;
        var redundantSubtitle = group.Kind is MusicGroupKind.Genre or MusicGroupKind.Playlist;
        MusicPanelSubtitle.Text = redundantSubtitle ? "" : group.Subtitle;
        MusicPanelSubtitle.Visibility = redundantSubtitle || string.IsNullOrWhiteSpace(group.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        MusicPanelDetail.Text = group.DetailLine;
        MusicPanelCover.Source = group.Cover;
        MusicPanelInitial.Text = group.Initial;
        for (var i = 0; i < group.Tracks.Count; i++)
            group.Tracks[i].DisplayTrackNumberLabel = group.Kind == MusicGroupKind.Playlist
                ? (i + 1).ToString() : group.Tracks[i].TrackNumberLabel;
        MusicTrackList.ItemsSource = null;
        MusicTrackList.ItemsSource = group.Tracks;

        MusicPanel.Visibility = Visibility.Visible;
        CloseOverlayBtn.Visibility = Visibility.Visible;
        HideOsd(force: true);
        VideoStage.Cursor = Cursors.Arrow;
        FadeIn(MusicPanel);
    }

    private void CloseMusic_Click(object sender, RoutedEventArgs e) => CloseMusicPanel();

    private void CloseMusicPanel()
    {
        if (MusicPanel == null) return;
        _openMusicGroup = null;
        MusicPanel.Visibility = Visibility.Collapsed;
        MusicGroupList.SelectedItem = null;
        UpdateCloseOverlayButton();
    }

    /// <summary>Group targeted by a panel button or a sidebar context menu action.</summary>
    private MusicGroup? ContextMusicGroup(object? sender = null)
    {
        if (sender is MenuItem && _menuTargetGroup is { } menuTarget)
        {
            _menuTargetGroup = null;
            return menuTarget;
        }
        return (sender as FrameworkElement)?.DataContext as MusicGroup
            ?? (MusicPanel.Visibility == Visibility.Visible ? _openMusicGroup : MusicGroupList.SelectedItem as MusicGroup);
    }

    private void GroupPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMusicGroup(sender) is { } group) PlayGroupTracks(group.Tracks, shuffle: false);
    }

    private void GroupShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMusicGroup(sender) is { } group) PlayGroupTracks(group.Tracks, shuffle: true);
    }

    private void GroupQueue_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMusicGroup(sender) is not { } group) return;
        foreach (var track in group.Tracks)
            if (!_audioQueue.Any(q => q.SameAs(track))) _audioQueue.Add(track);
        UpdateQueueLabel();
        ShowOverlay($"{group.Tracks.Count} titre(s) ajoutés à la file", spinning: false);
    }

    private void PlayGroupTracks(IReadOnlyList<PlayItem> tracks, bool shuffle)
    {
        if (tracks.Count == 0) return;
        var order = shuffle ? tracks.OrderBy(_ => _queueRandom.Next()).ToList() : tracks.ToList();
        _audioPlayContext = order;
        _audioAutoIndex = 0;
        _audioPlayingManualQueue = false;
        CloseMusicPanel();
        Play(order[0]);
    }

    private void MusicTrack_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MusicTrackList.SelectedItem is not PlayItem track) return;
        PrepareMusicGroupTrackPlayback(track);
        CloseMusicPanel();
        Play(track);
    }

    private void MusicTrackPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ContextAudioItem(sender) is not { } track) return;
        PrepareMusicGroupTrackPlayback(track);
        CloseMusicPanel();
        Play(track);
    }

    private void PrepareMusicGroupTrackPlayback(PlayItem track)
    {
        _audioPlayContext = _openMusicGroup?.Tracks.ToList();
        if (_openMusicGroup is not { } group) return;
        _audioAutoIndex = group.Tracks.FindIndex(item => item.SameAs(track));
        _audioPlayingManualQueue = false;
    }

    private void PrepareVisibleAudioPlayback(PlayItem track)
    {
        var visible = _view?.Cast<PlayItem>().Where(IsAudioOnlyItem).ToList() ?? _localAudioItems.ToList();
        _audioPlayContext = visible;
        _audioAutoIndex = visible.FindIndex(item => item.SameAs(track));
        _audioPlayingManualQueue = false;
    }

    // ============ PLAYLISTS ============
    private void PlaylistNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CreatePlaylist_Click(sender, e);
    }

    private void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = PlaylistNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        StateStore.Current.LocalPlaylists.Add(new LocalPlaylist { Name = name });
        PlaylistNameBox.Clear();
        StateStore.Save();
        if (_section == Section.LocalAudio && IsAudioGroupMode) RebuildMusicGroups();
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMusicGroup(sender) is not { Playlist: { } playlist }) return;
        StateStore.Current.LocalPlaylists.Remove(playlist);
        StateStore.Save();
        CloseMusicPanel();
        if (_section == Section.LocalAudio && IsAudioGroupMode) RebuildMusicGroups();
    }

    private void MusicGroupMenu_Opened(object sender, RoutedEventArgs e)
    {
        _menuTargetGroup = MusicGroupList.SelectedItem as MusicGroup;
        var isPlaylist = _menuTargetGroup is { Kind: MusicGroupKind.Playlist };
        ContextRemoveMusicGroup.IsEnabled = _menuTargetGroup?.Tracks.Count > 0;
        ContextRemoveMusicGroup.Header = _menuTargetGroup is { } group
            ? $"Retirer {group.Tracks.Count} titre(s) de la bibliothèque"
            : "Retirer de la bibliothèque";
        ContextPlaylistSeparator.Visibility = isPlaylist ? Visibility.Visible : Visibility.Collapsed;
        ContextDeletePlaylist.Visibility = isPlaylist ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveMusicGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMusicGroup(sender) is not { Tracks.Count: > 0 } group) return;
        var tracks = group.Tracks.ToList();
        var answer = MessageBox.Show(this,
            $"Retirer les {tracks.Count} titre(s) de « {group.Name} » de la bibliothèque locale ?\n\nLes fichiers resteront sur le disque.",
            "Retirer de la bibliothèque", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        CloseMusicPanel();
        RemoveLocalItems(tracks);
    }

    private void AddTrackToPlaylist(PlayItem track, LocalPlaylist playlist)
    {
        var path = LocalLibraryService.PathOf(track);
        if (!playlist.TrackPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) playlist.TrackPaths.Add(path);
        StateStore.Save();
        if (_section == Section.LocalAudio && AudioBrowseMode == "playlists") RebuildMusicGroups();
        ShowOverlay($"Ajouté à « {playlist.Name} »", spinning: false);
    }

    // "Ajouter à une playlist" submenu, rebuilt each time the menu opens.
    private void PopulatePlaylistMenu(MenuItem menu, PlayItem? track)
    {
        menu.Items.Clear();
        var playlists = StateStore.Current.LocalPlaylists;
        menu.IsEnabled = playlists.Count > 0 && track != null;
        if (!menu.IsEnabled)
        {
            menu.Items.Add(new MenuItem { Header = "Aucune playlist — crée-la dans « Playlists »", IsEnabled = false });
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

    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_openMusicGroup is not { Playlist: { } playlist } group || ContextAudioItem(sender) is not { } track) return;
        var path = LocalLibraryService.PathOf(track);
        playlist.TrackPaths.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        StateStore.Save();
        group.Tracks.RemoveAll(t => t.SameAs(track));
        MusicTrackList.ItemsSource = null;
        MusicTrackList.ItemsSource = group.Tracks;
        MusicPanelDetail.Text = group.DetailLine;
        RebuildMusicGroups();
    }

    // ============ CONTEXT MENUS ============
    private PlayItem? ContextAudioItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as PlayItem
        ?? (MusicPanel.Visibility == Visibility.Visible ? MusicTrackList.SelectedItem as PlayItem : null)
        ?? ItemList.SelectedItem as PlayItem;

    private void ItemList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindListItem(e.OriginalSource) is { } row) row.IsSelected = true;
    }

    private void LocalItemMenu_Opened(object sender, RoutedEventArgs e)
    {
        var item = ItemList.SelectedItem as PlayItem;
        var audio = item != null && IsAudioOnlyItem(item);
        ContextPlayNext.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        ContextAddQueue.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        ContextAddPlaylist.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        ContextEditMeta.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        ContextRemoveLibrary.Visibility = item?.Kind == PlayItemKind.Local ? Visibility.Visible : Visibility.Collapsed;
        if (audio) PopulatePlaylistMenu(ContextAddPlaylist, item);
    }

    private void MusicTrackMenu_Opened(object sender, RoutedEventArgs e)
    {
        PopulatePlaylistMenu(TrackAddPlaylist, MusicTrackList.SelectedItem as PlayItem);
        TrackRemoveFromPlaylist.Visibility = _openMusicGroup is { Kind: MusicGroupKind.Playlist }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlayNow_Click(object sender, RoutedEventArgs e)
    {
        if (ContextAudioItem(sender) is not { } item) return;
        if (IsAudioOnlyItem(item)) PrepareVisibleAudioPlayback(item);
        Play(item);
    }

    private void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (ContextAudioItem(sender) is not { } item || !IsAudioOnlyItem(item)) return;
        var duplicate = _audioQueue.FirstOrDefault(queued => queued.SameAs(item));
        if (duplicate != null) _audioQueue.Remove(duplicate);
        _audioQueue.Insert(0, item);
        UpdateQueueLabel();
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (ContextAudioItem(sender) is not { } item || !IsAudioOnlyItem(item)) return;
        _audioQueue.Add(item);
        UpdateQueueLabel();
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (ContextAudioItem(sender) is not { } item || item.Kind != PlayItemKind.Local) return;
        RemoveLocalItem(item);
        if (_openMusicGroup is { } group)
        {
            group.Tracks.RemoveAll(t => t.SameAs(item));
            if (group.Tracks.Count == 0) CloseMusicPanel();
            else
            {
                MusicTrackList.ItemsSource = null;
                MusicTrackList.ItemsSource = group.Tracks;
                MusicPanelDetail.Text = group.DetailLine;
            }
        }
    }

    // ============ METADATA EDITOR ============
    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (ContextAudioItem(sender) is not { } item || !IsAudioOnlyItem(item)) return;
        var path = LocalLibraryService.PathOf(item);
        if (!File.Exists(path))
        {
            ShowOverlay("Fichier introuvable sur le disque.", spinning: false);
            return;
        }

        var metadata = AudioMetadataReader.Read(path, item.Name);
        var editor = new MetadataEditorWindow(path, metadata) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } edit) return;

        // mpv holds the file open while it plays: release it before writing.
        var wasCurrent = _current?.SameAs(item) == true;
        if (wasCurrent)
        {
            try { _videoBackend?.Stop(PlaybackEndReason.Replaced); } catch { }
            HideAudioVisualizer();
            _current = null;
        }

        try
        {
            AudioMetadataWriter.Write(path, edit);
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("Écriture des métadonnées impossible", ex);
            ShowOverlay("Impossible d’écrire les métadonnées (fichier verrouillé ?)", spinning: false);
            return;
        }

        LocalLibraryService.RefreshFromFile(item);
        SynchronizeFavoriteCopy(item);
        SaveLocalLibrary();
        DebugConsole.Info($"Métadonnées enregistrées : {item.Name}");
        ShowOverlay("Métadonnées enregistrées", spinning: false);

        if (_section is Section.LocalAudio or Section.Fav)
        {
            if (_section == Section.Fav) { ShowSection(Section.Fav); return; }
            var reopen = _openMusicGroup?.Name;
            ShowSection(Section.LocalAudio);
            if (reopen != null && IsAudioGroupMode)
            {
                var group = _musicGroups.FirstOrDefault(g =>
                    string.Equals(g.Name, reopen, StringComparison.CurrentCultureIgnoreCase))
                    ?? _musicGroups.FirstOrDefault(g => g.Tracks.Any(t => t.SameAs(item)));
                if (group != null) OpenMusicGroup(group);
                else CloseMusicPanel();
            }
        }
    }

    // ============ QUEUE / SHUFFLE / REPEAT ============
    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        _audioQueue.Clear();
        UpdateQueueLabel();
    }

    private void Shuffle_Checked(object sender, RoutedEventArgs e) => _audioShuffle = ShuffleToggle.IsChecked == true;
    private void Repeat_Checked(object sender, RoutedEventArgs e) => _audioRepeat = RepeatToggle.IsChecked == true;

    private bool TryPlayNextAudio()
    {
        if (_audioQueue.Count > 0)
        {
            var next = _audioQueue[0];
            _audioQueue.RemoveAt(0);
            UpdateQueueLabel();
            _audioPlayingManualQueue = true;
            Play(next);
            return true;
        }
        if (_audioRepeat && _current != null) { Play(_current); return true; }
        return PlayAdjacentAudio(1);
    }

    private bool PlayAdjacentAudio(int direction)
    {
        var source = GetAudioPlaybackSource();
        if (source.Count == 0) return false;
        if (!_audioPlayingManualQueue && _current != null)
        {
            var currentIndex = source.FindIndex(item => item.SameAs(_current));
            if (currentIndex >= 0) _audioAutoIndex = currentIndex;
        }
        _audioAutoIndex = _audioShuffle
            ? _queueRandom.Next(source.Count)
            : (_audioAutoIndex + direction + source.Count) % source.Count;
        _audioPlayingManualQueue = false;
        Play(source[_audioAutoIndex]);
        return true;
    }

    private void UpdateQueueLabel() => QueueCountText.Text = $"File : {_audioQueue.Count}";
}
