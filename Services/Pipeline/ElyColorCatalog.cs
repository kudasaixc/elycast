using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.Pipeline;

/// <summary>Built-in ELYCOLOR looks plus the user's saved custom filters.</summary>
public static class ElyColorCatalog
{

    // mpv equalizer values, range -100..100. Guiding principles: brightness is
    // a black-floor offset (washes the image - avoid, prefer gamma), contrast
    // above ~+18 combined with negative gamma crushes shadow detail, hue
    // shifts tint skin very fast (±2 max on live-action), and every look must
    // stay watchable over a full film, not just on a demo scene.
    public static readonly IReadOnlyList<ElyColorFilter> BuiltIn = new ElyColorFilter[]
    {
        new()
        {
            Id = "off",
            Name = "ELYCOLOR Off",
            IncludeVideoPipeline = false
        },
        new()
        {
            // Flat anime masters take saturation well, but +28 bled the cel
            // aplats; the hue shift tinted skin - gone. Slight gamma lift
            // keeps line-art detail in dark scenes.
            Id = "elycolor-anime",
            Name = "ELYCOLOR Vivid Anime",
            IncludeVideoPipeline = false,
            Saturation = 22,
            Brightness = 0,
            Contrast = 10,
            Gamma = -2
        },
        new()
        {
            // Subtle pop for live-action: skin must stay natural, so about
            // half the anime boost, and no black-floor lift.
            Id = "elycolor-film",
            Name = "ELYCOLOR Vivid Film",
            IncludeVideoPipeline = false,
            Saturation = 12,
            Brightness = 0,
            Contrast = 8,
            Gamma = -2
        },
        new()
        {
            // Cold desaturated dread. The old contrast 25 / gamma -12 /
            // brightness -7 trio crushed everything the genre lives on -
            // horror needs *readable* shadows to be scary.
            Id = "elycolor-horror",
            Name = "ELYCOLOR Horror",
            IncludeVideoPipeline = false,
            Saturation = -22,
            Brightness = -3,
            Contrast = 15,
            Gamma = -6,
            Hue = -5
        },
        new()
        {
            // Punchy grass and kits without neon turf; brightness lift
            // removed (washed the pitch on day games).
            Id = "elycolor-sport",
            Name = "ELYCOLOR Sport Live",
            IncludeVideoPipeline = false,
            Saturation = 15,
            Brightness = 0,
            Contrast = 8,
            Gamma = 0
        },
        new()
        {
            // True black & white: silver-print look = strong contrast but
            // neutral floor, and a light gamma dip for dense blacks that
            // still hold texture.
            Id = "elycolor-noir",
            Name = "ELYCOLOR Deep Noir",
            IncludeVideoPipeline = false,
            Saturation = -100,
            Brightness = 0,
            Contrast = 22,
            Gamma = -4
        },
        new()
        {
            // Low-fatigue evening look: softened contrast, raised gamma to
            // open shadows, near-neutral colour.
            Id = "elycolor-soft",
            Name = "ELYCOLOR Soft Comfort",
            IncludeVideoPipeline = false,
            Saturation = 3,
            Brightness = 0,
            Contrast = -6,
            Gamma = 6
        }
    };

    public static IEnumerable<ElyColorFilter> All(Settings settings) =>
        BuiltIn.Concat(settings.ElyColorCustomFilters ?? new());

    public static ElyColorFilter? Find(Settings settings, string id) =>
        All(settings).FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static ElyColorFilter Active(Settings settings) =>
        Find(settings, settings.ElyColorFilterId) ?? BuiltIn[0];
}
