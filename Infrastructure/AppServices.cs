using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.ElySmart;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.Shell;
using Elysium_Cast_IPTV.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Elysium_Cast_IPTV.Infrastructure;

/// <summary>Composition root: every long-lived service of the player.</summary>
public static class AppServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppStateStore, StateStoreAdapter>();
        services.AddSingleton<IProfileStore, ProfileStoreAdapter>();
        services.AddSingleton<PlaybackSession>();
        services.AddSingleton<MediaLibrary>();
        services.AddSingleton<AudioQueue>(_ => new AudioQueue());
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<ShaderInstaller>();
        services.AddSingleton<VideoPipeline>();
        services.AddSingleton<LocalLibraryService>();
        services.AddSingleton<MagpieUpscalerService>();
        services.AddSingleton<MpvNativeInstaller>();
        services.AddSingleton<AudioVisualEngine>();
        services.AddSingleton<BenchmarkEngine>();
        services.AddSingleton<PerformanceHistory>();
        services.AddSingleton<HealthAnalyzer>();
        services.AddSingleton<NotificationEngine>();
        services.AddSingleton<RuntimeOptimizer>();
        services.AddSingleton<ShellServices>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider();
    }
}
