using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Playback start/stop/pause, live reconnection, EPG, seek, audio/subtitle
/// tracks, statistics, local-audio continuation and the reactions to backend
/// events. Every delayed update re-checks the playback generation and backend.
/// </summary>
internal sealed class PlaybackController
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly MediaLibrary _library;
    private readonly AudioQueue _queue;
    private readonly IAppStateStore _store;
    private readonly VideoPipeline _pipeline;
    private readonly AudioVisualizerController _visualizer;
    private readonly OsdController _osd;
    private readonly WindowsMediaTransportService _mediaTransport;

    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _epgTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    // Render priority (not the default Background): the AudioCore+ visualizer floods
    // the UI thread with CompositionTarget.Rendering work at Render priority, which
    // would otherwise starve a Background-priority timer and freeze the seek bar for
    // seconds at a time. 250 ms keeps the position readout feeling real-time.
    private readonly DispatcherTimer _progressTimer =
        new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
    private DispatcherTimer? _reconnectTimer;
    private CancellationTokenSource? _epgCts;
    private EpgEntry? _epgNow;
    private bool _userSeeking;
    private bool _loadingSubtitleChoices;
    private bool _loadingAudioChoices;

    public PlaybackController(MainWindow view, PlayerViewModel vm, PlaybackSession session, MediaLibrary library,
        AudioQueue queue, IAppStateStore store, VideoPipeline pipeline, AudioVisualizerController visualizer,
        OsdController osd, WindowsMediaTransportService mediaTransport)
    {
        _view = view;
        _vm = vm;
        _session = session;
        _library = library;
        _queue = queue;
        _store = store;
        _pipeline = pipeline;
        _visualizer = visualizer;
        _osd = osd;
        _mediaTransport = mediaTransport;

        _view.SubtitleCombo.ItemsSource = SubtitleOptions;
        _view.AudioCombo.ItemsSource = AudioOptions;
        _statsTimer.Tick += (_, _) => UpdateStats();
        _epgTimer.Tick += (_, _) => UpdateEpgProgress();
        _progressTimer.Tick += (_, _) => UpdateSeek();

        _view.SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _userSeeking = true));
        _view.SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { SeekToSlider(); _userSeeking = false; }));
        _view.SeekSlider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => SeekToSlider()), true);
    }

    public ObservableCollection<TrackOption> SubtitleOptions { get; } = new();
    public ObservableCollection<TrackOption> AudioOptions { get; } = new();

    /// <summary>A series cannot be played directly: the catalogue opens its episodes.</summary>
    public event Action<PlayItem>? SeriesRequested;

    /// <summary>The persisted last-played item changed (resume button).</summary>
    public event Action? HistoryChanged;

    /// <summary>Media started or ended: OSD rows that depend on playback refresh.</summary>
    public event Action? MediaStateChanged;

    private Settings Settings => _store.Settings;

    // ============ PLAY ============
    public void Play(PlayItem item, bool persistHistory = true)
    {
        var backend = _session.Backend;
        if (backend == null) return;
        if (item.Kind == PlayItemKind.Series) { SeriesRequested?.Invoke(item); return; }
        if (item.Kind == PlayItemKind.Local && !File.Exists(LocalLibraryService.PathOf(item)))
        {
            _vm.ShowOverlay("File not found on disk.", spinning: false); return;
        }
        _reconnectTimer?.Stop();
        _userSeeking = false;
        var generation = _session.NextGeneration();
        CancelEpgRequest();
        _session.Current = item;
        _vm.NowPlayingTitle = item.Name;
        _vm.NowPlayingSubtitle = PlaybackText.NowPlayingSubtitle(item);
        _vm.IsEpgVisible = false;
        ResetTrackChoices();
        _vm.ShowOverlay("Loading...", spinning: true);
        _osd.Show();

        // seek bar + skip buttons only make sense for finite media (films / episodes)
        var seekable = MediaKinds.IsSeekable(item);
        _vm.IsSeekVisible = seekable;
        _vm.IsSkipBackVisible = false;
        _vm.IsSkipForwardVisible = false;
        if (seekable)
        {
            _vm.SeekPosition = 0; _vm.CurrentTimeText = "0:00"; _vm.TotalTimeText = "0:00";
            _progressTimer.Start();
        }
        else _progressTimer.Stop();

        try
        {
            var url = _library.Source.GetStreamUrl(item);
            DebugConsole.Info($"Play [{item.KindLabel}] {item.Name}");
            backend.Volume = (int)_view.VolumeSlider.Value;
            backend.Play(url);
            _vm.ShowPlayGlyph = false;
            _visualizer.Start(item);
            _pipeline.ApplyElySound();
            _pipeline.ApplyElyFlow();
            MediaStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            DebugConsole.Exception($"Failed to start playback: {item.Name}", ex);
            _vm.ShowOverlay("Could not start playback.", spinning: false);
            _vm.NowPlayingSubtitle = LocalizationService.T("Error");
            _visualizer.Hide();
            return;
        }

        if (persistHistory)
        {
            _library.Profile.LastPlayed = item;
            _store.Save();
            HistoryChanged?.Invoke();
        }

        if (_vm.IsStatsEnabled) _statsTimer.Start();
        if (item.Kind == PlayItemKind.Live && _library.Source.IsXtream)
        {
            var cts = new CancellationTokenSource();
            _epgCts = cts;
            LoadEpgAsync(item.Id, generation, cts.Token).Forget("EPG");
        }
        else { _epgTimer.Stop(); _vm.IsEpgVisible = false; }
    }

    /// <summary>Plays a raw path/URL outside the catalogue (console diagnostic).</summary>
    public void PlayRaw(string path)
    {
        var backend = _session.Backend;
        if (backend == null) { DebugConsole.Error("playfile: no video backend."); return; }
        _session.Current = null;
        _vm.ShowOverlay("Loading (local file)...", spinning: true);
        DebugConsole.Info("playfile -> " + path);
        backend.Volume = (int)_view.VolumeSlider.Value;
        backend.Play(path);
        _vm.ShowPlayGlyph = false;
    }

    // ============ TRANSPORT ============
    /// <summary>
    /// Pauses/resumes the loaded media; with nothing loaded, plays
    /// <paramref name="fallback"/> (the selected list item) or the last media.
    /// </summary>
    public void PlayPause(PlayItem? fallback)
    {
        var backend = _session.Backend;
        if (backend == null) return;
        if (backend.IsPlaying)
        {
            backend.Pause();
            _vm.ShowPlayGlyph = true;
            // Audio visualizer: the engine sees IsPlaying=false and lets the
            // spectrum settle - rendering keeps running, no freeze needed.
        }
        else if (backend.HasMedia)
        {
            backend.Resume();
            _vm.ShowPlayGlyph = false;
            _visualizer.ShowIfAudio();
        }
        else if (fallback != null) Play(fallback);
        else if (_session.Current != null) Play(_session.Current);
    }

    public void ResumeFromSystemControls()
    {
        var backend = _session.Backend;
        if (backend?.HasMedia != true) return;
        if (!backend.IsPlaying) backend.Resume();
    }

    public void PauseFromSystemControls()
    {
        if (_session.IsPlaying) _session.Backend!.Pause();
    }

    public void Stop()
    {
        if (_session.HasMedia)
            _session.Backend!.Stop(PlaybackEndReason.UserStop);
    }

    public void OnVolumeChanged(double value)
    {
        if (_session.Backend != null) _session.Backend.Volume = (int)value;
        _vm.VolumeText = $"{(int)value}%";
        if (_session.Connected && Settings.RememberVolume) Settings.DefaultVolume = (int)value;
    }

    private void TryAutoReconnect()
    {
        if (!_session.Connected || !Settings.AutoReconnect || _session.Current == null) return;
        var generation = _session.Generation;
        _reconnectTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_session.Connected && generation == _session.Generation && _session.Current != null)
            {
                DebugConsole.Warn("Automatic reconnection...");
                Play(_session.Current);
            }
        };
        timer.Start();
    }

    // ============ LOCAL AUDIO CONTINUATION ============
    private static bool IsPlayableFile(PlayItem item) => File.Exists(LocalLibraryService.PathOf(item));

    /// <summary>
    /// Previous/next for local audio (manual queue first, then the automatic
    /// continuation). Returns false when the current media is not local audio.
    /// </summary>
    public bool TryZapAudio(int direction)
    {
        if (_session.Current == null || !MediaKinds.IsAudioOnly(_session.Current)) return false;
        var next = _queue.Zap(direction, _session.Current, _library.LocalAudio, IsPlayableFile);
        if (next != null) Play(next);
        return true;
    }

    private bool TryPlayNextAudio()
    {
        var next = _queue.NextAfterEnd(_session.Current, _library.LocalAudio, IsPlayableFile);
        if (next == null) return false;
        Play(next);
        return true;
    }

    // ============ EPG ============
    private async Task LoadEpgAsync(string streamId, int generation, CancellationToken ct)
    {
        try
        {
            var list = await _library.Source.GetShortEpgAsync(streamId, ct: ct);
            if (ct.IsCancellationRequested || generation != _session.Generation || _session.Current?.Id != streamId) return;
            if (list.Count == 0) { _vm.IsEpgVisible = false; return; }
            var now = list.FirstOrDefault(x => x.Start <= DateTime.Now && DateTime.Now < x.End) ?? list[0];
            var next = list.SkipWhile(x => x != now).Skip(1).FirstOrDefault();
            _vm.NowPlayingSubtitle = next != null
                ? LocalizationService.Format("{0} · Next: {1}", now.Title, next.Title)
                : $"● {now.Title}";
            _epgNow = now;
            _vm.EpgPercent = now.ProgressPercent;
            _vm.IsEpgVisible = true;
            _epgTimer.Start();
        }
        catch (OperationCanceledException) { }
    }

    public void CancelEpgRequest()
    {
        _epgCts?.Cancel();
        _epgCts?.Dispose();
        _epgCts = null;
        _epgTimer.Stop();
    }

    private void UpdateEpgProgress()
    {
        if (_epgNow != null) _vm.EpgPercent = _epgNow.ProgressPercent;
    }

    // ============ SEEK (VOD / episodes) ============
    private void UpdateSeek()
    {
        var backend = _session.Backend;
        if (backend == null) return;
        var len = backend.LengthMs;
        var pos = backend.PositionMs;
        if (len <= 0) { _vm.TotalTimeText = "-"; _vm.UpdateSkipButtons(0, 0); return; }

        if (!_userSeeking)
        {
            _vm.SeekMaximum = len / 1000.0;
            _vm.SeekPosition = Math.Clamp(pos / 1000.0, 0, _vm.SeekMaximum);
        }
        _vm.CurrentTimeText = PlaybackText.FormatTime(pos);
        _vm.TotalTimeText = PlaybackText.FormatTime(len);
        _vm.UpdateSkipButtons(len, pos);
    }

    private void SeekToSlider()
    {
        var backend = _session.Backend;
        if (backend == null || backend.LengthMs <= 0) return;
        backend.PositionMs = (long)(_vm.SeekPosition * 1000);
        _vm.CurrentTimeText = PlaybackText.FormatTime(backend.PositionMs);
    }

    public void SeekRelative(long deltaMs)
    {
        var backend = _session.Backend;
        if (backend == null || backend.LengthMs <= 0) return;
        var t = Math.Clamp(backend.PositionMs + deltaMs, 0, backend.LengthMs);
        backend.PositionMs = t;
        _vm.SeekPosition = t / 1000.0;
        _vm.CurrentTimeText = PlaybackText.FormatTime(t);
        _vm.UpdateSkipButtons(backend.LengthMs, t);
    }

    // ============ AUDIO / SUBTITLE TRACKS ============
    public void ResetTrackChoices()
    {
        _loadingSubtitleChoices = true;
        Replace(SubtitleOptions, TrackChoices.Defaults(subtitles: true));
        _view.SubtitleCombo.SelectedIndex = 0;
        _view.SubtitleCombo.Visibility = Visibility.Collapsed;
        _loadingSubtitleChoices = false;

        _loadingAudioChoices = true;
        Replace(AudioOptions, TrackChoices.Defaults(subtitles: false));
        _view.AudioCombo.SelectedIndex = 0;
        _view.AudioCombo.Visibility = Visibility.Collapsed;
        _loadingAudioChoices = false;
    }

    private async Task RefreshTracksSoonAsync(int generation)
    {
        var expectedBackend = _session.Backend;
        await Task.Delay(1200);
        if (!_session.IsCurrent(generation, expectedBackend) || _session.Current == null) return;
        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.Invoke(RefreshTracks);
            return;
        }
        RefreshTracks();
    }

    private void RefreshTracks()
    {
        RefreshTrackList(subtitles: true);
        RefreshTrackList(subtitles: false);
    }

    private void RefreshTrackList(bool subtitles)
    {
        var backend = _session.Backend;
        if (backend == null) return;
        var options = subtitles ? SubtitleOptions : AudioOptions;
        var combo = subtitles ? _view.SubtitleCombo : _view.AudioCombo;

        if (subtitles) _loadingSubtitleChoices = true; else _loadingAudioChoices = true;
        List<TrackOption> built;
        try
        {
            built = TrackChoices.Build(subtitles, subtitles ? backend.GetSubtitleTracks() : backend.GetAudioTracks());
        }
        catch (Exception ex)
        {
            DebugConsole.Warn((subtitles ? "Subtitles unavailable: " : "Audio tracks unavailable: ") + ex.Message);
            built = TrackChoices.Defaults(subtitles);
        }
        Replace(options, built);

        var selected = TrackChoices.PickPreferred(options, subtitles ? Settings.PreferredSubtitle : Settings.PreferredAudio);
        combo.SelectedItem = selected;
        combo.Visibility = TrackChoices.HasChoice(subtitles, options.Count) ? Visibility.Visible : Visibility.Collapsed;
        if (subtitles) _loadingSubtitleChoices = false; else _loadingAudioChoices = false;

        if (selected is { IsAuto: false }) ApplyTrack(subtitles, selected, persist: false);
    }

    public void OnSubtitleSelectionChanged()
    {
        if (_loadingSubtitleChoices || _view.SubtitleCombo.SelectedItem is not TrackOption opt) return;
        ApplyTrack(subtitles: true, opt, persist: true);
    }

    public void OnAudioSelectionChanged()
    {
        if (_loadingAudioChoices || _view.AudioCombo.SelectedItem is not TrackOption opt) return;
        ApplyTrack(subtitles: false, opt, persist: true);
    }

    private void ApplyTrack(bool subtitles, TrackOption opt, bool persist)
    {
        try
        {
            var backend = _session.Backend;
            if (backend != null && !opt.IsAuto)
            {
                if (subtitles) backend.SetSubtitleTrack(opt.Id); else backend.SetAudioTrack(opt.Id);
            }
            if (persist)
            {
                if (subtitles) Settings.PreferredSubtitle = opt.Key; else Settings.PreferredAudio = opt.Key;
                _store.Save();
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn((subtitles ? "Could not select subtitles: " : "Could not select audio track: ") + ex.Message);
        }
    }

    private static void Replace(ObservableCollection<TrackOption> target, IEnumerable<TrackOption> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    // ============ STATS ============
    public void SetStatsVisible(bool on)
    {
        _vm.IsStatsEnabled = on;
        _view.StatsSwitch.IsChecked = on;
        Settings.ShowStats = on; _store.Save();
        if (on && _session.IsPlaying) _statsTimer.Start(); else _statsTimer.Stop();
    }

    public void ToggleStats() => SetStatsVisible(!_vm.IsStatsEnabled);

    public void UpdateStats()
    {
        var backend = _session.Backend;
        if (backend == null || !backend.HasMedia) { _vm.StatsText = "-"; return; }
        try
        {
            // Audio mode: video stats are meaningless (0x0) - show the stream
            // parameters and the live analysis of the visualizer instead.
            _vm.StatsText = _session.Current != null && MediaKinds.IsAudioOnly(_session.Current)
                ? _visualizer.BuildStats()
                : PlaybackText.VideoStats(backend.GetStats(), ElyColorCatalog.Active(Settings).Name);
        }
        catch (Exception ex)
        {
            _vm.StatsText = LocalizationService.T("statistics unavailable");
            // File-only: this runs every second while the panel is open.
            DebugConsole.Trace("Statistics unavailable: " + ex.Message);
        }
    }

    // ============ BACKEND EVENTS ============
    public void OnBackendPlaying()
    {
        var generation = _session.Generation;
        _vm.HideOverlay(); _vm.ShowPlayGlyph = false; _osd.Show();
        _osd.RefreshSafeAreaSoonAsync().Forget("OSD layout refresh");
        RefreshTracksSoonAsync(generation).Forget("Track refresh");
        MediaStateChanged?.Invoke();
        DebugConsole.Success($"Now playing: {_session.Current?.Name}");
        if (_session.Current != null && MediaKinds.IsAudioOnly(_session.Current))
            _mediaTransport.SetState(hasMedia: true, playing: true);
    }

    public void OnBackendFailed(string message)
    {
        if (!_session.Connected) return;
        _vm.ShowOverlay(message, spinning: false);
        _vm.NowPlayingSubtitle = LocalizationService.T("Error");
        DebugConsole.Error($"Playback failed: {_session.Current?.Name} ({message})");
        if (PlaybackTerminationPolicy.ForFailure(
                _session.Current?.Kind == PlayItemKind.Live,
                Settings.AutoReconnect) == PlaybackTerminationAction.ReconnectLive)
            TryAutoReconnect();
    }

    public void OnBackendEnded(PlaybackEndReason reason)
    {
        var current = _session.Current;
        var endedAudio = reason == PlaybackEndReason.NaturalEnd && current != null && MediaKinds.IsAudioOnly(current);
        var termination = PlaybackTerminationPolicy.ForEnd(reason, current?.Kind == PlayItemKind.Live);
        if (termination == PlaybackTerminationAction.Ignore) return;
        _mediaTransport.SetState(hasMedia: false, playing: false);
        DebugConsole.Info($"Playback ended: reason={reason}, type={current?.Kind}, media={current?.Name}");

        _session.NextGeneration();
        CancelEpgRequest();
        _reconnectTimer?.Stop();
        _statsTimer.Stop();
        _epgTimer.Stop();
        _progressTimer.Stop();
        _vm.ShowPlayGlyph = true;
        _vm.IsEpgVisible = false;
        _vm.IsSkipBackVisible = false;
        _vm.IsSkipForwardVisible = false;
        ResetTrackChoices();
        if (current != null && MediaKinds.IsAudioOnly(current))
            _visualizer.Hide();
        _session.Backend?.Clear();

        if (endedAudio && TryPlayNextAudio()) return;

        if (termination == PlaybackTerminationAction.ManualStop)
        {
            _vm.NowPlayingSubtitle = LocalizationService.T("Stopped");
            _vm.IsSeekVisible = false;
            _vm.ShowOverlay("Playback stopped", spinning: false);
        }
        else if (termination == PlaybackTerminationAction.ReconnectLive)
        {
            _vm.NowPlayingSubtitle = LocalizationService.T("Stream interrupted");
            _vm.ShowOverlay("Stream interrupted: reconnecting...", spinning: true);
            TryAutoReconnect();
        }
        else
        {
            _vm.NowPlayingSubtitle = LocalizationService.T("Finished");
            if (_vm.IsSeekVisible)
            {
                _vm.SeekPosition = _vm.SeekMaximum;
                _vm.CurrentTimeText = _vm.TotalTimeText;
            }
            _vm.ShowOverlay("Playback finished. Select Play to restart", spinning: false);
        }

        MediaStateChanged?.Invoke();
        _osd.Show();
    }

    public void OnBackendPaused()
    {
        _vm.ShowPlayGlyph = true;
        _osd.Show();
        if (_session.Current != null && MediaKinds.IsAudioOnly(_session.Current))
            _mediaTransport.SetState(hasMedia: true, playing: false);
    }

    // ============ TEARDOWN ============
    /// <summary>Stops everything playback-related for a sign-out.</summary>
    public void ResetForSignOut()
    {
        CancelEpgRequest();
        _session.NextGeneration();
        _reconnectTimer?.Stop();
        StopBackend(PlaybackEndReason.Teardown, "Sign-out: stopping playback");
        _statsTimer.Stop(); _epgTimer.Stop(); _progressTimer.Stop();
        _visualizer.Hide();
        _mediaTransport.Clear();
        _queue.Reset();
        _session.Current = null;
        _vm.ShowOverlay("Select a channel to start playback", spinning: false);
        _vm.NowPlayingTitle = LocalizationService.T("Nothing playing");
        _vm.NowPlayingSubtitle = LocalizationService.T("Waiting...");
        _vm.IsSeekVisible = false;
        _vm.IsSkipBackVisible = false;
        _vm.IsSkipForwardVisible = false;
        _vm.IsEpgVisible = false;
    }

    /// <summary>The current local file was removed from the library.</summary>
    public void StopRemovedMedia()
    {
        _session.NextGeneration();
        _progressTimer.Stop(); _statsTimer.Stop();
        _mediaTransport.Clear();
        StopBackend(PlaybackEndReason.Replaced, "Library removal: stopping playback");
        _visualizer.Hide();
        _session.Current = null;
        _vm.ShowOverlay(LocalizationService.T("Local file removed."), spinning: false);
    }

    /// <summary>mpv holds the file open while it plays: release it before writing tags.</summary>
    public void ReleaseCurrentFile()
    {
        StopBackend(PlaybackEndReason.Replaced, "Metadata edit: releasing the file");
        _visualizer.Hide();
        _session.Current = null;
    }

    private void StopBackend(PlaybackEndReason reason, string context)
    {
        try { _session.Backend?.Stop(reason); }
        catch (Exception ex) { DebugConsole.Exception(context, ex); }
    }

    public void Shutdown()
    {
        CancelEpgRequest();
        _reconnectTimer?.Stop();
        _progressTimer.Stop(); _statsTimer.Stop();
    }
}
