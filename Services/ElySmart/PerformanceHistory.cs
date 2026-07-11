namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record PerformanceSample(DateTimeOffset At, double ProcessCpu, double SystemRamPercent, double PrivateRamMb,
    double PlaybackFps, double FrameTimeMs, long DroppedFrames, double UiDelayMs, double VisualizerFps, double GpuPercent = -1, double VramMb = -1);

public sealed class PerformanceHistory
{
    private readonly object _sync = new();
    private readonly Queue<PerformanceSample> _samples = new();
    public void Add(PerformanceSample sample) { lock (_sync) { _samples.Enqueue(sample); while (_samples.Count > 300) _samples.Dequeue(); } }
    public IReadOnlyList<PerformanceSample> Snapshot(TimeSpan period) { lock (_sync) { var cutoff = DateTimeOffset.Now - period; return _samples.Where(s => s.At >= cutoff).ToArray(); } }
    public PerformanceSample? Average(TimeSpan period)
    {
        var s = Snapshot(period); if (s.Count == 0) return null;
        return new(DateTimeOffset.Now, s.Average(x => x.ProcessCpu), s.Average(x => x.SystemRamPercent), s.Average(x => x.PrivateRamMb),
            s.Average(x => x.PlaybackFps), s.Average(x => x.FrameTimeMs), (long)s.Average(x => x.DroppedFrames), s.Average(x => x.UiDelayMs),
            s.Average(x => x.VisualizerFps), s.Where(x => x.GpuPercent >= 0).DefaultIfEmpty().Average(x => x?.GpuPercent ?? -1),
            s.Where(x => x.VramMb >= 0).DefaultIfEmpty().Average(x => x?.VramMb ?? -1));
    }
}
