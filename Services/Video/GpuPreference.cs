using Microsoft.Win32;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Windows per-application graphics preference (Settings &gt; Display &gt;
/// Graphics). On hybrid laptops the screen is driven by the integrated GPU, so
/// without a preference every D3D11 device ElyCast or libmpv creates lands on
/// the iGPU: RTX VSR, NVENC and FRUC are unavailable and shader chains drop
/// frames. The preference is keyed by executable path, so it is lost whenever
/// the executable moves (a new install location).
/// </summary>
public static class GpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string HighPerformance = "GpuPreference=2;";

    /// <summary>
    /// Requests the high-performance GPU for this executable when an NVIDIA
    /// driver is present and the user never chose a preference for it. An
    /// explicit user choice (including power saving) is left untouched. DXGI
    /// reads the preference when a factory is created, so calling this before
    /// any video backend exists applies it to the current process.
    /// </summary>
    public static void EnsureHighPerformance()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !VideoBackendFactory.HasNvidiaDriver()) return;
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key.GetValue(exe) is string existing && existing.Contains("GpuPreference=", StringComparison.Ordinal))
            {
                if (!existing.Contains("GpuPreference=2", StringComparison.Ordinal))
                    DebugConsole.Warn("Windows graphics preference keeps ElyCast off the high-performance GPU (user choice).");
                return;
            }
            key.SetValue(exe, HighPerformance, RegistryValueKind.String);
            DebugConsole.Info("Windows graphics preference set to High performance for ElyCast.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            DebugConsole.Warn("Could not set the Windows graphics preference: " + ex.Message);
        }
    }
}
