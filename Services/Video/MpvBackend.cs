using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using LibMPVSharp;
using LibMPVSharp.WPF;

namespace Elysium_Cast_IPTV.Services.Video;

public sealed class MpvBackend : IVideoBackend
{
    private readonly LoggingVideoView _view;
    private readonly MPVMediaPlayer _player;
    private bool _hasMedia;
    private int _volume = 75;

    public MpvBackend(string? nativeDllPath = null)
    {
        DebugConsole.Step("mpv: création du backend…");

        if (!string.IsNullOrWhiteSpace(nativeDllPath))
        {
            var dir = Path.GetDirectoryName(nativeDllPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                DebugConsole.Step($"mpv: SetDllDirectory -> {dir}");
                SetDllDirectory(dir);
            }
        }

        try
        {
            // First real P/Invoke into libmpv (mpv_create + mpv_initialize). If
            // the native DLL is missing, has the wrong architecture or is broken,
            // this is where it blows up.
            DebugConsole.Step("mpv: chargement de libmpv + mpv_create (new MPVMediaPlayer)…");
            _player = new MPVMediaPlayer();
            if (!string.IsNullOrWhiteSpace(nativeDllPath)) SetDllDirectory(null);

            // A verbose persistent mpv log leaks provider credentials carried in
            // stream URLs. Keep diagnostics in the application's redacted log.
            Try("msg-level=warn", () => _player.SetProperty("msg-level", "warn"));
            Try("terminal=no", () => _player.SetProperty("terminal", "no"));

            DebugConsole.Step("mpv: création de la surface vidéo (LoggingVideoView)…");
            _view = new LoggingVideoView { MediaPlayer = _player };

            DebugConsole.Step("mpv: configuration GPU (vo/hwdec/scalers)…");
            ConfigureGpuDefaults();

            DebugConsole.Success("mpv: backend créé.");
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("mpv: échec de création du backend", ex);
            // Tear down any partially-created native handle before bubbling up so
            // the factory can fall back to VLC without leaking the mpv client.
            try { _player?.Dispose(); } catch (Exception inner) { DebugConsole.Exception("mpv: échec du nettoyage partiel", inner); }
            throw;
        }
    }

    public static string? LocateNative()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll"),
            Path.Combine(StateStore.FolderPath, "tools", "mpv", "libmpv-2.dll")
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var toolsRoot = Path.Combine(StateStore.FolderPath, "tools", "mpv");
        if (Directory.Exists(toolsRoot))
        {
            var installed = Directory.EnumerateFiles(toolsRoot, "libmpv-2.dll", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (installed != null) return installed.FullName;
        }

        return null;
    }

    public string Name => "mpv GPU";
    public FrameworkElement View => _view;
    public bool IsAvailable => true;
    public bool HasMedia => _hasMedia;

