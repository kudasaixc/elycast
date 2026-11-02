using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Xunit;

namespace ElyCast.UnitTests;

public class PlaybackTextTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(-5000, "0:00")]
    [InlineData(65_000, "1:05")]
    [InlineData(3_725_000, "1:02:05")]
    public void Time_uses_hours_only_when_needed(long ms, string expected) =>
        Assert.Equal(expected, PlaybackText.FormatTime(ms));

    [Fact]
    public void Skip_buttons_hide_when_they_cannot_do_useful_work()
    {
        Assert.Equal((false, true), PlaybackText.SkipButtons(true, 120_000, 5_000));
        Assert.Equal((true, true), PlaybackText.SkipButtons(true, 120_000, 60_000));
        Assert.Equal((true, false), PlaybackText.SkipButtons(true, 120_000, 110_000));
        Assert.Equal((false, false), PlaybackText.SkipButtons(false, 120_000, 60_000));
        Assert.Equal((false, false), PlaybackText.SkipButtons(true, 0, 0));
    }

    [Fact]
    public void Now_playing_subtitle_combines_kind_and_category()
    {
        Assert.Equal("Live · News", PlaybackText.NowPlayingSubtitle(new PlayItem { Kind = PlayItemKind.Live, CategoryName = "News" }));
        Assert.Equal("Movie", PlaybackText.NowPlayingSubtitle(new PlayItem { Kind = PlayItemKind.Movie }));
    }

    [Fact]
    public void Video_stats_report_upscaling_direction()
    {
        var up = PlaybackText.VideoStats(Stats(1080, 2160), "ELYCOLOR Off");
        var down = PlaybackText.VideoStats(Stats(2160, 1080), "ELYCOLOR Off");
        var native = PlaybackText.VideoStats(Stats(1080, 1080), "ELYCOLOR Off");

        Assert.Contains("▲ UPSCALE x2", up);
        Assert.Contains("▼ downscale x0", down);
        Assert.Contains("1:1 (native", native);
        Assert.Contains("none (mpv scaler)", native);
    }

    [Fact]
    public void Video_stats_label_ai_shaders_against_the_display_ratio()
    {
        Assert.Contains("[AI upscale active]", PlaybackText.VideoStats(Stats(720, 1440, "FSRCNNX"), "x"));
        Assert.Contains("[sharpening only", PlaybackText.VideoStats(Stats(1440, 720, "FSRCNNX"), "x"));
    }

    [Fact]
    public void Audio_stats_never_label_the_target_as_measured_fps()
    {
        var text = PlaybackText.AudioStats(new AudioStatsSnapshot("mpv", "FLAC", "44.1 kHz", "2", "900 kb/s",
            HasAnalysis: true, AnalysisRateHz: 120, EstimatedBpm: 0, AudioCoreActive: false, VisualFps: 0,
            RenderMs: 1.2, TargetFps: 240, VSync: true, Bass: 0.5, Energy: 1, IsPlaying: true));

        Assert.Contains("measuring...", text);
        Assert.Contains("target 240", text);
        Assert.Contains("detecting...", text);
        Assert.Contains("▮▮▮▮▮▯▯▯▯▯ 50%", text);
        Assert.Contains("CPU render", text);
    }

    private static VideoStats Stats(uint sourceHeight, uint outputHeight, string shaders = "") =>
        new("mpv", 1920, sourceHeight, 3840, outputHeight, 25, 4000, 10, 0, "Playing", "ewa_lanczossharp", "d3d11va", shaders);
}
