using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.Audio;

/// <summary>
/// Shared thumbnail cache for embedded cover art. Decoding happens off the UI
/// thread; results (including "no cover" = null) are memoised per file so a
/// virtualised list never re-reads the same tag twice.
/// </summary>
public static class CoverArtCache
{
    private const int ThumbnailWidth = 160;
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string path, out ImageSource? cover) => Cache.TryGetValue(path, out cover);

    /// <summary>Fills <see cref="PlayItem.Cover"/> asynchronously (UI thread callback).</summary>
    public static void LoadInto(PlayItem item)
    {
        var path = item.DirectUrl ?? item.Id;
        if (string.IsNullOrWhiteSpace(path)) return;
        Task.Run(() =>
        {
            var cover = GetOrDecode(path);
            if (cover == null) return;
            Application.Current?.Dispatcher.BeginInvoke(() => item.SetCover(cover));
        });
    }

    /// <summary>Blocking read+decode with memoisation. Safe to call from any thread.</summary>
    public static ImageSource? GetOrDecode(string path) =>
        Cache.GetOrAdd(path, static p => Decode(p));

    public static void Invalidate(string path) => Cache.TryRemove(path, out _);

    private static ImageSource? Decode(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var picture = file.Tag.Pictures.FirstOrDefault(p => p.Data?.Data is { Length: > 0 });
            if (picture == null) return null;
            return DecodeBytes(picture.Data.Data, ThumbnailWidth);
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Artwork unreadable (" + Path.GetFileName(path) + "): " + ex.Message);
            return null;
        }
    }

    public static ImageSource? DecodeBytes(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Artwork decoding failed: " + ex.Message);
            return null;
        }
    }
}
