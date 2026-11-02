using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Playback;

/// <summary>
/// The single owner of "what is playing, on which backend, for which
/// generation". Every delayed or asynchronous playback update captures
/// <see cref="Generation"/> and the backend, then checks <see cref="IsCurrent"/>
/// before touching the UI, so stale work can never update a newer playback.
/// </summary>
public sealed class PlaybackSession
{
    private int _generation;

    public IVideoBackend? Backend { get; set; }
    public PlayItem? Current { get; set; }

    /// <summary>True once an IPTV profile or the local-only shell is open.</summary>
    public bool Connected { get; set; }

    public bool ShuttingDown { get; set; }

    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Invalidates every pending update tied to the previous generation.</summary>
    public int NextGeneration() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(int generation, IVideoBackend? backend) =>
        !ShuttingDown && generation == Generation && ReferenceEquals(backend, Backend);

    public bool HasMedia => Backend?.HasMedia == true;
    public bool IsPlaying => Backend?.IsPlaying == true;

    /// <summary>True while a media item is selected and the backend holds it.</summary>
    public bool IsShowingMedia => Current != null && HasMedia;
}
