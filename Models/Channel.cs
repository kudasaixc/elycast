using System.Text.Json.Serialization;

namespace Elysium_Cast_IPTV.Models;

/// <summary>
/// A single live stream. Populated either from the Xtream Codes
/// <c>get_live_streams</c> endpoint or parsed from an M3U playlist.
/// </summary>
public class Channel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("stream_id")]
    public int StreamId { get; set; }

    [JsonPropertyName("stream_icon")]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    /// <summary>Resolved category label (country / theme), e.g. "WORLD CUP 2026".</summary>
    [JsonIgnore]
    public string CategoryName { get; set; } = "";

    /// <summary>Direct playback URL (M3U entries). Null for Xtream streams.</summary>
    [JsonIgnore]
    public string? DirectUrl { get; set; }

    /// <summary>First letter used as a fallback avatar when no logo is available.</summary>
    [JsonIgnore]
    public string Initial =>
        string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
}

/// <summary>A live category returned by <c>get_live_categories</c>.</summary>
public class Category
{
    [JsonPropertyName("category_id")]
    public string CategoryId { get; set; } = "";

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; } = "";
}
