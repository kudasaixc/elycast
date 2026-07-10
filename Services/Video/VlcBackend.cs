using System.Windows;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Media = LibVLCSharp.Shared.Media;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Elysium_Cast_IPTV.Services.Video;

public sealed class VlcBackend : IVideoBackend
{
    private static bool _coreInitialized;
    private static readonly object CoreLock = new();

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly VlcVideo _view = new();
    private volatile bool _hasMedia;

    public VlcBackend()
    {
        EnsureCoreInitialized();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet");
        _player = new MediaPlayer(_libVlc);
        _view.Attach(_player);

        _player.Playing += (_, _) => Playing?.Invoke();
        _player.Paused += (_, _) => Paused?.Invoke();
        _player.EndReached += (_, _) => { _hasMedia = false; Ended?.Invoke(); };
        _player.EncounteredError += (_, _) => { _hasMedia = false; Failed?.Invoke("Impossible de lire ce flux."); };
    }

    public string Name => "VLC bitmap";
    public FrameworkElement View => _view;
    public bool IsAvailable => true;
    public bool HasMedia => _hasMedia;
    public bool IsPlaying => _player.IsPlaying;

    public long PositionMs
    {
        get => _player.Time;
        set => _player.Time = value;
    }

    public long LengthMs => _player.Length;

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = value;
    }

    public event Action? Playing;
    public event Action? Paused;
    public event Action? Ended;
    public event Action<string>? Failed;

    public void Play(string url)
    {
        using var media = new Media(_libVlc, new Uri(url), ":network-caching=1500");
        if (!_player.Play(media)) throw new InvalidOperationException("VLC a refusé le média demandé.");
        _hasMedia = true;
    }

    public void Resume() => _player.Play();
    public void Pause() => _player.Pause();

    public void Stop()
    {
        _player.Stop();
        _hasMedia = false;
    }

    public void Clear() => _view.Clear();

    public void SeekRelative(long deltaMs)
    {
        if (LengthMs <= 0) return;
        PositionMs = Math.Clamp(PositionMs + deltaMs, 0, LengthMs);
    }

    public void SetFullscreen(bool fullscreen)
    {
        // The WPF shell owns fullscreen so the OSD and menus stay in the same layer.
    }

    public VideoStats GetStats()
    {
        uint w = 0, h = 0;
        try { _player.Size(0, ref w, ref h); } catch { }

        var st = _player.Media?.Statistics;
        return new VideoStats(
            Name,
            w,
            h,
            w,
            h,
            _player.Fps,
            (st?.InputBitrate ?? 0) * 8000.0,
            st?.DisplayedPictures ?? 0,
            st?.LostPictures ?? 0,
            _player.State.ToString());
    }

    public IReadOnlyList<VideoTrack> GetSubtitleTracks()
    {
        try
        {
            return (_player.SpuDescription ?? [])
                .Where(t => t.Id >= 0)
                .Select(t => new VideoTrack(t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Piste {t.Id}" : t.Name))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void SetSubtitleTrack(int id)
    {
        if (id >= -1) _player.SetSpu(id);
    }

    public IReadOnlyList<VideoTrack> GetAudioTracks()
    {
        try
        {
            return (_player.AudioTrackDescription ?? [])
                .Where(t => t.Id >= 0)
                .Select(t => new VideoTrack(t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Piste {t.Id}" : t.Name))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void SetAudioTrack(int id)
    {
        if (id >= -1) _player.SetAudioTrack(id);
    }

    public void Dispose()
    {
        try { _player.Stop(); } catch { }
        _player.Dispose();
        _libVlc.Dispose();
    }

    private static void EnsureCoreInitialized()
    {
        if (_coreInitialized) return;
        lock (CoreLock)
        {
            if (_coreInitialized) return;
            DebugConsole.Info("Initialisation du backend VLC...");
            Core.Initialize();
            _coreInitialized = true;
        }
    }
}
