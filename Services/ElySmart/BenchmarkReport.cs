using System.Text.Json.Serialization;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public enum MetricProvenance { Measured, Detected, Estimated, Unavailable, Skipped }
public enum ElySmartWorkload { Iptv, Films, Series, Anime, Audio, Mixed }

public sealed record ElySmartMetric(string Name, double? Value, string Unit, MetricProvenance Provenance, string Detail = "");
public sealed record BenchmarkResult(string Id, string Name, int Score, TimeSpan Duration,
    IReadOnlyList<ElySmartMetric> Metrics, string Status, string Detail);
public sealed record ElySmartRecommendation(string Setting, string Value, string Title, string Reason,
    string ExpectedGain, string EstimatedCost, bool Critical, int Confidence);

public sealed class ElySmartConfiguration
{
    public string Renderer { get; set; } = "mpv-gpu";
    public string Upscaling { get; set; } = "none";
    public bool ElyFlow { get; set; }
    public bool RtxVsr { get; set; }
    public string ElyColor { get; set; } = "off";
    public bool ElySound { get; set; }
    public int VisualizerFps { get; set; } = 60;
    public int Particles { get; set; } = 96;
    public bool Parallax { get; set; }
    public double Blur { get; set; } = 45.6;
}

public sealed class BenchmarkReport
{
    public int SchemaVersion { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public ElySmartWorkload Profile { get; set; } = ElySmartWorkload.Mixed;
    public HardwareSnapshot Hardware { get; set; } = new();
    public string HardwareFingerprint { get; set; } = "";
    public List<BenchmarkResult> Results { get; set; } = new();
    public Dictionary<string, int> Scores { get; set; } = new();
    public int GlobalScore { get; set; }
    public int MeasurementCoveragePercent { get; set; }
    public string Rating { get; set; } = "Non évalué";
    public ElySmartConfiguration Configuration { get; set; } = new();
    public List<ElySmartRecommendation> Recommendations { get; set; } = new();
    public List<string> Journal { get; set; } = new();
    public bool Cancelled { get; set; }
}

public sealed record BenchmarkProgress(int Percent, string Stage, string Detail);
