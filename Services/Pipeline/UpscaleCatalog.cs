namespace Elysium_Cast_IPTV.Services.Pipeline;

/// <summary>Every internal mpv upscaling mode (id + English display label).</summary>
public static class UpscaleCatalog
{
    public static readonly IReadOnlyList<(string Id, string Label)> Modes =
    [
        ("none", "None"),
        ("anime4k-hq", "Anime4K: Quality"),
        ("anime4k-fast", "Anime4K: Fast"),
        ("anime4k-denoise", "Anime4K: Denoise"),
        ("anime4k-deblur", "Anime4K: Deblur"),
        ("fsrcnnx", "FSRCNNX"),
        ("fsrcnnx-hq", "FSRCNNX HQ"),
        ("fsr", "AMD FSR + CAS"),
        ("nvscaler", "NVIDIA Image Scaling"),
        ("ewa_lanczossharp", "EWA Lanczos (sharp)"),
        ("lanczos", "Lanczos"),
        ("spline36", "Spline36"),
        ("mitchell", "Mitchell"),
        ("catmull_rom", "Catmull-Rom"),
        ("bilinear", "Bilinear"),
    ];

    public static bool Contains(string id) => Modes.Any(m => m.Id == id);
}
