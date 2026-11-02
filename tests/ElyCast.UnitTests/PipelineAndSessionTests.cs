using System.Windows.Media;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Xunit;
using static ElyCast.UnitTests.TestData;

namespace ElyCast.UnitTests;

public class PipelineAndSessionTests
{
    [Theory]
    [InlineData("59.94", "60000/1001")]
    [InlineData("60000/1001", "60000/1001")]
    [InlineData("120", "120")]
    [InlineData("garbage", "60")]
    [InlineData("-3", "60")]
    public void Target_fps_is_normalized(string input, string expected) =>
        Assert.Equal(expected, VideoPipeline.NormalizeFps(input));

    [Fact]
    public void Unknown_elycolor_id_falls_back_to_off()
    {
        var settings = new Settings { ElyColorFilterId = "deleted-custom" };

        Assert.Equal("off", ElyColorCatalog.Active(settings).Id);
    }

    [Fact]
    public void Custom_elycolor_filters_are_listed_after_the_built_ins()
    {
        var settings = new Settings();
        settings.ElyColorCustomFilters.Add(new ElyColorFilter { Id = "mine", Name = "Mine" });
        settings.ElyColorFilterId = "MINE";

        Assert.Equal("mine", ElyColorCatalog.All(settings).Last().Id);
        Assert.Equal("mine", ElyColorCatalog.Active(settings).Id);
    }

    [Fact]
    public void Elysound_custom_preset_is_created_on_demand()
    {
        var settings = new Settings { ElySoundPresetId = "custom", ElySoundCustomProfile = null! };

        var active = ElySoundProfiles.Active(settings);

        Assert.Same(settings.ElySoundCustomProfile, active);
        Assert.Equal(ElySoundCatalog.BuiltIn.Count + 1, ElySoundProfiles.All(settings).Count());
    }

    [Fact]
    public void Upscale_catalog_ids_are_unique() =>
        Assert.Equal(UpscaleCatalog.Modes.Count, UpscaleCatalog.Modes.Select(m => m.Id).Distinct().Count());

    [Fact]
    public void Session_rejects_updates_from_an_older_generation_or_shutdown()
    {
        var session = new PlaybackSession();
        var generation = session.NextGeneration();

        Assert.True(session.IsCurrent(generation, null));
        session.NextGeneration();
        Assert.False(session.IsCurrent(generation, null));
        session.ShuttingDown = true;
        Assert.False(session.IsCurrent(session.Generation, null));
    }

    [Theory]
    [InlineData("song.flac", true)]
    [InlineData("SONG.MP3", true)]
    [InlineData("movie.mkv", false)]
    public void Only_local_audio_files_are_audio_only(string file, bool expected) =>
        Assert.Equal(expected, MediaKinds.IsAudioOnly(PlayItem.FromLocalFile(@"C:\x\" + file)));

    [Fact]
    public void Live_channels_are_not_seekable()
    {
        Assert.False(MediaKinds.IsSeekable(Live("1")));
        Assert.True(MediaKinds.IsSeekable(Track("a")));
    }

    [Fact]
    public void Monochrome_artwork_gives_a_gray_palette()
    {
        var pixels = Enumerable.Range(0, 48 * 48).SelectMany(i => i % 2 == 0
            ? new byte[] { 30, 30, 30, 255 }
            : new byte[] { 220, 220, 220, 255 }).ToArray();

        var palette = AudioPalette.FromBgra(pixels);

        Assert.True(palette.Length >= 2);
        Assert.All(palette, c => Assert.True(c.R == c.G && c.G == c.B));
    }

    [Fact]
    public void Colorful_artwork_keeps_its_dominant_hues()
    {
        var pixels = Enumerable.Range(0, 48 * 48).SelectMany(i => i % 2 == 0
            ? new byte[] { 0, 0, 230, 255 }   // red (BGRA)
            : new byte[] { 230, 0, 0, 255 })  // blue
            .ToArray();

        var palette = AudioPalette.FromBgra(pixels);

        Assert.Contains(palette, c => c.R > 200 && c.B < 40);
        Assert.Contains(palette, c => c.B > 200 && c.R < 40);
    }

    [Fact]
    public void Default_visualizer_palette_is_fully_opaque_rainbow() =>
        Assert.Equal(8, AudioPalette.Default.Count);
}
