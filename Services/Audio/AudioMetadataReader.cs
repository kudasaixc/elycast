namespace Elysium_Cast_IPTV.Services.Audio;

public sealed record AudioMetadata(
    string Title,
    string? Artist,
    string? Album,
    byte[]? CoverBytes,
    string? CoverMimeType,
    bool HasEmbeddedTitle,
    string? AlbumArtist = null,
    string? Genre = null,
    uint TrackNumber = 0,
    uint DiscNumber = 0,
    double DurationSeconds = 0);

/// <summary>Single source of truth for local audio tags used by the UI and SMTC.</summary>
public static class AudioMetadataReader
{
    public static AudioMetadata Read(string path, string fallbackTitle)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var embeddedTitle = Clean(tag.Title);
            var artist = Clean(tag.JoinedPerformers) ?? Clean(tag.JoinedAlbumArtists);
            var album = Clean(tag.Album);
            var albumArtist = Clean(tag.JoinedAlbumArtists);
            var genre = Clean(tag.JoinedGenres);
            var picture = tag.Pictures.FirstOrDefault(p => p.Data?.Data is { Length: > 0 });
            return new AudioMetadata(
                embeddedTitle ?? fallbackTitle,
                artist,
                album,
                picture?.Data.Data,
                Clean(picture?.MimeType),
                embeddedTitle != null,
                albumArtist,
                genre,
                tag.Track,
                tag.Disc,
                file.Properties?.Duration.TotalSeconds ?? 0);
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Métadonnées audio illisibles : " + ex.Message);
            return new AudioMetadata(fallbackTitle, null, null, null, null, false);
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
