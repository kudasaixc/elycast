using System.IO;
using System.Text.RegularExpressions;
using Elysium_Cast_IPTV.Services;
using Xunit;

namespace ElyCast.UnitTests;

/// <summary>
/// English source strings are the catalogue keys: editing an English text in
/// code or XAML silently drops its French translation. These checks turn that
/// drift into a failing test.
/// </summary>
public partial class LocalizationCoverageTests
{
    // Product names, technical identifiers and units are deliberately not translated.
    private static readonly HashSet<string> Untranslated = new(StringComparer.Ordinal)
    {
        "ElyCast", "ELYCAST AUDIO", "Elycast Audio", "ELYCAST AudioCore+", "ELYCOLOR", "ELYFLOW", "ELYSMART", "ELYSOUND+",
        "Audio", "ANIMATION", "gain", ".ts", "FSR", "FSRCNNX", "FSRCNNX HQ", "AMD FSR + CAS", "Catmull-Rom", "Lanczos",
        "Mitchell", "Spline36", "NVIDIA Image Scaling", "NVIDIA Optical Flow FRUC", "RTX Video Super Resolution",
        "Xtream Codes", "📡  Xtream Codes", "📄  Playlist M3U", "Magpie - Anime4K", "Magpie - FSR", "Magpie - FSRCNNX",
        "mpv GPU", "mpv GPU + RTX VSR", "mpv GPU (gpu-next, D3D11)", "mpv GPU + RTX Video Super Resolution",
        "1080p (Full HD)", "Dolomites",
    };

    [GeneratedRegex(@"^[\d.,\s]+(fps|FPS|Hz|kHz)$")]
    private static partial Regex UnitPattern();

    [GeneratedRegex(@"(?:LocalizationService\.T|LocalizationService\.Format|\bShowOverlay)\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeLiteral();

    [GeneratedRegex(@"\b(?:Text|Content|ToolTip|Header|Title)=""([^""{][^""]*)""")]
    private static partial Regex XamlText();

    [GeneratedRegex("[A-Za-z]{2}")]
    private static partial Regex HasWord();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ElyCast TV Player.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static bool NeedsTranslation(string text) =>
        HasWord().IsMatch(text) && !Untranslated.Contains(text) && !UnitPattern().IsMatch(text);

    [Fact]
    public void Every_code_string_shown_to_the_user_has_a_french_translation()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(root, "tests")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.EndsWith("LocalizationCatalog.cs"));
        var missing = files
            .SelectMany(f => CodeLiteral().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Text: Unescape(m.Groups[1].Value))))
            .Where(x => NeedsTranslation(x.Text) && !LocalizationService.IsCatalogText(x.Text))
            .Select(x => $"{x.File}: {x.Text}")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "Missing French entries:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_xaml_text_has_a_french_translation()
    {
        var root = RepoRoot();
        var missing = Directory.EnumerateFiles(root, "*.xaml", SearchOption.TopDirectoryOnly)
            .SelectMany(f => XamlText().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Text: Decode(m.Groups[1].Value))))
            .Where(x => NeedsTranslation(x.Text) && !LocalizationService.IsCatalogText(x.Text))
            .Select(x => $"{x.File}: {x.Text}")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "Missing French entries:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Catalog_never_uses_em_dashes()
    {
        var catalog = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "LocalizationCatalog.cs"));
        Assert.DoesNotContain("—", catalog);
    }

    [Fact]
    public void Formatted_templates_translate_with_their_arguments()
    {
        LocalizationService.Initialize("fr");
        try
        {
            Assert.Equal("Saison 3", LocalizationService.Format("Season {0}", 3));
            Assert.Equal("File : 4", LocalizationService.Format("Queue: {0}", 4));
        }
        finally
        {
            LocalizationService.Initialize("en");
        }
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static string Decode(string s) => s.Replace("&#x0a;", "\n").Replace("&#10;", "\n").Replace("&amp;", "&")
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
}
