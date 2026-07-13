namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record CapabilityCost(string Id, int Quality, double Gpu, double Cpu, int VramMb, string Note);

public static class CapabilityDatabase
{
    private static readonly Dictionary<string, CapabilityCost> Costs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anime4k-fast"] = new("anime4k-fast", 73, 7, 2, 45, "Light anime upscaling"),
        ["anime4k-hq"] = new("anime4k-hq", 94, 18, 3, 95, "Quality Anime4K chain"),
        ["fsrcnnx"] = new("fsrcnnx", 88, 15, 3, 110, "General-purpose neural upscaling"),
        ["rtx-vsr"] = new("rtx-vsr", 91, 9, 1, 120, "Indicative cost; validated only by a real test"),
        ["elyflow"] = new("elyflow", 98, 12, 2, 140, "Interpolation ×2 NVIDIA"),
        ["visualizer-120"] = new("visualizer-120", 88, 1, 4, 30, "High-cadence audio visualizer"),
        ["background-dynamic"] = new("background-dynamic", 82, 2, 1, 30, "Animated blurred background")
    };

    public static CapabilityCost? Find(string id) => Costs.GetValueOrDefault(id);
    public static IReadOnlyCollection<CapabilityCost> All => Costs.Values;
}
