using System.IO;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Export;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Video;
using Xunit;

namespace ElyCast.UnitTests;

public class ShaderCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ElyCast TV Player.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Every_shader_used_by_a_method_or_effect_is_bundled_in_the_repository()
    {
        var folder = Path.Combine(RepoRoot(), "Assets", "Shaders");
        var methods = new[] { "fsr", "nvscaler", "fsrcnnx", "fsrcnnx-hq", "anime4k-hq", "anime4k-fast", "anime4k-denoise", "anime4k-deblur", "ewa_lanczossharp" };
        var used = methods.SelectMany(m => ShaderCatalog.ChainFor(m, "high", ShaderCatalog.Effects.Select(e => e.Id)))
            .Select(f => ShaderCatalog.IsTuned(f, out var pristine) ? pristine : f)
            .Distinct();

        foreach (var file in used)
        {
            Assert.Contains(file, ShaderCatalog.Bundled);
            var path = Path.Combine(folder, file);
            Assert.True(File.Exists(path), "missing " + file);
            Assert.Contains("//!HOOK", File.ReadAllText(path));
        }
        Assert.True(File.Exists(Path.Combine(folder, "THIRD-PARTY-NOTICES.md")));
        Assert.True(File.Exists(Path.Combine(folder, "LGPL-3.0.txt")));
    }

    [Fact]
    public void Effects_wrap_the_upscaler_chain_in_a_fixed_order()
    {
        var chain = ShaderCatalog.ChainFor("anime4k-fast", "off", ["vibrance", "chroma", "darken"]);

        Assert.Equal("KrigBilateral.glsl", chain[0]);
        Assert.Equal("Anime4K_Clamp_Highlights.glsl", chain[1]);
        Assert.Equal(["Anime4K_Darken_HQ.glsl", "ElyCast_Vibrance.glsl"], chain.TakeLast(2));
    }

    [Fact]
    public void Unknown_and_duplicate_effects_are_dropped()
    {
        Assert.Equal(["chroma", "vibrance"], ShaderCatalog.NormalizeEffects(["VIBRANCE", "removed-effect", "chroma", null, "vibrance"]));
        Assert.Empty(ShaderCatalog.EffectChain(["deband"]));
    }

    [Fact]
    public void Anime_deband_tunes_mpv_debanding()
    {
        var anime = ShaderCatalog.DebandOptions(["deband"]).ToDictionary(o => o.Key, o => o.Value);
        var normal = ShaderCatalog.DebandOptions([]).ToDictionary(o => o.Key, o => o.Value);

        Assert.Equal("2", anime["deband-iterations"]);
        Assert.Equal("1", normal["deband-iterations"]);
    }

    [Fact]
    public void Anime_profiles_carry_their_pipeline_and_effects()
    {
        var ultra = ElyColorCatalog.BuiltIn.Single(f => f.Id == "elycolor-anime-ultra");
        var eco = ElyColorCatalog.BuiltIn.Single(f => f.Id == "elycolor-anime-eco");

        Assert.True(ultra.IncludeVideoPipeline);
        Assert.Equal("anime4k-fast", ultra.UpscaleMethod);
        Assert.Equal(ultra.VideoEffects, ShaderCatalog.NormalizeEffects(ultra.VideoEffects));
        Assert.Equal(["vibrance", "deband"], eco.VideoEffects);
    }

    [Fact]
    public void Exports_apply_the_same_effects_and_deband()
    {
        var settings = new Settings { UpscaleMethod = "none", VideoEffects = ["vibrance", "deband"] };
        var request = new ClipExportRequest("C:/in.mkv", "C:/out.mp4", 0, 10, 1920, 1080, ClipQuality.High);
        var options = ClipExportPlanner.BuildOptions(request, settings, "libx264").ToDictionary(o => o.Key, o => o.Value);

        Assert.Equal("2", options["deband-iterations"]);
        Assert.Contains("Vibrance", string.Join(" ", ClipExportPlanner.DescribePipeline(settings)));
    }
}
