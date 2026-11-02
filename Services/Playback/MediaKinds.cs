using System.IO;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.Playback;

public static class MediaKinds
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "alac", "aiff", "ape"
    };

    /// <summary>Local audio file: drives the visualizer, the queue and the Windows media controls.</summary>
    public static bool IsAudioOnly(PlayItem item)
    {
        if (item.Kind != PlayItemKind.Local) return false;
        var ext = item.Ext;
        if (string.IsNullOrWhiteSpace(ext))
            ext = Path.GetExtension(item.DirectUrl ?? item.Id).TrimStart('.');
        return AudioExtensions.Contains(ext);
    }

    /// <summary>Finite media get a seek bar and skip buttons; live channels do not.</summary>
    public static bool IsSeekable(PlayItem item) =>
        item.Kind is PlayItemKind.Movie or PlayItemKind.Episode or PlayItemKind.Local;
}
