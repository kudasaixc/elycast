namespace Elysium_Cast_IPTV.Models;

/// <summary>Global, user-tweakable application settings (persisted).</summary>
public class Settings
{
    // Appearance
    public string AccentColor { get; set; } = "#FF8B5CF6";

    // Playback
    public bool ShowStats { get; set; } = false;
    public bool AutoReconnect { get; set; } = true;
    public bool RememberVolume { get; set; } = true;
    public int DefaultVolume { get; set; } = 75;
    public string LiveStreamFormat { get; set; } = "ts"; // "ts" | "m3u8"
    public string VideoBackend { get; set; } = "mpv-gpu"; // "mpv-gpu" | "vlc-bitmap" | "rtx-sdk" | "elycore" (ex-"elyflow")
    public string PreferredSubtitle { get; set; } = "auto"; // "auto" | "off" | "name:<track name>"
    public string PreferredAudio { get; set; } = "auto"; // "auto" | "name:<track name>"
    public string UpscalerEngine { get; set; } = "off"; // "off" | "magpie-fsr" | "magpie-anime4k" | "magpie-fsrcnnx"
    public string MagpiePath { get; set; } = "";
    public string MagpieHotkey { get; set; } = "Alt+Shift+Q";

    // Internal mpv upscaling (GPU). TargetHeight 0 = native (scale to window only).
    public int UpscaleTargetHeight { get; set; } = 0; // 0 | 1080 | 1440 | 2160 | 4320
    public string UpscaleMethod { get; set; } = "none"; // method id, "none" = off
    public string UpscaleSharpen { get; set; } = "off"; // "off" | "low" | "medium" | "high"
    public string ElyColorFilterId { get; set; } = "off";
    public List<ElyColorFilter> ElyColorCustomFilters { get; set; } = new();
    public bool ElySoundEnabled { get; set; } = false;
    public bool ElySoundVirtualSurround { get; set; } = false;
    public string ElySoundPresetId { get; set; } = "cinema";
    public ElySoundProfile ElySoundCustomProfile { get; set; } = ElySoundProfile.DefaultCustom();
    public bool ElyFlowEnabled { get; set; } = false;
    public bool ElyFlowRtxVsrEnabled { get; set; } = true;
    public string ElyFlowEngine { get; set; } = "nvidia-fruc"; // "nvidia-fruc" | "mpv-pacing"
    public string ElyFlowTargetFps { get; set; } = "60"; // "30" | "60000/1001" | "60" | "120" | "144" | "165" | "240" | "360"
    public double ElyFlowLiveBufferSeconds { get; set; } = 5.0;

    // Which upscaling modes appear in the OSD quick-selector (under the seek bar).
    public List<string> OsdUpscaleModes { get; set; } = new()
    {
        "none", "anime4k-hq", "anime4k-fast", "anime4k-denoise", "fsrcnnx", "fsr", "ewa_lanczossharp"
    };

    // Behaviour
    public bool ZapWithArrows { get; set; } = true;
    public bool ConfirmExit { get; set; } = false;
    public double BootSeconds { get; set; } = 5.5;
}

/// <summary>Image filter preset, optionally carrying a complete video pipeline.</summary>
public class ElyColorFilter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "ELYCOLOR Custom";
    public bool IncludeVideoPipeline { get; set; } = true;
    public string VideoBackend { get; set; } = "mpv-gpu";
    public string UpscalerEngine { get; set; } = "off";
    public int UpscaleTargetHeight { get; set; } = 0;
    public string UpscaleMethod { get; set; } = "none";
    public string UpscaleSharpen { get; set; } = "off";
    public int Saturation { get; set; }
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Gamma { get; set; }
    public int Hue { get; set; }
}

/// <summary>Audio post-processing profile applied live through mpv audio filters.</summary>
public class ElySoundProfile
{
    public string Id { get; set; } = "custom";
    public string Name { get; set; } = "ELYSOUND+ Custom";
    public int Preamp { get; set; }
    public int Bass { get; set; }
    public int LowMid { get; set; }
    public int Mid { get; set; }
    public int Presence { get; set; }
    public int Treble { get; set; }
    public int Clarity { get; set; }
    public int Width { get; set; }
    public int Compressor { get; set; }
    public int Limiter { get; set; } = 85;

    public static ElySoundProfile DefaultCustom() => new()
    {
        Id = "custom",
        Name = "ELYSOUND+ Custom",
        Preamp = -3,
        Bass = 3,
        LowMid = 0,
        Mid = 1,
        Presence = 2,
        Treble = 1,
        Clarity = 1,
        Width = 6,
        Compressor = 6,
        Limiter = 90
    };
}

/// <summary>Per-connection state: favourites and the last watched item.</summary>
public class ProfileState
{
    public List<PlayItem> Favorites { get; set; } = new();
    public PlayItem? LastPlayed { get; set; }
}

/// <summary>Everything persisted in state.json.</summary>
public class AppState
{
    public Settings Settings { get; set; } = new();
    public Dictionary<string, ProfileState> Profiles { get; set; } = new();
    public List<PlayItem> LocalLibrary { get; set; } = new();
}
