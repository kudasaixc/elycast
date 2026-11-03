using System.Globalization;
using System.IO;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Export;

/// <summary>Picture quality of an exported clip (encoder rate-control level).</summary>
public enum ClipQuality { Standard, High, Maximum }

/// <summary>
/// One clip export. <see cref="StartSeconds"/>/<see cref="EndSeconds"/> are
/// absolute media positions; null exports the whole source (a live recording).
/// <see cref="Source"/> may embed Xtream credentials and is never logged.
/// </summary>
public sealed record ClipExportRequest(
    string Source,
    string OutputPath,
    double? StartSeconds,
    double? EndSeconds,
    int OutputWidth,
    int OutputHeight,
    ClipQuality Quality,
    string? AudioTrackId = null);

/// <summary>
/// WPF-free policy of the clip export: output geometry, default resolution,
/// file naming and the exact libmpv encoding options that reproduce the
/// playback pipeline (GLSL upscaler chain, scalers, deband, ELYCOLOR equalizer
/// and the ELYSOUND+ graph). The picture is rendered by mpv's own GPU renderer
/// through <c>vf=gpu</c>, so a shader chain behaves exactly as on screen: the
/// AI upscalers (Anime4K, FSRCNNX, NIS, FSR) fire whenever the output is
/// larger than the source. RTX VSR and NvOFFRUC are ELYCORE presentation
/// stages and are not part of an export.
/// </summary>
public static class ClipExportPlanner
{
    public static readonly int[] StandardHeights = [720, 1080, 1440, 2160];

    private static readonly double[] StandardAspects = [16.0 / 9.0, 4.0 / 3.0, 21.0 / 9.0, 1.85, 2.39];

    /// <summary>Hardware encoders first; libx264 always works.</summary>
    public static readonly string[] EncoderPreference = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    /// <summary>
    /// Default export height: the persisted upscaling target when set; with a
    /// GLSL upscaler, at least 2x the source (capped to 4K, never below 1080p)
    /// so the chain actually engages; otherwise the source height.
    /// </summary>
    public static int DefaultHeight(int sourceHeight, Settings settings)
    {
        if (settings.UpscaleTargetHeight > 0)
            return Math.Min(settings.UpscaleTargetHeight, 2160);
        if (sourceHeight <= 0)
            return 1080;
        if (ShaderCatalog.IsShaderMethod(settings.UpscaleMethod) && sourceHeight < 2160)
            return StandardHeights.FirstOrDefault(h => h >= Math.Max(1080, sourceHeight * 2), 2160);
        return Math.Min(Even(sourceHeight), 2160);
    }

