using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record OptimizationChange(string Setting, string Before, string After, string Reason);

public sealed class RuntimeOptimizer
{
    public OptimizationChange? ReduceDecorativeLoad(Settings s, HealthIssue issue)
    {
        if (s.AudioBackgroundMouseParallax) { s.AudioBackgroundMouseParallax = false; return new("Parallax", "Enabled", "Disabled", issue.Detail); }
        if (s.AudioParticleCount > 72) { var before = s.AudioParticleCount; s.AudioParticleCount = Math.Max(72, before - 24); return new("Particles", before.ToString(), s.AudioParticleCount.ToString(), issue.Detail); }
        if (s.AudioVisualizerTargetFps > 60) { var before = s.AudioVisualizerTargetFps; s.AudioVisualizerTargetFps = before > 120 ? 120 : 60; return new("Visualizer FPS", before.ToString(), s.AudioVisualizerTargetFps.ToString(), issue.Detail); }
        if (s.AudioBackgroundBlur > 30) { var before = s.AudioBackgroundBlur; s.AudioBackgroundBlur = Math.Max(30, before - 6); return new("Blur", before.ToString("0"), s.AudioBackgroundBlur.ToString("0"), issue.Detail); }
        if (s.AudioBackgroundSlowPan) { s.AudioBackgroundSlowPan = false; return new("Background movement", "Enabled", "Disabled", issue.Detail); }
        return null;
    }
}
