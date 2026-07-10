using System.Text.Json.Serialization;

namespace Elysium_Cast_IPTV.Models;

public enum ProfileKind { Xtream, M3u }

/// <summary>A saved connection the user can reload later.</summary>
public class Profile
{
    public string Name { get; set; } = "";
    public ProfileKind Kind { get; set; } = ProfileKind.Xtream;

    // Xtream
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>DPAPI-protected, base64 encoded. Never the clear password.</summary>
    public string ProtectedPassword { get; set; } = "";

    // M3U
    public string M3uPath { get; set; } = "";

    [JsonIgnore]
    public string Subtitle => Kind == ProfileKind.Xtream
        ? $"Xtream · {Username}"
        : "Playlist M3U";

    [JsonIgnore]
    public string Initial =>
        string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
}
