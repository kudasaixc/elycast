using System.IO;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Export;
using Xunit;

namespace ElyCast.UnitTests;

public class ClipExportPlannerTests
{
    private static ClipExportRequest Request(double? start = 1080, double? end = 1260) =>
        new("C:/media/episode.mkv", "C:/out/clip.mp4", start, end, 2560, 1440, ClipQuality.High);

    private static Dictionary<string, string> Options(Settings settings, string encoder = "libx264") =>
        ClipExportPlanner.BuildOptions(Request(), settings, encoder).ToDictionary(o => o.Key, o => o.Value);

    [Theory]
    [InlineData("18:00", 1080)]
    [InlineData("1:02:30", 3750)]
    [InlineData("95", 95)]
    [InlineData("0:07,5", 7.5)]
    public void Clock_times_are_parsed(string text, double expected)
    {
        Assert.True(ClipExportPlanner.TryParseTime(text, out var seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12:75")]
    [InlineData("a:10")]
    [InlineData("1:2:3:4")]
    public void Invalid_times_are_rejected(string text) =>
        Assert.False(ClipExportPlanner.TryParseTime(text, out _));

    [Theory]
    [InlineData(1080, "18:00")]
    [InlineData(3750, "1:02:30")]
    public void Times_are_formatted_as_clock(double seconds, string expected) =>
        Assert.Equal(expected, ClipExportPlanner.FormatTime(seconds));

    [Fact]
    public void Output_size_keeps_the_display_aspect_with_even_dimensions()
    {
        Assert.Equal((2560, 1440), ClipExportPlanner.OutputSize(1920, 1080, 1440));
        Assert.Equal((1920, 1080), ClipExportPlanner.OutputSize(0, 0, 1080));
        Assert.Equal((1920, 1080), ClipExportPlanner.OutputSize(854, 480, 1080));
        Assert.Equal((1440, 1080), ClipExportPlanner.OutputSize(640, 480, 1080));
        var (w, h) = ClipExportPlanner.OutputSize(720, 576, 1081);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void Default_height_engages_an_ai_upscaler()
    {
        var anime4k = new Settings { UpscaleMethod = "anime4k-hq" };
        Assert.Equal(1080, ClipExportPlanner.DefaultHeight(480, anime4k));
        Assert.Equal(1440, ClipExportPlanner.DefaultHeight(720, anime4k));
        Assert.Equal(2160, ClipExportPlanner.DefaultHeight(1080, anime4k));

        Assert.Equal(1440, ClipExportPlanner.DefaultHeight(720, new Settings { UpscaleMethod = "anime4k-hq", UpscaleTargetHeight = 1440 }));
        Assert.Equal(720, ClipExportPlanner.DefaultHeight(720, new Settings { UpscaleMethod = "none" }));
    }

    [Fact]
    public void Options_render_through_vf_gpu_at_the_requested_size()
    {
        var options = Options(new Settings());

        Assert.Equal("gpu=w=2560:h=1440,format=yuv420p", options["vf"]);
        Assert.Equal("1080", options["start"]);
        Assert.Equal("1260", options["end"]);
        Assert.Equal("C:/out/clip.mp4", options["o"]);
        Assert.Equal("libx264", options["ovc"]);
        Assert.Equal("no", options["terminal"]);
    }

    [Fact]
    public void Whole_recording_has_no_range()
    {
        var options = ClipExportPlanner.BuildOptions(Request(null, null), new Settings(), "libx264")
            .ToDictionary(o => o.Key, o => o.Value);

        Assert.False(options.ContainsKey("start"));
        Assert.False(options.ContainsKey("end"));
    }

    [Fact]
    public void Elycolor_and_elysound_follow_the_active_settings()
    {
        var off = Options(new Settings { ElySoundEnabled = false });
        Assert.False(off.ContainsKey("af"));
        Assert.Equal("0", off["saturation"]);

        var on = Options(new Settings { ElySoundEnabled = true, ElyColorFilterId = "elycolor-anime" });
        Assert.StartsWith("@elysound:lavfi=[volume@preamp=", on["af"]);
        Assert.Contains("extrastereo@width", on["af"]);
        Assert.Equal("22", on["saturation"]);
    }

    [Fact]
    public void Installed_shader_chain_is_applied()
    {
        var options = Options(new Settings { UpscaleMethod = "anime4k-hq" });

        Assert.Equal("ewa_lanczossharp", options["scale"]);
        // Only files present on disk are listed; every listed file is part of the chain.
        foreach (var file in options["glsl-shaders"].Split(';', StringSplitOptions.RemoveEmptyEntries))
            Assert.StartsWith("Anime4K_", Path.GetFileName(file));
    }

    [Fact]
    public void Output_paths_are_unique_and_safe()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var first = ClipExportPlanner.OutputPath(folder, "Show: S01E02?", 1080, 1260, 1440);
            Assert.Equal("Show  S01E02 18-00 to 21-00 1440p.mp4", Path.GetFileName(first));
            File.WriteAllText(first, "");
            var second = ClipExportPlanner.OutputPath(folder, "Show: S01E02?", 1080, 1260, 1440);
            Assert.EndsWith("(2).mp4", second);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("h264_nvenc", "cq=19")]
    [InlineData("libx264", "crf=18")]
    public void High_quality_maps_to_encoder_rate_control(string encoder, string expected) =>
        Assert.Contains(expected, ClipExportPlanner.EncoderOptions(encoder, ClipQuality.High));
}
