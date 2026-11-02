using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Elysium_Cast_IPTV;

/// <summary>Converts a hex colour string (e.g. "#FF8B5CF6") to a SolidColorBrush.</summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var hex = value as string;
            if (string.IsNullOrEmpty(hex)) return Brushes.Transparent;
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException) { return Brushes.Transparent; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>true → Visible, false → Collapsed (or the reverse with Invert).</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && flag != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && (visibility == Visibility.Visible) != Invert;
}

/// <summary>true → play triangle, false → pause bars.</summary>
public class PlayPauseGlyphConverter : IValueConverter
{
    private static readonly Geometry Play = Freeze(Geometry.Parse("M 5,3 L 12,8 L 5,13 Z"));
    private static readonly Geometry Pause = Freeze(Geometry.Parse("M 4,3 L 6.5,3 L 6.5,13 L 4,13 Z M 9.5,3 L 12,3 L 12,13 L 9.5,13 Z"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Play : Pause;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
