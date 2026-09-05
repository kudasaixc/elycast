using System.IO;
using System.Net.Http;
using System.Text.Json;
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
    private readonly HttpClient _http;

    public IptvService() : this(Http) { }
    internal IptvService(HttpClient http) => _http = http;

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
            throw new ArgumentException("The Xtream URL must be a valid HTTP or HTTPS URL.", nameof(url));

        if (!string.IsNullOrEmpty(server.UserInfo))
            throw new ArgumentException("Enter credentials in the username and password fields.");
        BaseUrl = server.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (BaseUrl.EndsWith("/player_api.php", StringComparison.OrdinalIgnoreCase))
            BaseUrl = BaseUrl[..^15];
        Username = username.Trim();
        Password = password;
        IsXtream = true;
        ProfileKey = $"{BaseUrl}|{Username}";

        DebugConsole.Info("Connecting to Xtream.");

        using var authentication = await GetAsync<JsonDocument>("", ct);
        if (authentication == null || !authentication.RootElement.TryGetProperty("user_info", out var userInfo) ||
            !userInfo.TryGetProperty("auth", out var auth) || auth.ToString() != "1")
            throw new InvalidOperationException(LocalizationService.T("The server rejected these credentials."));

        // get_live_categories is the route that groups channels by country/theme.
        var categories = await GetAsync<List<Category>>("get_live_categories", ct) ?? new();
        DebugConsole.Success($"{categories.Count} categories retrieved.");

        var channels = await GetAsync<List<Channel>>("get_live_streams", ct) ?? new();
        DebugConsole.Success($"{channels.Count} channels retrieved.");

        // resolve category id -> name
        var map = CategoryMap(categories);
        foreach (var ch in channels)
            ch.CategoryName = ch.CategoryId != null && map.TryGetValue(ch.CategoryId, out var n) ? n : LocalizationService.T("Other");

        return (categories, channels);
    }

    private async Task<T?> GetAsync<T>(string action, CancellationToken ct)
    {
        var requestUrl =
            $"{BaseUrl}/player_api.php?username={Uri.EscapeDataString(Username)}" +
            $"&password={Uri.EscapeDataString(Password)}&action={action}";

        using var response = await _http.GetAsync(requestUrl, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    /// <summary>Builds the playable URL for a channel (direct for M3U, Xtream otherwise).</summary>
    public string GetStreamUrl(Channel channel) =>
        !string.IsNullOrEmpty(channel.DirectUrl)
            ? channel.DirectUrl!
            : StreamUrl("live", channel.StreamId.ToString(), LiveExt);

    private string StreamUrl(string kind, string id, string? extension) =>
        $"{BaseUrl}/{kind}/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{Uri.EscapeDataString(id)}.{Uri.EscapeDataString(string.IsNullOrWhiteSpace(extension) ? "mp4" : extension)}";

    private static Dictionary<string, string> CategoryMap(IEnumerable<Category> categories) => categories
        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.CategoryId))
        .GroupBy(c => c.CategoryId, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().CategoryName);

    private static string LiveExt =>
        StateStore.Settings.LiveStreamFormat == "m3u8" ? "m3u8" : "ts";

    /// <summary>Builds the playable URL for any <see cref="PlayItem"/>.</summary>
    public string GetStreamUrl(PlayItem item) => item.Kind switch
    {
        PlayItemKind.Live => !string.IsNullOrEmpty(item.DirectUrl)
            ? item.DirectUrl!
            : StreamUrl("live", item.Id, LiveExt),
        PlayItemKind.Movie => StreamUrl("movie", item.Id, item.Ext),
        PlayItemKind.Episode => StreamUrl("series", item.Id, item.Ext),
        PlayItemKind.Local => item.DirectUrl ?? item.Id,
        _ => ""
    };

    // ============================================================ VOD / SERIES
    public async Task<List<VodStream>> GetVodAsync(CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        var cats = await GetAsync<List<Category>>("get_vod_categories", ct) ?? new();
        var map = CategoryMap(cats);
        var vods = await GetAsync<List<VodStream>>("get_vod_streams", ct) ?? new();
        foreach (var v in vods)
            v.CategoryName = v.CategoryId != null && map.TryGetValue(v.CategoryId, out var n) ? n : LocalizationService.T("Other");
        DebugConsole.Success($"{vods.Count} movies retrieved.");
        return vods;
    }

    public async Task<List<SeriesItem>> GetSeriesAsync(CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        var cats = await GetAsync<List<Category>>("get_series_categories", ct) ?? new();
        var map = CategoryMap(cats);
        var series = await GetAsync<List<SeriesItem>>("get_series", ct) ?? new();
        foreach (var s in series)
            s.CategoryName = s.CategoryId != null && map.TryGetValue(s.CategoryId, out var n) ? n : LocalizationService.T("Other");
        DebugConsole.Success($"{series.Count} series retrieved.");
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
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleStringConverter))]
        public string? start_timestamp { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleStringConverter))]
        public string? stop_timestamp { get; set; }
    }

    public async Task<List<EpgEntry>> GetShortEpgAsync(string streamId, int limit = 6, CancellationToken ct = default)
    {
        if (!IsXtream) return new();
        try
        {
            var resp = await GetAsync<EpgResponse>($"get_short_epg&stream_id={Uri.EscapeDataString(streamId)}&limit={Math.Clamp(limit, 1, 100)}", ct);
            var list = new List<EpgEntry>();
            foreach (var e in resp?.epg_listings ?? new())
            {
                if (e == null) continue;
                var start = FromUnix(e.start_timestamp);
                var end = FromUnix(e.stop_timestamp);
                if (start == DateTime.MinValue || end <= start) continue;
                list.Add(new EpgEntry
                {
                    Title = DecodeB64(e.title),
                    Description = DecodeB64(e.description),
                    Start = start,
                    End = end
                });
            }
            return list.OrderBy(e => e.Start).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugConsole.Warn("EPG unavailable: " + ex.Message);
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
        long.TryParse(ts, out var v) && v is >= -62135596800 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime : DateTime.MinValue;

    // ================================================================ M3U

    /// <summary>
    /// Loads an M3U playlist from a local file path or a remote URL. The
    /// <c>group-title</c> attribute is used as the category (country / theme).
    /// </summary>
    public async Task<(List<Category> categories, List<Channel> channels)> LoadM3uAsync(
        string pathOrUrl, CancellationToken ct = default)
    {
        pathOrUrl = pathOrUrl.Trim();
        DebugConsole.Info("Loading M3U playlist.");
        IsXtream = false;
        ProfileKey = "m3u|" + pathOrUrl.Trim();
        string content;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            content = await _http.GetStringAsync(uri, ct);
        else
            content = await File.ReadAllTextAsync(pathOrUrl, ct);

        var channels = await Task.Run(() => PlaylistParser.Parse(content, pathOrUrl, ct), ct);
        var categories = channels
            .Select(c => c.CategoryName)
            .Distinct()
            .Select((name, i) => new Category { CategoryId = name, CategoryName = name })
            .ToList();

        DebugConsole.Success($"{channels.Count} M3U channels across {categories.Count} categories.");
        return (categories, channels);
    }

}
