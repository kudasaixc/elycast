using System.IO;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;

namespace ElyCast.UnitTests;

internal static class TestData
{
    public static PlayItem Track(string name, string folder = @"C:\Music") =>
        PlayItem.FromLocalFile(Path.Combine(folder, name + ".mp3"));

    public static PlayItem Video(string name) =>
        PlayItem.FromLocalFile(Path.Combine(@"C:\Videos", name + ".mkv"));

    public static PlayItem Live(string id, string? url = null) =>
        new() { Kind = PlayItemKind.Live, Id = id, Name = "Channel " + id, DirectUrl = url };

    public static List<PlayItem> Tracks(params string[] names) => names.Select(n => Track(n)).ToList();
}

/// <summary>In-memory persistence: counts saves instead of writing DPAPI files.</summary>
internal sealed class InMemoryStateStore : IAppStateStore
{
    public AppState Current { get; } = new();
    public Settings Settings => Current.Settings;
    public int SaveCount { get; private set; }

    public ProfileState ForProfile(string key)
    {
        if (!Current.Profiles.TryGetValue(key, out var state))
            Current.Profiles[key] = state = new ProfileState();
        return state;
    }

    public void Save() => SaveCount++;
}
