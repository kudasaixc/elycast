using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Export;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>Services resolved from the DI container for the player shell.</summary>
public sealed record ShellServices(
    IAppStateStore Store,
    IProfileStore Profiles,
    PlaybackSession Session,
    MediaLibrary Library,
    AudioQueue Queue,
    PlayerViewModel ViewModel,
    VideoPipeline Pipeline,
    LocalLibraryService LocalLibrary,
    MagpieUpscalerService Magpie,
    MpvNativeInstaller MpvInstaller,
    AudioVisualEngine AudioEngine,
    ShaderInstaller Shaders,
    ClipExporter ClipExporter);
