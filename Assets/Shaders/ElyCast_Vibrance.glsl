// ElyCast vibrance (part of ElyCast, same license as the application).
//
// Saturation boosts every colour alike, so already vivid colours clip
// (glows, red and pink scenes). Vibrance scales the boost by how dull a pixel
// is: muted colours gain saturation, saturated ones stay as they are and
// greys stay grey. It runs after scaling, on display RGB.

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC ElyCast vibrance

#define VIBRANCE 0.30

vec4 hook() {
    vec4 color = HOOKED_texOff(0);
    float high = max(color.r, max(color.g, color.b));
    float low = min(color.r, min(color.g, color.b));
    float saturation = high - low;
    float luma = dot(color.rgb, vec3(0.2126, 0.7152, 0.0722));
    float boost = 1.0 + VIBRANCE * (1.0 - saturation) * (1.0 - saturation);
    color.rgb = clamp(mix(vec3(luma), color.rgb, boost), 0.0, 1.0);
    return color;
}
