using System.Windows;
using System.Windows.Media;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Derives the ENTIRE UI palette from the accent colour and swaps the
/// application resources at runtime: accent brushes, tinted background
/// gradient, card/field strokes, hover/selection/popup tints, muted/faint
/// text and the login glow. Consumers reference these keys with
/// DynamicResource, so a change propagates instantly — no restart needed.
/// </summary>
public static class ThemeManager
{
    public static readonly string[] Presets =
    {
        "#FF8B5CF6", // violet (default)
        "#FFB066FF", // light violet
        "#FF22D3EE", // cyan
        "#FF38BDF8", // sky
        "#FF34D399", // emerald
        "#FFF472B6", // pink
        "#FFFB7185", // rose
        "#FFF59E0B", // amber
        "#FFEF4444", // red
        "#FFFFFFFF"  // mono white
    };

    public static void Apply(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Apply(c);
        }
        catch { /* invalid hex – ignore */ }
    }

    public static void Apply(Color c)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        var light = Lighten(c, 0.30);
        var deep = Darken(c, 0.35);

        // Replace the resources outright. Consumers use DynamicResource, so the
        // change propagates even though the old brushes were frozen on style seal.
        res["AccentABrush"] = new SolidColorBrush(c);
        res["AccentBBrush"] = new SolidColorBrush(light);

        var grad = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        grad.GradientStops.Add(new GradientStop(deep, 0));
        grad.GradientStops.Add(new GradientStop(c, 0.5));
        grad.GradientStops.Add(new GradientStop(light, 1));
        res["AccentBrush"] = grad;

        // ----- derived palette: everything that used to be hardcoded violet.
        // Text: near-white / gray / dark-gray with a light accent tint.
        res["TextBrush"] = new SolidColorBrush(Mix(Color.FromRgb(0xF0, 0xF0, 0xF0), light, 0.12));
        res["MutedBrush"] = new SolidColorBrush(Mix(Color.FromRgb(0x8C, 0x8C, 0x8C), c, 0.28));
        res["FaintBrush"] = new SolidColorBrush(Mix(Color.FromRgb(0x5A, 0x5A, 0x5A), c, 0.22));

        // Pastel accent for OSD icons/labels; deep accent for small dark chips.
        res["AccentSoftBrush"] = new SolidColorBrush(Lighten(c, 0.55));
        res["AccentDeepBrush"] = new SolidColorBrush(Darken(c, 0.72));

        // Strokes and interaction tints: the accent with graded alpha, exactly
        // like the original violet #xxB066FF family.
        res["CardStroke"] = new SolidColorBrush(WithAlpha(light, 0x1F));
        res["FieldStroke"] = new SolidColorBrush(WithAlpha(light, 0x33));
        res["HoverFaintBrush"] = new SolidColorBrush(WithAlpha(light, 0x22));
        res["HoverSoftBrush"] = new SolidColorBrush(WithAlpha(light, 0x33));
        res["HighlightBrush"] = new SolidColorBrush(WithAlpha(light, 0x2E));
        res["SelectionBrush"] = new SolidColorBrush(WithAlpha(c, 0x3A));
        res["ScrollThumbBrush"] = new SolidColorBrush(WithAlpha(light, 0x40));

        // Solid dark surfaces: combo popup and the login-card glow.
        res["PopupBrush"] = new SolidColorBrush(Mix(Colors.Black, c, 0.11));
        res["GlowColor"] = Mix(Colors.Black, c, 0.16);

        // App background: near-black with a whisper of accent (noir-violâtre,
        // noir-rougeâtre… selon l'accent), fading to pure black.
        var bg = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0.6, 1)
        };
        bg.GradientStops.Add(new GradientStop(Mix(Colors.Black, c, 0.070), 0));
        bg.GradientStops.Add(new GradientStop(Mix(Colors.Black, c, 0.035), 0.5));
        bg.GradientStops.Add(new GradientStop(Colors.Black, 1));
        res["AppBackground"] = bg;

        // The Win32 debug console follows the accent too (banner, prompt…).
        DebugConsole.AccentColor = ClosestConsoleColor(c);
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));

    /// <summary>Linear blend: <paramref name="amount"/> of <paramref name="tint"/> into <paramref name="baseColor"/>.</summary>
    private static Color Mix(Color baseColor, Color tint, double amount) => Color.FromRgb(
        (byte)(baseColor.R + (tint.R - baseColor.R) * amount),
        (byte)(baseColor.G + (tint.G - baseColor.G) * amount),
        (byte)(baseColor.B + (tint.B - baseColor.B) * amount));

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Nearest 16-colour console approximation of the accent.</summary>
    private static ConsoleColor ClosestConsoleColor(Color c)
    {
        // Achromatic accents (mono white preset) stay white.
        double r = c.R, g = c.G, b = c.B;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max - min < 24) return ConsoleColor.White;

        var delta = max - min;
        double hue;
        if (max == r) hue = 60.0 * (((g - b) / delta) % 6.0);
        else if (max == g) hue = 60.0 * ((b - r) / delta + 2.0);
        else hue = 60.0 * ((r - g) / delta + 4.0);
        if (hue < 0) hue += 360.0;

        return hue switch
        {
            < 20 => ConsoleColor.Red,
            < 55 => ConsoleColor.Yellow,
            < 90 => ConsoleColor.Green,
            < 160 => ConsoleColor.Green,
            < 210 => ConsoleColor.Cyan,
            < 260 => ConsoleColor.Blue,
            < 340 => ConsoleColor.Magenta,
            _ => ConsoleColor.Red
        };
    }
}