    /// <summary>Even output size for the display aspect of the source (16:9 when unknown).</summary>
    public static (int Width, int Height) OutputSize(int displayWidth, int displayHeight, int targetHeight)
    {
        var height = Even(Math.Clamp(targetHeight, 144, 4320));
        var aspect = displayWidth > 0 && displayHeight > 0 ? displayWidth / (double)displayHeight : 16.0 / 9.0;
        // 854x480 and similar sources are rounded 16:9: snap to the standard
        // ratio so a 1080p export is 1920 wide, not 1922.
        foreach (var standard in StandardAspects)
            if (Math.Abs(aspect / standard - 1) < 0.01) { aspect = standard; break; }
        var width = Even((int)Math.Round(height * aspect));
        return (Math.Max(2, width), height);
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    /// <summary>Parses "18:30", "1:02:03", "95" or "95.5" into seconds.</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
                return false;
            // Minutes and seconds fields of a clock time stay below 60.
            if (i > 0 && value >= 60) return false;
            total = total * 60 + value;
        }
        seconds = total;
        return true;
    }

    /// <summary>A unique, filesystem-safe "Title 18-00 to 21-00 1080p.mp4" path.</summary>
    public static string OutputPath(string folder, string title, double? start, double? end, int height)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((title ?? "").Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length > 80) clean = clean[..80].Trim();
        if (clean.Length == 0) clean = "ElyCast clip";

        var range = start is { } s && end is { } e
            ? $" {FormatTime(s).Replace(':', '-')} to {FormatTime(e).Replace(':', '-')}"
            : " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var stem = $"{clean}{range} {height}p";
        var path = Path.Combine(folder, stem + ".mp4");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}).mp4");
        return path;
    }

    /// <summary>Short description of the enhancements an export will apply.</summary>
    public static IReadOnlyList<string> DescribePipeline(Settings settings)
    {
        var parts = new List<string>();
        var shaders = ShaderCatalog.Describe(string.Join(";", ShaderCatalog.ChainFor(settings.UpscaleMethod, settings.UpscaleSharpen)));
        if (!string.IsNullOrEmpty(shaders)) parts.Add(shaders);
        var color = ElyColorCatalog.Active(settings);
        if (!color.Id.Equals("off", StringComparison.OrdinalIgnoreCase)) parts.Add(color.Name);
        if (settings.ElySoundEnabled) parts.Add(ElySoundProfiles.Active(settings).Name);
        return parts;
    }

    /// <summary>
    /// Ordered libmpv options (set before mpv_initialize) for one encoding
    /// attempt with <paramref name="encoder"/>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildOptions(ClipExportRequest request, Settings settings, string encoder)
    {
        var options = new List<KeyValuePair<string, string>>();
        void Set(string name, string value) => options.Add(new(name, value));

        // Never let libmpv print or persist the source: it can embed credentials.
        Set("terminal", "no");
        Set("config", "no");
        Set("ytdl", "no");
        Set("idle", "no");
        Set("o", request.OutputPath);
        Set("of", "mp4");
        Set("ofopts", "movflags=+faststart");
        Set("ovc", encoder);
        Set("ovcopts", EncoderOptions(encoder, request.Quality));
        Set("oac", "aac");
        Set("oacopts", "b=" + (request.Quality == ClipQuality.Standard ? "192k" : "256k"));
        Set("ocopy-metadata", "no");
        if (request.StartSeconds is { } start) Set("start", Seconds(start));
        if (request.EndSeconds is { } end) Set("end", Seconds(end));
        if (!string.IsNullOrWhiteSpace(request.AudioTrackId)) Set("aid", request.AudioTrackId);
        Set("sid", "no");

        // Copy-back decoding: vf=gpu runs its own offscreen GPU context and
        // takes system-memory frames.
        Set("hwdec", "auto-copy");

        // Same renderer configuration as MpvHwndBackend.ConfigureGpuDefaults,
        // without the display-only frame pacing options.
        Set("profile", "gpu-hq");
        Set("cscale", "ewa_lanczossoft");
        Set("dscale", "mitchell");
        Set("deband", "yes");
        var method = settings.UpscaleMethod;
        var chain = ShaderCatalog.ChainFor(method, settings.UpscaleSharpen)
            .Select(ShaderCatalog.PathFor).Where(File.Exists).Select(p => p.Replace('\\', '/'));
        Set("glsl-shaders", string.Join(";", chain));
        // "none" maps to bilinear on screen to save GPU; an export is offline,
        // so keep a clean neutral scaler instead of a blurry one.
        Set("scale", method == "none" ? "ewa_lanczos" : ShaderCatalog.IsShaderMethod(method) ? "ewa_lanczossharp" : method);

        var color = ElyColorCatalog.Active(settings);
        Set("saturation", color.Saturation.ToString(CultureInfo.InvariantCulture));
        Set("brightness", color.Brightness.ToString(CultureInfo.InvariantCulture));
        Set("contrast", color.Contrast.ToString(CultureInfo.InvariantCulture));
        Set("gamma", color.Gamma.ToString(CultureInfo.InvariantCulture));
        Set("hue", color.Hue.ToString(CultureInfo.InvariantCulture));

        // Exactly the output size: the shader WHEN conditions see the same
        // OUTPUT/LUMA ratio as a window of that size. yuv420p keeps the file
        // playable everywhere (vf=gpu otherwise emits 4:4:4).
        Set("vf", string.Create(CultureInfo.InvariantCulture,
            $"gpu=w={request.OutputWidth}:h={request.OutputHeight},format=yuv420p"));

        // The clip is always stereo AAC, so the stereo ELYSOUND+ topology
        // (with its width stage) is the one that matches the output.
        Set("audio-channels", "stereo");
        if (settings.ElySoundEnabled)
        {
            var values = ElySoundGraph.Resolve(ElySoundProfiles.Active(settings), settings.ElySoundVirtualSurround, stereo: true);
            Set("af", $"@{ElySoundController.MpvLabel}:lavfi=[{ElySoundGraph.BuildTopology(true, values)}]");
        }
        return options;
    }

    public static string EncoderOptions(string encoder, ClipQuality quality)
    {
        var level = (int)quality;
        return encoder switch
        {
            "h264_nvenc" => $"preset=p{5 + level},tune=hq,rc=vbr,cq={new[] { 23, 19, 16 }[level]},b=0,profile=high",
            "h264_amf" => $"quality=quality,rc=cqp,qp_i={new[] { 22, 18, 15 }[level]},qp_p={new[] { 24, 20, 17 }[level]},profile=high",
            "h264_qsv" => $"preset=slower,global_quality={new[] { 23, 19, 16 }[level]},profile=high",
            _ => $"preset={(level == 0 ? "medium" : "slow")},crf={new[] { 21, 18, 15 }[level]},profile=high"
        };
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static int Even(int value) => value + (value & 1);
}
