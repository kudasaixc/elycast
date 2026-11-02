using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ElyCast.UnitTests;

/// <summary>
/// Static checks on the XAML sources: themed colours must come from resources,
/// and every referenced resource key must exist (a misspelled DynamicResource
/// fails silently at runtime and simply renders nothing).
/// </summary>
public partial class XamlGuardTests
{
    [GeneratedRegex(@"\b(?!Tag\b)(\w+)=""#[0-9A-Fa-f]{6,8}""")]
    private static partial Regex LiteralColor();

    [GeneratedRegex(@"\{(?:Dynamic|Static)Resource\s+([\w.]+)\s*\}")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex ResourceKey();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ElyCast TV Player.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(RepoRoot(), "*.xaml", SearchOption.TopDirectoryOnly);

    [Fact]
    public void Windows_never_hardcode_colors()
    {
        var offenders = XamlFiles()
            .Where(f => !f.EndsWith("App.xaml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(x => LiteralColor().IsMatch(x.Text))
            .Select(x => $"{x.File}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use an App.xaml resource instead:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_referenced_resource_key_is_defined()
    {
        var sources = XamlFiles().Select(File.ReadAllText).ToList();
        var defined = sources.SelectMany(s => ResourceKey().Matches(s).Select(m => m.Groups[1].Value)).ToHashSet();
        // Keys created only at runtime by ThemeManager are still declared in
        // App.xaml with defaults, so nothing needs to be exempted here.
        var missing = sources
            .SelectMany(s => ResourceReference().Matches(s).Select(m => m.Groups[1].Value))
            .Where(key => !defined.Contains(key))
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "Undefined resource keys: " + string.Join(", ", missing));
    }
}
