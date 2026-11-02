using System.IO;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.Library;

/// <summary>
/// In-memory catalogue of the open session: remote live/VOD/series lists, the
/// local audio/video libraries and the active profile's favourites. It owns
/// the data rules (deduplication, favourite marking, library removal, favourite
/// repair) and never touches WPF.
/// </summary>
public sealed class MediaLibrary
{
    private readonly IAppStateStore _store;

    public MediaLibrary(IAppStateStore store) => _store = store;

    /// <summary>
    /// The IPTV account behind the remote lists. Each sign-in owns a separate
    /// service; it is published here only after its own request succeeded.
    /// </summary>
    public IptvService Source { get; private set; } = new();

    public List<PlayItem> LiveItems { get; private set; } = new();

    /// <summary>Lazily fetched; null means "not loaded yet" so a failed load is retried.</summary>
    public List<PlayItem>? MovieItems { get; set; }

    /// <summary>Lazily fetched; null means "not loaded yet" so a failed load is retried.</summary>
    public List<PlayItem>? SeriesItems { get; set; }

    public List<PlayItem> LocalAudio { get; private set; } = new();
    public List<PlayItem> LocalVideo { get; private set; } = new();

    /// <summary>Favourites and last-played item of the signed-in profile.</summary>
    public ProfileState Profile { get; private set; } = new();

    // ============ SESSION ============
    public void OpenProfile(IptvService source, IEnumerable<PlayItem> liveItems)
    {
        Source = source;
        Profile = _store.ForProfile(source.ProfileKey);
        LiveItems = liveItems.ToList();
        ForgetRemoteCatalog();
        MarkFavorites(LiveItems);
    }

    public void OpenLocalOnly()
    {
        Source = new IptvService();
        LiveItems.Clear();
        ForgetRemoteCatalog();
        Profile = _store.ForProfile("local-only");
        MarkFavorites(LocalAudio);
        MarkFavorites(LocalVideo);
    }

    /// <summary>Sign-out: drops the account and its lazily loaded lists.</summary>
    public void CloseSource()
    {
        Source = new IptvService();
        ForgetRemoteCatalog();
    }

    public void ForgetRemoteCatalog()
    {
        MovieItems = null;
        SeriesItems = null;
    }

