using System.Globalization;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Playback;

/// <summary>Input for the audio statistics panel (local audio playback).</summary>
public sealed record AudioStatsSnapshot(
    string? BackendName,
    string Codec,
    string SampleRate,
    string Channels,
    string Bitrate,
    bool HasAnalysis,
    double AnalysisRateHz,
    double EstimatedBpm,
    bool AudioCoreActive,
    double VisualFps,
    double RenderMs,
    int TargetFps,
    bool VSync,
    double Bass,
    double Energy,
    bool IsPlaying);

/// <summary>Pure text formatting for the player HUD and statistics panel.</summary>
public static class PlaybackText
{
    public const long SkipStepMs = 15_000;

    public static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// Skip buttons appear only when they can do useful work: no "back" trap
    /// at the very beginning and no "forward" in the last step.
    /// </summary>
    public static (bool Back, bool Forward) SkipButtons(bool seekVisible, long lengthMs, long positionMs)
    {
        if (!seekVisible || lengthMs <= 0) return (false, false);
        return (positionMs >= SkipStepMs, positionMs <= lengthMs - SkipStepMs);
    }

    public static string NowPlayingSubtitle(PlayItem item)
    {
        var kindLabel = LocalizationService.T(item.KindLabel);
        return string.IsNullOrWhiteSpace(item.CategoryName) ? kindLabel : $"{kindLabel} · {item.CategoryName}";
    }

    public static string VideoStats(VideoStats st, string elyColorName)
    {
        // Upscale factor: output (display) vs source (decoded) height.
        var ratio = st.SourceHeight > 0 ? (double)st.OutputHeight / st.SourceHeight : 0;
        string upscale;
        if (ratio <= 0) upscale = "-";
        else if (ratio > 1.02) upscale = $"▲ UPSCALE x{ratio:0.0#}  ({st.Scaler})";
        else if (ratio < 0.98) upscale = $"▼ downscale x{ratio:0.0#}  ({st.Scaler})";
        else upscale = $"1:1 ({LocalizationService.T("native")}, {st.Scaler})";

        // The AI chain either upscales (display > source) or falls back to
        // its CAS/NVSharpen sharpening pass - make that visible. A GPU
        // pre-upscale alone ("RTX VSR ×2") carries no GLSL chain to label.
        var gpuOnly = !st.Shaders.Contains(" + ", StringComparison.Ordinal) &&
                      (st.Shaders.StartsWith("RTX VSR", StringComparison.Ordinal) || st.Shaders.StartsWith("VPP GPU", StringComparison.Ordinal));
        var shaders = string.IsNullOrEmpty(st.Shaders)
            ? LocalizationService.T("none (mpv scaler)")
            : gpuOnly ? st.Shaders
            : st.Shaders + LocalizationService.T(ratio > 1.02 ? "  [AI upscale active]" : "  [sharpening only, display ≤ source]");

        return
            $"{LocalizationService.T("Backend"),-11}: {st.Backend}\n" +
            $"{LocalizationService.T("Decode"),-11}: {st.Hwdec}\n" +
            $"{LocalizationService.T("Source"),-11}: {st.SourceWidth}x{st.SourceHeight}\n" +
            $"{LocalizationService.T("Output"),-11}: {st.OutputWidth}x{st.OutputHeight}\n" +
            $"{LocalizationService.T("Upscaling"),-11}: {upscale}\n" +
            $"{LocalizationService.T("AI shaders"),-11}: {shaders}\n" +
            $"ELYCOLOR   : {LocalizationService.T(elyColorName)}\n" +
            $"FPS        : {st.Fps:0.0}\n" +
            $"Bitrate    : {st.BitrateKbps:0} kb/s\n" +
            $"{LocalizationService.T("Displayed"),-11}: {st.DisplayedFrames}\n" +
            $"{LocalizationService.T("Dropped"),-11}: {st.DroppedFrames}\n" +
            $"{LocalizationService.T("State"),-11}: {LocalizationService.T(st.State)}";
    }

    public static string AudioStats(AudioStatsSnapshot s)
    {
        static string Gauge(double v)
        {
            var filled = (int)Math.Round(Math.Clamp(v, 0, 1) * 10);
            return new string('▮', filled) + new string('▯', 10 - filled) + $" {v * 100:0}%";
        }

        var bpm = s.EstimatedBpm > 0 ? $"≈ {s.EstimatedBpm:0}" : LocalizationService.T("detecting...");
        var analysis = LocalizationService.T(s.HasAnalysis ? "real-time FFT (dedicated thread)" : "unavailable (format)");
        var renderLine = s.AudioCoreActive
            ? $"{LocalizationService.T("GPU render"),-11}: {s.RenderMs:0.00} ms/frame\n"
            : $"{LocalizationService.T("CPU render"),-11}: {s.RenderMs:0.00} ms/frame\n";

        return
            $"{LocalizationService.T("Backend"),-11}: {s.BackendName}\n" +
            $"{LocalizationService.T("Codec"),-11}: {s.Codec}\n" +
            $"{LocalizationService.T("Sample rate"),-11}: {s.SampleRate}\n" +
            $"{LocalizationService.T("Channels"),-11}: {s.Channels}\n" +
            $"Bitrate    : {s.Bitrate}\n" +
            $"{LocalizationService.T("Analysis"),-11}: {analysis}\n" +
            $"{LocalizationService.T("Actual FFT"),-11}: {s.AnalysisRateHz:0.0} Hz\n" +
            $"{LocalizationService.T("Renderer"),-11}: {LocalizationService.T(s.AudioCoreActive ? "ELYCAST AudioCore+" : "Classic (WPF)")}\n" +
            $"{LocalizationService.T("Visual FPS"),-11}: {(s.VisualFps > 0 ? s.VisualFps.ToString("0.0", CultureInfo.InvariantCulture) : LocalizationService.T("measuring..."))} " +
            $"({LocalizationService.T("target")} {(s.TargetFps <= 0 ? LocalizationService.T("unlimited") : s.TargetFps.ToString(CultureInfo.InvariantCulture))}, VSync {(s.VSync ? "ON" : "OFF")})\n" +
            renderLine +
            $"{LocalizationService.T("Bass"),-11}: {Gauge(s.Bass)}\n" +
            $"{LocalizationService.T("Energy"),-11}: {Gauge(s.Energy)}\n" +
            $"BPM        : {bpm}\n" +
            $"{LocalizationService.T("State"),-11}: {LocalizationService.T(s.IsPlaying ? "Playing" : "Paused")}";
    }
}
