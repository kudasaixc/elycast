using System.IO;
using System.Text.RegularExpressions;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Static knowledge about the GLSL upscaling shaders: which files each method
/// needs, where to download them from, and the sharpen "companion" variants we
/// generate from CAS / NVSharpen.
///
/// Why companions: the stock AI upscalers (FSR, NVScaler, FSRCNNX, the Anime4K
/// x2 passes…) carry //!WHEN conditions that skip the pass entirely unless the
/// video is displayed LARGER than the source. A 1080p stream in a smaller
/// window therefore looked completely untouched. Each method now pairs its
/// upscaler with an adaptive sharpener gated to "display ≤ source" (the agyild
/// CAS / NVSharpen ports, relaxed from their stock exactly-1:1 gate), so the
/// mode always has a visible effect - like a game running DLSS at 100% render
/// scale. The sharpen setting tunes the companions' strength, since mpv's own
/// --sharpen option is a no-op on vo=gpu-next.
/// </summary>
public static class ShaderCatalog
{
    public static string Anime4kDir => Path.Combine(StateStore.FolderPath, "tools", "anime4k");
    public static string ShadersDir => Path.Combine(StateStore.FolderPath, "tools", "shaders");

    private const string Anime4kRaw = "https://raw.githubusercontent.com/bloc97/Anime4K/v4.0.1/glsl/";
    private const string FsrcnnxRelease = "https://github.com/igv/FSRCNN-TensorFlow/releases/download/1.1/";
    private const string GistFsr = "https://gist.githubusercontent.com/agyild/82219c545228d70c5604f865ce0b0ce5/raw/";
    private const string GistCas = "https://gist.githubusercontent.com/agyild/bbb4e58298b2f86aa24da3032a0d2ee6/raw/";
    private const string GistNis = "https://gist.githubusercontent.com/agyild/7e8951915b2bf24526a9343d951db214/raw/";

