using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;
using System.IO;

namespace Elysium_Cast_IPTV.Services;

/// <summary>Imports and organises local media without coupling filesystem/tag work to WPF.</summary>
public sealed class LocalLibraryService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".alac", ".aiff", ".ape" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv" };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public Task<IReadOnlyList<PlayItem>> ImportFolderAsync(string folder, bool audio, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PlayItem>>(() =>
        {
            Func<string, bool> predicate = audio ? IsAudio : IsVideo;
            var result = new List<PlayItem>();
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(predicate))
            {
                ct.ThrowIfCancellationRequested();
                result.Add(audio ? CreateAudioItem(path) : PlayItem.FromLocalFile(path));
            }
            return audio
                ? result.OrderBy(i => i.AlbumArtist ?? i.Artist ?? "Artiste inconnu", StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(i => i.Album ?? "Album inconnu", StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(i => i.DiscNumber).ThenBy(i => i.TrackNumber).ThenBy(i => i.Name).ToList()
                : result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, ct);

    public static PlayItem EnrichAudioItem(PlayItem item) => CreateAudioItem(item.DirectUrl ?? item.Id);

    private static PlayItem CreateAudioItem(string path)
    {
        var item = PlayItem.FromLocalFile(path);
        var metadata = AudioMetadataReader.Read(path, item.Name);
        item.Name = metadata.Title;
        item.Artist = metadata.Artist;
        item.Album = metadata.Album;
        item.AlbumArtist = metadata.AlbumArtist;
        item.Genre = metadata.Genre;
        item.TrackNumber = metadata.TrackNumber;
        item.DiscNumber = metadata.DiscNumber;
        item.DurationSeconds = metadata.DurationSeconds;
        item.CategoryName = metadata.Artist ?? metadata.AlbumArtist ?? "Artiste inconnu";
        return item;
    }

    /// <summary>Re-reads tags of an existing item in place (after a metadata edit).</summary>
    public static void RefreshFromFile(PlayItem item)
    {
        var path = PathOf(item);
        var metadata = AudioMetadataReader.Read(path, Path.GetFileNameWithoutExtension(path));
        item.Name = metadata.Title;
        item.Artist = metadata.Artist;
        item.Album = metadata.Album;
        item.AlbumArtist = metadata.AlbumArtist;
        item.Genre = metadata.Genre;
        item.TrackNumber = metadata.TrackNumber;
        item.DiscNumber = metadata.DiscNumber;
        item.DurationSeconds = metadata.DurationSeconds;
        item.CategoryName = metadata.Artist ?? metadata.AlbumArtist ?? "Artiste inconnu";
        item.ResetCover();
    }

    /// <summary>Groups the flat track list into browsable albums / artists / genres / playlists.</summary>
    public static List<MusicGroup> BuildGroups(string mode, IReadOnlyList<PlayItem> tracks, IReadOnlyList<LocalPlaylist> playlists)
    {
        static IOrderedEnumerable<PlayItem> AlbumOrder(IEnumerable<PlayItem> items) => items
            .OrderBy(t => t.Album ?? "Album inconnu", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase);

        switch (mode)
        {
            case "albums":
                return tracks.GroupBy(t => t.Album ?? "Album inconnu", StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new MusicGroup
                    {
                        Kind = MusicGroupKind.Album,
                        Name = g.Key,
                        Subtitle = MostCommon(g, t => t.AlbumArtist ?? t.Artist) ?? "Artiste inconnu",
                        Tracks = AlbumOrder(g).ToList()
                    })
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            case "genres":
                return tracks.GroupBy(t => t.Genre ?? "Genre inconnu", StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new MusicGroup
                    {
                        Kind = MusicGroupKind.Genre,
                        Name = g.Key,
                        Subtitle = g.Count() > 1 ? $"{g.Count()} titres" : "1 titre",
                        Tracks = AlbumOrder(g).ToList()
                    })
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            case "playlists":
                return playlists.Select(p =>
                {
                    var resolved = ResolvePlaylist(p, tracks).ToList();
                    return new MusicGroup
                    {
                        Kind = MusicGroupKind.Playlist,
                        Name = p.Name,
                        Subtitle = resolved.Count > 1 ? $"{resolved.Count} titres" : $"{resolved.Count} titre",
                        Tracks = resolved,
                        Playlist = p
                    };
                }).ToList();

            default: // artists (a collaboration belongs to every performer group)
                return tracks.SelectMany(track => AudioMetadataWriter.SplitList(track.Artist ?? track.AlbumArtist ?? "Artiste inconnu")
                        .Select(artist => (Artist: artist, Track: track)))
                    .GroupBy(pair => pair.Artist, pair => pair.Track, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g =>
                    {
                        var albums = g.Select(t => t.Album).Where(a => a != null).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
                        var titles = g.Count() > 1 ? $"{g.Count()} titres" : "1 titre";
                        return new MusicGroup
                        {
                            Kind = MusicGroupKind.Artist,
                            Name = g.Key,
                            Subtitle = albums > 0 ? $"{albums} album{(albums > 1 ? "s" : "")} · {titles}" : titles,
                            Tracks = AlbumOrder(g).ToList()
                        };
                    })
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    private static string? MostCommon(IEnumerable<PlayItem> items, Func<PlayItem, string?> selector) => items
        .Select(selector).Where(v => !string.IsNullOrWhiteSpace(v))
        .GroupBy(v => v!, StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    public static void MergeInto(List<PlayItem> target, IEnumerable<PlayItem> imported)
    {
        var known = target.Select(PathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        target.AddRange(imported.Where(item => known.Add(PathOf(item))));
    }

    public static IReadOnlyList<PlayItem> ResolvePlaylist(LocalPlaylist playlist, IEnumerable<PlayItem> library)
    {
        var byPath = library.ToDictionary(PathOf, StringComparer.OrdinalIgnoreCase);
        return playlist.TrackPaths.Where(byPath.ContainsKey).Select(path => byPath[path]).ToList();
    }

    public static string PathOf(PlayItem item) => item.DirectUrl ?? item.Id;
}
