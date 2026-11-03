using System.IO;
using System.Text.RegularExpressions;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Static knowledge about the GLSL upscaling shaders: which files each method
/// needs (all bundled with ElyCast), the stackable image effects and the sharpen "companion" variants we
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
    /// <summary>Shaders shipped with ElyCast (Assets/Shaders, copied next to the executable).</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "shaders");

    /// <summary>Sharpen-tuned variants generated at runtime from the bundled pristine files.</summary>
    public static string ShadersDir => Path.Combine(StateStore.FolderPath, "tools", "shaders");

    /// <summary>
    /// Every pristine shader ElyCast ships. Nothing is downloaded: the files
    /// are part of the installation (see Assets/Shaders/THIRD-PARTY-NOTICES.md).
    /// </summary>
    public static readonly IReadOnlyList<string> Bundled =
    [
        "FSR.glsl", "CAS.glsl", "CAS-scaled.glsl", "NVScaler.glsl", "NVSharpen.glsl",
        "FSRCNNX_x2_8-0-4-1.glsl", "FSRCNNX_x2_16-0-4-1.glsl",
        "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl", "Anime4K_Restore_CNN_M.glsl",
        "Anime4K_Restore_CNN_Soft_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_Upscale_CNN_x2_M.glsl",
        "Anime4K_Upscale_CNN_x2_S.glsl", "Anime4K_Upscale_Denoise_CNN_x2_VL.glsl",
        "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl",
        "Anime4K_Darken_HQ.glsl", "Anime4K_Thin_Fast.glsl", "KrigBilateral.glsl", "adaptive-sharpen.glsl",
        "ElyCast_Vibrance.glsl",
    ];

    /// <summary>
    /// Image effects that stack on any upscaling method. <see cref="Before"/>
    /// effects precede the upscaler chain (chroma reconstruction feeds it);
    /// the others follow it. "deband" is not a shader: it tunes mpv's own
    /// debanding. Measured on an RTX 4050 Laptop, 1080p anime fullscreen:
    /// vibrance and deband are close to free, the whole set adds about 1 W
    /// on top of Anime4K Fast.
    /// </summary>
    public static readonly IReadOnlyList<VideoEffect> Effects =
    [
        new("chroma", "Sharp colours", "KrigBilateral.glsl", Before: true),
        new("darken", "Darker lines", "Anime4K_Darken_HQ.glsl"),
        new("thin", "Thinner lines", "Anime4K_Thin_Fast.glsl"),
        new("sharpen", "Adaptive sharpening", "adaptive-sharpen.glsl"),
        new("vibrance", "Vibrance", "ElyCast_Vibrance.glsl"),
        new("deband", "Anime deband", null),
    ];

    /// <summary>Known effect ids only, without duplicates, in catalog order.</summary>
    public static List<string> NormalizeEffects(IEnumerable<string?>? effects)
    {
        var requested = new HashSet<string>(effects?.Where(e => e != null).Select(e => e!) ?? [], StringComparer.OrdinalIgnoreCase);
        return Effects.Where(e => requested.Contains(e.Id)).Select(e => e.Id).ToList();
    }

    /// <summary>Shader files of the effects alone (used while the upscaler chain is suspended).</summary>
    public static IReadOnlyList<string> EffectChain(IEnumerable<string>? effects) => ChainFor("none", "off", effects);

    /// <summary>The upscaling chain of a method wrapped by the selected effects.</summary>
    public static IReadOnlyList<string> ChainFor(string method, string sharpen, IEnumerable<string>? effects)
    {
        var selected = NormalizeEffects(effects);
        var chosen = Effects.Where(e => e.Shader != null && selected.Contains(e.Id)).ToList();
        return chosen.Where(e => e.Before).Select(e => e.Shader!)
            .Concat(ChainFor(method, sharpen))
            .Concat(chosen.Where(e => !e.Before).Select(e => e.Shader!))
            .ToList();
    }

    /// <summary>
    /// mpv debanding: the anime profile runs two passes with a wider search
    /// and a finer grain (smooth gradients without visible noise); otherwise
    /// mpv's defaults.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DebandOptions(IEnumerable<string>? effects)
    {
        var anime = NormalizeEffects(effects).Contains("deband");
        return
        [
            new("deband-iterations", anime ? "2" : "1"),
            new("deband-threshold", anime ? "64" : "48"),
            new("deband-range", anime ? "20" : "16"),
            new("deband-grain", anime ? "16" : "32"),
        ];
    }

    /// <summary>Absolute path of a shader file: bundled pristine file or generated tuned variant.</summary>
    public static string PathFor(string fileName) =>
        Path.Combine(IsTuned(fileName, out _) ? ShadersDir : BundledDir, fileName);

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
                f.StartsWith("Anime4K_Darken", StringComparison.OrdinalIgnoreCase) ? "Darker lines" :
                f.StartsWith("Anime4K_Thin", StringComparison.OrdinalIgnoreCase) ? "Thinner lines" :
                f.StartsWith("Anime4K", StringComparison.OrdinalIgnoreCase) ? "Anime4K" :
                f.StartsWith("KrigBilateral", StringComparison.OrdinalIgnoreCase) ? "KrigBilateral" :
                f.StartsWith("adaptive-sharpen", StringComparison.OrdinalIgnoreCase) ? "Adaptive Sharpen" :
                f.StartsWith("ElyCast_Vibrance", StringComparison.OrdinalIgnoreCase) ? "Vibrance" :
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

/// <summary>An image effect: id persisted in settings/profiles, English label, shader file (null for mpv options).</summary>
public sealed record VideoEffect(string Id, string Label, string? Shader, bool Before = false);
