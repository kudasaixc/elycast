using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;

namespace Elysium_Cast_IPTV.Services.Pipeline;

/// <summary>Built-in ELYSOUND+ presets plus the single editable custom profile.</summary>
public static class ElySoundProfiles
{
    public static IEnumerable<ElySoundProfile> All(Settings settings)
    {
        foreach (var p in ElySoundCatalog.BuiltIn) yield return p;
        yield return settings.ElySoundCustomProfile ?? ElySoundProfile.DefaultCustom();
    }

    public static ElySoundProfile Active(Settings settings)
    {
        if (string.Equals(settings.ElySoundPresetId, "custom", StringComparison.OrdinalIgnoreCase))
            return settings.ElySoundCustomProfile ??= ElySoundProfile.DefaultCustom();
        return ElySoundCatalog.BuiltIn.FirstOrDefault(p => p.Id.Equals(settings.ElySoundPresetId, StringComparison.OrdinalIgnoreCase))
               ?? ElySoundCatalog.BuiltIn[0];
    }
}
