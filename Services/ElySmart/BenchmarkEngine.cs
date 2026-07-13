using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed class BenchmarkEngine
{
    private readonly HardwareDetector _hardware = new();
    private readonly QualityScorer _scorer = new();
    private readonly RecommendationEngine _recommendations = new();
    public static string ReportPath => Path.Combine(StateStore.FolderPath, "elysmart-report.json");

    public async Task<BenchmarkReport> RunAsync(ElySmartWorkload workload, IProgress<BenchmarkProgress>? progress, CancellationToken token)
    {
        _journal.Clear();
        var report = new BenchmarkReport { Profile = workload };
        try
        {
            Log("Starting ELYSMART benchmark."); progress?.Report(new(3, "Detection", "Hardware and driver inventory"));
            report.Hardware = await _hardware.DetectAsync(token);
            report.HardwareFingerprint = HardwareDetector.Fingerprint(report.Hardware);
            Log($"Machine fingerprint: {report.HardwareFingerprint}");

            progress?.Report(new(14, "CPU", "SIMD throughput and scheduler stability"));
            report.Results.Add(await CpuBenchmark(token));
            progress?.Report(new(26, "Memory", "Managed memory bandwidth"));
            report.Results.Add(await MemoryBenchmark(token));
            progress?.Report(new(38, "Storage", "Temporary sequential write and read"));
            report.Results.Add(await StorageBenchmark(token));
            progress?.Report(new(50, "GPU", "Available pipeline preflight"));
            report.Results.AddRange(CapabilityBenchmarks(report.Hardware));
            progress?.Report(new(64, "Audio", "2048-point FFT and analysis load"));
            report.Results.Add(await FftBenchmark(token));
            progress?.Report(new(76, "Visualizer", "Particle, palette, and animation simulation"));
            report.Results.Add(await VisualizerBenchmark(token));
            AddMediaMatrix(report);

            progress?.Report(new(88, "Recommendations", "Usage-weighted scoring"));
            var profile = BenchmarkProfiles.Get(workload);
            report.Scores = _scorer.Score(report.Results);
            report.GlobalScore = _scorer.Global(report.Scores, profile);
            report.MeasurementCoveragePercent = (int)Math.Round(report.Scores.Values.Count(v => v > 0) * 100.0 / Math.Max(1, report.Scores.Count));
            report.Rating = QualityScorer.Rating(report.GlobalScore);
            (report.Configuration, report.Recommendations) = _recommendations.Recommend(report.Hardware, profile, report.Results);
            progress?.Report(new(96, "Report", "Atomic result persistence"));
            report.Journal = _journal.ToList();
            await SaveAsync(report, token);
            progress?.Report(new(100, "Complete", $"Score {report.GlobalScore}/100: {report.Rating}"));
            return report;
        }
        catch (OperationCanceledException) { report.Cancelled = true; Log("Benchmark canceled."); report.Journal = _journal.ToList(); return report; }
    }

    private readonly List<string> _journal = new();
    private void Log(string message) { var line = $"{DateTimeOffset.Now:O} {message}"; _journal.Add(line); DebugConsole.Info("ELYSMART: " + message); }

    public static BenchmarkReport? LoadLast()
    {
        try { return File.Exists(ReportPath) ? JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(ReportPath)) : null; } catch { return null; }
    }
    private static async Task SaveAsync(BenchmarkReport report, CancellationToken token)
    {
        Directory.CreateDirectory(StateStore.FolderPath); var temp = ReportPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
        File.Move(temp, ReportPath, true);
    }

    private static Task<BenchmarkResult> CpuBenchmark(CancellationToken token) => Task.Run(() =>
    {
        const int count = 4_000_000; var data = new float[count]; Array.Fill(data, .99991f); var sw = Stopwatch.StartNew();
        var width = Vector<float>.Count; var acc = Vector<float>.Zero; var passes = 0;
        do { for (var pass = 0; pass < 12; pass++) { for (var i = 0; i <= count - width; i += width) acc += new Vector<float>(data, i) * new Vector<float>(1.00001f); passes++; } token.ThrowIfCancellationRequested(); } while (sw.Elapsed < TimeSpan.FromMilliseconds(400));
        sw.Stop(); GC.KeepAlive(acc); var ops = count * passes * 2d / sw.Elapsed.TotalSeconds / 1e9;
        // ElyCast's managed coordination/FFT path is comfortable above 2
        // GFLOP/s single-thread SIMD and has full headroom above 8 GFLOP/s.
        var score = Math.Clamp((int)Math.Round(45 + ops / 8.0 * 55), 45, 100);
        return new BenchmarkResult("cpu-simd", "CPU SIMD", score, sw.Elapsed,
            new[] { new ElySmartMetric("Throughput", ops, "GFLOP/s", MetricProvenance.Measured) }, "ok", "Reproducible vectorized SIMD loop");
    }, token);

    private static Task<BenchmarkResult> MemoryBenchmark(CancellationToken token) => Task.Run(() =>
    {
        var a = new byte[128 * 1024 * 1024]; var b = new byte[a.Length]; Random.Shared.NextBytes(a); var sw = Stopwatch.StartNew(); var loops = 0;
        do { token.ThrowIfCancellationRequested(); Buffer.BlockCopy(a, 0, b, 0, a.Length); loops++; } while (sw.Elapsed < TimeSpan.FromMilliseconds(400));
        sw.Stop(); var gbps = a.Length * loops / sw.Elapsed.TotalSeconds / 1e9;
        // 5 GB/s is sufficient for the managed UI/audio path; 12 GB/s gives
        // full margin. This is an ElyCast readiness score, not a RAM leaderboard.
        var score = Math.Clamp((int)Math.Round(45 + gbps / 12.0 * 55), 45, 100);
        return new BenchmarkResult("memory-copy", "Memory", score, sw.Elapsed,
            new[] { new ElySmartMetric("Copy", gbps, "GB/s", MetricProvenance.Measured), new ElySmartMetric("Block", 128, "MB", MetricProvenance.Measured) }, "ok", "Sequential memory copy");
    }, token);

    private static async Task<BenchmarkResult> StorageBenchmark(CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"elysmart-{Guid.NewGuid():N}.bin"); var buffer = new byte[4 * 1024 * 1024]; Random.Shared.NextBytes(buffer); const int loops = 32;
        try
        {
            var sw = Stopwatch.StartNew(); await using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                for (var i = 0; i < loops; i++) await f.WriteAsync(buffer, token);
            sw.Stop(); var write = buffer.Length * loops / sw.Elapsed.TotalSeconds / 1e6; sw.Restart();
            await using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan)) while (await f.ReadAsync(buffer, token) > 0) { }
            sw.Stop(); var read = buffer.Length * loops / sw.Elapsed.TotalSeconds / 1e6;
            return new("storage-sequential", "Storage", Math.Clamp((int)(Math.Min(read, write) / 12), 15, 100), sw.Elapsed,
                new[] { new ElySmartMetric("Read", read, "MB/s", MetricProvenance.Measured, "Includes the file-system cache"), new ElySmartMetric("Write", write, "MB/s", MetricProvenance.Measured, "Throughput observed by ElyCast") }, "ok", "128 MB temporary file; application measurement, not a raw NVMe benchmark");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static IReadOnlyList<BenchmarkResult> CapabilityBenchmarks(HardwareSnapshot hw)
    {
        var list = new List<BenchmarkResult>(); var mpv = MpvHwndBackend.LocateNative() != null;
        list.Add(Cap("gpu-mpv", "MPV renderer", mpv, mpv ? "libmpv and gpu-next pipeline available" : "libmpv missing"));
        list.Add(Cap("gpu-vlc", "VLC renderer", true, "VLC runtime linked to the application"));
        var msg = ElyFlowRendererInterop.Available ? "Preflight rejected" : "ELYCORE DLL missing";
        var core = ElyFlowRendererInterop.Available && ElyFlowRendererInterop.Preflight(out msg) == 0;
        list.Add(Cap("elycore-preflight", "ELYCORE renderer", core, core ? "D3D11/WGL preflight passed" : msg));
        var rtx = hw.Gpus.Any(g => g.Rtx); list.Add(Cap("vsr-capability", "RTX VSR", rtx && core, rtx ? "RTX detected; effectiveness must be confirmed on undersized media" : "RTX GPU missing"));
        list.Add(Cap("elyflow-capability", "ELYFLOW", core && ElyFlowService.Probe().FrucCapable, "Real NvOFFRUC probe"));
        return list;
        static BenchmarkResult Cap(string id, string name, bool ok, string detail) => new(id, name, ok ? 90 : 0, TimeSpan.Zero,
            new[] { new ElySmartMetric("Availability", ok ? 1 : 0, "bool", MetricProvenance.Detected, detail) }, ok ? "ok" : "unavailable", detail);
    }

    private static Task<BenchmarkResult> FftBenchmark(CancellationToken token) => Task.Run(() =>
    {
        const int n = 2048, iterations = 1500; var real = new double[n]; var imag = new double[n]; var sw = Stopwatch.StartNew();
        for (var k = 0; k < iterations; k++) { token.ThrowIfCancellationRequested(); for (var i = 0; i < n; i++) { var angle = 2 * Math.PI * i * (k % 97 + 1) / n; real[i] = Math.Sin(angle); imag[i] = 0; } Fft(real, imag); }
        sw.Stop(); var fps = iterations / sw.Elapsed.TotalSeconds;
        return new BenchmarkResult("fft-2048", "Audio FFT", Math.Clamp((int)(fps / 8), 20, 100), sw.Elapsed,
            new[] { new ElySmartMetric("Analyses", fps, "FFT/s", MetricProvenance.Measured), new ElySmartMetric("Size", n, "points", MetricProvenance.Measured) }, "ok", "CPU radix-2 FFT");
    }, token);

    private static void Fft(double[] r, double[] im) { for (int i = 1, j = 0; i < r.Length; i++) { int bit = r.Length >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (r[i], r[j], im[i], im[j]) = (r[j], r[i], im[j], im[i]); } for (var len = 2; len <= r.Length; len <<= 1) for (var i = 0; i < r.Length; i += len) for (var j = 0; j < len / 2; j++) { var a = -2 * Math.PI * j / len; var c = Math.Cos(a); var s = Math.Sin(a); var ur = r[i + j]; var ui = im[i + j]; var vr = r[i + j + len / 2] * c - im[i + j + len / 2] * s; var vi = r[i + j + len / 2] * s + im[i + j + len / 2] * c; r[i + j] = ur + vr; im[i + j] = ui + vi; r[i + j + len / 2] = ur - vr; im[i + j + len / 2] = ui - vi; } }

    private static Task<BenchmarkResult> VisualizerBenchmark(CancellationToken token) => Task.Run(() =>
    {
        const int frames = 10000, particles = 192; var x = new double[particles]; var y = new double[particles]; var sw = Stopwatch.StartNew(); double sink = 0;
        for (var f = 0; f < frames; f++) { token.ThrowIfCancellationRequested(); for (var p = 0; p < particles; p++) { x[p] += Math.Sin((f + p) * .01) * .2; y[p] += Math.Cos((f - p) * .013) * .2; sink += Math.Sqrt(x[p] * x[p] + y[p] * y[p]); } }
        sw.Stop(); GC.KeepAlive(sink); var rate = frames / sw.Elapsed.TotalSeconds;
        return new BenchmarkResult("visualizer-particles", "Visualizer and particles", Math.Clamp((int)(rate / 80), 25, 100), sw.Elapsed,
            new[] { new ElySmartMetric("Simulation", rate, "frames/s", MetricProvenance.Measured), new ElySmartMetric("Particles", particles, "", MetricProvenance.Measured) }, "ok", "Pure CPU load; WPF composition tracked by RuntimeMonitor");
    }, token);

    private static void AddMediaMatrix(BenchmarkReport report)
    {
        foreach (var (id, name) in new[] { ("decode-h264-1080", "1080p H.264 decode"), ("decode-hevc-1080", "1080p HEVC decode"), ("decode-hevc-4k", "4K HEVC decode"), ("decode-av1", "AV1 decode"), ("decode-vp9", "VP9 decode"), ("shader-anime4k-lite", "Anime4K Lite"), ("shader-anime4k-hq", "Anime4K High"), ("elycolor-pipeline", "ELYCOLOR"), ("audio-elysound", "ELYSOUND+") })
            report.Results.Add(new(id, name, 0, TimeSpan.Zero, new[] { new ElySmartMetric("Measurement", null, "", MetricProvenance.Skipped, "No deterministic sample; user playback will never be interrupted") }, "skipped", "Requires the ELYSMART benchmark media pack"));
    }
}
