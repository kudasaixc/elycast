#include "ElyAudioCoreScene.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <d3dcompiler.h>

namespace
{
    static_assert(sizeof(ElyAudioCoreSettingsNative) == 68);
    static_assert(sizeof(ElyAudioCoreVisualFrameNative) == 24);
    static_assert(sizeof(ElyAudioCoreLineNative) == 24);
    static_assert(sizeof(ElyAudioCoreEllipseNative) == 24);
    static_assert(sizeof(ElyAudioCoreStatsNative) == 40);

    constexpr float BackgroundCacheScale = 0.45f;
    constexpr float BackgroundMarginDip = 54.0f;

    constexpr const char* Shader = R"(
cbuffer BackgroundScene : register(b0)
{
    float4 targetSource; // target width/height, source width/height
    float4 rootCache;    // root width/height DIPs, cache width/height
    float4 transform;    // scale, translate X/Y DIPs, dim
    float4 blurInfo;     // radius in cache pixels, texel step X/Y, has background
};

Texture2D sceneTexture : register(t0);
SamplerState linearSampler : register(s0);

struct FullscreenOut { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
FullscreenOut FullscreenVS(uint id : SV_VertexID)
{
    FullscreenOut output;
    output.uv = float2((id << 1) & 2, id & 2);
    output.position = float4(output.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

float4 BackgroundSourcePS(FullscreenOut input) : SV_TARGET
{
    float2 root = max(rootCache.xy, 1.0.xx);
    float2 elementSize = root + 108.0.xx;
    float2 elementUv = (input.uv * root + 54.0.xx) / elementSize;
    float sourceAspect = max(targetSource.z, 1.0) / max(targetSource.w, 1.0);
    float elementAspect = elementSize.x / elementSize.y;
    if (elementAspect > sourceAspect)
        elementUv.y = (elementUv.y - 0.5) * (sourceAspect / elementAspect) + 0.5;
    else
        elementUv.x = (elementUv.x - 0.5) * (elementAspect / sourceAspect) + 0.5;
    return sceneTexture.Sample(linearSampler, elementUv);
}

float4 GaussianBlurPS(FullscreenOut input) : SV_TARGET
{
    float radius = clamp(blurInfo.x, 0.0, 48.0);
    if (radius < 0.01)
        return sceneTexture.Sample(linearSampler, input.uv);

    float sigma = max(0.5, radius / 3.0);
    float4 sum = 0;
    float weightSum = 0;
    [loop] for (int sampleIndex = -48; sampleIndex <= 48; ++sampleIndex)
    {
        float distance = abs((float)sampleIndex);
        if (distance > radius) continue;
        float weight = exp(-0.5 * distance * distance / (sigma * sigma));
        sum += sceneTexture.Sample(linearSampler,
            input.uv + float2(sampleIndex * blurInfo.y, sampleIndex * blurInfo.z)) * weight;
        weightSum += weight;
    }
    return sum / max(weightSum, 0.00001);
}

float3 ClassicRadialGradient(float2 uv)
{
    float distance = length((uv - float2(0.5, 0.45)) / float2(0.82, 0.82));
    const float3 center = float3(34.0, 11.0, 24.0) / 255.0;
    const float3 middle = float3(8.0, 7.0, 13.0) / 255.0;
    const float3 edge = 0.0.xxx;
    return distance <= 0.58
        ? lerp(center, middle, saturate(distance / 0.58))
        : lerp(middle, edge, saturate((distance - 0.58) / 0.42));
}

float4 BackgroundCompositePS(FullscreenOut input) : SV_TARGET
{
    float3 color = ClassicRadialGradient(input.uv);
    if (blurInfo.w > 0.5)
    {
        float2 root = max(rootCache.xy, 1.0.xx);
        float2 translated = float2(transform.y / root.x, transform.z / root.y);
        float2 cacheUv = (input.uv - 0.5.xx - translated) / max(transform.x, 0.001) + 0.5.xx;
        float4 photo = sceneTexture.Sample(linearSampler, cacheUv);
        color = lerp(color, photo.rgb, photo.a);
    }
    // WPF's black AudioBackgroundDimmer is above both the radial brush and image.
    color *= 1.0 - saturate(transform.w);
    return float4(color, 1.0);
}

cbuffer PrimitiveScene : register(b1)
{
    float2 primitiveResolution;
    float2 primitivePadding;
};

struct PrimitiveIn
{
    float2 bound : POSITION;
    float2 p0 : TEXCOORD0;
    float2 p1 : TEXCOORD1;
    float4 parameters : TEXCOORD2; // radius X/Y, thickness, kind
    float4 color : COLOR0;
};
struct PrimitiveOut
{
    float4 position : SV_POSITION;
    nointerpolation float2 p0 : TEXCOORD0;
    nointerpolation float2 p1 : TEXCOORD1;
    nointerpolation float4 parameters : TEXCOORD2;
    nointerpolation float4 color : COLOR0;
};

PrimitiveOut PrimitiveVS(PrimitiveIn input)
{
    PrimitiveOut output;
    float2 ndc = input.bound / max(primitiveResolution, 1.0.xx) * float2(2, -2) + float2(-1, 1);
    output.position = float4(ndc, 0, 1);
    output.p0 = input.p0;
    output.p1 = input.p1;
    output.parameters = input.parameters;
    output.color = input.color;
    return output;
}

float EllipseSignedDistance(float2 pixelPosition, float2 center, float2 radii)
{
    radii = max(radii, 0.001.xx);
    float2 normalized = (pixelPosition - center) / radii;
    float normalizedLength = length(normalized);
    float gradientLength = max(length(normalized / radii), 0.0001);
    return (normalizedLength - 1.0) / gradientLength;
}

float4 PrimitivePS(PrimitiveOut input) : SV_TARGET
{
    float signedDistance;
    if (input.parameters.w < 0.5) // WPF line with round start/end caps
    {
        float2 segment = input.p1 - input.p0;
        float projection = saturate(dot(input.position.xy - input.p0, segment) /
            max(dot(segment, segment), 0.0001));
        signedDistance = length(input.position.xy - (input.p0 + segment * projection))
            - input.parameters.z * 0.5;
    }
    else
    {
        float ellipseDistance = EllipseSignedDistance(input.position.xy, input.p0, input.parameters.xy);
        signedDistance = input.parameters.w < 1.5
            ? ellipseDistance // filled particle
            : abs(ellipseDistance) - input.parameters.z * 0.5; // shockwave stroke
    }

    // One physical pixel of analytic coverage closely matches WPF's aliased-off
    // vector edges while retaining subpixel pen widths at high DPI.
    float coverage = saturate(0.5 - signedDistance);
    return float4(input.color.rgb, input.color.a * coverage);
}
)";

    template <typename T>
    void Release(T*& value)
    {
        if (value) value->Release();
        value = nullptr;
    }

    HRESULT Compile(const char* entry, const char* profile, ID3DBlob** blob)
    {
        ID3DBlob* errors = nullptr;
        const HRESULT result = D3DCompile(Shader, std::strlen(Shader), "ELYCAST AudioCore+",
            nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, blob, &errors);
        if (FAILED(result) && errors)
        {
            char path[MAX_PATH]{};
            if (GetTempPathA(MAX_PATH, path) > 0)
            {
                strcat_s(path, "elycast_audiocore_shader.txt");
                FILE* file = nullptr;
                if (fopen_s(&file, path, "wb") == 0 && file)
                {
                    std::fprintf(file, "%s (%s)\r\n", entry, profile);
                    std::fwrite(errors->GetBufferPointer(), 1, errors->GetBufferSize(), file);
                    std::fclose(file);
                }
            }
        }
        Release(errors);
        return result;
    }

    double Now()
    {
        using clock = std::chrono::steady_clock;
        return std::chrono::duration<double>(clock::now().time_since_epoch()).count();
    }

    void DecodeColor(uint32_t color, float output[4])
    {
        output[0] = static_cast<float>((color >> 16) & 255u) / 255.0f;
        output[1] = static_cast<float>((color >> 8) & 255u) / 255.0f;
        output[2] = static_cast<float>(color & 255u) / 255.0f;
        output[3] = static_cast<float>((color >> 24) & 255u) / 255.0f;
    }
}

ElyAudioCoreScene::~ElyAudioCoreScene()
{
    Reset();
}

void ElyAudioCoreScene::ReleaseBackgroundCache()
{
    Release(backgroundBlurSrv_);
    Release(backgroundBlurRtv_);
    Release(backgroundBlurTexture_);
    Release(backgroundTempSrv_);
    Release(backgroundTempRtv_);
    Release(backgroundTempTexture_);
    Release(backgroundBaseSrv_);
    Release(backgroundBaseRtv_);
    Release(backgroundBaseTexture_);
    cacheWidth_ = cacheHeight_ = 0;
    cachedTargetWidth_ = cachedTargetHeight_ = 0;
    cachedBlur_ = cachedRootWidthDip_ = cachedRootHeightDip_ = -1.0f;
    cachedBackgroundRevision_ = ~uint64_t{};
}

void ElyAudioCoreScene::ReleaseDeviceResources()
{
    ReleaseBackgroundCache();
    Release(backgroundSource_);
    Release(alphaBlend_);
    Release(sampler_);
    Release(primitiveVertices_);
    Release(primitiveConstants_);
    Release(backgroundConstants_);
    Release(primitiveLayout_);
    Release(primitivePs_);
    Release(backgroundCompositePs_);
    Release(blurPs_);
    Release(backgroundSourcePs_);
    Release(primitiveVs_);
    Release(fullscreenVs_);
    primitiveVertexCapacity_ = 0;
    uploadedBackgroundRevision_ = ~uint64_t{};
    owner_ = nullptr;
}

void ElyAudioCoreScene::Reset()
{
    std::lock_guard lock(mutex_);
    ReleaseDeviceResources();
}

bool ElyAudioCoreScene::EnsureResources(ID3D11Device* device)
{
    if (owner_ == device && fullscreenVs_ && primitiveVs_ && backgroundSourcePs_ && blurPs_ &&
        backgroundCompositePs_ && primitivePs_ && primitiveLayout_ && backgroundConstants_ &&
        primitiveConstants_ && sampler_ && alphaBlend_)
        return true;

    ReleaseDeviceResources();
    owner_ = device;
    HRESULT result = S_OK;
    ID3DBlob* blob = nullptr;

    if (SUCCEEDED(result)) result = Compile("FullscreenVS", "vs_5_0", &blob);
    if (SUCCEEDED(result)) result = device->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &fullscreenVs_);
    Release(blob);

