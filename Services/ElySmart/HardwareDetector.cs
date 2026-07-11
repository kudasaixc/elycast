using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;

namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed class HardwareSnapshot
{
    public string Cpu { get; set; } = "Inconnu";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public uint CpuMaxMhz { get; set; }
    public uint CpuCurrentMhz { get; set; }
    public long CacheL2Kb { get; set; }
    public long CacheL3Kb { get; set; }
    public List<string> InstructionSets { get; set; } = new();
    public List<GpuSnapshot> Gpus { get; set; } = new();
    public double RamGb { get; set; }
    public double RamFreeGb { get; set; }
    public uint RamMhz { get; set; }
    public List<DisplaySnapshot> Displays { get; set; } = new();
    public List<StorageSnapshot> Storage { get; set; } = new();
    public string Windows { get; set; } = Environment.OSVersion.VersionString;
    public int WindowsBuild { get; set; } = Environment.OSVersion.Version.Build;
    public bool OnBattery { get; set; }
}

public sealed record GpuSnapshot(string Name, string Vendor, string Driver, double VramGb, string Architecture,
    bool D3D11, bool D3D12, bool Vulkan, bool OpenGl, bool Cuda, bool Rtx, bool QuickSync, bool AmdVcn);
public sealed record DisplaySnapshot(string Name, uint Width, uint Height, uint RefreshHz, bool Hdr);
public sealed record StorageSnapshot(string Model, string MediaType, double SizeGb, string Interface);

public sealed class HardwareDetector
{
    public Task<HardwareSnapshot> DetectAsync(CancellationToken token) => Task.Run(() => Detect(token), token);

    private static HardwareSnapshot Detect(CancellationToken token)
    {
        var result = new HardwareSnapshot { Threads = Environment.ProcessorCount };
        Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed,L2CacheSize,L3CacheSize FROM Win32_Processor", o =>
        {
            token.ThrowIfCancellationRequested();
            result.Cpu = Text(o, "Name", result.Cpu).Trim();
            result.Cores += Int(o, "NumberOfCores"); result.Threads = Math.Max(result.Threads, Int(o, "NumberOfLogicalProcessors"));
            result.CpuMaxMhz = UInt(o, "MaxClockSpeed"); result.CpuCurrentMhz = UInt(o, "CurrentClockSpeed");
            result.CacheL2Kb += Long(o, "L2CacheSize"); result.CacheL3Kb += Long(o, "L3CacheSize");
        });
        if (Sse.IsSupported) result.InstructionSets.Add("SSE");
        if (Sse2.IsSupported) result.InstructionSets.Add("SSE2");
        if (Avx.IsSupported) result.InstructionSets.Add("AVX");
        if (Avx2.IsSupported) result.InstructionSets.Add("AVX2");
        if (Avx512F.IsSupported) result.InstructionSets.Add("AVX-512F");

        Query("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem", o =>
        { result.RamGb = Long(o, "TotalVisibleMemorySize") / 1048576d; result.RamFreeGb = Long(o, "FreePhysicalMemory") / 1048576d; });
        Query("SELECT ConfiguredClockSpeed FROM Win32_PhysicalMemory", o => result.RamMhz = Math.Max(result.RamMhz, UInt(o, "ConfiguredClockSpeed")));
        Query("SELECT Name,DriverVersion,AdapterRAM,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate FROM Win32_VideoController", o =>
        {
            var name = Text(o, "Name", "GPU inconnu"); var upper = name.ToUpperInvariant();
            var vendor = upper.Contains("NVIDIA") ? "NVIDIA" : upper.Contains("AMD") || upper.Contains("RADEON") ? "AMD" : upper.Contains("INTEL") ? "Intel" : "Inconnu";
            result.Gpus.Add(new(name, vendor, Text(o, "DriverVersion", ""), AdapterRamGb(o), NvidiaArchitecture(name),
                true, OperatingSystem.IsWindowsVersionAtLeast(10), DetectLibrary("vulkan-1.dll"), DetectLibrary("opengl32.dll"),
                vendor == "NVIDIA" && DetectLibrary("nvcuda.dll"), upper.Contains("RTX"), vendor == "Intel", vendor == "AMD"));
            var w = UInt(o, "CurrentHorizontalResolution"); var h = UInt(o, "CurrentVerticalResolution");
            if (w > 0 && h > 0) result.Displays.Add(new(name, w, h, UInt(o, "CurrentRefreshRate"), false));
        });
        Query("SELECT Model,MediaType,Size,InterfaceType FROM Win32_DiskDrive", o => result.Storage.Add(new(
            Text(o, "Model", "Disque"), ClassifyStorage(Text(o, "Model", "") + " " + Text(o, "MediaType", ""), Text(o, "InterfaceType", "")),
            Long(o, "Size") / 1073741824d, Text(o, "InterfaceType", ""))));
        result.OnBattery = false; // RuntimeMonitor can enrich this through a platform power provider.
        return result;
    }

    public static string Fingerprint(HardwareSnapshot h)
    {
        var raw = $"{h.Cpu}|{h.Cores}|{h.Threads}|{h.RamGb:0.0}|{string.Join(';', h.Gpus.Select(g => $"{g.Name}:{g.Driver}"))}|{string.Join(';', h.Displays.Select(d => $"{d.Width}x{d.Height}@{d.RefreshHz}:{d.Hdr}"))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
    }

    private static void Query(string query, Action<ManagementObject> visit) { try { using var s = new ManagementObjectSearcher(query); foreach (var o in s.Get().Cast<ManagementObject>()) visit(o); } catch { } }
    private static string Text(ManagementObject o, string n, string d) => o[n]?.ToString() ?? d;
    private static int Int(ManagementObject o, string n) => int.TryParse(o[n]?.ToString(), out var v) ? v : 0;
    private static uint UInt(ManagementObject o, string n) => uint.TryParse(o[n]?.ToString(), out var v) ? v : 0;
    private static long Long(ManagementObject o, string n) => long.TryParse(o[n]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double AdapterRamGb(ManagementObject o) => Math.Max(0, Long(o, "AdapterRAM") / 1073741824d);
    private static bool DetectLibrary(string name) => File.Exists(Path.Combine(Environment.SystemDirectory, name));
    private static string ClassifyStorage(string text, string iface) => text.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe" : text.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD" : iface.Contains("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : "HDD/indéterminé";
    private static string NvidiaArchitecture(string n) => n.Contains("RTX 50", StringComparison.OrdinalIgnoreCase) ? "Blackwell" : n.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ? "Ada" : n.Contains("RTX 30", StringComparison.OrdinalIgnoreCase) ? "Ampere" : n.Contains("RTX 20", StringComparison.OrdinalIgnoreCase) ? "Turing" : "Non déterminée";
}
