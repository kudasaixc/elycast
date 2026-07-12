#pragma once

#include "ElyAudioCoreNative.h"
#include <array>
#include <cstdint>
#include <d3d11.h>
#include <mutex>
#include <vector>

class ElyAudioCoreScene
{
public:
    ~ElyAudioCoreScene();

    bool Render(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* target,
                uint32_t width, uint32_t height, double seconds);
    void Push(const double* bands, int count, float bass, float energy, float beat);
    void PushVisualFrame(const ElyAudioCoreVisualFrameNative& frame,
                         const ElyAudioCoreLineNative* bars, int barCount,
                         const ElyAudioCoreEllipseNative* particles, int particleCount,
                         const ElyAudioCoreEllipseNative* waves, int waveCount);
    void Beat(float strength);
    void SetPalette(const uint32_t* colors, int count);
    void SetSettings(const ElyAudioCoreSettingsNative& settings);
    void SetPointer(float x, float y);
    void SetLayout(float centerX, float centerY, float innerRadius, float unitScale);
    void SetBackground(const uint8_t* bgra, uint32_t width, uint32_t height, uint32_t stride);
    ElyAudioCoreStatsNative Stats() const;
    int TargetFps() const;
    bool VSync() const;
    uint64_t FrameSequence() const;
    void Reset();

private:
    struct PrimitiveVertex
    {
        float bound[2];
        float p0[2];
        float p1[2];
        float parameters[4];
        float color[4];
    };
    struct BackgroundConstants
    {
        float targetSource[4];
        float rootCache[4];
        float transform[4];
        float blurInfo[4];
    };
    struct PrimitiveConstants { float resolution[2], padding[2]; };

    bool EnsureResources(ID3D11Device* device);
    bool EnsureBackgroundTexture(ID3D11Device* device);
    bool EnsureBackgroundCache(ID3D11Device* device, ID3D11DeviceContext* context,
                               uint32_t targetWidth, uint32_t targetHeight,
                               const ElyAudioCoreVisualFrameNative& frame,
                               const ElyAudioCoreSettingsNative& settings);
    bool EnsurePrimitiveBuffer(ID3D11Device* device, size_t vertexCount);
    bool DrawFullscreen(ID3D11DeviceContext* context, ID3D11RenderTargetView* target,
                        ID3D11PixelShader* shader, ID3D11ShaderResourceView* source,
                        uint32_t width, uint32_t height, const BackgroundConstants& constants);
    void ReleaseBackgroundCache();
    void ReleaseDeviceResources();

    mutable std::mutex mutex_;
    ElyAudioCoreSettingsNative settings_
    {
        sizeof(ElyAudioCoreSettingsNative), 192, 1.0f, .75f, 30.0f,
        1, 1, 0, 1, 1, 60, .5f, .5f, .5f, .5f, .18f, .001f
    };
    ElyAudioCoreVisualFrameNative visualFrame_
    {
        sizeof(ElyAudioCoreVisualFrameNative), 1.0f, 1.0f, 1.045f, 0.0f, 0.0f
    };
    std::array<ElyAudioCoreLineNative, ELY_AUDIO_CORE_BAR_COUNT> bars_{};
    std::array<ElyAudioCoreEllipseNative, ELY_AUDIO_CORE_MAX_PARTICLES> particles_{};
    std::array<ElyAudioCoreEllipseNative, ELY_AUDIO_CORE_MAX_WAVES> waves_{};
    std::array<PrimitiveVertex,
        (ELY_AUDIO_CORE_BAR_COUNT + ELY_AUDIO_CORE_MAX_PARTICLES + ELY_AUDIO_CORE_MAX_WAVES) * 6> stagingVertices_{};
    int barCount_ = 0;
    int particleCount_ = 0;
    int waveCount_ = 0;
    uint64_t frameSequence_ = 0;

    // The legacy scalar feed remains ABI-compatible and useful to diagnostics,
    // but visual behaviour is sourced exclusively from the resolved WPF frame.
    float bass_ = 0.0f;
    float energy_ = 0.0f;
    float beat_ = 0.0f;
    std::array<uint32_t, 8> palette_{};

    ID3D11Device* owner_ = nullptr;
    ID3D11VertexShader* fullscreenVs_ = nullptr;
    ID3D11VertexShader* primitiveVs_ = nullptr;
    ID3D11PixelShader* backgroundSourcePs_ = nullptr;
    ID3D11PixelShader* blurPs_ = nullptr;
    ID3D11PixelShader* backgroundCompositePs_ = nullptr;
    ID3D11PixelShader* primitivePs_ = nullptr;
    ID3D11InputLayout* primitiveLayout_ = nullptr;
    ID3D11Buffer* backgroundConstants_ = nullptr;
    ID3D11Buffer* primitiveConstants_ = nullptr;
    ID3D11Buffer* primitiveVertices_ = nullptr;
    size_t primitiveVertexCapacity_ = 0;
    ID3D11SamplerState* sampler_ = nullptr;
    ID3D11BlendState* alphaBlend_ = nullptr;

    std::vector<uint8_t> pendingBackground_;
    uint32_t backgroundWidth_ = 0;
    uint32_t backgroundHeight_ = 0;
    uint32_t backgroundStride_ = 0;
    uint64_t backgroundRevision_ = 0;
    ID3D11ShaderResourceView* backgroundSource_ = nullptr;
    uint64_t uploadedBackgroundRevision_ = ~uint64_t{};

    ID3D11Texture2D* backgroundBaseTexture_ = nullptr;
    ID3D11RenderTargetView* backgroundBaseRtv_ = nullptr;
    ID3D11ShaderResourceView* backgroundBaseSrv_ = nullptr;
    ID3D11Texture2D* backgroundTempTexture_ = nullptr;
    ID3D11RenderTargetView* backgroundTempRtv_ = nullptr;
    ID3D11ShaderResourceView* backgroundTempSrv_ = nullptr;
    ID3D11Texture2D* backgroundBlurTexture_ = nullptr;
    ID3D11RenderTargetView* backgroundBlurRtv_ = nullptr;
    ID3D11ShaderResourceView* backgroundBlurSrv_ = nullptr;
    uint32_t cacheWidth_ = 0;
    uint32_t cacheHeight_ = 0;
    uint32_t cachedTargetWidth_ = 0;
    uint32_t cachedTargetHeight_ = 0;
    float cachedBlur_ = -1.0f;
    float cachedRootWidthDip_ = -1.0f;
    float cachedRootHeightDip_ = -1.0f;
    uint64_t cachedBackgroundRevision_ = ~uint64_t{};

    uint64_t framesInWindow_ = 0;
    uint64_t totalFrames_ = 0;
    double fpsStart_ = 0.0;
    double actualFps_ = 0.0;
    double frameMs_ = 0.0;
    int32_t lastError_ = 0;
};
