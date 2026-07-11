namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record CapabilityCost(string Id, int Quality, double Gpu, double Cpu, int VramMb, string Note);

public static class CapabilityDatabase
{
    private static readonly Dictionary<string, CapabilityCost> Costs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anime4k-fast"] = new("anime4k-fast", 73, 7, 2, 45, "Upscaling anime léger"),
        ["anime4k-hq"] = new("anime4k-hq", 94, 18, 3, 95, "Chaîne Anime4K qualité"),
        ["fsrcnnx"] = new("fsrcnnx", 88, 15, 3, 110, "Upscaling neuronal généraliste"),
        ["rtx-vsr"] = new("rtx-vsr", 91, 9, 1, 120, "Coût indicatif, validé seulement par test réel"),
        ["elyflow"] = new("elyflow", 98, 12, 2, 140, "Interpolation ×2 NVIDIA"),
        ["visualizer-120"] = new("visualizer-120", 88, 1, 4, 30, "Visualiseur audio haute cadence"),
        ["background-dynamic"] = new("background-dynamic", 82, 2, 1, 30, "Fond animé et flouté")
    };

    public static CapabilityCost? Find(string id) => Costs.GetValueOrDefault(id);
    public static IReadOnlyCollection<CapabilityCost> All => Costs.Values;
}
