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
        Add("VideoBackend", config.Renderer, "Renderer recommandé", config.Renderer == "elycore" ? "ELYCORE a réussi son préflight D3D11/WGL et le GPU NVIDIA est présent." : "Choix du meilleur backend dont les dépendances ont été vérifiées.", "Meilleure stabilité de présentation", "Aucun coût ajouté isolément", true, 92);
        if (config.RtxVsr) Add("ElyFlowRtxVsrEnabled", "true", "Activer RTX VSR", "GPU RTX, pilote et préflight ELYCORE compatibles. L'efficacité reste vérifiée média par média par le renderer.", "Netteté accrue sur sources sous la résolution d'écran", "+9 % GPU estimés, +120 Mo VRAM", true, 86);
        Add("UpscaleMethod", config.Upscaling, "Upscaling adapté au contenu", $"Le profil {profile.Id} donne la priorité à la qualité {profile.VideoQuality:P0} et la machine dispose de {(strongGpu ? "marge GPU" : "ressources modérées")}.", "Qualité accrue avec marge de stabilité", CapabilityDatabase.Find(config.Upscaling) is { } c ? $"+{c.Gpu:0}% GPU, +{c.VramMb} Mo" : "Coût faible", true, 82);
        Add("AudioVisualizerTargetFps", config.VisualizerFps.ToString(), "Cadence du visualiseur", "Cadence bornée par la puissance mesurée et le taux de rafraîchissement détecté.", $"Animation jusqu'à {config.VisualizerFps} FPS", config.VisualizerFps > 60 ? "+4 % CPU estimés" : "Coût contenu", false, 78);
        return (config, r);

        void Add(string setting, string value, string title, string reason, string gain, string cost, bool critical, int confidence) =>
            r.Add(new(setting, value, title, reason, gain, cost, critical, confidence));
    }
}
