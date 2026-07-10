using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Talks to an Xtream Codes panel (authenticates, lists categories/streams and
/// builds playable URLs) and also parses plain M3U playlists.
/// </summary>
public class IptvService
{
    // Keep the platform certificate validation intact. Accepting every certificate
    // exposed account credentials and stream URLs to any HTTPS man-in-the-middle.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public string BaseUrl { get; private set; } = "";
    public string Username { get; private set; } = "";
    public string Password { get; private set; } = "";
    public bool IsXtream { get; private set; }
    public string ProfileKey { get; private set; } = "";

    static IptvService()
    {
        if (!Http.DefaultRequestHeaders.UserAgent.TryParseAdd("VLC/3.0 LibVLC/3.0"))
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("ElyCast/2.0");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    // ======================================================== XTREAM CODES
    /// <summary>
    /// Authenticates and returns the live categories + the full live-stream list,
    /// with each channel's <see cref="Channel.CategoryName"/> resolved. Throws on
    /// failure.
    /// </summary>
    public async Task<(List<Category> categories, List<Channel> channels)> ConnectAsync(
        string url, string username, string password, CancellationToken ct = default)
    {
        var candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "http://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https"))
            throw new ArgumentException("L'URL Xtream doit être une URL HTTP ou HTTPS valide.", nameof(url));

        BaseUrl = server.GetLeftPart(UriPartial.Path).TrimEnd('/');
        Username = username.Trim();
        Password = password;
        IsXtream = true;
        ProfileKey = $"{BaseUrl}|{Username}";

        DebugConsole.Info($"Connexion Xtream -> {BaseUrl}");

        // get_live_categories is the route that groups channels by country/theme.
        var categories = await GetAsync<List<Category>>("get_live_categories", ct) ?? new();
        DebugConsole.Success($"{categories.Count} catégories récupérées.");

        var channels = await GetAsync<List<Channel>>("get_live_streams", ct) ?? new();
        DebugConsole.Success($"{channels.Count} chaînes récupérées.");

        // resolve category id -> name
        var map = categories.ToDictionary(c => c.CategoryId, c => c.CategoryName);
        foreach (var ch in channels)
            ch.CategoryName = ch.CategoryId != null && map.TryGetValue(ch.CategoryId, out var n) ? n : "Autres";

        return (categories, channels);
    }

    private async Task<T?> GetAsync<T>(string action, CancellationToken ct)
    {
        var requestUrl =
            $"{BaseUrl}/player_api.php?username={Uri.EscapeDataString(Username)}" +
            $"&password={Uri.EscapeDataString(Password)}&action={action}";

        using var response = await Http.GetAsync(requestUrl, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    /// <summary>Builds the playable URL for a channel (direct for M3U, Xtream otherwise).</summary>
    public string GetStreamUrl(Channel channel) =>
        !string.IsNullOrEmpty(channel.DirectUrl)
            ? channel.DirectUrl!
            : $"{BaseUrl}/live/{Username}/{Password}/{channel.StreamId}.{LiveExt}";

    private static string LiveExt =>
        StateStore.Settings.LiveStreamFormat == "m3u8" ? "m3u8" : "ts";

    /// <summary>Builds the playable URL for any <see cref="PlayItem"/>.</summary>
    public string GetStreamUrl(PlayItem item) => item.Kind switch
    {
        PlayItemKind.Live => !string.IsNullOrEmpty(item.DirectUrl)
            ? item.DirectUrl!
            : $"{BaseUrl}/live/{Username}/{Password}/{item.Id}.{LiveExt}",
        PlayItemKind.Movie => $"{BaseUrl}/movie/{Username}/{Password}/{item.Id}.{item.Ext ?? "mp4"}",
        PlayItemKind.Episode => $"{BaseUrl}/series/{Username}/{Password}/{item.Id}.{item.Ext ?? "mp4"}",
        PlayItemKind.Local => item.DirectUrl ?? item.Id,
        _ => ""
    };

    // ============================================================ VOD / SERIES
    public async Task<List<VodStream>> GetVodAsync(CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        var cats = await GetAsync<List<Category>>("get_vod_categories", ct) ?? new();
        var map = cats.ToDictionary(c => c.CategoryId, c => c.CategoryName);
        var vods = await GetAsync<List<VodStream>>("get_vod_streams", ct) ?? new();
        foreach (var v in vods)
            v.CategoryName = v.CategoryId != null && map.TryGetValue(v.CategoryId, out var n) ? n : "Autres";
        DebugConsole.Success($"{vods.Count} films récupérés.");
        return vods;
    }

    public async Task<List<SeriesItem>> GetSeriesAsync(CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        var cats = await GetAsync<List<Category>>("get_series_categories", ct) ?? new();
        var map = cats.ToDictionary(c => c.CategoryId, c => c.CategoryName);
        var series = await GetAsync<List<SeriesItem>>("get_series", ct) ?? new();
        foreach (var s in series)
            s.CategoryName = s.CategoryId != null && map.TryGetValue(s.CategoryId, out var n) ? n : "Autres";
        DebugConsole.Success($"{series.Count} séries récupérées.");
        return series;
    }

    public async Task<SeriesInfo> GetSeriesInfoAsync(string seriesId, CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        return await GetAsync<SeriesInfo>($"get_series_info&series_id={Uri.EscapeDataString(seriesId)}", ct) ?? new();
    }

    // ================================================================== EPG
    private class EpgResponse { public List<EpgRaw>? epg_listings { get; set; } }
    private class EpgRaw
    {
        public string? title { get; set; }
        public string? description { get; set; }
        public string? start_timestamp { get; set; }
        public string? stop_timestamp { get; set; }
    }

    public async Task<List<EpgEntry>> GetShortEpgAsync(string streamId, int limit = 6, CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        try
        {
            var resp = await GetAsync<EpgResponse>($"get_short_epg&stream_id={streamId}&limit={limit}", ct);
            var list = new List<EpgEntry>();
            foreach (var e in resp?.epg_listings ?? new())
            {
                list.Add(new EpgEntry
                {
                    Title = DecodeB64(e.title),
                    Description = DecodeB64(e.description),
                    Start = FromUnix(e.start_timestamp),
                    End = FromUnix(e.stop_timestamp)
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("EPG indisponible : " + ex.Message);
            return new();
        }
    }

    private static string DecodeB64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return s; }
    }

    private static DateTime FromUnix(string? ts) =>
        long.TryParse(ts, out var v) ? DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime : DateTime.MinValue;

    // ================================================================ M3U
    private static readonly Regex AttrRx = new("(\\w[\\w-]*)=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Loads an M3U playlist from a local file path or a remote URL. The
    /// <c>group-title</c> attribute is used as the category (country / theme).
    /// </summary>
    public async Task<(List<Category> categories, List<Channel> channels)> LoadM3uAsync(
        string pathOrUrl, CancellationToken ct = default)
    {
        DebugConsole.Info($"Chargement M3U -> {pathOrUrl}");
        IsXtream = false;
        ProfileKey = "m3u|" + pathOrUrl.Trim();
        string content;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            content = await Http.GetStringAsync(uri, ct);
        else
            content = await File.ReadAllTextAsync(pathOrUrl, ct);

        var channels = ParseM3u(content);
        var categories = channels
            .Select(c => c.CategoryName)
            .Distinct()
            .Select((name, i) => new Category { CategoryId = name, CategoryName = name })
            .ToList();

        DebugConsole.Success($"{channels.Count} chaînes M3U sur {categories.Count} catégories.");
        return (categories, channels);
    }

    private static List<Channel> ParseM3u(string content)
    {
        var result = new List<Channel>();
        var lines = content.Replace("\r", "").Split('\n');
        Channel? pending = null;
        int id = 1;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pending = new Channel { StreamId = id++ };
                var attrs = AttrRx.Matches(line);
                foreach (Match m in attrs)
                {
                    var key = m.Groups[1].Value.ToLowerInvariant();
                    var val = m.Groups[2].Value;
                    switch (key)
                    {
                        case "tvg-logo": pending.StreamIcon = val; break;
                        case "group-title": pending.CategoryName = val; pending.CategoryId = val; break;
                        case "tvg-name": if (string.IsNullOrEmpty(pending.Name)) pending.Name = val; break;
                    }
                }
                // display name after the last comma
                var comma = line.LastIndexOf(',');
                if (comma >= 0 && comma < line.Length - 1)
                    pending.Name = line[(comma + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(pending.CategoryName))
                    pending.CategoryName = "Autres";
            }
            else if (!line.StartsWith("#") && pending != null)
            {
                pending.DirectUrl = line;
                result.Add(pending);
                pending = null;
            }
        }
        return result;
    }
}
