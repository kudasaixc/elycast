namespace Elysium_Cast_IPTV.Services.ElySmart;

public sealed record BenchmarkProfile(ElySmartWorkload Id, double Stability, double VideoQuality, double Fluidity, double AudioQuality, double Efficiency);

public static class BenchmarkProfiles
{
    public static BenchmarkProfile Get(ElySmartWorkload profile) => profile switch
    {
        ElySmartWorkload.Iptv => new(profile, .34, .18, .25, .08, .15),
        ElySmartWorkload.Films => new(profile, .18, .38, .16, .14, .14),
        ElySmartWorkload.Series => new(profile, .24, .28, .18, .12, .18),
        ElySmartWorkload.Anime => new(profile, .17, .40, .21, .08, .14),
        ElySmartWorkload.Audio => new(profile, .18, .04, .18, .43, .17),
        _ => new(profile, .24, .23, .20, .17, .16)
    };
}
