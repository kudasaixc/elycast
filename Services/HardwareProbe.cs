using System.Globalization;
using System.IO;
using System.Management;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services;

public sealed record GpuInfo(string Name, string DriverVersion, string NvidiaDriverRelease, bool IsNvidia, bool IsRtx);

public sealed record HardwareInfo(
    string CpuName,
    int CpuCores,
    int CpuThreads,
    double RamGb,
    IReadOnlyList<GpuInfo> Gpus,
    bool NvidiaDriverPresent)
{
    public GpuInfo? PrimaryGpu =>
        Gpus.FirstOrDefault(g => g.IsNvidia) ?? Gpus.FirstOrDefault();
}

/// <summary>
/// Proposed playback configuration derived from the detected hardware and the
/// user's content preferences. The onboarding wizard shows it and lets the
/// user override each choice before anything is persisted.
/// </summary>
public sealed record EngineRecommendation(
    string VideoBackend,
    bool ElyFlowVsr,
    bool ElyFlowFruc,
    string UpscaleMethod,
    string ElyColorFilterId,
    string Reason);

/// <summary>
/// One-shot WMI/driver probe of the machine (CPU, RAM, GPUs) plus the
/// recommendation logic used by the first-run wizard and the "hw" console
/// command. Pure diagnostics: nothing here mutates settings or backends.
/// </summary>
public static class HardwareProbe
{
    public static HardwareInfo Detect()
    {
        var cpuName = "CPU inconnu";
        int cores = Environment.ProcessorCount, threads = Environment.ProcessorCount;
        try
        {
            using var cpu = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var o in cpu.Get().Cast<ManagementObject>())
            {
                cpuName = (o["Name"]?.ToString() ?? cpuName).Trim();
                if (int.TryParse(o["NumberOfCores"]?.ToString(), out var c)) cores = c;
                if (int.TryParse(o["NumberOfLogicalProcessors"]?.ToString(), out var t)) threads = t;
                break;
            }
        }
        catch { }

        double ramGb = 0;
        try
        {
            using var mem = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var o in mem.Get().Cast<ManagementObject>())
                if (double.TryParse(o["TotalPhysicalMemory"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var bytes))
                    ramGb = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 1);
        }
        catch { }

        var gpus = new List<GpuInfo>();
        try
        {
            using var video = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (var o in video.Get().Cast<ManagementObject>())
            {
                var name = (o["Name"]?.ToString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var driver = o["DriverVersion"]?.ToString() ?? "";
                var isNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                gpus.Add(new GpuInfo(
                    Name: name,
                    DriverVersion: driver,
                    NvidiaDriverRelease: isNvidia ? NvidiaReleaseFromWmi(driver) : "",
                    IsNvidia: isNvidia,
                    IsRtx: name.Contains("RTX", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { }

        var nvapi = false;
        try
        {
            nvapi = File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll"));
        }
        catch { }

        return new HardwareInfo(cpuName, cores, threads, ramGb, gpus, nvapi);
    }

    /// <summary>
    /// WMI reports NVIDIA drivers as "32.0.15.7716"; the marketing release is
    /// the last five digits reformatted: 577.16.
    /// </summary>
    public static string NvidiaReleaseFromWmi(string wmiVersion)
    {
        var digits = new string(wmiVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return wmiVersion;
        var tail = digits[^5..];
        return tail[..3] + "." + tail[3..];
    }

    /// <summary>RTX VSR needs an RTX 20+ GPU and driver release 531 or newer.</summary>
    public static bool SupportsRtxVsr(HardwareInfo hw)
    {
        var gpu = hw.PrimaryGpu;
        if (gpu is not { IsNvidia: true, IsRtx: true }) return false;
        var release = gpu.NvidiaDriverRelease.Split('.').FirstOrDefault() ?? "";
        return !int.TryParse(release, out var major) || major >= 531;
    }

    public static EngineRecommendation Recommend(HardwareInfo hw, IReadOnlyCollection<string> interests)
    {
        var mpvPresent = !string.IsNullOrWhiteSpace(MpvHwndBackend.LocateNative());
        var vsrCapable = SupportsRtxVsr(hw);
        var elyflow = ElyFlowService.Probe();

        string backend;
        string reason;
        bool vsr = false, fruc = false;

        if (hw.PrimaryGpu is { IsNvidia: true } &&
            ElyFlowRendererInterop.Available &&
            ElyFlowRendererInterop.Preflight(out _) == 0)
        {
            backend = "elycore";
            vsr = vsrCapable;
            fruc = elyflow.FrucCapable;
            reason = "GPU NVIDIA + renderer natif ELYCORE disponible : pipeline zéro-copie D3D11"
                     + (vsr ? " avec RTX VSR" : "")
                     + (fruc ? " et interpolation FRUC (ELYFLOW)" : "") + ".";
        }
        else if (hw.NvidiaDriverPresent && vsrCapable)
        {
            backend = "rtx-sdk";
            reason = "GPU RTX détecté : mpv gpu-next + RTX Video Super Resolution via le processeur vidéo D3D11.";
        }
        else if (mpvPresent || hw.PrimaryGpu != null)
        {
            backend = "mpv-gpu";
            reason = mpvPresent
                ? "Pipeline GPU mpv (gpu-next, décodage matériel, scalers avancés) — le meilleur choix sans RTX."
                : "Pipeline GPU mpv recommandé — libmpv sera téléchargé pendant l'installation.";
        }
        else
        {
            backend = "vlc-bitmap";
            reason = "Aucun GPU exploitable détecté : VLC (compatibilité maximale).";
        }

        // Content preferences refine the enhancement chain, never the backend.
        var upscale = "ewa_lanczossharp";
        var elycolor = "off";
        if (interests.Contains("anime"))
        {
            upscale = hw.PrimaryGpu?.IsRtx == true ? "anime4k-hq" : "anime4k-fast";
            elycolor = "elycolor-anime";
        }
        else if (interests.Contains("sport"))
        {
            upscale = "fsrcnnx";
            elycolor = "elycolor-sport";
        }
        else if (interests.Contains("cinema"))
        {
            upscale = "fsrcnnx-hq";
            elycolor = "elycolor-film";
        }

        return new EngineRecommendation(backend, vsr, fruc, upscale, elycolor, reason);
    }
}