    if (SUCCEEDED(result)) result = Compile("PrimitiveVS", "vs_5_0", &blob);
    if (SUCCEEDED(result)) result = device->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &primitiveVs_);
    if (SUCCEEDED(result))
    {
        const D3D11_INPUT_ELEMENT_DESC elements[] =
        {
            { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, static_cast<UINT>(offsetof(PrimitiveVertex, bound)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, static_cast<UINT>(offsetof(PrimitiveVertex, p0)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 1, DXGI_FORMAT_R32G32_FLOAT, 0, static_cast<UINT>(offsetof(PrimitiveVertex, p1)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, static_cast<UINT>(offsetof(PrimitiveVertex, parameters)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, static_cast<UINT>(offsetof(PrimitiveVertex, color)), D3D11_INPUT_PER_VERTEX_DATA, 0 }
        };
        result = device->CreateInputLayout(elements, static_cast<UINT>(std::size(elements)),
            blob->GetBufferPointer(), blob->GetBufferSize(), &primitiveLayout_);
    }
    Release(blob);

    const struct { const char* entry; ID3D11PixelShader** shader; } pixelShaders[] =
    {
        { "BackgroundSourcePS", &backgroundSourcePs_ },
        { "GaussianBlurPS", &blurPs_ },
        { "BackgroundCompositePS", &backgroundCompositePs_ },
        { "PrimitivePS", &primitivePs_ }
    };
    for (const auto& pixelShader : pixelShaders)
    {
        if (FAILED(result)) break;
        result = Compile(pixelShader.entry, "ps_5_0", &blob);
        if (SUCCEEDED(result))
            result = device->CreatePixelShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, pixelShader.shader);
        Release(blob);
    }

    D3D11_BUFFER_DESC buffer{};
    buffer.ByteWidth = sizeof(BackgroundConstants);
    buffer.Usage = D3D11_USAGE_DYNAMIC;
    buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    buffer.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (SUCCEEDED(result)) result = device->CreateBuffer(&buffer, nullptr, &backgroundConstants_);
    buffer.ByteWidth = sizeof(PrimitiveConstants);
    if (SUCCEEDED(result)) result = device->CreateBuffer(&buffer, nullptr, &primitiveConstants_);

    D3D11_SAMPLER_DESC sampler{};
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    if (SUCCEEDED(result)) result = device->CreateSamplerState(&sampler, &sampler_);

    D3D11_BLEND_DESC blend{};
    auto& target = blend.RenderTarget[0];
    target.BlendEnable = TRUE;
    target.SrcBlend = D3D11_BLEND_SRC_ALPHA;
    target.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    target.BlendOp = D3D11_BLEND_OP_ADD;
    target.SrcBlendAlpha = D3D11_BLEND_ONE;
    target.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
    target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (SUCCEEDED(result)) result = device->CreateBlendState(&blend, &alphaBlend_);

    lastError_ = static_cast<int32_t>(result);
    if (FAILED(result))
    {
        ReleaseDeviceResources();
        return false;
    }
    return true;
}

bool ElyAudioCoreScene::EnsureBackgroundTexture(ID3D11Device* device)
{
    if (uploadedBackgroundRevision_ == backgroundRevision_) return true;
    Release(backgroundSource_);
    ReleaseBackgroundCache();
    uploadedBackgroundRevision_ = backgroundRevision_;
    if (pendingBackground_.empty() || backgroundWidth_ == 0 || backgroundHeight_ == 0) return true;

    D3D11_TEXTURE2D_DESC description{};
    description.Width = backgroundWidth_;
    description.Height = backgroundHeight_;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.Usage = D3D11_USAGE_IMMUTABLE;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    const D3D11_SUBRESOURCE_DATA initial{ pendingBackground_.data(), backgroundStride_, 0 };
    ID3D11Texture2D* texture = nullptr;
    HRESULT result = device->CreateTexture2D(&description, &initial, &texture);
    if (SUCCEEDED(result)) result = device->CreateShaderResourceView(texture, nullptr, &backgroundSource_);
    Release(texture);
    lastError_ = static_cast<int32_t>(result);
    return SUCCEEDED(result);
}

bool ElyAudioCoreScene::DrawFullscreen(ID3D11DeviceContext* context, ID3D11RenderTargetView* target,
    ID3D11PixelShader* shader, ID3D11ShaderResourceView* source,
    uint32_t width, uint32_t height, const BackgroundConstants& constants)
{
    D3D11_MAPPED_SUBRESOURCE mapped{};
    HRESULT result = context->Map(backgroundConstants_, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    if (FAILED(result)) { lastError_ = static_cast<int32_t>(result); return false; }
    std::memcpy(mapped.pData, &constants, sizeof(constants));
    context->Unmap(backgroundConstants_, 0);

    const D3D11_VIEWPORT viewport{ 0, 0, static_cast<float>(width), static_cast<float>(height), 0, 1 };
    context->RSSetViewports(1, &viewport);
    context->OMSetRenderTargets(1, &target, nullptr);
    context->OMSetBlendState(nullptr, nullptr, 0xffffffffu);
    context->IASetInputLayout(nullptr);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(fullscreenVs_, nullptr, 0);
    context->PSSetShader(shader, nullptr, 0);
    context->VSSetConstantBuffers(0, 1, &backgroundConstants_);
    context->PSSetConstantBuffers(0, 1, &backgroundConstants_);
    context->PSSetShaderResources(0, 1, &source);
    context->PSSetSamplers(0, 1, &sampler_);
    context->Draw(3, 0);
    ID3D11ShaderResourceView* nullResource = nullptr;
    context->PSSetShaderResources(0, 1, &nullResource);
    return true;
}

bool ElyAudioCoreScene::EnsureBackgroundCache(ID3D11Device* device, ID3D11DeviceContext* context,
    uint32_t targetWidth, uint32_t targetHeight, const ElyAudioCoreVisualFrameNative& frame,
    const ElyAudioCoreSettingsNative& settings)
{
    if (!backgroundSource_)
    {
        ReleaseBackgroundCache();
        return true;
    }

    const uint32_t desiredWidth = std::max(1u, static_cast<uint32_t>(std::lround(targetWidth * BackgroundCacheScale)));
    const uint32_t desiredHeight = std::max(1u, static_cast<uint32_t>(std::lround(targetHeight * BackgroundCacheScale)));
    const bool sizeChanged = desiredWidth != cacheWidth_ || desiredHeight != cacheHeight_;
    const bool dirty = sizeChanged || targetWidth != cachedTargetWidth_ || targetHeight != cachedTargetHeight_ ||
        std::abs(settings.blur - cachedBlur_) > 0.001f ||
        std::abs(frame.rootWidthDip - cachedRootWidthDip_) > 0.01f ||
        std::abs(frame.rootHeightDip - cachedRootHeightDip_) > 0.01f ||
        cachedBackgroundRevision_ != backgroundRevision_;
    if (!dirty) return true;

    if (sizeChanged)
    {
        ReleaseBackgroundCache();
        cacheWidth_ = desiredWidth;
        cacheHeight_ = desiredHeight;
        const auto createSurface = [&](ID3D11Texture2D** texture, ID3D11RenderTargetView** rtv,
                                       ID3D11ShaderResourceView** srv) -> HRESULT
        {
            D3D11_TEXTURE2D_DESC description{};
            description.Width = cacheWidth_;
            description.Height = cacheHeight_;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            HRESULT value = device->CreateTexture2D(&description, nullptr, texture);
            if (SUCCEEDED(value)) value = device->CreateRenderTargetView(*texture, nullptr, rtv);
            if (SUCCEEDED(value)) value = device->CreateShaderResourceView(*texture, nullptr, srv);
            return value;
        };
        HRESULT result = createSurface(&backgroundBaseTexture_, &backgroundBaseRtv_, &backgroundBaseSrv_);
        if (SUCCEEDED(result)) result = createSurface(&backgroundTempTexture_, &backgroundTempRtv_, &backgroundTempSrv_);
        if (SUCCEEDED(result)) result = createSurface(&backgroundBlurTexture_, &backgroundBlurRtv_, &backgroundBlurSrv_);
        if (FAILED(result))
        {
            lastError_ = static_cast<int32_t>(result);
            ReleaseBackgroundCache();
            return false;
        }
    }

    BackgroundConstants constants{};
    constants.targetSource[0] = static_cast<float>(targetWidth);
    constants.targetSource[1] = static_cast<float>(targetHeight);
    constants.targetSource[2] = static_cast<float>(backgroundWidth_);
    constants.targetSource[3] = static_cast<float>(backgroundHeight_);
    constants.rootCache[0] = std::max(frame.rootWidthDip, 1.0f);
    constants.rootCache[1] = std::max(frame.rootHeightDip, 1.0f);
    constants.rootCache[2] = static_cast<float>(cacheWidth_);
    constants.rootCache[3] = static_cast<float>(cacheHeight_);
    if (!DrawFullscreen(context, backgroundBaseRtv_, backgroundSourcePs_, backgroundSource_,
        cacheWidth_, cacheHeight_, constants)) return false;

    const float pixelsPerDip = 0.5f * (cacheWidth_ / constants.rootCache[0] + cacheHeight_ / constants.rootCache[1]);
    constants.blurInfo[0] = std::clamp(settings.blur * pixelsPerDip, 0.0f, 48.0f);
    constants.blurInfo[1] = 1.0f / static_cast<float>(cacheWidth_);
    constants.blurInfo[2] = 0.0f;
    if (!DrawFullscreen(context, backgroundTempRtv_, blurPs_, backgroundBaseSrv_,
        cacheWidth_, cacheHeight_, constants)) return false;
    constants.blurInfo[1] = 0.0f;
    constants.blurInfo[2] = 1.0f / static_cast<float>(cacheHeight_);
    if (!DrawFullscreen(context, backgroundBlurRtv_, blurPs_, backgroundTempSrv_,
        cacheWidth_, cacheHeight_, constants)) return false;

    cachedTargetWidth_ = targetWidth;
    cachedTargetHeight_ = targetHeight;
    cachedBlur_ = settings.blur;
    cachedRootWidthDip_ = frame.rootWidthDip;
    cachedRootHeightDip_ = frame.rootHeightDip;
    cachedBackgroundRevision_ = backgroundRevision_;
    return true;
}

bool ElyAudioCoreScene::EnsurePrimitiveBuffer(ID3D11Device* device, size_t vertexCount)
{
    if (primitiveVertices_ && primitiveVertexCapacity_ >= vertexCount) return true;
    Release(primitiveVertices_);
    primitiveVertexCapacity_ = std::max(vertexCount, stagingVertices_.size());
    D3D11_BUFFER_DESC description{};
    description.ByteWidth = static_cast<UINT>(primitiveVertexCapacity_ * sizeof(PrimitiveVertex));
    description.Usage = D3D11_USAGE_DYNAMIC;
    description.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    const HRESULT result = device->CreateBuffer(&description, nullptr, &primitiveVertices_);
    lastError_ = static_cast<int32_t>(result);
    return SUCCEEDED(result);
}

bool ElyAudioCoreScene::Render(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* target,
    uint32_t width, uint32_t height, double seconds)
{
    (void)seconds;
    const double begin = Now();
    std::lock_guard lock(mutex_);
    if (!EnsureResources(device) || !EnsureBackgroundTexture(device) ||
        !EnsureBackgroundCache(device, context, width, height, visualFrame_, settings_))
        return false;

    ID3D11RenderTargetView* targetView = nullptr;
    HRESULT result = device->CreateRenderTargetView(target, nullptr, &targetView);
    if (FAILED(result))
    {
        lastError_ = static_cast<int32_t>(result);
        return false;
    }

    BackgroundConstants background{};
    background.targetSource[0] = static_cast<float>(width);
    background.targetSource[1] = static_cast<float>(height);
    background.targetSource[2] = static_cast<float>(backgroundWidth_);
    background.targetSource[3] = static_cast<float>(backgroundHeight_);
    background.rootCache[0] = std::max(visualFrame_.rootWidthDip, 1.0f);
    background.rootCache[1] = std::max(visualFrame_.rootHeightDip, 1.0f);
    background.rootCache[2] = static_cast<float>(cacheWidth_);
    background.rootCache[3] = static_cast<float>(cacheHeight_);
    background.transform[0] = visualFrame_.backgroundScale;
    background.transform[1] = visualFrame_.backgroundTranslateXDip;
    background.transform[2] = visualFrame_.backgroundTranslateYDip;
    background.transform[3] = settings_.dim;
    background.blurInfo[3] = backgroundBlurSrv_ ? 1.0f : 0.0f;
    if (!DrawFullscreen(context, targetView, backgroundCompositePs_, backgroundBlurSrv_, width, height, background))
    {
        Release(targetView);
        return false;
    }

    size_t vertexCount = 0;
    const auto appendQuad = [&](float left, float top, float right, float bottom,
        float p0x, float p0y, float p1x, float p1y,
        float radiusX, float radiusY, float thickness, float kind, uint32_t color)
    {
        const float bounds[6][2] =
        {
            { left, top }, { right, top }, { left, bottom },
            { left, bottom }, { right, top }, { right, bottom }
        };
        float decoded[4];
        DecodeColor(color, decoded);
        for (const auto& bound : bounds)
        {
            auto& vertex = stagingVertices_[vertexCount++];
            vertex.bound[0] = bound[0]; vertex.bound[1] = bound[1];
            vertex.p0[0] = p0x; vertex.p0[1] = p0y;
            vertex.p1[0] = p1x; vertex.p1[1] = p1y;
            vertex.parameters[0] = radiusX; vertex.parameters[1] = radiusY;
            vertex.parameters[2] = thickness; vertex.parameters[3] = kind;
            std::copy_n(decoded, 4, vertex.color);
        }
    };

    // WPF draw order is significant because every primitive uses source-over.
    for (int index = 0; index < particleCount_; ++index)
    {
        const auto& particle = particles_[index];
        const float x = particle.x * width;
        const float y = particle.y * height;
        const float radiusX = particle.radiusX * width;
        const float radiusY = particle.radiusY * height;
        appendQuad(x - radiusX - 1, y - radiusY - 1, x + radiusX + 1, y + radiusY + 1,
            x, y, 0, 0, radiusX, radiusY, 0, 1, particle.color);
    }
    for (int index = 0; index < waveCount_; ++index)
    {
        const auto& wave = waves_[index];
        const float x = wave.x * width;
        const float y = wave.y * height;
        const float radiusX = wave.radiusX * width;
        const float radiusY = wave.radiusY * height;
        const float thickness = wave.thickness * height;
        const float padding = thickness * 0.5f + 1;
        appendQuad(x - radiusX - padding, y - radiusY - padding,
            x + radiusX + padding, y + radiusY + padding,
            x, y, 0, 0, radiusX, radiusY, thickness, 2, wave.color);
    }
    for (int index = 0; index < barCount_; ++index)
    {
        const auto& bar = bars_[index];
        const float x0 = bar.x0 * width;
        const float y0 = bar.y0 * height;
        const float x1 = bar.x1 * width;
        const float y1 = bar.y1 * height;
        const float thickness = bar.thickness * height;
        const float padding = thickness * 0.5f + 1;
        appendQuad(std::min(x0, x1) - padding, std::min(y0, y1) - padding,
            std::max(x0, x1) + padding, std::max(y0, y1) + padding,
            x0, y0, x1, y1, 0, 0, thickness, 0, bar.color);
    }

    if (vertexCount > 0)
    {
        if (!EnsurePrimitiveBuffer(device, vertexCount))
        {
            Release(targetView);
            return false;
        }
        D3D11_MAPPED_SUBRESOURCE mapped{};
        result = context->Map(primitiveVertices_, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        if (FAILED(result))
        {
            lastError_ = static_cast<int32_t>(result);
            Release(targetView);
            return false;
        }
        std::memcpy(mapped.pData, stagingVertices_.data(), vertexCount * sizeof(PrimitiveVertex));
        context->Unmap(primitiveVertices_, 0);

        PrimitiveConstants constants{ { static_cast<float>(width), static_cast<float>(height) }, { 0, 0 } };
        result = context->Map(primitiveConstants_, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        if (FAILED(result))
        {
            lastError_ = static_cast<int32_t>(result);
            Release(targetView);
            return false;
        }
        std::memcpy(mapped.pData, &constants, sizeof(constants));
        context->Unmap(primitiveConstants_, 0);

        const D3D11_VIEWPORT viewport{ 0, 0, static_cast<float>(width), static_cast<float>(height), 0, 1 };
        const UINT stride = sizeof(PrimitiveVertex);
        const UINT offset = 0;
        context->RSSetViewports(1, &viewport);
        context->OMSetRenderTargets(1, &targetView, nullptr);
        context->OMSetBlendState(alphaBlend_, nullptr, 0xffffffffu);
        context->IASetInputLayout(primitiveLayout_);
        context->IASetVertexBuffers(0, 1, &primitiveVertices_, &stride, &offset);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(primitiveVs_, nullptr, 0);
        context->PSSetShader(primitivePs_, nullptr, 0);
        context->VSSetConstantBuffers(1, 1, &primitiveConstants_);
        context->PSSetConstantBuffers(1, 1, &primitiveConstants_);
        context->Draw(static_cast<UINT>(vertexCount), 0);
        context->OMSetBlendState(nullptr, nullptr, 0xffffffffu);
    }
    Release(targetView);

    lastError_ = 0;
    ++framesInWindow_;
    ++totalFrames_;
    const double now = Now();
    const double elapsedMs = (now - begin) * 1000.0;
    frameMs_ = frameMs_ <= 0 ? elapsedMs : frameMs_ * 0.94 + elapsedMs * 0.06;
    if (fpsStart_ <= 0) fpsStart_ = now;
    if (now - fpsStart_ >= 0.75)
    {
        actualFps_ = framesInWindow_ / (now - fpsStart_);
        framesInWindow_ = 0;
        fpsStart_ = now;
    }
    return true;
}

void ElyAudioCoreScene::Push(const double* bands, int count, float bass, float energy, float beat)
{
    (void)bands;
    (void)count;
    std::lock_guard lock(mutex_);
    bass_ = std::clamp(bass, 0.0f, 1.0f);
    energy_ = std::clamp(energy, 0.0f, 1.0f);
    beat_ = std::clamp(beat, 0.0f, 1.0f);
}

void ElyAudioCoreScene::PushVisualFrame(const ElyAudioCoreVisualFrameNative& frame,
    const ElyAudioCoreLineNative* bars, int barCount,
    const ElyAudioCoreEllipseNative* particles, int particleCount,
    const ElyAudioCoreEllipseNative* waves, int waveCount)
{
    std::lock_guard lock(mutex_);
    visualFrame_ = frame;
    barCount_ = std::clamp(barCount, 0, ELY_AUDIO_CORE_BAR_COUNT);
    particleCount_ = std::clamp(particleCount, 0, ELY_AUDIO_CORE_MAX_PARTICLES);
    waveCount_ = std::clamp(waveCount, 0, ELY_AUDIO_CORE_MAX_WAVES);
    if (bars && barCount_ > 0) std::copy_n(bars, barCount_, bars_.begin());
    if (particles && particleCount_ > 0) std::copy_n(particles, particleCount_, particles_.begin());
    if (waves && waveCount_ > 0) std::copy_n(waves, waveCount_, waves_.begin());
    ++frameSequence_;
}

void ElyAudioCoreScene::Beat(float strength)
{
    std::lock_guard lock(mutex_);
    beat_ = std::max(beat_, std::clamp(strength, 0.0f, 1.0f));
}

void ElyAudioCoreScene::SetPalette(const uint32_t* colors, int count)
{
    std::lock_guard lock(mutex_);
    for (size_t index = 0; index < palette_.size(); ++index)
        palette_[index] = colors && count > 0 ? colors[index % static_cast<size_t>(count)] : 0xff8b5cf6u;
}

void ElyAudioCoreScene::SetSettings(const ElyAudioCoreSettingsNative& settings)
{
    std::lock_guard lock(mutex_);
    settings_ = settings;
}

void ElyAudioCoreScene::SetPointer(float x, float y)
{
    std::lock_guard lock(mutex_);
    settings_.mouseX = std::clamp(x, 0.0f, 1.0f);
    settings_.mouseY = std::clamp(y, 0.0f, 1.0f);
}

void ElyAudioCoreScene::SetLayout(float centerX, float centerY, float innerRadius, float unitScale)
{
    std::lock_guard lock(mutex_);
    settings_.centerX = centerX;
    settings_.centerY = centerY;
    settings_.innerRadius = innerRadius;
    settings_.unitScale = unitScale;
}

void ElyAudioCoreScene::SetBackground(const uint8_t* data, uint32_t width, uint32_t height, uint32_t stride)
{
    std::lock_guard lock(mutex_);
    pendingBackground_.clear();
    backgroundWidth_ = width;
    backgroundHeight_ = height;
    backgroundStride_ = stride;
    if (data && width > 0 && height > 0 && stride >= width * 4u)
        pendingBackground_.assign(data, data + static_cast<size_t>(stride) * height);
    ++backgroundRevision_;
}

ElyAudioCoreStatsNative ElyAudioCoreScene::Stats() const
{
    std::lock_guard lock(mutex_);
    return { sizeof(ElyAudioCoreStatsNative), 1, actualFps_, frameMs_, totalFrames_, lastError_ };
}

int ElyAudioCoreScene::TargetFps() const
{
    std::lock_guard lock(mutex_);
    return std::clamp(settings_.targetFps, 30, 360);
}

bool ElyAudioCoreScene::VSync() const
{
    std::lock_guard lock(mutex_);
    return settings_.vsync != 0;
}

uint64_t ElyAudioCoreScene::FrameSequence() const
{
    std::lock_guard lock(mutex_);
    return frameSequence_;
}
