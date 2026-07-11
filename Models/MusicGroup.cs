using System.ComponentModel;
using System.Windows.Media;

namespace Elysium_Cast_IPTV.Models;

public enum MusicGroupKind { Album, Artist, Genre, Playlist }

/// <summary>
/// A browsable unit of the local music library (one album, artist, genre or
/// playlist) shown in the sidebar with its cover art. The cover resolves
/// asynchronously after the groups are built.
/// </summary>
public sealed class MusicGroup : INotifyPropertyChanged
{
    public MusicGroupKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public List<PlayItem> Tracks { get; init; } = new();
    public LocalPlaylist? Playlist { get; init; }

    private ImageSource? _cover;
    public ImageSource? Cover
    {
        get => _cover;
        set { if (!ReferenceEquals(_cover, value)) { _cover = value; PropertyChanged?.Invoke(this, new(nameof(Cover))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "♪" : Name.Trim()[0].ToString().ToUpperInvariant();
    public string CountLabel => Tracks.Count.ToString();

    public string KindLabel => Kind switch
    {
        MusicGroupKind.Album => "Album",
        MusicGroupKind.Artist => "Artiste",
        MusicGroupKind.Genre => "Genre",
        MusicGroupKind.Playlist => "Playlist",
        _ => ""
    };

    /// <summary>"12 titres · 47 min" — shown in the detail panel header.</summary>
    public string DetailLine
    {
        get
        {
            var count = Tracks.Count;
            var label = count > 1 ? $"{count} titres" : $"{count} titre";
            var total = TimeSpan.FromSeconds(Tracks.Sum(t => t.DurationSeconds));
            if (total.TotalSeconds < 1) return label;
            var duration = total.TotalHours >= 1
                ? $"{(int)total.TotalHours} h {total.Minutes:00} min"
                : $"{(int)total.TotalMinutes} min";
            return $"{label} · {duration}";
        }
    }

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Tracks.Any(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || (t.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
}
