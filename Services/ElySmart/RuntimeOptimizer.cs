using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record OptimizationChange(string Setting, string Before, string After, string Reason);

public sealed class RuntimeOptimizer
{
    public OptimizationChange? ReduceDecorativeLoad(Settings s, HealthIssue issue)
    {
        if (s.AudioBackgroundMouseParallax) { s.AudioBackgroundMouseParallax = false; return new("Parallaxe", "Activée", "Désactivée", issue.Detail); }
        if (s.AudioParticleCount > 72) { var before = s.AudioParticleCount; s.AudioParticleCount = Math.Max(72, before - 24); return new("Particules", before.ToString(), s.AudioParticleCount.ToString(), issue.Detail); }
        if (s.AudioVisualizerTargetFps > 60) { var before = s.AudioVisualizerTargetFps; s.AudioVisualizerTargetFps = before > 120 ? 120 : 60; return new("FPS visualiseur", before.ToString(), s.AudioVisualizerTargetFps.ToString(), issue.Detail); }
        if (s.AudioBackgroundBlur > 30) { var before = s.AudioBackgroundBlur; s.AudioBackgroundBlur = Math.Max(30, before - 6); return new("Flou", before.ToString("0"), s.AudioBackgroundBlur.ToString("0"), issue.Detail); }
        if (s.AudioBackgroundSlowPan) { s.AudioBackgroundSlowPan = false; return new("Déplacement du fond", "Activé", "Désactivé", issue.Detail); }
        return null;
    }
}
