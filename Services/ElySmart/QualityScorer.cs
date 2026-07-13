namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed class QualityScorer
{
    public Dictionary<string, int> Score(IReadOnlyList<BenchmarkResult> results)
    {
        int Group(params string[] prefixes)
        {
            var values = results.Where(r => prefixes.Any(p => r.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                r.Status == "ok" && r.Metrics.Any(m => m.Provenance == MetricProvenance.Measured)).Select(r => r.Score).ToArray();
            return values.Length == 0 ? 0 : (int)Math.Round(values.Average());
        }
        return new()
        {
            ["CPU"] = Group("cpu"), ["GPU"] = Group("gpu", "elycore", "vsr", "elyflow"),
            ["Decode"] = Group("decode"), ["Shaders"] = Group("shader"),
            ["ELYCOLOR"] = Group("elycolor"), ["ELYFLOW"] = Group("elyflow"),
            ["Audio"] = Group("audio", "fft"), ["Visualizer"] = Group("visualizer")
        };
    }

    public int Global(IReadOnlyDictionary<string, int> scores, BenchmarkProfile p)
    {
        // Missing/Skipped domains are unknown, not mediocre. Renormalize the
        // workload weights over domains that contain genuine measurements.
        var weighted = new (string Name, double Weight)[]
        {
            ("CPU", p.Efficiency), ("GPU", (p.VideoQuality + p.Fluidity) / 2),
            ("Decode", p.Stability), ("Shaders", p.VideoQuality / 2),
            ("Audio", p.AudioQuality), ("Visualizer", .08),
            ("ELYCOLOR", p.VideoQuality / 4), ("ELYFLOW", p.Fluidity / 3)
        };
        var available = weighted.Where(x => scores.GetValueOrDefault(x.Name) > 0).ToArray();
        if (available.Length == 0) return 0;
        var totalWeight = available.Sum(x => x.Weight);
        return Math.Clamp((int)Math.Round(available.Sum(x => scores[x.Name] * x.Weight) / totalWeight), 0, 100);
    }

    public static string Rating(int score) => score >= 92 ? "Excellent" : score >= 80 ? "Very good" : score >= 65 ? "Good" : score >= 48 ? "Fair" : "Limited";
}
