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
        if (avg30.PlaybackFps > 0 && avg30.PlaybackFps < 27) issues.Add(new(HealthIssueKind.SustainedLowFps, "Baisse durable de fluidité", $"Cadence moyenne {avg30.PlaybackFps:0.0} FPS sur 30 s.", "Réduire progressivement les effets décoratifs", 3));
        if (avg30.ProcessCpu > 85) issues.Add(new(HealthIssueKind.HighCpu, "CPU fortement sollicité", $"ElyCast utilise {avg30.ProcessCpu:0}% du CPU en moyenne.", "Réduire visualiseur et shaders avec consentement", 2));
        if (avg30.GpuPercent > 95) issues.Add(new(HealthIssueKind.HighGpu, "GPU saturé", $"Charge GPU {avg30.GpuPercent:0}% sur 30 s.", "Désactiver parallaxe puis réduire les particules", 2));
        if (avg5?.PrivateRamMb > 1800) issues.Add(new(HealthIssueKind.HighMemory, "Mémoire élevée", $"Mémoire privée moyenne {avg5.PrivateRamMb:0} Mo.", "Libérer les caches graphiques non utilisés", 2));
        if (avg30.UiDelayMs > 45) issues.Add(new(HealthIssueKind.UiStall, "Interface ralentie", $"Retard UI moyen {avg30.UiDelayMs:0} ms.", "Réduire la cadence du visualiseur", 2));
        return issues;
    }
}