    /// <summary>Download source for every pristine shader file, by bare name.</summary>
    public static readonly IReadOnlyDictionary<string, string> DownloadUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FSR.glsl"] = GistFsr + "FSR.glsl",
            ["CAS.glsl"] = GistCas + "CAS.glsl",
            ["CAS-scaled.glsl"] = GistCas + "CAS-scaled.glsl",
            ["NVScaler.glsl"] = GistNis + "NVScaler.glsl",
            ["NVSharpen.glsl"] = GistNis + "NVSharpen.glsl",
            ["FSRCNNX_x2_8-0-4-1.glsl"] = FsrcnnxRelease + "FSRCNNX_x2_8-0-4-1.glsl",
            ["FSRCNNX_x2_16-0-4-1.glsl"] = FsrcnnxRelease + "FSRCNNX_x2_16-0-4-1.glsl",
            ["Anime4K_Clamp_Highlights.glsl"] = Anime4kRaw + "Restore/Anime4K_Clamp_Highlights.glsl",
            ["Anime4K_Restore_CNN_VL.glsl"] = Anime4kRaw + "Restore/Anime4K_Restore_CNN_VL.glsl",
            ["Anime4K_Restore_CNN_M.glsl"] = Anime4kRaw + "Restore/Anime4K_Restore_CNN_M.glsl",
            ["Anime4K_Restore_CNN_Soft_VL.glsl"] = Anime4kRaw + "Restore/Anime4K_Restore_CNN_Soft_VL.glsl",
            ["Anime4K_Upscale_CNN_x2_VL.glsl"] = Anime4kRaw + "Upscale/Anime4K_Upscale_CNN_x2_VL.glsl",
            ["Anime4K_Upscale_CNN_x2_M.glsl"] = Anime4kRaw + "Upscale/Anime4K_Upscale_CNN_x2_M.glsl",
            ["Anime4K_Upscale_CNN_x2_S.glsl"] = Anime4kRaw + "Upscale/Anime4K_Upscale_CNN_x2_S.glsl",
            ["Anime4K_Upscale_Denoise_CNN_x2_VL.glsl"] = Anime4kRaw + "Upscale%2BDenoise/Anime4K_Upscale_Denoise_CNN_x2_VL.glsl",
            ["Anime4K_AutoDownscalePre_x2.glsl"] = Anime4kRaw + "Upscale/Anime4K_AutoDownscalePre_x2.glsl",
            ["Anime4K_AutoDownscalePre_x4.glsl"] = Anime4kRaw + "Upscale/Anime4K_AutoDownscalePre_x4.glsl",
        };

    /// <summary>Absolute path of a shader file (Anime4K has its own folder).</summary>
    public static string PathFor(string fileName) =>
        Path.Combine(fileName.StartsWith("Anime4K_", StringComparison.OrdinalIgnoreCase) ? Anime4kDir : ShadersDir, fileName);

    /// <summary>True when the method renders through a GLSL upscaler chain.</summary>
    public static bool IsShaderMethod(string method) => method is
        "fsr" or "nvscaler" or "fsrcnnx" or "fsrcnnx-hq" or
        "anime4k-hq" or "anime4k-fast" or "anime4k-denoise" or "anime4k-deblur";

    /// <summary>
    /// The ordered shader chain (bare file names) for a method + sharpen level.
    /// Empty for "none" and for plain mpv scalers with sharpening off.
    /// </summary>
    public static IReadOnlyList<string> ChainFor(string method, string sharpen)
    {
        var cas = TunedFileName("CAS.glsl", sharpen);
        var casScaled = TunedFileName("CAS-scaled.glsl", sharpen);
        var nvSharpen = TunedFileName("NVSharpen.glsl", sharpen);
        var sharpenOn = sharpen is "low" or "medium" or "high";

        // Anime4K presets (Mode A = restore, B = deblur, C = denoise). Their
        // Restore passes already run at any display size; only add CAS when the
        // user explicitly asked for extra sharpening.
        string[] A4k(params string[] files) => sharpenOn ? files.Append(cas).ToArray() : files;

        return method switch
        {
            "anime4k-hq" => A4k("Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"),
            "anime4k-fast" => A4k("Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_M.glsl", "Anime4K_Upscale_CNN_x2_M.glsl", "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_S.glsl"),
            "anime4k-denoise" => A4k("Anime4K_Clamp_Highlights.glsl", "Anime4K_Upscale_Denoise_CNN_x2_VL.glsl", "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"),
            "anime4k-deblur" => A4k("Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_Soft_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"),
            // FSR.glsl already ends with RCAS when upscaling; CAS covers ≤ 1:1.
            "fsr" => new[] { "FSR.glsl", cas },
            "nvscaler" => new[] { "NVScaler.glsl", nvSharpen },
            // FSRCNNX only fires at ≥ 1.3x; CAS-scaled fills the 1.0–1.3x band
            // (its WHEN skips it once the CNN already reached the output size).
            "fsrcnnx" => new[] { "FSRCNNX_x2_8-0-4-1.glsl", casScaled, cas },
            "fsrcnnx-hq" => new[] { "FSRCNNX_x2_16-0-4-1.glsl", casScaled, cas },
            "none" => Array.Empty<string>(),
            // Plain mpv scaler: shaders only carry the sharpen setting.
            _ => sharpenOn ? new[] { casScaled, cas } : Array.Empty<string>(),
        };
    }

    /// <summary>Chain files not present on disk yet (bare names).</summary>
    public static IReadOnlyList<string> MissingFor(string method, string sharpen)
    {
        var missing = new List<string>();
        foreach (var file in ChainFor(method, sharpen))
            if (!File.Exists(PathFor(file)) && !missing.Contains(file))
                missing.Add(file);
        return missing;
    }

    // ---- sharpen-tuned companions -------------------------------------------

    private static readonly Regex TunedRx = new(@"^(?<base>.+)\.elycast-[a-z]+\.glsl$", RegexOptions.IgnoreCase);

    /// <summary>Name of the generated variant tuned for a sharpen level.</summary>
    public static string TunedFileName(string baseFile, string sharpen)
    {
        var level = sharpen is "low" or "medium" or "high" ? sharpen : "off";
        return Path.GetFileNameWithoutExtension(baseFile) + ".elycast-" + level + ".glsl";
    }

    /// <summary>True when <paramref name="fileName"/> is a generated variant.</summary>
    public static bool IsTuned(string fileName, out string baseFileName)
    {
        var m = TunedRx.Match(fileName);
        baseFileName = m.Success ? m.Groups["base"].Value + ".glsl" : "";
        return m.Success;
    }

    /// <summary>
    /// Generates the tuned variant of a pristine sharpener: writes the strength
    /// #define, and relaxes the stock "exactly 1:1" //!WHEN gate of CAS /
    /// NVSharpen to "not upscaling" (RPN: ratio 1.0 &gt; NOT) so they also run
    /// when the video is displayed smaller than the source - the common IPTV
    /// case. Above 1:1 the upscaler passes of the chain take over.
    /// </summary>
    public static string GenerateTuned(string baseFile, string pristineText, string sharpen)
    {
        // CAS "SHARPENING" still sharpens mildly at 0.0; NVSharpen default 0.25.
        var (cas, nvs) = sharpen switch
        {
            "low" => ("0.35", "0.40"),
            "medium" => ("0.65", "0.65"),
            "high" => ("1.0", "0.90"),
            _ => ("0.0", "0.25")
        };
        var value = baseFile.StartsWith("NV", StringComparison.OrdinalIgnoreCase) ? nvs : cas;

        var text = Regex.Replace(pristineText,
            @"(?m)^(#define\s+(?:SHARPENING|SHARPNESS)\s+)[0-9.]+",
            "${1}" + value);
        return text.Replace(
            "//!WHEN OUTPUT.w OUTPUT.h * LUMA.w LUMA.h * / 1.0 > ! OUTPUT.w OUTPUT.h * LUMA.w LUMA.h * / 1.0 < ! *",
            "//!WHEN OUTPUT.w OUTPUT.h * LUMA.w LUMA.h * / 1.0 > !");
    }

    /// <summary>Human-readable summary of an mpv glsl-shaders list ("FSR + CAS").</summary>
    public static string Describe(string glslShaders)
    {
        if (string.IsNullOrWhiteSpace(glslShaders)) return "";
        var names = new List<string>();
        foreach (var entry in glslShaders.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.GetFileName(entry.Trim());
            var label =
                f.StartsWith("Anime4K", StringComparison.OrdinalIgnoreCase) ? "Anime4K" :
                f.StartsWith("FSRCNNX", StringComparison.OrdinalIgnoreCase) ? "FSRCNNX" :
                f.StartsWith("FSR", StringComparison.OrdinalIgnoreCase) ? "FSR" :
                f.StartsWith("NVScaler", StringComparison.OrdinalIgnoreCase) ? "NIS" :
                f.StartsWith("NVSharpen", StringComparison.OrdinalIgnoreCase) ? "NVSharpen" :
                f.StartsWith("CAS", StringComparison.OrdinalIgnoreCase) ? "CAS" :
                Path.GetFileNameWithoutExtension(f);
            if (!names.Contains(label)) names.Add(label);
        }
        return string.Join(" + ", names);
    }
}
