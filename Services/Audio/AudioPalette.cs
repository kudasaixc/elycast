using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Elysium_Cast_IPTV.Services.Audio;

/// <summary>Dominant-colour extraction for the audio visualizer palette.</summary>
public static class AudioPalette
{
    /// <summary>Rainbow palette used when no adaptive palette applies.</summary>
    public static IReadOnlyList<Color> Default { get; } =
    [
        Color.FromRgb(255, 40, 40), Color.FromRgb(255, 145, 35), Color.FromRgb(245, 225, 40),
        Color.FromRgb(55, 230, 95), Color.FromRgb(35, 220, 235), Color.FromRgb(55, 105, 255),
        Color.FromRgb(155, 70, 255), Color.FromRgb(245, 55, 195)
    ];

    public static Color[] ExtractDominant(BitmapSource source)
    {
        const int size = 48;
        var scaled = new TransformedBitmap(source, new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
        var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        formatted.CopyPixels(pixels, size * 4, 0);
        return FromBgra(pixels);
    }

    /// <summary>Palette from raw BGRA pixels (separated from decoding for testability).</summary>
    public static Color[] FromBgra(byte[] pixels)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        var grayHistogram = new int[16];
        var sampled = 0;
        var chromatic = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var luminance = (int)Math.Round(r * 0.2126 + g * 0.7152 + b * 0.0722);
            grayHistogram[Math.Min(15, luminance / 16)]++;
            sampled++;
            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            if (max < 28 || min > 238 || saturation < 0.16) continue;
            chromatic++;
            var key = (r / 32 << 6) | (g / 32 << 3) | b / 32;
            var value = buckets.GetValueOrDefault(key);
            buckets[key] = (value.R + r, value.G + g, value.B + b, value.Count + 1);
        }

        // A monochrome cover is a valid palette, not an extraction failure.
        // Ignore isolated JPEG tint/noise: it must occupy a meaningful part of
        // the artwork before coloured accents are allowed into the visualizer.
        if (sampled == 0 || chromatic < sampled * 0.12)
        {
            var grays = grayHistogram.Select((count, bin) => (count, bin))
                .Where(x => x.count > 0)
                .OrderByDescending(x => x.count)
                .Take(5)
                .Select(x => (byte)Math.Clamp(x.bin * 16 + 8, 38, 235))
                .Distinct()
                .OrderBy(x => x)
                .Select(x => Color.FromRgb(x, x, x))
                .ToArray();
            return grays.Length >= 2
                ? grays
                : new[] { Color.FromRgb(96, 96, 96), Color.FromRgb(205, 205, 205), Color.FromRgb(245, 245, 245) };
        }

        var colors = buckets.Values.OrderByDescending(v => v.Count).Take(6)
            .Select(v => Color.FromRgb((byte)(v.R / v.Count), (byte)(v.G / v.Count), (byte)(v.B / v.Count))).ToArray();
        return colors.Length >= 2 ? colors : new[] { Color.FromRgb(139, 92, 246), Color.FromRgb(34, 211, 238), Color.FromRgb(251, 146, 60) };
    }
}
