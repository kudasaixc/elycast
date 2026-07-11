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
            Log("Début du benchmark ELYSMART."); progress?.Report(new(3, "Détection", "Inventaire matériel et pilotes"));
            report.Hardware = await _hardware.DetectAsync(token);
            report.HardwareFingerprint = HardwareDetector.Fingerprint(report.Hardware);
            Log($"Empreinte machine: {report.HardwareFingerprint}");

            progress?.Report(new(14, "CPU", "Débit SIMD et stabilité du scheduler"));
            report.Results.Add(await CpuBenchmark(token));
            progress?.Report(new(26, "Mémoire", "Bande passante mémoire gérée"));
            report.Results.Add(await MemoryBenchmark(token));
            progress?.Report(new(38, "Stockage", "Écriture et lecture séquentielles temporaires"));
            report.Results.Add(await StorageBenchmark(token));
            progress?.Report(new(50, "GPU", "Préflight des pipelines disponibles"));
            report.Results.AddRange(CapabilityBenchmarks(report.Hardware));
            progress?.Report(new(64, "Audio", "FFT 2048 points et charge d'analyse"));
            report.Results.Add(await FftBenchmark(token));
            progress?.Report(new(76, "Visualiseur", "Simulation particules, palette et animation"));
            report.Results.Add(await VisualizerBenchmark(token));
            AddMediaMatrix(report);

            progress?.Report(new(88, "Recommandations", "Scoring pondéré par usage"));
            var profile = BenchmarkProfiles.Get(workload);
            report.Scores = _scorer.Score(report.Results);
            report.GlobalScore = _scorer.Global(report.Scores, profile);
            report.MeasurementCoveragePercent = (int)Math.Round(report.Scores.Values.Count(v => v > 0) * 100.0 / Math.Max(1, report.Scores.Count));
            report.Rating = QualityScorer.Rating(report.GlobalScore);
            (report.Configuration, report.Recommendations) = _recommendations.Recommend(report.Hardware, profile, report.Results);
            progress?.Report(new(96, "Rapport", "Persistance atomique du résultat"));
            report.Journal = _journal.ToList();
            await SaveAsync(report, token);
            progress?.Report(new(100, "Terminé", $"Score {report.GlobalScore}/100 — {report.Rating}"));
            return report;
        }
        catch (OperationCanceledException) { report.Cancelled = true; Log("Benchmark annulé."); report.Journal = _journal.ToList(); return report; }
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
            new[] { new ElySmartMetric("Débit", ops, "GFLOP/s", MetricProvenance.Measured) }, "ok", "Boucle SIMD vectorisée reproductible");
    }, token);

    private static Task<BenchmarkResult> MemoryBenchmark(CancellationToken token) => Task.Run(() =>
    {
        var a = new byte[128 * 1024 * 1024]; var b = new byte[a.Length]; Random.Shared.NextBytes(a); var sw = Stopwatch.StartNew(); var loops = 0;
        do { token.ThrowIfCancellationRequested(); Buffer.BlockCopy(a, 0, b, 0, a.Length); loops++; } while (sw.Elapsed < TimeSpan.FromMilliseconds(400));
        sw.Stop(); var gbps = a.Length * loops / sw.Elapsed.TotalSeconds / 1e9;
        // 5 GB/s is sufficient for the managed UI/audio path; 12 GB/s gives
        // full margin. This is an ElyCast readiness score, not a RAM leaderboard.
        var score = Math.Clamp((int)Math.Round(45 + gbps / 12.0 * 55), 45, 100);
        return new BenchmarkResult("memory-copy", "Mémoire", score, sw.Elapsed,
            new[] { new ElySmartMetric("Copie", gbps, "GB/s", MetricProvenance.Measured), new ElySmartMetric("Bloc", 128, "Mo", MetricProvenance.Measured) }, "ok", "Copie mémoire séquentielle");
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
            return new("storage-sequential", "Stockage", Math.Clamp((int)(Math.Min(read, write) / 12), 15, 100), sw.Elapsed,
                new[] { new ElySmartMetric("Lecture", read, "MB/s", MetricProvenance.Measured, "Inclut le cache du système de fichiers"), new ElySmartMetric("Écriture", write, "MB/s", MetricProvenance.Measured, "Débit observé par ElyCast") }, "ok", "Fichier temporaire 128 Mo; mesure applicative, pas un benchmark NVMe brut");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static IReadOnlyList<BenchmarkResult> CapabilityBenchmarks(HardwareSnapshot hw)
    {
        var list = new List<BenchmarkResult>(); var mpv = MpvHwndBackend.LocateNative() != null;
        list.Add(Cap("gpu-mpv", "Renderer MPV", mpv, mpv ? "libmpv et pipeline gpu-next disponibles" : "libmpv absent"));
        list.Add(Cap("gpu-vlc", "Renderer VLC", true, "Runtime VLC lié à l'application"));
        var msg = ElyFlowRendererInterop.Available ? "Préflight refusé" : "DLL ELYCORE absente";
        var core = ElyFlowRendererInterop.Available && ElyFlowRendererInterop.Preflight(out msg) == 0;
        list.Add(Cap("elycore-preflight", "Renderer ELYCORE", core, core ? "Préflight D3D11/WGL réussi" : msg));
        var rtx = hw.Gpus.Any(g => g.Rtx); list.Add(Cap("vsr-capability", "RTX VSR", rtx && core, rtx ? "RTX détecté; efficacité à confirmer sur média sous-résolu" : "GPU RTX absent"));
        list.Add(Cap("elyflow-capability", "ELYFLOW", core && ElyFlowService.Probe().FrucCapable, "Probe NvOFFRUC réel"));
        return list;
        static BenchmarkResult Cap(string id, string name, bool ok, string detail) => new(id, name, ok ? 90 : 0, TimeSpan.Zero,
            new[] { new ElySmartMetric("Disponibilité", ok ? 1 : 0, "bool", MetricProvenance.Detected, detail) }, ok ? "ok" : "unavailable", detail);
    }

    private static Task<BenchmarkResult> FftBenchmark(CancellationToken token) => Task.Run(() =>
    {
        const int n = 2048, iterations = 1500; var real = new double[n]; var imag = new double[n]; var sw = Stopwatch.StartNew();
        for (var k = 0; k < iterations; k++) { token.ThrowIfCancellationRequested(); for (var i = 0; i < n; i++) { var angle = 2 * Math.PI * i * (k % 97 + 1) / n; real[i] = Math.Sin(angle); imag[i] = 0; } Fft(real, imag); }
        sw.Stop(); var fps = iterations / sw.Elapsed.TotalSeconds;
        return new BenchmarkResult("fft-2048", "FFT audio", Math.Clamp((int)(fps / 8), 20, 100), sw.Elapsed,
            new[] { new ElySmartMetric("Analyses", fps, "FFT/s", MetricProvenance.Measured), new ElySmartMetric("Taille", n, "points", MetricProvenance.Measured) }, "ok", "FFT radix-2 CPU");
    }, token);

    private static void Fft(double[] r, double[] im) { for (int i = 1, j = 0; i < r.Length; i++) { int bit = r.Length >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (r[i], r[j], im[i], im[j]) = (r[j], r[i], im[j], im[i]); } for (var len = 2; len <= r.Length; len <<= 1) for (var i = 0; i < r.Length; i += len) for (var j = 0; j < len / 2; j++) { var a = -2 * Math.PI * j / len; var c = Math.Cos(a); var s = Math.Sin(a); var ur = r[i + j]; var ui = im[i + j]; var vr = r[i + j + len / 2] * c - im[i + j + len / 2] * s; var vi = r[i + j + len / 2] * s + im[i + j + len / 2] * c; r[i + j] = ur + vr; im[i + j] = ui + vi; r[i + j + len / 2] = ur - vr; im[i + j + len / 2] = ui - vi; } }

    private static Task<BenchmarkResult> VisualizerBenchmark(CancellationToken token) => Task.Run(() =>
    {
        const int frames = 10000, particles = 192; var x = new double[particles]; var y = new double[particles]; var sw = Stopwatch.StartNew(); double sink = 0;
        for (var f = 0; f < frames; f++) { token.ThrowIfCancellationRequested(); for (var p = 0; p < particles; p++) { x[p] += Math.Sin((f + p) * .01) * .2; y[p] += Math.Cos((f - p) * .013) * .2; sink += Math.Sqrt(x[p] * x[p] + y[p] * y[p]); } }
        sw.Stop(); GC.KeepAlive(sink); var rate = frames / sw.Elapsed.TotalSeconds;
        return new BenchmarkResult("visualizer-particles", "Visualiseur et particules", Math.Clamp((int)(rate / 80), 25, 100), sw.Elapsed,
            new[] { new ElySmartMetric("Simulation", rate, "frames/s", MetricProvenance.Measured), new ElySmartMetric("Particules", particles, "", MetricProvenance.Measured) }, "ok", "Charge CPU pure; composition WPF suivie par RuntimeMonitor");
    }, token);

    private static void AddMediaMatrix(BenchmarkReport report)
    {
        foreach (var (id, name) in new[] { ("decode-h264-1080", "Décodage 1080p H.264"), ("decode-hevc-1080", "Décodage 1080p HEVC"), ("decode-hevc-4k", "Décodage 4K HEVC"), ("decode-av1", "Décodage AV1"), ("decode-vp9", "Décodage VP9"), ("shader-anime4k-lite", "Anime4K Lite"), ("shader-anime4k-hq", "Anime4K High"), ("elycolor-pipeline", "ELYCOLOR"), ("audio-elysound", "ELYSOUND+") })
            report.Results.Add(new(id, name, 0, TimeSpan.Zero, new[] { new ElySmartMetric("Mesure", null, "", MetricProvenance.Skipped, "Échantillon déterministe absent; aucune lecture utilisateur ne sera interrompue") }, "skipped", "Nécessite le pack média de benchmark ELYSMART"));
    }
}
