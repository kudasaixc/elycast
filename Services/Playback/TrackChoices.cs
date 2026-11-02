using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Playback;

public sealed record TrackOption(int Id, string Name, string Key, bool IsAuto = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// Builds the audio/subtitle pickers and matches the persisted preference
/// ("auto", "off" or "name:&lt;track name&gt;") against the tracks of a new media.
/// </summary>
public static class TrackChoices
{
    public static List<TrackOption> Defaults(bool subtitles) => subtitles
        ?
        [
            new TrackOption(int.MinValue, LocalizationService.T("Subtitles"), "auto", IsAuto: true),
            new TrackOption(-1, LocalizationService.T("Disabled"), "off")
        ]
        : [new TrackOption(int.MinValue, LocalizationService.T("Audio track"), "auto", IsAuto: true)];

    public static List<TrackOption> Build(bool subtitles, IEnumerable<VideoTrack> tracks)
    {
        var options = Defaults(subtitles);
        foreach (var track in tracks)
        {
            var name = string.IsNullOrWhiteSpace(track.Name) ? LocalizationService.Format("Track {0}", track.Id) : track.Name;
            if (options.Any(x => x.Id == track.Id)) continue;
            options.Add(new TrackOption(track.Id, name, "name:" + name));
        }
        return options;
    }

    public static TrackOption? PickPreferred(IReadOnlyList<TrackOption> options, string? preferred)
    {
        preferred ??= "";
        return options.FirstOrDefault(x => x.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase))
               ?? options.FirstOrDefault(x => preferred.StartsWith("name:", StringComparison.OrdinalIgnoreCase) &&
                                              x.Name.Contains(preferred[5..], StringComparison.OrdinalIgnoreCase))
               ?? options.FirstOrDefault();
    }

    /// <summary>The picker is shown only when the media offers a real choice.</summary>
    public static bool HasChoice(bool subtitles, int optionCount) => optionCount > (subtitles ? 2 : 1);
}
