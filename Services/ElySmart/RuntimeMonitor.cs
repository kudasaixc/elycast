using System.Diagnostics;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record PlaybackTelemetry(double Fps, double FrameTimeMs, long DroppedFrames, double VisualizerFps);

public sealed class RuntimeMonitor : IDisposable
{
    public event EventHandler<PerformanceSample>? Sampled;
    private readonly Func<PlaybackTelemetry> _telemetry;
    private readonly PerformanceHistory _history;
    private readonly HealthAnalyzer _analyzer;
    private readonly NotificationEngine _notifications;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private TimeSpan _lastCpu; private DateTimeOffset _lastAt;
    public RuntimeMonitor(Func<PlaybackTelemetry> telemetry, PerformanceHistory history, HealthAnalyzer analyzer, NotificationEngine notifications)
    { _telemetry = telemetry; _history = history; _analyzer = analyzer; _notifications = notifications; }
    public void Start() { if (_loop == null) { var p = Process.GetCurrentProcess(); _lastCpu = p.TotalProcessorTime; _lastAt = DateTimeOffset.Now; _loop = LoopAsync(_cts.Token); } }
    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token))
        {
            var before = Stopwatch.GetTimestamp();
            var t = await System.Windows.Application.Current.Dispatcher.InvokeAsync(_telemetry, System.Windows.Threading.DispatcherPriority.Background);
            var uiMs = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            var now = DateTimeOffset.Now; using var p = Process.GetCurrentProcess(); var cpu = p.TotalProcessorTime; var wall = Math.Max(.001, (now - _lastAt).TotalSeconds);
            var cpuPct = Math.Clamp((cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100, 0, 100); _lastCpu = cpu; _lastAt = now;
            var gc = GC.GetGCMemoryInfo(); var ramPct = gc.TotalAvailableMemoryBytes > 0 ? p.WorkingSet64 / (double)gc.TotalAvailableMemoryBytes * 100 : 0;
            var sample = new PerformanceSample(now, cpuPct, ramPct, p.PrivateMemorySize64 / 1048576d, t.Fps, t.FrameTimeMs, t.DroppedFrames, uiMs, t.VisualizerFps);
            _history.Add(sample); Sampled?.Invoke(this, sample);
            if (now.Second % 10 == 0) _notifications.Evaluate(_analyzer.Analyze(_history));
        }
    }
    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}
