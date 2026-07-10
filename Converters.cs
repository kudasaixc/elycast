using System.Globalization;
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
        catch { return Brushes.Transparent; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
