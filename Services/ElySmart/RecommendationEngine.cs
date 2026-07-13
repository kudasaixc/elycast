using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed class RecommendationEngine
{
    public (ElySmartConfiguration Configuration, List<ElySmartRecommendation> Recommendations) Recommend(
        HardwareSnapshot hw, BenchmarkProfile profile, IReadOnlyList<BenchmarkResult> results)
    {
        var gpu = hw.Gpus.FirstOrDefault(g => g.Vendor == "NVIDIA") ?? hw.Gpus.FirstOrDefault();
        var strongGpu = gpu is { VramGb: >= 5 } || gpu?.Rtx == true;
        var visualizerFast = results.FirstOrDefault(r => r.Id == "visualizer-particles")?.Score >= 90;
        var refresh = hw.Displays.Count == 0 ? 60 : (int)hw.Displays.Max(d => d.RefreshHz);
        var recommendedVisualizerFps = strongGpu && visualizerFast
            ? refresh >= 240 ? 240 : refresh >= 144 ? 144 : refresh >= 120 ? 120 : 60
            : 60;
        var elyCoreAvailable = ElyFlowRendererInterop.Available && ElyFlowRendererInterop.Preflight(out _) == 0;
        var config = new ElySmartConfiguration
        {
            Renderer = elyCoreAvailable && gpu?.Vendor == "NVIDIA" ? "elycore" : gpu != null || MpvHwndBackend.LocateNative() != null ? "mpv-gpu" : "vlc-bitmap",
            Upscaling = profile.Id == ElySmartWorkload.Anime ? (strongGpu ? "anime4k-hq" : "anime4k-fast") : strongGpu ? "fsrcnnx" : "ewa_lanczossharp",
            RtxVsr = gpu?.Rtx == true && elyCoreAvailable,
            ElyFlow = gpu?.Rtx == true && elyCoreAvailable,
            ElyColor = profile.Id switch { ElySmartWorkload.Anime => "elycolor-anime", ElySmartWorkload.Films => "elycolor-film", ElySmartWorkload.Iptv => "elycolor-sport", _ => "off" },
            ElySound = profile.Id is ElySmartWorkload.Audio or ElySmartWorkload.Films,
            VisualizerFps = recommendedVisualizerFps,
            Particles = strongGpu && visualizerFast ? 128 : 96,
            Parallax = false
        };
        var r = new List<ElySmartRecommendation>();
        Add("VideoBackend", config.Renderer, "Recommended renderer", config.Renderer == "elycore" ? "ELYCORE passed its D3D11/WGL preflight and an NVIDIA GPU is present." : "Selected the best backend whose dependencies were verified.", "More stable presentation", "No isolated added cost", true, 92);
        if (config.RtxVsr) Add("ElyFlowRtxVsrEnabled", "true", "Enable RTX VSR", "RTX GPU, driver, and ELYCORE preflight are compatible. Effectiveness is still verified per media item by the renderer.", "Sharper undersized sources", "+9% estimated GPU, +120 MB VRAM", true, 86);
        Add("UpscaleMethod", config.Upscaling, "Content-adapted upscaling", $"The {profile.Id} profile prioritizes quality at {profile.VideoQuality:P0}, and the machine has {(strongGpu ? "GPU headroom" : "moderate resources")}.", "Higher quality with stability headroom", CapabilityDatabase.Find(config.Upscaling) is { } c ? $"+{c.Gpu:0}% GPU, +{c.VramMb} MB" : "Low cost", true, 82);
        Add("AudioVisualizerTargetFps", config.VisualizerFps.ToString(), "Visualizer frame rate", "Frame rate is bounded by measured performance and the detected display refresh rate.", $"Animation up to {config.VisualizerFps} FPS", config.VisualizerFps > 60 ? "+4% estimated CPU" : "Contained cost", false, 78);
        return (config, r);

        void Add(string setting, string value, string title, string reason, string gain, string cost, bool critical, int confidence) =>
            r.Add(new(setting, value, title, reason, gain, cost, critical, confidence));
    }
}
