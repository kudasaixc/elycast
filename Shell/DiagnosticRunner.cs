using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Opt-in automated smoke/performance hooks driven by environment variables
/// (<c>ELYCAST_DIAGNOSTIC_FILE</c>, <c>..._ELYSOUND_SWEEP</c>, <c>..._PIPELINE_SWEEP</c>).
/// Never active in a normal startup.
/// </summary>
internal sealed class DiagnosticRunner
{
    private readonly Dispatcher _dispatcher;
    private readonly PlaybackSession _session;
    private readonly PlaybackController _playback;
    private readonly ConnectionController _connection;

    public DiagnosticRunner(Dispatcher dispatcher, PlaybackSession session, PlaybackController playback, ConnectionController connection)
    {
        _dispatcher = dispatcher;
        _session = session;
        _playback = playback;
        _connection = connection;
    }

    public void StartIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            DebugConsole.Warn("Diagnostic playback: file not found.");
            return;
        }

        // A short dispatcher delay lets backend creation settle before opening the media.
        _session.Connected = true;
        _connection.GoToPlayer();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var backend = _session.Backend;
            if (backend == null) return;
            DebugConsole.Info("Diagnostic playback: local file.");
            _playback.Play(PlayItem.FromLocalFile(path), persistHistory: false);
            ElySoundSweepAsync().Forget("Diagnostic ELYSOUND+ sweep");
            PipelineSweepAsync().Forget("Diagnostic pipeline sweep");
            LogStatsAsync(backend).Forget("Diagnostic statistics");
        };
        timer.Start();
    }

    private static bool Requested(string variable) =>
        string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal);

    private async Task PipelineSweepAsync()
    {
        if (!Requested("ELYCAST_DIAGNOSTIC_PIPELINE_SWEEP")) return;
        if (_session.Backend is not MpvHwndBackend { IsElyCoreRenderer: true } backend) return;

        await Task.Delay(1800);
        static string Snapshot(MpvHwndBackend current, ElyFlowRendererInterop.RendererState state) =>
            current.GetElyCoreVsrAuditSnapshot() + Environment.NewLine +
            $"ELYCORE target rebuilds: {state.TargetRebuilds}{Environment.NewLine}" +
            $"ELYCORE FRUC effective: {state.FrucInitialized != 0}{Environment.NewLine}" +
            $"ELYCORE frames presented: {state.FramesPresented}";

        DebugConsole.Info("Diagnostic pipeline initial:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(false);
        await Task.Delay(700);
        DebugConsole.Info("Diagnostic pipeline VSR off:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(true);
        await Task.Delay(900);
        DebugConsole.Info("Diagnostic pipeline VSR on:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(false);
        await Task.Delay(700);
        DebugConsole.Info("Diagnostic pipeline VSR off #2:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreVsr(true);
        await Task.Delay(900);
        DebugConsole.Info("Diagnostic pipeline VSR on #2:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreFruc(false);
        await Task.Delay(500);
        DebugConsole.Info("Diagnostic pipeline FRUC off:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
        backend.ConfigureElyCoreFruc(true);
        await Task.Delay(3600);
        DebugConsole.Info("Diagnostic pipeline VSR+FRUC:\n" + Snapshot(backend, ElyFlowRendererInterop.GetState()));
    }

    private async Task ElySoundSweepAsync()
    {
        if (!Requested("ELYCAST_DIAGNOSTIC_ELYSOUND_SWEEP")) return;
        if (_session.Backend is not IElySoundBackend dsp) return;

        await Task.Delay(1200);
        foreach (var profile in ElySoundCatalog.BuiltIn)
        {
            if (_session.Backend is not IElySoundBackend current || !ReferenceEquals(current, dsp)) return;
            var before = _session.Backend.PositionMs;
            var stopwatch = Stopwatch.StartNew();
            var result = dsp.ApplyElySound(profile, enabled: true, virtualSurround: true);
            stopwatch.Stop();
            var after = _session.Backend.PositionMs;
            DebugConsole.Info($"Diagnostic ELYSOUND+ {profile.Id}: applied={result.Applied}, pending={result.Pending}, " +
                              $"commande={stopwatch.Elapsed.TotalMilliseconds:0.0} ms, position {before}->{after} ms (delta={after - before} ms), {result.Message}");
            await Task.Delay(350);
        }

        var backend = _session.Backend;
        if (backend?.HasMedia == true)
        {
            backend.Pause();
            var pausedAt = backend.PositionMs;
            await Task.Delay(300);
            var pausedApply = dsp.ApplyElySound(ElySoundCatalog.BuiltIn[1], enabled: true, virtualSurround: false);
            var pausedAfter = backend.PositionMs;
            DebugConsole.Info($"Diagnostic ELYSOUND+ pause: applied={pausedApply.Applied}, position {pausedAt}->{pausedAfter} ms");
            backend.Resume();
            await Task.Delay(300);
            var beforeSeek = backend.PositionMs;
            backend.SeekRelative(1000);
            await Task.Delay(300);
            DebugConsole.Info($"Playback pause/resume/seek diagnostic: before={beforeSeek} ms, after={backend.PositionMs} ms");
        }
    }

    private async Task LogStatsAsync(IVideoBackend expectedBackend)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_session.Backend, expectedBackend)) return;
            var stats = expectedBackend.GetStats();
            DebugConsole.Info(string.Create(CultureInfo.InvariantCulture,
                $"Renderer diagnostic: source={stats.SourceWidth}x{stats.SourceHeight}, output={stats.OutputWidth}x{stats.OutputHeight}, FPS={stats.Fps:0.###}, displayed={stats.DisplayedFrames}, dropped={stats.DroppedFrames}, pipeline={stats.Shaders}"));
        });
    }
}
