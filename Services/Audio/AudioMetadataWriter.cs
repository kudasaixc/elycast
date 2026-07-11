namespace Elysium_Cast_IPTV.Services.Audio;

/// <summary>Edited tag values to persist into an audio file. Null string = clear the field.</summary>
public sealed record AudioTagEdit(
    string? Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    string? Genre,
    uint TrackNumber,
    uint DiscNumber,
    byte[]? CoverBytes,
    string? CoverMimeType,
    bool RemoveCover);

/// <summary>Writes tags back into the file itself so every player sees them.</summary>
public static class AudioMetadataWriter
{
    public static void Write(string path, AudioTagEdit edit)
    {
        using (var file = TagLib.File.Create(path))
        {
            var tag = file.Tag;
            tag.Title = Normalize(edit.Title);
            tag.Performers = SplitList(edit.Artist);
            tag.Album = Normalize(edit.Album);
            tag.AlbumArtists = SplitList(edit.AlbumArtist);
            tag.Genres = SplitList(edit.Genre);
            tag.Track = edit.TrackNumber;
            tag.Disc = edit.DiscNumber;

            if (edit.RemoveCover)
                tag.Pictures = [];
            else if (edit.CoverBytes is { Length: > 0 })
                tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(edit.CoverBytes))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = edit.CoverMimeType ?? "image/jpeg",
                        Description = "Cover"
                    }
                ];

            file.Save();
        }
        CoverArtCache.Invalidate(path);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Multi-value fields (performers, genres) accept "A; B" or "A, B".
    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
