#pragma once

#include <cstdint>

constexpr int ELY_AUDIO_CORE_BAR_COUNT = 112;
constexpr int ELY_AUDIO_CORE_MAX_PARTICLES = 384;
constexpr int ELY_AUDIO_CORE_MAX_WAVES = 6;

struct ElyAudioCoreSettingsNative
{
    uint32_t structSize;
    int32_t particleCount;
    float particleDistance;
    float dim;
    float blur;
    int32_t slowZoom;
    int32_t slowPan;
    int32_t parallax;
    int32_t shake;
    int32_t vsync;
    int32_t targetFps;
    float mouseX;
    float mouseY;
    float centerX;
    float centerY;
    float innerRadius;
    float unitScale;
};

// Positions/radii are normalized against the complete OverlayRoot. Colours
// use WPF's resolved AARRGGBB value, including its quantized opacity.
struct ElyAudioCoreLineNative
{
    float x0, y0, x1, y1, thickness;
    uint32_t color;
};

struct ElyAudioCoreEllipseNative
{
    float x, y, radiusX, radiusY, thickness;
    uint32_t color;
};

struct ElyAudioCoreVisualFrameNative
{
    uint32_t structSize;
    float rootWidthDip;
    float rootHeightDip;
    float backgroundScale;
    float backgroundTranslateXDip;
    float backgroundTranslateYDip;
};

struct ElyAudioCoreStatsNative
{
    uint32_t structSize;
    int32_t active;
    double actualFps;
    double gpuFrameMs;
    uint64_t frames;
    int32_t lastError;
};
