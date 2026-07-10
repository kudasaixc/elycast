using System.Windows;
using System.Windows.Media;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Recolours the accent brushes at runtime. The styles reference these brush
/// *instances* via StaticResource, so mutating their colour propagates
/// everywhere instantly — no restart needed.
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
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));
}
