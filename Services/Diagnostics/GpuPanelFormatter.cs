using System.Globalization;
using System.Text;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Diagnostics;

/// <summary>
/// The "gpu" console panel: a snapshot of the GPU video pipeline (decode,
/// VSR, FRUC, renderer, cadence and frame counters).
/// </summary>
public static class GpuPanelFormatter
{
    public static string Build(IVideoBackend? backend, Settings settings)
    {
        const string bar = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        static string Row(string label, string value) => (LocalizationService.T(label) + " ").PadRight(18, '.') + " " + LocalizationService.T(value);

        var sb = new StringBuilder();
        sb.AppendLine(bar);
        sb.AppendLine("ELYFLOW GPU");

        var flow = ElyFlowService.Probe();
        sb.AppendLine(string.IsNullOrWhiteSpace(flow.GpuName) ? LocalizationService.T("Non-NVIDIA GPU / not detected") : flow.GpuName);
        sb.AppendLine(Row("Driver", string.IsNullOrWhiteSpace(flow.DriverVersion)
            ? "-" : HardwareProbe.NvidiaReleaseFromWmi(flow.DriverVersion)));

        if (backend is not MpvHwndBackend mpv || !backend.HasMedia)
        {
            sb.AppendLine(Row("Pipeline", backend == null ? "no backend" : backend.Name));
            sb.AppendLine(Row("Playback", "none: start media, then type 'gpu' again"));
            sb.Append(bar);
            return sb.ToString();
        }

        var stats = mpv.GetStats();
        var hwdec = mpv.GetOption("hwdec-current");
        var decode = string.IsNullOrWhiteSpace(hwdec) || hwdec == "no"
            ? "software (CPU)"
            : flow.NvidiaGpu ? $"NVDEC ({hwdec})" : hwdec;
        sb.AppendLine(Row("Decode", decode));

        var native = mpv.IsElyCoreRenderer ? ElyFlowRendererInterop.GetState() : default;
        string upscaler;
        if (mpv.IsElyCoreRenderer && native.VsrEffective != 0 && native.VsrInputWidth > 0)
        {
            var ratio = Math.Min(native.VsrContentWidth / (double)native.VsrInputWidth,
                                 native.VsrContentHeight / (double)native.VsrInputHeight);
            upscaler = $"RTX VSR ×{ratio:0.0#} ({native.VsrInputWidth}×{native.VsrInputHeight} → {native.VsrContentWidth}×{native.VsrContentHeight})";
        }
        else if (mpv.IsElyCoreRenderer)
        {
            upscaler = native.VsrRequested != 0
                ? LocalizationService.Format("RTX VSR not effective (available={0}, code 0x{1:X8})", native.VsrAvailable, unchecked((uint)native.LastVsrStatus))
                : "off";
        }
        else
        {
            var vf = mpv.GetOption("vf");
            upscaler = vf.Contains("scaling-mode=nvidia", StringComparison.OrdinalIgnoreCase)
                ? "RTX VSR (d3d11vpp)"
                : vf.Contains("d3d11vpp", StringComparison.OrdinalIgnoreCase) ? "VPP GPU (d3d11vpp)"
                : settings.UpscaleMethod == "none" ? "off" : settings.UpscaleMethod;
        }
        sb.AppendLine(Row("Upscaler", upscaler));

        var frucActive = mpv.IsElyCoreRenderer && native.FrucInitialized != 0 && native.FramesInterpolated > 0;
        sb.AppendLine(Row("Interpolation", frucActive
            ? "FRUC ×2"
            : mpv.IsElyCoreRenderer && settings.ElyFlowEnabled
                ? LocalizationService.Format("FRUC waiting (code {0})", native.LastFrucStatus)
                : "off"));
        sb.AppendLine(Row("Renderer", mpv.IsElyCoreRenderer
            ? "D3D11 (ELYCORE, swapchain DXGI)"
            : "D3D11 (mpv gpu-next)"));

        var srcFps = mpv.IsElyCoreRenderer && native.SourceFps > 0 ? native.SourceFps : stats.Fps;
        var outFps = frucActive ? srcFps * 2.0 : srcFps;
        sb.AppendLine(Row("Source", $"{stats.SourceWidth}×{stats.SourceHeight} @ {srcFps:0.###} FPS"));
        sb.AppendLine(Row("Output", $"{stats.OutputWidth}×{stats.OutputHeight} @ {outFps:0.###} FPS"));

        sb.AppendLine();
        sb.AppendLine("GPU time");
        if (mpv.IsElyCoreRenderer)
        {
            // Native timing: rolling mpv+FRUC+Present cost per frame and the
            // maximum observed during the session.
            sb.AppendLine(Row("Frame (average)", native.AverageWorkMs > 0 ? $"{native.AverageWorkMs:0.0} ms" : "-"));
            sb.AppendLine(Row("Frame (peak)", native.MaxWorkMs > 0 ? $"{native.MaxWorkMs:0.0} ms" : "-"));
            sb.AppendLine(Row("Budget", srcFps > 0 ? LocalizationService.Format("{0:0.0} ms (half frame)", 500.0 / srcFps) : "-"));
        }
        else
        {
            var displayFps = mpv.GetOption("estimated-display-fps");
            sb.AppendLine(Row("Presentation", string.IsNullOrWhiteSpace(displayFps)
                ? "managed by mpv (gpu-next)"
                : LocalizationService.Format("managed by mpv (~{0} Hz)", displayFps)));
        }

        sb.AppendLine();
        sb.AppendLine("Frames");
        if (mpv.IsElyCoreRenderer)
        {
            sb.AppendLine(Row("Decoded", native.FramesRendered.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Interpolated", native.FramesInterpolated.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Presented", native.FramesPresented.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Dropped", stats.DroppedFrames.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Late", native.LatePresents.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            sb.AppendLine(Row("Presented", stats.DisplayedFrames.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Dropped", stats.DroppedFrames.ToString(CultureInfo.InvariantCulture)));
        }
        sb.Append(bar);
        return sb.ToString();
    }
}
