namespace Elysium_Cast_IPTV.Models;

/// <summary>Global, user-tweakable application settings (persisted).</summary>
public class Settings
{
    // Onboarding (first-run wizard). PreferredConnection: "xtream" | "m3u" | "local".
    public bool OnboardingCompleted { get; set; }
    public string UserDisplayName { get; set; } = "";
    public string PreferredConnection { get; set; } = "xtream";
    public List<string> ContentInterests { get; set; } = new(); // "sport" | "anime" | "cinema" | "tv" | "music" | "docs"
    // Name of a saved Profile to connect automatically at startup (set by the
    // onboarding wizard when the user enters his credentials there). Empty =
    // land on the login screen. Deleting the profile disables it.
    public string AutoConnectProfile { get; set; } = "";

    // Appearance
    public string AccentColor { get; set; } = "#FF8B5CF6";

    // Local audio-player scene.
    public string AudioBackgroundMode { get; set; } = "solid"; // "solid" | "cover" | "image"
    public string AudioBackgroundImage { get; set; } = "sunset";
    public double AudioBackgroundBlur { get; set; } = 45.6;
    public double AudioBackgroundDim { get; set; } = 0.85;
    public bool AudioBackgroundSlowZoom { get; set; } = true;
    public bool AudioBackgroundSlowPan { get; set; } = true;
    public bool AudioBackgroundMouseParallax { get; set; } = false;
    public double AudioBackgroundParallaxIntensity { get; set; } = 1.0;
    public bool AudioPaletteAutomatic { get; set; } = true;
    public bool AudioParticleAdaptiveColors { get; set; } = true;
    public int AudioVisualizerTargetFps { get; set; } = 60;
    public bool AudioVisualizerVSync { get; set; } = true;
    public int AudioParticleCount { get; set; } = 96;
    public double AudioParticleDistance { get; set; } = 1.0;
    public bool AudioVisualizerShake { get; set; } = true;
    public string AudioVisualizerRenderer { get; set; } = "classic"; // "classic" | "audiocore"
    public string AudioBrowseMode { get; set; } = "albums";
    public bool ElySmartAutoOptimizeDecorative { get; set; }
    public bool ElySmartNotificationsEnabled { get; set; } = true;
    public string ElySmartWorkload { get; set; } = "Mixed";
    public List<string> ElySmartIgnoredHealthIssues { get; set; } = new();

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
    // "fsr" was dropped from the defaults: on real IPTV sources it brought
    // nothing over FSRCNNX/Anime4K and duplicated the external Magpie path.
    public List<string> OsdUpscaleModes { get; set; } = new()
    {
        "none", "anime4k-hq", "anime4k-fast", "anime4k-denoise", "fsrcnnx", "ewa_lanczossharp"
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
    public int Version { get; set; }
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
    /// <summary>Sample-domain limiter ceiling in dBFS; presets reserve additional inter-sample headroom.</summary>
    public double LimiterCeilingDb { get; set; } = -2.3;

    // Legacy v1 value (70..100 linear percent). Kept only so protected state
    // written by older builds can be migrated without losing the custom EQ.
    public int Limiter { get; set; }

    public static ElySoundProfile DefaultCustom() => new()
    {
        Id = "custom",
        Name = "ELYSOUND+ Custom",
        Version = 2,
        Preamp = -3,
        Bass = 3,
        LowMid = 0,
        Mid = 1,
        Presence = 2,
        Treble = 1,
        Clarity = 1,
        Width = 6,
        Compressor = 6,
        LimiterCeilingDb = -2.3
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
    public List<PlayItem> LocalAudioLibrary { get; set; } = new();
    public List<PlayItem> LocalVideoLibrary { get; set; } = new();
    public List<LocalPlaylist> LocalPlaylists { get; set; } = new();
}
