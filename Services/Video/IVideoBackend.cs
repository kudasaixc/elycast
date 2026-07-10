using System.Windows;

namespace Elysium_Cast_IPTV.Services.Video;

public interface IVideoBackend : IDisposable
{
    string Name { get; }
    FrameworkElement View { get; }
    bool IsAvailable { get; }
    bool HasMedia { get; }
    bool IsPlaying { get; }
    long PositionMs { get; set; }
    long LengthMs { get; }
    int Volume { get; set; }

    event Action? Playing;
    event Action? Paused;
    event Action? Ended;
    event Action<string>? Failed;

    void Play(string url);
    void Resume();
    void Pause();
    void Stop();
    void Clear();
    void SeekRelative(long deltaMs);
    void SetFullscreen(bool fullscreen);
    VideoStats GetStats();
    IReadOnlyList<VideoTrack> GetSubtitleTracks();
    void SetSubtitleTrack(int id);
    IReadOnlyList<VideoTrack> GetAudioTracks();
    void SetAudioTrack(int id);
}

public sealed record VideoStats(
    string Backend,
    uint SourceWidth,
    uint SourceHeight,
    uint OutputWidth,
    uint OutputHeight,
    double Fps,
    double BitrateKbps,
    long DisplayedFrames,
    long DroppedFrames,
    string State,
    string Scaler = "—",
    string Hwdec = "—",
    string Shaders = "");

public sealed record VideoTrack(int Id, string Name);
