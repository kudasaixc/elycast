using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Video;
using Microsoft.Win32;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{
    private ImageSource? _audioEmbeddedCover;
    private Brush? _classicAudioBackgroundBrush;
    private string _audioCoreBackgroundKey = "";
    private Color[]? _audioAdaptivePalette;
    private string? _audioAdaptivePaletteKey;
    private float _audioCoreCenterX = .5f, _audioCoreCenterY = .5f, _audioCoreInnerRadius = .18f, _audioCoreUnitScale = .001f;
    private double _audioLastRenderedSeconds;
    private double _audioFpsWindowStartSeconds;
    private int _audioFpsWindowFrames;
    private double _audioActualFps;
    private double _audioNextFrameSeconds;
    private double _audioLastPlayerSyncSeconds;
    private bool _audioCoreRuntimeFailed;
    private int _audioCoreFailureCount;
    private double _audioCoreLastHealthCheck;
    private double _audioParallaxX, _audioParallaxY;
    private readonly AudioVisualizerSurface.LinePrimitive[] _audioResolvedBars = new AudioVisualizerSurface.LinePrimitive[AudioVisualizerSurface.BarCount];
    private readonly AudioVisualizerSurface.EllipsePrimitive[] _audioResolvedParticles = new AudioVisualizerSurface.EllipsePrimitive[AudioVisualizerSurface.MaxParticleCount];
    private readonly AudioVisualizerSurface.EllipsePrimitive[] _audioResolvedWaves = new AudioVisualizerSurface.EllipsePrimitive[AudioVisualizerSurface.MaxShockwaveCount];
    private readonly ElyAudioCoreInterop.LinePrimitive[] _audioCoreBars = new ElyAudioCoreInterop.LinePrimitive[AudioVisualizerSurface.BarCount];
    private readonly ElyAudioCoreInterop.EllipsePrimitive[] _audioCoreParticles = new ElyAudioCoreInterop.EllipsePrimitive[AudioVisualizerSurface.MaxParticleCount];
    private readonly ElyAudioCoreInterop.EllipsePrimitive[] _audioCoreWaves = new ElyAudioCoreInterop.EllipsePrimitive[AudioVisualizerSurface.MaxShockwaveCount];


    // ============ NAV / SECTIONS ============
    private async void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!_connected || _suppressNav) return;
        if (sender is not RadioButton { IsChecked: true, Tag: string target }) return;
        if (target == "Settings") { _sectionBeforeSettings = _section; OpenSettingsPanel(); return; }

        // Local-only mode sets _connected so the local library can use the
        // normal player shell, but it has no IPTV service behind Live/VOD/Series.
        // Never turn that absence into a misleading cached "0 item" catalogue.
        if (string.IsNullOrEmpty(_iptv.ProfileKey) && target is "Live" or "Movies" or "Series")
        {
            Disconnect_Click(this, new RoutedEventArgs());
            StatusText.Text = "Connecte un profil IPTV pour accéder au Live, aux Films et aux Séries.";
            return;
        }
        CloseSettingsPanel();
        CloseSeriesPanel();
        CloseMusicPanel();

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
        if (s == Section.Movies && _movieItems == null)
        {
            SectionTitle.Text = "Films…";
            try { _movieItems = (await _iptv.GetVodAsync(ct)).Select(PlayItem.FromVod).ToList(); MarkFavorites(_movieItems); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DebugConsole.Error("VOD : " + ex.Message);
                SectionTitle.Text = "Films indisponibles — réessaie";
                return;
            }
        }
        else if (s == Section.Series && _seriesItems == null)
        {
            SectionTitle.Text = "Séries…";
            try { _seriesItems = (await _iptv.GetSeriesAsync(ct)).Select(PlayItem.FromSeries).ToList(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DebugConsole.Error("Séries : " + ex.Message);
                SectionTitle.Text = "Séries indisponibles — réessaie";
                return;
            }
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
            Section.LocalAudio => GetAudioSectionItems(),
            Section.LocalVideo => _localVideoItems,
            Section.Fav => GetFavorites(),
            _ => _liveItems
        };
        SectionTitle.Text = s switch
        {
            Section.LocalAudio => "Musique locale",
            Section.LocalVideo => "Vidéos locales",
            Section.Live => "Chaînes", Section.Movies => "Films",
            Section.Series => "Séries", Section.Fav => "Favoris", _ => ""
        };

        LocalActionsPanel.Visibility = s is Section.LocalAudio or Section.LocalVideo ? Visibility.Visible : Visibility.Collapsed;
        AudioLibraryActions.Visibility = s == Section.LocalAudio ? Visibility.Visible : Visibility.Collapsed;
        VideoLibraryActions.Visibility = s == Section.LocalVideo ? Visibility.Visible : Visibility.Collapsed;

        // Local audio browses through grouped covers (albums / artists / genres /
        // playlists); the flat PlayItem list only serves "tous les titres" and the
        // other sections.
        var groupMode = s == Section.LocalAudio && IsAudioGroupMode;
        MusicGroupList.Visibility = groupMode ? Visibility.Visible : Visibility.Collapsed;
        ItemList.Visibility = groupMode ? Visibility.Collapsed : Visibility.Visible;
        CategoryCombo.Visibility = groupMode ? Visibility.Collapsed : Visibility.Visible;
        PlaylistCreateRow.Visibility = s == Section.LocalAudio && AudioBrowseMode == "playlists"
            ? Visibility.Visible : Visibility.Collapsed;
        RemoveLocalButton.Visibility = s == Section.LocalVideo || (s == Section.LocalAudio && !groupMode)
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateResumeButton();
        if (groupMode)
        {
            RebuildMusicGroups();
            return;
        }
        _items.Reset(source);
        _view = new ListCollectionView(_items) { Filter = FilterItem };
        ItemList.ItemsSource = _view;
        BuildCategoryFilter(source);
        UpdateCount();
    }

    private List<PlayItem> GetFavorites() { MarkFavorites(_state.Favorites); return _state.Favorites.ToList(); }

    // ============ LOCAL LIBRARY ============
    private async void LoadLocalLibrary()
    {
        _localAudioItems = DeduplicateLocal(StateStore.Current.LocalAudioLibrary);
        _localVideoItems = DeduplicateLocal(StateStore.Current.LocalVideoLibrary);
        RepairAndEnrichLocalFavorites();
        MarkFavorites(_localAudioItems);
        MarkFavorites(_localVideoItems);

        var snapshot = _localAudioItems.ToList();
        var enriched = await Task.Run(() => snapshot.Select(LocalLibraryService.EnrichAudioItem).ToList());
        var enrichedByPath = enriched.ToDictionary(LocalLibraryService.PathOf, StringComparer.OrdinalIgnoreCase);
        // Merge into the live list: imports may append while TagLib is reading
        // the startup snapshot, and removals must not be resurrected.
        for (var i = 0; i < _localAudioItems.Count; i++)
            if (enrichedByPath.TryGetValue(LocalLibraryService.PathOf(_localAudioItems[i]), out var refreshed))
                _localAudioItems[i] = refreshed;
        RepairAndEnrichLocalFavorites();
        MarkFavorites(_localAudioItems);
        SaveLocalLibrary();
        if (_connected && _section == Section.LocalAudio) ShowSection(Section.LocalAudio);
    }

    private void SaveLocalLibrary()
    {
        StateStore.Current.LocalAudioLibrary = _localAudioItems.ToList();
        StateStore.Current.LocalVideoLibrary = _localVideoItems.ToList();
        StateStore.Save();
    }

    private void AddLocalFiles_Click(object sender, RoutedEventArgs e)
    {
        ImportLocalFolder_Click(sender, e);
    }

    private void RemoveLocalFile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not PlayItem item || item.Kind != PlayItemKind.Local) return;
        RemoveLocalItem(item);
    }

    private void ResetAudioLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_localAudioItems.Count == 0)
        {
            ShowOverlay("La bibliothèque audio est déjà vide", spinning: false);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Retirer les {_localAudioItems.Count} titre(s) de la bibliothèque audio ?\n" +
            "Les fichiers ne sont pas supprimés du disque.",
            "Réinitialiser la bibliothèque", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        CloseMusicPanel();
        RemoveLocalItems(_localAudioItems.ToList());
    }

    private void RemoveLocalItem(PlayItem item)
    {
        RemoveLocalItems([item]);
    }

    private void RemoveLocalItems(IEnumerable<PlayItem> items)
    {
        var targets = items.Where(item => item.Kind == PlayItemKind.Local).DistinctBy(LocalLibraryService.PathOf).ToList();
        if (targets.Count == 0) return;

        bool Matches(PlayItem candidate) => targets.Any(target => candidate.SameAs(target));
        var removedPaths = targets.Select(LocalLibraryService.PathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _localAudioItems.RemoveAll(Matches);
        _localVideoItems.RemoveAll(Matches);
        foreach (var playlist in StateStore.Current.LocalPlaylists)
            playlist.TrackPaths.RemoveAll(path => removedPaths.Contains(path));
        for (var i = _audioQueue.Count - 1; i >= 0; i--)
            if (Matches(_audioQueue[i])) _audioQueue.RemoveAt(i);
        _audioPlayContext?.RemoveAll(Matches);
        foreach (var profile in StateStore.Current.Profiles.Values)
            profile.Favorites.RemoveAll(Matches);

        if (_current != null && Matches(_current))
        {
            try { _videoBackend?.Stop(PlaybackEndReason.Replaced); } catch { }
            HideAudioVisualizer();
            _current = null;
            ShowOverlay("Fichier local retire.", spinning: false);
        }

        UpdateQueueLabel();
        SaveLocalLibrary();
        StateStore.Save();
        ShowSection(_section);
    }

    private void SynchronizeFavoriteCopy(PlayItem item)
    {
        var favorite = _state.Favorites.FirstOrDefault(f => f.SameAs(item));
        if (favorite == null) return;
        favorite.Name = item.Name; favorite.Icon = item.Icon; favorite.Ext = item.Ext;
        favorite.CategoryName = item.CategoryName; favorite.DirectUrl = item.DirectUrl;
        favorite.Artist = item.Artist; favorite.Album = item.Album; favorite.AlbumArtist = item.AlbumArtist;
        favorite.Genre = item.Genre; favorite.TrackNumber = item.TrackNumber;
        favorite.DiscNumber = item.DiscNumber; favorite.DurationSeconds = item.DurationSeconds;
    }

    private void RepairAndEnrichLocalFavorites()
    {
        var local = _localAudioItems.Concat(_localVideoItems).ToList();
        foreach (var favorite in StateStore.Current.Profiles.Values.SelectMany(p => p.Favorites)
                     .Where(f => f.Kind == PlayItemKind.Local))
        {
            var path = LocalLibraryService.PathOf(favorite);
            var match = local.FirstOrDefault(i => i.SameAs(favorite));
            if (match == null && !File.Exists(path))
            {
                var name = Path.GetFileName(path);
                var candidates = local.Where(i => string.Equals(Path.GetFileName(LocalLibraryService.PathOf(i)), name,
                    StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (candidates.Count == 1) match = candidates[0];
            }
            if (match == null) continue;
            favorite.Id = match.Id; favorite.DirectUrl = match.DirectUrl;
            favorite.Name = match.Name; favorite.CategoryName = match.CategoryName;
            favorite.Artist = match.Artist; favorite.Album = match.Album; favorite.AlbumArtist = match.AlbumArtist;
            favorite.Genre = match.Genre; favorite.TrackNumber = match.TrackNumber;
            favorite.DiscNumber = match.DiscNumber; favorite.DurationSeconds = match.DurationSeconds;
        }
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
        return string.IsNullOrWhiteSpace(q) || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (c.Artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.Album?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        _selectedCategory = CategoryCombo.SelectedItem as string ?? AllCategories;
        _view?.Refresh(); UpdateCount();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_section == Section.LocalAudio && IsAudioGroupMode) { ApplyMusicGroupFilter(); return; }
        _view?.Refresh(); UpdateCount();
    }
    private void UpdateCount() => CountText.Text = (_view?.Count ?? 0).ToString();

    private void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is not PlayItem item) return;
        if (item.Kind == PlayItemKind.Series) OpenSeries(item);
        else
        {
            // Launched from the flat list: playback advances through the library.
            if (IsAudioOnlyItem(item)) PrepareVisibleAudioPlayback(item);
            Play(item);
        }
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
                Ext = item.Ext, CategoryName = item.CategoryName, DirectUrl = item.DirectUrl, IsFavorite = true,
                Artist = item.Artist, Album = item.Album, AlbumArtist = item.AlbumArtist, Genre = item.Genre,
                TrackNumber = item.TrackNumber, DiscNumber = item.DiscNumber, DurationSeconds = item.DurationSeconds
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
            var metadata = AudioMetadataReader.Read(item.DirectUrl ?? item.Id, item.Name);
            _audioEngine.Start(item.DirectUrl ?? item.Id);
            AudioVisualizerTitle.Text = metadata.Title;
            _audioDefaultDisc ??= AudioDiscBrush.ImageSource;
            ApplyAudioMetadata(item, metadata);
            OverlayRoot.UpdateLayout();
            _mediaTransport.SetAudio(item, metadata);
            _mediaTransport.SetState(hasMedia: true, playing: true);
            ApplyAudioVisualizerSettings();
            ApplyAudioRendererSelection(showFeedback: false);
            AudioVisualizerLayer.Visibility = Visibility.Visible;
            _audioVisualStopwatch.Restart();
            _audioLastTickSeconds = 0;
            _audioLastRenderedSeconds = 0;
            _audioFpsWindowStartSeconds = 0;
            _audioFpsWindowFrames = 0;
            _audioActualFps = 0;
            _audioNextFrameSeconds = 0;
            _audioLastPlayerSyncSeconds = 0;
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
            _mediaTransport.Clear();
        }
    }

    private void HideAudioVisualizer()
    {
        if (_audioRenderHooked)
        {
            CompositionTarget.Rendering -= OnAudioVisualFrame;
            _audioRenderHooked = false;
        }
        // AudioCore+ draws into ELYCORE's shared swapchain; unhooking the WPF
        // frame pump alone leaves the native scene painting over the next video.
        if (ElyAudioCoreInterop.Available) ElyAudioCoreInterop.SetScene(false);
        AudioVisualizerLayer.Visibility = Visibility.Collapsed;
        _audioVisualStopwatch.Reset();
        _audioLastTickSeconds = 0;
        _audioLastRenderedSeconds = 0;
        _audioFpsWindowStartSeconds = 0;
        _audioFpsWindowFrames = 0;
        _audioActualFps = 0;
        _audioNextFrameSeconds = 0;
        _audioLastPlayerSyncSeconds = 0;
        _audioEngine.Stop();
        AudioVisualizerShake.X = 0;
        AudioVisualizerShake.Y = 0;
        AudioVisualizerScale.ScaleX = 1;
        AudioVisualizerScale.ScaleY = 1;
        AudioCenterScale.ScaleX = 1;
        AudioCenterScale.ScaleY = 1;
        AudioSurface.ResetScene();
        ResetAudioMetadataUi();
        AudioBackgroundImage.Source = null;
        AudioBackgroundImage.Opacity = 0;
        _audioBassEnergy = 0;
        _audioFullEnergy = 0;
        _audioBeatPulse = 0;
    }

    // Embedded tags (title / artist / album / cover): when present, the layout
    // splits — visualizer on the left, big cover with the credits on the
    // right. The centre disc ALWAYS keeps the Elycast Audio artwork.
    private void ApplyAudioMetadata(PlayItem item, AudioMetadata metadata)
    {
        var title = metadata.Title;
        var artist = metadata.Artist;
        var album = metadata.Album;
        ImageSource? cover = null;

        try
        {
            if (metadata.CoverBytes is { Length: > 0 } coverBytes)
            {
                using var stream = new MemoryStream(coverBytes);
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
        catch (Exception ex) { DebugConsole.Warn("Pochette audio illisible : " + ex.Message); }

        _audioEmbeddedCover = cover;
        if (cover == null && !metadata.HasEmbeddedTitle && artist == null && album == null)
        {
            ResetAudioMetadataUi();
            return;
        }

        AudioMetaTitle.Text = title;
        AudioMetaArtist.Text = artist ?? "";
        AudioMetaArtist.Visibility = artist == null ? Visibility.Collapsed : Visibility.Visible;
        AudioMetaAlbum.Text = album ?? "";
        AudioMetaAlbum.Visibility = album == null ? Visibility.Collapsed : Visibility.Visible;
        AudioCoverBrush.ImageSource = cover ?? _audioDefaultDisc;
        AudioMetaColumn.Width = new GridLength(1, GridUnitType.Star);
        AudioMetaPanel.Visibility = Visibility.Visible;
        AudioTitleBlock.Visibility = Visibility.Collapsed;
        DebugConsole.Info($"Métadonnées audio : {title}" +
                          (artist != null ? $" — {artist}" : "") +
                          (cover != null ? " (pochette intégrée)" : " (sans pochette)"));
    }

    private void ResetAudioMetadataUi()
    {
        AudioMetaPanel.Visibility = Visibility.Collapsed;
        AudioMetaColumn.Width = new GridLength(0);
        AudioTitleBlock.Visibility = Visibility.Visible;
        AudioCoverBrush.ImageSource = null;
        _audioEmbeddedCover = null;
    }

    private static readonly IReadOnlyDictionary<string, string> AudioBackgroundAssets = new Dictionary<string, string>
    {
        ["mountains"] = "mountains.jpg", ["sunset"] = "sunset.jpg", ["night-sky"] = "night-sky.jpg",
        ["paris"] = "paris.jpg", ["new-york"] = "new-york.jpg"
    };
    private static readonly Dictionary<string, ImageSource> AudioBackgroundAssetCache = new(StringComparer.OrdinalIgnoreCase);

    private void ApplyAudioVisualizerSettings()
    {
        if (AudioBackgroundImage == null) return;
        var s = StateStore.Settings;
        ImageSource? source = null;
        if (s.AudioBackgroundMode == "cover")
            source = _audioEmbeddedCover ?? LoadAudioBackgroundAsset(s.AudioBackgroundImage);
        else if (s.AudioBackgroundMode == "image")
            source = LoadAudioBackgroundAsset(s.AudioBackgroundImage);

        AudioBackgroundImage.Source = source;
        AudioBackgroundImage.Opacity = source == null ? 0 : 1;
        AudioBackgroundBlurEffect.Radius = s.AudioBackgroundBlur;
        AudioBackgroundDimmer.Background = new SolidColorBrush(Color.FromArgb((byte)(s.AudioBackgroundDim * 255), 0, 0, 0));
        AudioSurface.ActiveParticleCount = s.AudioParticleCount;
        AudioSurface.ParticleDistance = s.AudioParticleDistance;

        var backgroundKey = source == null ? "none"
            : ReferenceEquals(source, _audioEmbeddedCover)
                ? $"cover:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source)}"
                : $"asset:{s.AudioBackgroundImage}";

        // The adaptive palette depends only on the background image, not on the
        // slider being dragged — so memoise it by background key instead of running
        // the (expensive) dominant-colour extraction on every settings change.
        Color[]? palette = null;
        if (source is BitmapSource bitmap && s.AudioPaletteAutomatic && s.AudioParticleAdaptiveColors)
        {
            if (_audioAdaptivePalette == null || !string.Equals(backgroundKey, _audioAdaptivePaletteKey, StringComparison.Ordinal))
            {
                _audioAdaptivePalette = ExtractDominantPalette(bitmap);
                _audioAdaptivePaletteKey = backgroundKey;
            }
            palette = _audioAdaptivePalette;
        }
        else _audioAdaptivePaletteKey = null;
        AudioSurface.SetPalette(palette);

        var backgroundChanged = !string.Equals(backgroundKey, _audioCoreBackgroundKey, StringComparison.Ordinal);
        if (backgroundChanged) _audioCoreBackgroundKey = backgroundKey;
        ConfigureAudioCore(palette, source as BitmapSource, updateBackground: backgroundChanged);
    }

    private void ConfigureAudioCore(Color[]? palette, BitmapSource? background = null, Point? pointer = null, bool updateBackground = false)
    {
        var s = StateStore.Settings;
        var p = pointer ?? new Point(.5, .5);
        UpdateAudioCoreLayoutMetrics();
        ElyAudioCoreInterop.Configure(new ElyAudioCoreInterop.Settings
        {
            ParticleCount = s.AudioParticleCount,
            ParticleDistance = (float)s.AudioParticleDistance,
            // WPF materializes the dimmer as an 8-bit alpha brush.
            Dim = (byte)(s.AudioBackgroundDim * 255) / 255f,
            Blur = (float)s.AudioBackgroundBlur,
            SlowZoom = s.AudioBackgroundSlowZoom ? 1 : 0,
            SlowPan = s.AudioBackgroundSlowPan ? 1 : 0,
            Parallax = s.AudioBackgroundMouseParallax ? 1 : 0,
            Shake = s.AudioVisualizerShake ? 1 : 0,
            VSync = s.AudioVisualizerVSync ? 1 : 0,
            TargetFps = s.AudioVisualizerTargetFps,
            MouseX = (float)p.X,
            MouseY = (float)p.Y,
            CenterX = _audioCoreCenterX,
            CenterY = _audioCoreCenterY,
            InnerRadius = _audioCoreInnerRadius,
            UnitScale = _audioCoreUnitScale
        }, (palette ?? [
                Color.FromRgb(255, 40, 40), Color.FromRgb(255, 145, 35), Color.FromRgb(245, 225, 40),
                Color.FromRgb(55, 230, 95), Color.FromRgb(35, 220, 235), Color.FromRgb(55, 105, 255),
                Color.FromRgb(155, 70, 255), Color.FromRgb(245, 55, 195)])
            .Select(c => 0xff000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B).ToArray());
        if (updateBackground && background != null)
        {
            // Keep the same decoded source as WPF. AudioCore+ performs WPF's
            // 0.45 BitmapCache reduction after UniformToFill, so an early 512
            // px resize would permanently remove detail before the blur pass.
            var width = background.PixelWidth;
            var height = background.PixelHeight;
            var bgra = new FormatConvertedBitmap(background, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            ElyAudioCoreInterop.SetBackground(pixels, (uint)width, (uint)height, (uint)(width * 4));
        }
        else if (updateBackground) ElyAudioCoreInterop.SetBackground([], 0, 0, 0);
    }

    private void UpdateAudioCoreLayoutMetrics(bool pushNative = false)
    {
        if (OverlayRoot.ActualWidth <= 1 || OverlayRoot.ActualHeight <= 1
            || AudioOuterRing.ActualWidth <= 1 || AudioOuterRing.ActualHeight <= 1) return;
        try
        {
            var localCenter = new Point(AudioOuterRing.ActualWidth / 2, AudioOuterRing.ActualHeight / 2);
            var center = AudioOuterRing.TranslatePoint(localCenter, OverlayRoot);
            var unitPoint = AudioOuterRing.TranslatePoint(new Point(localCenter.X + 1, localCenter.Y), OverlayRoot);
            var unitPixels = (unitPoint - center).Length / Math.Max(.001, AudioVisualizerScale.ScaleX);
            var centerX = (float)(center.X / OverlayRoot.ActualWidth);
            var centerY = (float)(center.Y / OverlayRoot.ActualHeight);
            var unitScale = (float)(unitPixels / OverlayRoot.ActualHeight);
            var innerRadius = 198 * unitScale;
            var changed = Math.Abs(centerX - _audioCoreCenterX) > .0001f
                || Math.Abs(centerY - _audioCoreCenterY) > .0001f
                || Math.Abs(unitScale - _audioCoreUnitScale) > .00001f;
            _audioCoreCenterX = centerX; _audioCoreCenterY = centerY;
            _audioCoreUnitScale = unitScale; _audioCoreInnerRadius = innerRadius;
            if (pushNative && changed)
                ElyAudioCoreInterop.SetLayout(centerX, centerY, innerRadius, unitScale);
        }
        catch (InvalidOperationException) { }
    }

    private void PushResolvedAudioCoreFrame(double backgroundScale, double panX, double panY)
    {
        var rootWidth = OverlayRoot.ActualWidth;
        var rootHeight = OverlayRoot.ActualHeight;
        if (rootWidth <= 1 || rootHeight <= 1 || AudioSurface.ActualWidth <= 1 || AudioSurface.ActualHeight <= 1)
            return;

        AudioSurface.CopyResolvedPrimitives(
            _audioResolvedBars, out var barCount,
            _audioResolvedParticles, out var particleCount,
            _audioResolvedWaves, out var waveCount);

        try
        {
            var transform = AudioSurface.TransformToAncestor(OverlayRoot);
            var origin = transform.Transform(new Point(0, 0));
            var unitX = transform.Transform(new Point(1, 0));
            var unitY = transform.Transform(new Point(0, 1));
            var scaleX = (unitX - origin).Length;
            var scaleY = (unitY - origin).Length;
            var thicknessScale = (scaleX + scaleY) * 0.5;

            for (var i = 0; i < barCount; i++)
            {
                var source = _audioResolvedBars[i];
                var p0 = transform.Transform(new Point(source.X0, source.Y0));
                var p1 = transform.Transform(new Point(source.X1, source.Y1));
                _audioCoreBars[i] = new ElyAudioCoreInterop.LinePrimitive
                {
                    X0 = (float)(p0.X / rootWidth),
                    Y0 = (float)(p0.Y / rootHeight),
                    X1 = (float)(p1.X / rootWidth),
                    Y1 = (float)(p1.Y / rootHeight),
                    Thickness = (float)(source.Thickness * thicknessScale / rootHeight),
                    Color = PackAudioCoreColor(source.Color)
                };
            }
            for (var i = 0; i < particleCount; i++)
            {
                var source = _audioResolvedParticles[i];
                var center = transform.Transform(new Point(source.X, source.Y));
                _audioCoreParticles[i] = new ElyAudioCoreInterop.EllipsePrimitive
                {
                    X = (float)(center.X / rootWidth),
                    Y = (float)(center.Y / rootHeight),
                    RadiusX = (float)(source.RadiusX * scaleX / rootWidth),
                    RadiusY = (float)(source.RadiusY * scaleY / rootHeight),
                    Thickness = 0,
                    Color = PackAudioCoreColor(source.Color)
                };
            }
            for (var i = 0; i < waveCount; i++)
            {
                var source = _audioResolvedWaves[i];
                var center = transform.Transform(new Point(source.X, source.Y));
                _audioCoreWaves[i] = new ElyAudioCoreInterop.EllipsePrimitive
                {
                    X = (float)(center.X / rootWidth),
                    Y = (float)(center.Y / rootHeight),
                    RadiusX = (float)(source.RadiusX * scaleX / rootWidth),
                    RadiusY = (float)(source.RadiusY * scaleY / rootHeight),
                    Thickness = (float)(source.Thickness * thicknessScale / rootHeight),
                    Color = PackAudioCoreColor(source.Color)
                };
            }

            ElyAudioCoreInterop.PushVisualFrame(new ElyAudioCoreInterop.VisualFrame
            {
                RootWidthDip = (float)rootWidth,
                RootHeightDip = (float)rootHeight,
                BackgroundScale = (float)backgroundScale,
                BackgroundTranslateXDip = (float)panX,
                BackgroundTranslateYDip = (float)panY
            }, _audioCoreBars, barCount, _audioCoreParticles, particleCount, _audioCoreWaves, waveCount);
        }
        catch (InvalidOperationException) { }
    }

    private static uint PackAudioCoreColor(Color color) =>
        (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    private bool WantsAudioCore => StateStore.Settings.AudioVisualizerRenderer == "audiocore";
    private bool CanUseAudioCore => !_audioCoreRuntimeFailed && _videoBackend is MpvHwndBackend { IsElyCoreRenderer: true };

    private void RefreshAudioRendererStatus()
    {
        if (AudioRendererStatusText == null) return;
        AudioRendererStatusText.Text = _audioCoreRuntimeFailed
            ? "AudioCore+ a rencontré des erreurs D3D11 répétées — fallback Classique actif."
            : WantsAudioCore
            ? CanUseAudioCore
                ? "ELYCAST AudioCore+ actif — scène native D3D11, FRUC ignoré pour l’audio."
                : "AudioCore+ indisponible avec ce backend — fallback Classique (WPF) actif."
            : "Renderer Classique (WPF) actif.";
    }

    private void ApplyAudioRendererSelection(bool showFeedback)
    {
        if (showFeedback) { _audioCoreRuntimeFailed = false; _audioCoreFailureCount = 0; }
        var native = WantsAudioCore && CanUseAudioCore;
        var activated = native && ElyAudioCoreInterop.SetScene(true);
        if (!activated) ElyAudioCoreInterop.SetScene(false);
        if (activated)
        {
            // A renderer replacement owns a fresh D3D device/scene; force the
            // current background texture and every setting into that instance.
            _audioCoreBackgroundKey = "";
            ApplyAudioVisualizerSettings();
        }
        _classicAudioBackgroundBrush ??= AudioVisualizerLayer.Background;
        AudioVisualizerLayer.Background = activated ? Brushes.Transparent : _classicAudioBackgroundBrush;
        AudioBackgroundImage.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
        AudioBackgroundDimmer.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
        // Hidden preserves layout/ActualWidth so the classic surface can remain
        // the canonical animation simulation without submitting WPF draw calls.
        AudioSurface.Visibility = activated ? Visibility.Hidden : Visibility.Visible;
        // Keep the exact WPF centre artwork and decorative rings in both modes;
        // AudioCore+ replaces only the expensive spectrum/particle surface.
        AudioOuterRing.Visibility = Visibility.Visible;
        AudioGlowRing.Visibility = Visibility.Visible;
        AudioCenterDisc.Visibility = Visibility.Visible;
        AudioTitleBlock.Visibility = AudioMetaPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        AudioVisualizerLayer.Visibility = _current != null && IsAudioOnlyItem(_current)
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshAudioRendererStatus();
        // Never use VideoOverlay as renderer feedback: it is intentionally
        // opaque and would cover the native HWND scene while leaving only the
        // WPF centre artwork visible. Status stays in the settings row/Stats.
        if (_videoBackend?.HasMedia == true) HideOverlay();
        if (showFeedback) ShowOsd();
    }

    private static ImageSource? LoadAudioBackgroundAsset(string key)
    {
        if (!AudioBackgroundAssets.TryGetValue(key, out var file)) return null;
        if (AudioBackgroundAssetCache.TryGetValue(key, out var cached)) return cached;
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Assets/AudioBackgrounds/{file}", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1920;
            image.EndInit();
            image.Freeze();
            AudioBackgroundAssetCache[key] = image;
            return image;
        }
    }

    private static Color[] ExtractDominantPalette(BitmapSource source)
    {
        const int size = 48;
        var scaled = new TransformedBitmap(source, new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
        var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        formatted.CopyPixels(pixels, size * 4, 0);
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        var grayHistogram = new int[16];
        var sampled = 0;
        var chromatic = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var luminance = (int)Math.Round(r * 0.2126 + g * 0.7152 + b * 0.0722);
            grayHistogram[Math.Min(15, luminance / 16)]++;
            sampled++;
            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            if (max < 28 || min > 238 || saturation < 0.16) continue;
            chromatic++;
            var key = (r / 32 << 6) | (g / 32 << 3) | b / 32;
            var value = buckets.GetValueOrDefault(key);
            buckets[key] = (value.R + r, value.G + g, value.B + b, value.Count + 1);
        }

        // A monochrome cover is a valid palette, not an extraction failure.
        // Ignore isolated JPEG tint/noise: it must occupy a meaningful part of
        // the artwork before coloured accents are allowed into the visualizer.
        if (sampled == 0 || chromatic < sampled * 0.12)
        {
            var grays = grayHistogram.Select((count, bin) => (count, bin))
                .Where(x => x.count > 0)
                .OrderByDescending(x => x.count)
                .Take(5)
                .Select(x => (byte)Math.Clamp(x.bin * 16 + 8, 38, 235))
                .Distinct()
                .OrderBy(x => x)
                .Select(x => Color.FromRgb(x, x, x))
                .ToArray();
            return grays.Length >= 2
                ? grays
                : new[] { Color.FromRgb(96, 96, 96), Color.FromRgb(205, 205, 205), Color.FromRgb(245, 245, 245) };
        }

        var colors = buckets.Values.OrderByDescending(v => v.Count).Take(6)
            .Select(v => Color.FromRgb((byte)(v.R / v.Count), (byte)(v.G / v.Count), (byte)(v.B / v.Count))).ToArray();
        return colors.Length >= 2 ? colors : new[] { Color.FromRgb(139, 92, 246), Color.FromRgb(34, 211, 238), Color.FromRgb(251, 146, 60) };
    }

    // One call per displayed frame: feed the engine the player state, pull the
    // latest analysis snapshot, and drive the surface + the light transforms.
    private void OnAudioVisualFrame(object? sender, EventArgs e)
    {
        if (AudioVisualizerLayer.Visibility != Visibility.Visible) return;

        var now = _audioVisualStopwatch.Elapsed.TotalSeconds;
        var settings = StateStore.Settings;
        // 0 = illimité : aucune limite de cadence (intervalle nul = pas de gate).
        var frameInterval = settings.AudioVisualizerTargetFps > 0
            ? 1.0 / Math.Clamp(settings.AudioVisualizerTargetFps, 30, 480) : 0.0;
        // Phase accumulator instead of elapsed-time skipping. On a 360 Hz
        // display, a naive 240 FPS gate accepts every other VSync (=180 FPS);
        // this alternates 1/2 display intervals and converges to exactly 240.
        if (settings.AudioVisualizerVSync)
        {
            if (_audioNextFrameSeconds <= 0) _audioNextFrameSeconds = now;
            if (now + 0.0002 < _audioNextFrameSeconds) return;
            _audioNextFrameSeconds += frameInterval;
            if (now - _audioNextFrameSeconds > frameInterval * 2) _audioNextFrameSeconds = now + frameInterval;
        }
        else _audioNextFrameSeconds = 0; // unlocked, still bounded by WPF's compositor cadence
        _audioLastRenderedSeconds = now;
        var dt = now - _audioLastTickSeconds;
        if (dt < 0.0005) return; // Rendering can fire twice for one frame.
        dt = Math.Min(dt, 0.1);
        _audioLastTickSeconds = now;
        _audioFpsWindowFrames++;
        var fpsWindowDuration = now - _audioFpsWindowStartSeconds;
        if (fpsWindowDuration >= 0.75)
        {
            _audioActualFps = _audioFpsWindowFrames / fpsWindowDuration;
            _audioFpsWindowStartSeconds = now;
            _audioFpsWindowFrames = 0;
        }

        // The UI thread owns the backend — the engine thread never touches mpv.
        // mpv property reads cross the native boundary; 120 state syncs/s are
        // enough for an analyzer running at 120 Hz and avoid 240-360 P/Invokes/s.
        if (now - _audioLastPlayerSyncSeconds >= 1.0 / 120.0)
        {
            _audioEngine.UpdatePlayerState(_videoBackend?.PositionMs ?? 0, _videoBackend?.IsPlaying == true);
            _audioLastPlayerSyncSeconds = now;
        }

        _audioEngine.ReadSnapshot(_audioSpectrumSnapshot, out _audioBassEnergy, out _audioFullEnergy);
        while (_audioEngine.TryDequeueBeat(out var strength))
        {
            _audioBeatPulse = Math.Max(_audioBeatPulse, 0.55 + strength * 0.45);
            AudioSurface.Beat(strength);
        }
        _audioBeatPulse *= Math.Pow(0.04, dt);

        // One simulation owns both renderers. AudioCore+ keeps this element
        // Hidden (laid out but not submitted) and consumes these exact resolved
        // vector primitives, so particles and waves cannot drift between paths.
        AudioSurface.Advance(dt, _audioSpectrumSnapshot, _audioBassEnergy, _audioFullEnergy, _audioBeatPulse);
        AudioOuterRingRotate.Angle = (AudioOuterRingRotate.Angle + (12 + _audioBassEnergy * 80) * dt) % 360;
        AudioGlowRing.Opacity = 0.35 + _audioBassEnergy * 0.55;
        AudioVisualizerScale.ScaleX = AudioVisualizerScale.ScaleY = 1.0 + _audioFullEnergy * 0.02;
        AudioCenterScale.ScaleX = AudioCenterScale.ScaleY = 1.0 + _audioBeatPulse * 0.085;

        var backgroundScale = settings.AudioBackgroundSlowZoom
            ? 1.045 + Math.Sin(now * 0.095) * 0.018 : 1.045;
        var panX = settings.AudioBackgroundSlowPan ? Math.Sin(now * 0.071) * 9 : 0;
        var panY = settings.AudioBackgroundSlowPan ? Math.Cos(now * 0.057) * 6 : 0;
        // Parallax target. IsMouseOver is unreliable here: in AudioCore+ the
        // native ELYCORE swapchain sits over the WPF layer and steals hit-tests,
        // so it flickered false and the offset snapped back (the jolts). Read the
        // cursor via Win32 + PointFromScreen (works under a native child window)
        // and smooth toward the target so entering/leaving is fluid in both
        // renderers instead of resetting abruptly.
        double targetParallaxX = 0, targetParallaxY = 0;
        if (settings.AudioBackgroundMouseParallax
            && AudioVisualizerLayer.ActualWidth > 1 && AudioVisualizerLayer.ActualHeight > 1
            && GetCursorPos(out var cursor))
        {
            try
            {
                var local = AudioVisualizerLayer.PointFromScreen(new Point(cursor.X, cursor.Y));
                if (local.X >= 0 && local.Y >= 0
                    && local.X < AudioVisualizerLayer.ActualWidth && local.Y < AudioVisualizerLayer.ActualHeight)
                {
                    var intensity = settings.AudioBackgroundParallaxIntensity;
                    targetParallaxX = (local.X / AudioVisualizerLayer.ActualWidth - 0.5) * -10 * intensity;
                    targetParallaxY = (local.Y / AudioVisualizerLayer.ActualHeight - 0.5) * -7 * intensity;
                }
            }
            catch (InvalidOperationException) { }
        }
        // Critically-damped-ish smoothing, frame-rate independent (~60 ms to settle).
        var parallaxLerp = 1 - Math.Pow(0.0000001, dt);
        _audioParallaxX += (targetParallaxX - _audioParallaxX) * parallaxLerp;
        _audioParallaxY += (targetParallaxY - _audioParallaxY) * parallaxLerp;
        panX += _audioParallaxX;
        panY += _audioParallaxY;
        AudioBackgroundScale.ScaleX = AudioBackgroundScale.ScaleY = backgroundScale;
        AudioBackgroundTranslate.X = panX;
        AudioBackgroundTranslate.Y = panY;

        // The background never shakes; the shared content transform does.
        var shake = settings.AudioVisualizerShake ? Math.Max(0, _audioBeatPulse - 0.6) * 5 : 0;
        if (shake > 0.001)
        {
            AudioVisualizerShake.X = (_audioVisualRandom.NextDouble() - 0.5) * shake;
            AudioVisualizerShake.Y = (_audioVisualRandom.NextDouble() - 0.5) * shake;
        }
        else AudioVisualizerShake.X = AudioVisualizerShake.Y = 0;

        if (WantsAudioCore && CanUseAudioCore && ElyAudioCoreInterop.Available)
        {
            UpdateAudioCoreLayoutMetrics(pushNative: true);
            ElyAudioCoreInterop.PushAudioFrame(_audioSpectrumSnapshot, (float)_audioBassEnergy,
                (float)_audioFullEnergy, (float)_audioBeatPulse);
            PushResolvedAudioCoreFrame(backgroundScale, panX, panY);
            if (now - _audioCoreLastHealthCheck >= 1)
            {
                _audioCoreLastHealthCheck = now;
                var health = ElyAudioCoreInterop.GetStats();
                _audioCoreFailureCount = health.LastError < 0 ? _audioCoreFailureCount + 1 : 0;
                if (_audioCoreFailureCount >= 3)
                {
                    _audioCoreRuntimeFailed = true;
                    ElyAudioCoreInterop.SetScene(false);
                    ApplyAudioRendererSelection(showFeedback: false);
                    DebugConsole.Error($"ELYCAST AudioCore+ désactivé après erreurs D3D11 répétées (0x{health.LastError:X8}).");
                }
            }
        }
    }
}
