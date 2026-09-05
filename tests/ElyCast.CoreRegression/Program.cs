using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;

internal static class Program
{
    private static int _passed;
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ElyCast-CoreRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run(root).GetAwaiter().GetResult();
            Console.WriteLine($"PASS {_passed} core regression checks");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL " + label);
        _passed++;
        Console.WriteLine("PASS " + label);
    }

    private static async Task Run(string root)
    {
        var playlist = PlaylistParser.Parse("\uFEFF#EXTM3U\n#EXTINF:-1 group-title=\"News, World\",News, live\n../live/a.ts\nplain.ts\n", "https://example.test/lists/list.m3u");
        Check(playlist.Count == 2, "plain and extended M3U entries both load");
        Check(playlist[0].Name == "News, live" && playlist[0].CategoryName == "News, World", "commas in M3U title and attributes survive");
        Check(playlist[0].DirectUrl == "https://example.test/live/a.ts", "remote relative streams resolve against playlist");
        Check(playlist[1].DirectUrl == "https://example.test/lists/plain.ts", "bare relative streams resolve");
        var local = PlaylistParser.Parse("song.mp3", Path.Combine(root, "list.m3u"));
        Check(local[0].DirectUrl == Path.Combine(root, "song.mp3"), "local relative streams resolve against file directory");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { PlaylistParser.Parse("song.mp3", Path.Combine(root, "list.m3u"), cancelled.Token); Check(false, "parser cancellation"); }
        catch (OperationCanceledException) { Check(true, "parser cancellation"); }

        var first = PlayItem.FromChannel(playlist[0]);
        var moved = PlayItem.FromChannel(playlist[0]); moved.Id = "998";
        var replaced = PlayItem.FromChannel(playlist[1]); replaced.Id = first.Id;
        Check(first.SameAs(moved), "M3U favorite survives playlist reordering");
        Check(!first.SameAs(replaced), "M3U favorite does not transfer to another URL");
        var track = PlayItem.FromLocalFile(Path.Combine(root, "Song.mp3"));
        var alternate = PlayItem.FromLocalFile(Path.Combine(root, "SONG.MP3"));
        Check(track.SameAs(alternate), "Windows local identity ignores path case");
        var savedPlaylist = new LocalPlaylist { TrackPaths = [track.Id, "missing.mp3"] };
        Check(LocalLibraryService.ResolvePlaylist(savedPlaylist, [track, alternate]).Count == 1, "duplicate library paths do not crash playlist resolution");

        var search = new MediaSearch();
        search.SetQuery("  beyonce   halo ");
        Check(search.Matches("Halo", "Beyoncé"), "search ignores accents and whitespace across fields");
        Check(!search.Matches("Other song", "Beyoncé"), "search requires every word");
        search.SetQuery(""); Check(search.Matches((string?)null), "empty query includes empty metadata");
        track.Album = alternate.Album = "Greatest Hits"; track.Artist = "Artist A"; alternate.Artist = "Artist B";
        Check(LocalLibraryService.BuildGroups("albums", [track, alternate], []).Count == 2, "different artists with the same album title stay separate");

        var malformed = JsonSerializer.Deserialize<AppState>("""
            {"Profiles":{"a":null,"b":{"Favorites":[null]}},"LocalLibrary":[null],"LocalAudioLibrary":[null],"LocalVideoLibrary":null,
             "LocalPlaylists":[null,{"TrackPaths":[null,"","a","A"]}],"Settings":{"ElyColorCustomFilters":[null]}}
            """);
        var normalized = StateStore.Normalize(malformed);
        Check(normalized.Profiles["a"].Favorites.Count == 0 && normalized.Profiles["b"].Favorites.Count == 0, "null profile entries do not discard all settings");
        Check(normalized.LocalAudioLibrary.Count == 0 && normalized.LocalLibrary.Count == 0, "null library entries are repaired");
        Check(normalized.LocalPlaylists.Single().TrackPaths.SequenceEqual(["a"]), "invalid playlist paths are removed and deduplicated");
        Check(normalized.Settings.ElyColorCustomFilters.Count == 0, "null custom presets are removed");

        var path = Path.Combine(root, "state-test.json");
        var legacyPath = Path.Combine(root, "legacy-test.json");
        File.WriteAllText(legacyPath, "[\"legacy secret\"]");
        var legacyStore = new ProtectedJsonStore<List<string>>(legacyPath, "test:v1:");
        legacyStore.Save(legacyStore.Load());
        Check(File.ReadAllText(legacyPath).StartsWith("test:v1:") && File.ReadAllText(legacyPath + ".bak").StartsWith("test:v1:"), "legacy migration also encrypts the backup");
        var store = new ProtectedJsonStore<List<string>>(path, "test:v1:");
        store.Load(); store.Save(["first"]); store.Save(["second"]);
        Check(!File.ReadAllText(path).Contains("second"), "saved payload is DPAPI protected");
        File.WriteAllText(path, "broken");
        Check(store.Load().SequenceEqual(["first"]), "corrupt primary recovers last-good backup");
        store.Save(["recovered"]);
        Check(store.Load().SequenceEqual(["recovered"]), "recovered store remains writable");
        Check(new ProtectedJsonStore<List<string>>(path + ".bak", "test:v1:").Load().SequenceEqual(["first"]), "recovery does not replace good backup with corrupt content");
        File.WriteAllText(path, "broken-primary"); File.WriteAllText(path + ".bak", "broken-backup");
        store.Load();
        try { store.Save([]); Check(false, "unreadable state preserved"); }
        catch (IOException) { Check(File.ReadAllText(path) == "broken-primary", "unreadable state preserved instead of overwritten by defaults"); }

        File.WriteAllText(Path.Combine(root, "song.mp3"), "synthetic");
        File.WriteAllText(Path.Combine(root, "clip.mp4"), "synthetic");
        File.WriteAllText(Path.Combine(root, "ignore.txt"), "synthetic");
        var discovered = LocalLibraryService.DiscoverFiles([root, Path.Combine(root, "song.mp3")], CancellationToken.None).ToList();
        Check(discovered.Count == 2, "mixed folder discovery filters extensions and deduplicates roots");
        try { LocalLibraryService.DiscoverFiles([root], cancelled.Token).ToList(); Check(false, "discovery cancellation"); }
        catch (OperationCanceledException) { Check(true, "discovery cancellation"); }

        using var http = new HttpClient(new FixtureHandler());
        var iptv = new IptvService(http);
        var (_, channels) = await iptv.ConnectAsync("https://example.test/player_api.php", "user/name", "p?#%/");
        Check(channels.Count == 1 && channels[0].CategoryName == "News", "numeric and duplicate Xtream category IDs accepted");
        var url = iptv.GetStreamUrl(channels[0]);
        Check(url.StartsWith("https://example.test/live/user%2Fname/p%3F%23%25%2F/", StringComparison.Ordinal), "Xtream credentials encoded as path segments");
        var movie = iptv.GetStreamUrl(new PlayItem { Kind = PlayItemKind.Movie, Id = "12", Ext = "" });
        Check(movie.EndsWith("12.mp4", StringComparison.Ordinal), "empty VOD extension falls back to mp4");
        var epg = await iptv.GetShortEpgAsync("1");
        Check(epg.Count == 1 && epg[0].Title == "News", "bad EPG timestamp does not discard valid programs");
        var episode = JsonSerializer.Deserialize<Episode>("{\"id\":123,\"episode_num\":\"4\"}");
        Check(episode?.Id == "123" && episode.EpisodeNum == 4, "numeric episode identifiers accepted");
        using var deniedHttp = new HttpClient(new FixtureHandler(deny: true));
        try { await new IptvService(deniedHttp).ConnectAsync("https://example.test", "u", "p"); Check(false, "authentication rejection"); }
        catch (InvalidOperationException) { Check(true, "rejected authentication cannot open an empty player"); }
        using var emptyHttp = new HttpClient(new FixtureHandler(empty: true));
        var emptyService = new IptvService(emptyHttp);
        var emptyResult = await emptyService.ConnectAsync("https://example.test", "u", "p");
        Check(emptyResult.channels.Count == 0 && emptyService.IsXtream, "authenticated VOD-only account remains usable");
    }

    private sealed class FixtureHandler(bool deny = false, bool empty = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var query = request.RequestUri!.Query;
            var body = query.Contains("action=get_live_categories")
                ? "[{\"category_id\":7,\"category_name\":\"News\"},{\"category_id\":\"7\",\"category_name\":\"Duplicate\"}]"
                : query.Contains("action=get_live_streams") ? empty ? "[]" : "[{\"stream_id\":1,\"category_id\":7,\"name\":\"News\"}]"
                : query.Contains("action=get_short_epg") ? """
                    {"epg_listings":[{"title":"bad","start_timestamp":"999999999999999","stop_timestamp":"1"},
                    {"title":"TmV3cw==","start_timestamp":1700000000,"stop_timestamp":1700003600}]}
                    """
                : deny ? "{\"user_info\":{\"auth\":0}}" : "{\"user_info\":{\"auth\":\"1\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
