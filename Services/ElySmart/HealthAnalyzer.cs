namespace Elysium_Cast_IPTV.Services.ElySmart;

public enum HealthIssueKind { SustainedLowFps, HighCpu, HighGpu, HighMemory, UiStall, DroppedFrames }
public sealed record HealthIssue(HealthIssueKind Kind, string Title, string Detail, string SuggestedAction, int Severity);

public sealed class HealthAnalyzer
{
    public IReadOnlyList<HealthIssue> Analyze(PerformanceHistory history)
    {
        var avg30 = history.Average(TimeSpan.FromSeconds(30)); var avg5 = history.Average(TimeSpan.FromMinutes(5));
        if (avg30 == null) return Array.Empty<HealthIssue>();
        var issues = new List<HealthIssue>();
        if (avg30.PlaybackFps > 0 && avg30.PlaybackFps < 27) issues.Add(new(HealthIssueKind.SustainedLowFps, "Sustained loss of smoothness", $"Average frame rate {avg30.PlaybackFps:0.0} FPS over 30 s.", "Reduce decorative effects progressively", 3));
        if (avg30.ProcessCpu > 85) issues.Add(new(HealthIssueKind.HighCpu, "High CPU load", $"ElyCast uses an average of {avg30.ProcessCpu:0}% CPU.", "Reduce the visualizer and shaders with consent", 2));
        if (avg30.GpuPercent > 95) issues.Add(new(HealthIssueKind.HighGpu, "GPU saturated", $"GPU load {avg30.GpuPercent:0}% over 30 s.", "Disable parallax, then reduce particles", 2));
        if (avg5?.PrivateRamMb > 1800) issues.Add(new(HealthIssueKind.HighMemory, "High memory use", $"Average private memory {avg5.PrivateRamMb:0} MB.", "Release unused graphics caches", 2));
        if (avg30.UiDelayMs > 45) issues.Add(new(HealthIssueKind.UiStall, "Slow interface", $"Average UI delay {avg30.UiDelayMs:0} ms.", "Reduce the visualizer frame rate", 2));
        return issues;
    }
}
