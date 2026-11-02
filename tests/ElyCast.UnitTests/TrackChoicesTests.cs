using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Xunit;

namespace ElyCast.UnitTests;

public class TrackChoicesTests
{
    [Fact]
    public void Subtitles_offer_auto_and_off_before_the_media_tracks()
    {
        var options = TrackChoices.Build(subtitles: true, [new VideoTrack(3, "English")]);

        Assert.Equal(["auto", "off", "name:English"], options.Select(o => o.Key));
        Assert.True(options[0].IsAuto);
    }

    [Fact]
    public void Unnamed_and_duplicate_tracks_are_normalized()
    {
        var options = TrackChoices.Build(subtitles: false, [new VideoTrack(1, ""), new VideoTrack(1, "dup")]);

        Assert.Equal(2, options.Count);
        Assert.Equal("Track 1", options[1].Name);
    }

    [Fact]
    public void Exact_key_preference_wins()
    {
        var options = TrackChoices.Build(subtitles: true, [new VideoTrack(1, "French"), new VideoTrack(2, "English")]);

        Assert.Equal(2, TrackChoices.PickPreferred(options, "name:English")!.Id);
    }

    [Fact]
    public void Name_preference_matches_a_track_containing_it()
    {
        var options = TrackChoices.Build(subtitles: false, [new VideoTrack(4, "English (Dolby 5.1)")]);

        Assert.Equal(4, TrackChoices.PickPreferred(options, "name:english")!.Id);
    }

    [Fact]
    public void Unknown_or_missing_preference_falls_back_to_auto()
    {
        var options = TrackChoices.Build(subtitles: true, [new VideoTrack(1, "French")]);

        Assert.True(TrackChoices.PickPreferred(options, "name:Klingon")!.IsAuto);
        Assert.True(TrackChoices.PickPreferred(options, null)!.IsAuto);
    }

    [Theory]
    [InlineData(true, 2, false)]
    [InlineData(true, 3, true)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, true)]
    public void Picker_shows_only_a_real_choice(bool subtitles, int count, bool expected) =>
        Assert.Equal(expected, TrackChoices.HasChoice(subtitles, count));
}
