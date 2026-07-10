using System.Text.Json.Serialization;

namespace Elysium_Cast_IPTV.Models;

// ========================================================== VOD (films)
public class VodStream
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("stream_id")] public int StreamId { get; set; }
    [JsonPropertyName("stream_icon")] public string? StreamIcon { get; set; }
    [JsonPropertyName("container_extension")] public string? ContainerExtension { get; set; }
    [JsonPropertyName("category_id")] public string? CategoryId { get; set; }
    [JsonIgnore] public string CategoryName { get; set; } = "";
}

// ========================================================== SERIES
public class SeriesItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("series_id")] public int SeriesId { get; set; }
    [JsonPropertyName("cover")] public string? Cover { get; set; }
    [JsonPropertyName("category_id")] public string? CategoryId { get; set; }
    [JsonPropertyName("plot")] public string? Plot { get; set; }
    [JsonIgnore] public string CategoryName { get; set; } = "";
}

public class SeriesInfo
{
    [JsonPropertyName("episodes")]
    public Dictionary<string, List<Episode>> Episodes { get; set; } = new();
}

public class Episode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("container_extension")] public string? ContainerExtension { get; set; }
    [JsonPropertyName("season")] public int Season { get; set; }
    [JsonPropertyName("episode_num")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int EpisodeNum { get; set; }
}

// ========================================================== EPG
public class EpgEntry
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    public double ProgressPercent
    {
        get
        {
            var total = (End - Start).TotalSeconds;
            if (total <= 0) return 0;
            var done = (DateTime.Now - Start).TotalSeconds;
            return Math.Clamp(done / total * 100.0, 0, 100);
        }
    }
}

// ========================================================== PLAY ITEM
public enum PlayItemKind { Live, Movie, Series, Episode, Local }

/// <summary>
/// A unified, serialisable playable (or drill-down) entry used across Live, VOD,
/// Series and Favourites lists, and for persisting favourites / resume.
/// </summary>
public class PlayItem : System.ComponentModel.INotifyPropertyChanged
{
    public PlayItemKind Kind { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Ext { get; set; }
    public string CategoryName { get; set; } = "";
    public string? DirectUrl { get; set; }

    private bool _isFavorite;
    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; PropertyChanged?.Invoke(this, new(nameof(IsFavorite))); } }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        PlayItemKind.Live => "Live",
        PlayItemKind.Movie => "Film",
        PlayItemKind.Series => "Série",
        PlayItemKind.Episode => "Épisode",
        PlayItemKind.Local => "Local",
        _ => ""
    };

    public static PlayItem FromChannel(Channel c) => new()
    {
        Kind = PlayItemKind.Live, Id = c.StreamId.ToString(), Name = c.Name,
        Icon = c.StreamIcon, CategoryName = c.CategoryName, DirectUrl = c.DirectUrl
    };

    public static PlayItem FromVod(VodStream v) => new()
    {
        Kind = PlayItemKind.Movie, Id = v.StreamId.ToString(), Name = v.Name,
        Icon = v.StreamIcon, Ext = v.ContainerExtension, CategoryName = v.CategoryName
    };

    public static PlayItem FromSeries(SeriesItem s) => new()
    {
        Kind = PlayItemKind.Series, Id = s.SeriesId.ToString(), Name = s.Name,
        Icon = s.Cover, CategoryName = s.CategoryName
    };

    public static PlayItem FromEpisode(Episode e, string seriesName) => new()
    {
        Kind = PlayItemKind.Episode, Id = e.Id,
        Name = $"{seriesName} — S{e.Season:00}E{e.EpisodeNum:00} {e.Title}".Trim(),
        Ext = e.ContainerExtension, CategoryName = seriesName
    };

    public static PlayItem FromLocalFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return new PlayItem
        {
            Kind = PlayItemKind.Local,
            Id = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Ext = ext,
            CategoryName = LocalCategoryFor(ext),
            DirectUrl = path
        };
    }

    private static string LocalCategoryFor(string ext)
    {
        var audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "alac", "aiff", "ape"
        };
        return audio.Contains(ext) ? "Audio local" : "Vidéo locale";
    }

    public bool SameAs(PlayItem? other) => other != null && other.Kind == Kind && other.Id == Id;
}

/// <summary>Tolerant int converter (Xtream sometimes returns numbers as strings).</summary>
public class FlexibleIntConverter : System.Text.Json.Serialization.JsonConverter<int>
{
    public override int Read(ref System.Text.Json.Utf8JsonReader reader, Type t, System.Text.Json.JsonSerializerOptions o)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number) return reader.GetInt32();
        var s = reader.GetString();
        return int.TryParse(s, out var v) ? v : 0;
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, System.Text.Json.JsonSerializerOptions o)
        => writer.WriteNumberValue(value);
}