    public bool IsPlaying
    {
        get
        {
            if (!_hasMedia) return false;
            // LibMPVSharp's boolean getter marshals MPV_FORMAT_FLAG with an
            // undersized buffer. Read the string representation instead.
            return !string.Equals(GetString("pause"), "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    public long PositionMs
    {
        get => (long)(GetDouble("time-pos") * 1000.0);
        set => SeekTo(value);
    }

    public long LengthMs => (long)(GetDouble("duration") * 1000.0);

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            Try(() => _player.SetProperty("volume", (double)_volume));
        }
    }

    public event Action? Playing;
    public event Action? Paused;
    public event Action<PlaybackEndReason>? Ended;
    public event Action<string>? Failed;

    public void Play(string url)
    {
        try
        {
            DebugConsole.Step("mpv: lecture demandée.");

            DebugConsole.Step("mpv: (re)configuration GPU avant lecture…");
            ConfigureGpuDefaults();

            DebugConsole.Step("mpv: ouverture du média (loadfile)…");
            _player.ExecuteCommand(["loadfile", url, "replace"]);

            DebugConsole.Step("mpv: démarrage de la lecture (pause=false)…");
            _player.SetProperty("pause", false);

            Volume = _volume;
            _hasMedia = true;

            DebugConsole.Success("mpv: lecture initialisée.");
            Playing?.Invoke();
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("mpv: échec de lecture", ex);
            Failed?.Invoke(ex.Message);
        }
    }

    public void Resume()
    {
        Try(() => _player.SetProperty("pause", false));
        Playing?.Invoke();
    }

    public void Pause()
    {
        Try(() => _player.SetProperty("pause", true));
        Paused?.Invoke();
    }

    public void Stop(PlaybackEndReason reason = PlaybackEndReason.UserStop)
    {
        var notify = _hasMedia;
        Try(() => _player.ExecuteCommand(["stop"]));
        _hasMedia = false;
        if (notify) Ended?.Invoke(reason);
    }

    public void Clear()
    {
        Try(() => _player.ExecuteCommand(["stop"]));
    }

    public void SeekRelative(long deltaMs)
    {
        if (LengthMs <= 0) return;
        SeekTo(Math.Clamp(PositionMs + deltaMs, 0, LengthMs));
    }

    public void SetFullscreen(bool fullscreen)
    {
        // Fullscreen is managed by the WPF window so overlays, menus and cursor
        // behaviour remain backend-independent.
    }

    public VideoStats GetStats()
    {
        var sourceW = (uint)Math.Max(0, GetLong("width"));
        var sourceH = (uint)Math.Max(0, GetLong("height"));
        var outputW = (uint)Math.Max(0, (int)_view.ActualWidth);
        var outputH = (uint)Math.Max(0, (int)_view.ActualHeight);
        var fps = GetDouble("estimated-vf-fps");
        if (fps <= 0) fps = GetDouble("container-fps");

        return new VideoStats(
            Name,
            sourceW,
            sourceH,
            outputW,
            outputH,
            fps,
            GetDouble("video-bitrate") / 1000.0,
            GetLong("video-frame-info/displayed"),
            GetLong("frame-drop-count") + GetLong("decoder-frame-drop-count"),
            _hasMedia ? (IsPlaying ? "Playing" : "Paused") : "Idle");
    }

    public IReadOnlyList<VideoTrack> GetSubtitleTracks()
    {
        var tracks = new List<VideoTrack>();
        var count = GetLong("track-list/count");
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(GetString($"track-list/{i}/type"), "sub", StringComparison.OrdinalIgnoreCase))
                continue;

            var id = (int)GetLong($"track-list/{i}/id");
            var title = GetString($"track-list/{i}/title");
            var lang = GetString($"track-list/{i}/lang");
            var name = string.Join(" ", new[] { title, string.IsNullOrWhiteSpace(lang) ? null : $"({lang})" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            tracks.Add(new VideoTrack(id, string.IsNullOrWhiteSpace(name) ? $"Piste {id}" : name));
        }
        return tracks;
    }

    public void SetSubtitleTrack(int id)
    {
        Try(() => _player.SetProperty("sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture)));
    }

    public IReadOnlyList<VideoTrack> GetAudioTracks() => GetTracksByType("audio");

    public void SetAudioTrack(int id)
    {
        Try(() => _player.SetProperty("aid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture)));
    }

    public void Dispose()
    {
        DebugConsole.Step("mpv: disposition du backend…");
        try { Stop(PlaybackEndReason.Teardown); }
        catch (Exception ex) { DebugConsole.Exception("mpv: échec de l'arrêt pendant Dispose", ex); }

        // NOTE: we intentionally do NOT set _view.MediaPlayer = null here. In the
        // beta LibMPVSharp.WPF the property-changed handler dereferences the new
        // (null) value and throws a NullReferenceException. It is also redundant:
        // _player.Dispose() calls ReleaseRenderContext() (mpv_render_context_free)
        // which stops the native OpenGL render-update callback before the client
        // is destroyed.
        try
        {
            DebugConsole.Step("mpv: libération du client libmpv (mpv_destroy)…");
            _player.Dispose();
            DebugConsole.Success("mpv: backend disposé.");
        }
        catch (Exception ex) { DebugConsole.Exception("mpv: échec de la libération de libmpv", ex); }
    }

    private void ConfigureGpuDefaults()
    {
        // IMPORTANT: this backend renders through the libmpv render API on an
        // OpenGL context (LibMPVSharp.WPF GLControl). That render path is fragile:
        // GPU hardware-decode surfaces (d3d11va/nvdec) cannot interop with the GL
        // context and crash on the first rendered frame, and the heavy "gpu-hq"
        // shader chain (EWA scalers + interpolation) overloads it on some drivers.
        //
        // Stability first (PRIORITÉ 0): software decode + a minimal, safe render
        // config. The advanced upscaling can be re-enabled incrementally once the
        // render pipeline is proven stable on this machine.
        Try("hwdec=no", () => _player.SetProperty("hwdec", "no"));
        Try("vo=libmpv", () => _player.SetProperty("vo", "libmpv"));
        Try("scale=bilinear", () => _player.SetProperty("scale", "bilinear"));
        Try("cscale=bilinear", () => _player.SetProperty("cscale", "bilinear"));
        Try("dscale=bilinear", () => _player.SetProperty("dscale", "bilinear"));
        Try("interpolation=no", () => _player.SetProperty("interpolation", "no"));
        Try("video-sync=audio", () => _player.SetProperty("video-sync", "audio"));
    }

    private IReadOnlyList<VideoTrack> GetTracksByType(string type)
    {
        var tracks = new List<VideoTrack>();
        var count = GetLong("track-list/count");
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(GetString($"track-list/{i}/type"), type, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = (int)GetLong($"track-list/{i}/id");
            var title = GetString($"track-list/{i}/title");
            var lang = GetString($"track-list/{i}/lang");
            var name = string.Join(" ", new[] { title, string.IsNullOrWhiteSpace(lang) ? null : $"({lang})" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            tracks.Add(new VideoTrack(id, string.IsNullOrWhiteSpace(name) ? $"Piste {id}" : name));
        }
        return tracks;
    }

    private void SeekTo(long ms)
    {
        var seconds = (ms / 1000.0).ToString(CultureInfo.InvariantCulture);
        Try(() => _player.ExecuteCommand(["seek", seconds, "absolute"]));
    }

    private long GetLong(string property)
    {
        try { return _player.GetPropertyLong(property); }
        catch { return 0; }
    }

    private double GetDouble(string property)
    {
        try { return _player.GetPropertyDouble(property); }
        catch { return 0; }
    }

    private string GetString(string property)
    {
        try { return _player.GetPropertyString(property) ?? ""; }
        catch { return ""; }
    }

    private static void Try(Action action)
    {
        try { action(); } catch { }
    }

    private static void Try(string step, Action action)
    {
        try { action(); }
        catch (Exception ex) { DebugConsole.Warn($"mpv: étape ignorée ({step}) : {ex.Message}"); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);
}
