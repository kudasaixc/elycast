using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Owns the lifetime of the <see cref="IVideoBackend"/>: creation through the
/// factory, the WPF/native view mount, event marshalling to the UI thread and
/// the ordered replacement path. Backend events reach subscribers only for the
/// backend and playback generation that raised them.
/// </summary>
internal sealed class BackendHost
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly VideoPipeline _pipeline;
    private readonly OsdController _osd;
    private readonly PlaybackController _playback;
    private readonly Dictionary<IVideoBackend, BackendEventHandlers> _handlers = new();
    private bool _replacementPending;

    private sealed record BackendEventHandlers(Action Playing, Action<string> Failed, Action<PlaybackEndReason> Ended, Action Paused);

    public BackendHost(MainWindow view, PlaybackSession session, IAppStateStore store, VideoPipeline pipeline,
        OsdController osd, PlaybackController playback)
    {
        _view = view;
        _session = session;
        _store = store;
        _pipeline = pipeline;
        _osd = osd;
        _playback = playback;
    }

    public event Action? Playing;
    public event Action<string>? Failed;
    public event Action<PlaybackEndReason>? Ended;
    public event Action? Paused;

    /// <summary>A click landed on the native video surface instead of the WPF overlay.</summary>
    public event Action? SurfaceClicked;

    // ============ CREATION ============
    public void Initialize()
    {
        if (_session.Backend != null) return;

        var diagnosticBackend = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_BACKEND");
        var preferredBackend = string.IsNullOrWhiteSpace(diagnosticBackend)
            ? _store.Settings.VideoBackend
            : diagnosticBackend;
        DebugConsole.Step($"Backend: creating through factory (preference='{preferredBackend}')...");
        var backend = VideoBackendFactory.Create(preferredBackend);
        _session.Backend = backend;

        DebugConsole.Step($"Backend: attaching video surface ({backend.Name}) to the visual tree...");
        _view.VideoHost.Content = backend.View;
        if (backend.View is MpvHwndHost nativeHost)
        {
            // HwndHost owns its own native input queue; relay it explicitly so
            // entering the picture reveals the OSD on the very first movement.
            nativeHost.PointerActivity += _osd.OnVideoSurfacePointerActivity;
            nativeHost.PointerLeft += _osd.OnVideoSurfacePointerLeft;
            nativeHost.PointerClicked += () => SurfaceClicked?.Invoke();
        }
        backend.Volume = (int)_view.VolumeSlider.Value;

        DebugConsole.Step("Backend: attaching events...");
        var events = new BackendEventHandlers(
            () => Dispatch(backend, () => Playing?.Invoke()),
            message => Dispatch(backend, () => Failed?.Invoke(message)),
            reason => Dispatch(backend, () => Ended?.Invoke(reason)),
            () => Dispatch(backend, () => Paused?.Invoke()));
        _handlers[backend] = events;
        backend.Playing += events.Playing;
        backend.Failed += events.Failed;
        backend.Ended += events.Ended;
        backend.Paused += events.Paused;

        _pipeline.ApplyAll();

        // A pipeline stage may legitimately schedule a backend replacement,
        // which clears the session's backend synchronously before creating the
        // new instance. Never dereference that transient null during startup.
        if (!ReferenceEquals(_session.Backend, backend))
        {
            DebugConsole.Info($"Backend: transition from {backend.Name} scheduled.");
            return;
        }

        DebugConsole.Success($"Backend initialized ({backend.Name}).");
    }

    // Native callbacks must never wait for the UI: marshal asynchronously and
    // drop the event if another playback or backend took over meanwhile.
    private void Dispatch(IVideoBackend backend, Action action)
    {
        var generation = _session.Generation;
        void Apply()
        {
            if (_session.IsCurrent(generation, backend)) action();
        }
        var dispatcher = _view.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(new Action(Apply));
    }

    private void Detach(IVideoBackend backend)
    {
        if (!_handlers.Remove(backend, out var events)) return;
        backend.Playing -= events.Playing;
        backend.Failed -= events.Failed;
        backend.Ended -= events.Ended;
        backend.Paused -= events.Paused;
    }

    // ============ REPLACEMENT ============
    /// <summary>
    /// The only UI-level backend replacement path. Order matters: detach
    /// events → stop → dispose (while the surface is still in the visual tree)
    /// → remove the surface → wait one dispatcher cycle → create → optionally
    /// replay, restoring position and pause state.
    /// </summary>
    public void Recreate(bool replayCurrent)
    {
        if (_session.ShuttingDown || _replacementPending) return;
        _replacementPending = true;
        var replacementGeneration = _session.NextGeneration();
        DebugConsole.Step("Backend change requested: tearing down the old backend...");

        var current = _session.Current;
        var hadMedia = _session.HasMedia;
        var wasPlaying = _session.IsPlaying;
        var resumePosition = _session.Backend?.PositionMs ?? 0;
        var volume = (int)_view.VolumeSlider.Value;

        var old = _session.Backend;
        // Mark the field null immediately so timers/callbacks firing during the
        // teardown no longer touch the backend we are about to destroy.
        _session.Backend = null;

        if (old != null)
        {
            // 1. Detach all managed event handlers from the OLD backend so late
            //    Playing/Failed/Ended/Paused events cannot reach the UI.
            try { DebugConsole.Step("Backend: detaching events..."); Detach(old); }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to detach events", ex); }

            // 2. Stop playback so no new native frames are decoded or rendered.
            try { DebugConsole.Step("Backend: stopping playback..."); old.Stop(PlaybackEndReason.Replaced); }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to stop playback", ex); }

            // 3. Dispose the OLD backend WHILE its video surface is still in the
            //    Visual Tree. The mpv render context (OpenGL) must be freed while
            //    its GL context is still alive - Dispose() internally detaches the
            //    native render callback before freeing libmpv.
            try { DebugConsole.Step("Backend: disposing the old backend..."); old.Dispose(); }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to dispose the old backend", ex); }

            // 4. Only now remove the surface from the Visual Tree. This triggers
            //    the GL/D3D context teardown, which is safe because no native
            //    handle still references it.
            try { DebugConsole.Step("Backend: removing the video surface from the visual tree..."); _view.VideoHost.Content = null; }
            catch (Exception ex) { DebugConsole.Exception("Backend: failed to remove the video surface", ex); }
        }
        else
        {
            _view.VideoHost.Content = null;
        }

        _playback.ResetTrackChoices();

        // 5. Wait one WPF Dispatcher cycle so the Unloaded handlers and native
        //    resource frees complete before a new backend is created.
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _replacementPending = false;
            if (_session.ShuttingDown) return;
            try
            {
                DebugConsole.Step("Backend: creating the new backend...");
                Initialize();
                if (_session.Backend != null) _session.Backend.Volume = volume;
                _playback.UpdateStats();
                DebugConsole.Success("Backend: new backend ready.");
            }
            catch (Exception ex)
            {
                DebugConsole.Exception("Backend: failed to create the new backend", ex);
                return;
            }

            if (!replayCurrent || !hadMedia || current == null || !_session.Connected ||
                replacementGeneration != _session.Generation || !ReferenceEquals(current, _session.Current)) return;

            _playback.Play(current);
            RestorePositionWhenReady(current, resumePosition, wasPlaying);
        }));
    }

    private void RestorePositionWhenReady(PlayItem current, long resumePosition, bool wasPlaying)
    {
        var expectedBackend = _session.Backend;
        var generation = _session.Generation;
        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (!_session.IsCurrent(generation, expectedBackend) || ++attempts > 50)
            { timer.Stop(); return; }
            if (expectedBackend?.HasMedia != true || (current.Kind != PlayItemKind.Live && expectedBackend.LengthMs <= 0)) return;
            timer.Stop();
            if (current.Kind != PlayItemKind.Live && resumePosition > 0)
                expectedBackend.PositionMs = Math.Min(resumePosition, expectedBackend.LengthMs);
            if (!wasPlaying) expectedBackend.Pause();
        };
        timer.Start();
    }

    // ============ SHUTDOWN ============
    public void DisposeBackend()
    {
        var backend = _session.Backend;
        _session.Backend = null;
        if (backend == null) return;
        try { Detach(backend); } catch (Exception ex) { DebugConsole.Exception("Shutdown: failed to detach events", ex); }
        try { DebugConsole.Step("Shutdown: stopping playback..."); backend.Stop(PlaybackEndReason.Teardown); }
        catch (Exception ex) { DebugConsole.Exception("Shutdown: failed to stop playback", ex); }
        try { DebugConsole.Step("Closing: disposing the backend..."); backend.Dispose(); }
        catch (Exception ex) { DebugConsole.Exception("Shutdown: failed to dispose backend", ex); }
    }
}