    // ============ FAVOURITES ============
    public void MarkFavorites(IEnumerable<PlayItem> items)
    {
        var set = Profile.Favorites.Select(f => f.IdentityKey).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items) item.IsFavorite = set.Contains(item.IdentityKey);
    }

    public List<PlayItem> GetFavorites()
    {
        MarkFavorites(Profile.Favorites);
        return Profile.Favorites.ToList();
    }

    /// <summary>
    /// Persists the favourite state already toggled on <paramref name="item"/>.
    /// Returns true when an existing favourite was removed.
    /// </summary>
    public bool CommitFavoriteToggle(PlayItem item)
    {
        var existing = Profile.Favorites.FirstOrDefault(f => f.SameAs(item));
        var removed = false;
        if (item.IsFavorite && existing == null)
        {
            Profile.Favorites.Add(new PlayItem
            {
                Kind = item.Kind, Id = item.Id, Name = item.Name, Icon = item.Icon,
                Ext = item.Ext, CategoryName = item.CategoryName, DirectUrl = item.DirectUrl, IsFavorite = true,
                Artist = item.Artist, Album = item.Album, AlbumArtist = item.AlbumArtist, Genre = item.Genre,
                TrackNumber = item.TrackNumber, DiscNumber = item.DiscNumber, DurationSeconds = item.DurationSeconds
            });
        }
        else if (!item.IsFavorite && existing != null)
        {
            Profile.Favorites.Remove(existing);
            removed = true;
        }
        _store.Save();
        return removed;
    }

    public void SynchronizeFavoriteCopy(PlayItem item)
    {
        var favorite = Profile.Favorites.FirstOrDefault(f => f.SameAs(item));
        if (favorite == null) return;
        favorite.Name = item.Name; favorite.Icon = item.Icon; favorite.Ext = item.Ext;
        favorite.CategoryName = item.CategoryName; favorite.DirectUrl = item.DirectUrl;
        favorite.Artist = item.Artist; favorite.Album = item.Album; favorite.AlbumArtist = item.AlbumArtist;
        favorite.Genre = item.Genre; favorite.TrackNumber = item.TrackNumber;
        favorite.DiscNumber = item.DiscNumber; favorite.DurationSeconds = item.DurationSeconds;
    }

    // ============ LOCAL LIBRARY ============
    public static List<PlayItem> DeduplicateLocal(IEnumerable<PlayItem>? items) => (items ?? [])
        .Where(item => item.Kind == PlayItemKind.Local && !string.IsNullOrWhiteSpace(item.DirectUrl ?? item.Id))
        .GroupBy(LocalLibraryService.PathOf, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToList();

    /// <summary>Loads the persisted libraries (startup shows persisted metadata immediately).</summary>
    public void LoadLocal()
    {
        LocalAudio = DeduplicateLocal(_store.Current.LocalAudioLibrary);
        LocalVideo = DeduplicateLocal(_store.Current.LocalVideoLibrary);
        RepairAndEnrichLocalFavorites();
        MarkFavorites(LocalAudio);
        MarkFavorites(LocalVideo);
    }

    /// <summary>
    /// Merges a background TagLib refresh into the live list: imports may have
    /// appended while the snapshot was read, and removals must not be resurrected.
    /// </summary>
    public void MergeEnrichedAudio(IEnumerable<PlayItem> enriched)
    {
        var enrichedByPath = enriched.ToDictionary(LocalLibraryService.PathOf, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < LocalAudio.Count; i++)
            if (enrichedByPath.TryGetValue(LocalLibraryService.PathOf(LocalAudio[i]), out var refreshed))
                LocalAudio[i] = refreshed;
        RepairAndEnrichLocalFavorites();
        MarkFavorites(LocalAudio);
        SaveLocal();
    }

    public void SaveLocal()
    {
        _store.Current.LocalAudioLibrary = LocalAudio.ToList();
        _store.Current.LocalVideoLibrary = LocalVideo.ToList();
        _store.Save();
    }

    /// <summary>Merges imported files; returns how many new entries were added.</summary>
    public int ApplyImported(IReadOnlyList<PlayItem> imported, bool audio)
    {
        var target = audio ? LocalAudio : LocalVideo;
        var before = target.Count;
        LocalLibraryService.MergeInto(target, imported);
        MarkFavorites(target);
        return target.Count - before;
    }

    /// <summary>The audio list as displayed in the flat "all tracks" view (grouped by artist).</summary>
    public List<PlayItem> AudioSectionItems()
    {
        foreach (var track in LocalAudio)
            track.CategoryName = track.Artist ?? track.AlbumArtist ?? LocalizationService.T("Unknown artist");
        return LocalAudio;
    }

    /// <summary>
    /// Removes local entries from both libraries, every playlist and every
    /// profile's favourites/last-played. Files on disk are never deleted.
    /// Returns the matcher for the removed paths, or null when nothing applies.
    /// The caller persists with <see cref="SaveLocal"/> after updating playback state.
    /// </summary>
    public Func<PlayItem, bool>? RemoveLocal(IEnumerable<PlayItem> items)
    {
        var targets = items.Where(item => item.Kind == PlayItemKind.Local).DistinctBy(LocalLibraryService.PathOf).ToList();
        if (targets.Count == 0) return null;

        var removedPaths = targets.Select(LocalLibraryService.PathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Matches(PlayItem candidate) => candidate.Kind == PlayItemKind.Local && removedPaths.Contains(LocalLibraryService.PathOf(candidate));

        LocalAudio.RemoveAll(Matches);
        LocalVideo.RemoveAll(Matches);
        foreach (var playlist in _store.Current.LocalPlaylists)
            playlist.TrackPaths.RemoveAll(path => removedPaths.Contains(path));
        foreach (var profile in _store.Current.Profiles.Values)
        {
            profile.Favorites.RemoveAll(Matches);
            if (profile.LastPlayed != null && Matches(profile.LastPlayed)) profile.LastPlayed = null;
        }
        return Matches;
    }

    /// <summary>
    /// Refreshes favourite copies from the library and repairs a moved file
    /// when exactly one library entry has the same file name.
    /// </summary>
    public void RepairAndEnrichLocalFavorites()
    {
        var local = LocalAudio.Concat(LocalVideo).ToList();
        foreach (var favorite in _store.Current.Profiles.Values.SelectMany(p => p.Favorites)
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
}
