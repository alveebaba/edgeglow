// Original EdgeGlow DXGI capture backend. No GDI capture, cursor manipulation,
// screen files, cloud calls, or full-resolution CPU readback.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <new>
#include <vector>
using Microsoft::WRL::ComPtr;

static const char ShaderCode[] = R"hlsl(
Texture2D sourceImage : register(t0);
SamplerState linearSampler : register(s0);
cbuffer Settings : register(b0) { float4 frameSettings; float4 colorSettings; };
struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID) {
    Vertex v; v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1); return v;
}
float2 sourceUV(float2 uv) {
    if (frameSettings.z == 2) return float2(uv.y, 1 - uv.x);
    if (frameSettings.z == 3) return 1 - uv;
    if (frameSettings.z == 4) return float2(1 - uv.y, uv.x);
    return uv;
}
float3 toSrgb(float3 light) {
    return lerp(12.92 * light, 1.055 * pow(max(light, 0), 1.0 / 2.4) - 0.055, step(0.0031308, light));
}
float3 pqToScRgb(float3 pq) {
    float3 p = pow(max(pq, 0), 1.0 / 78.84375);
    float3 rec2020 = pow(max(p - 0.8359375, 0) / max(18.8515625 - 18.6875 * p, 0.000001), 1.0 / 0.1593017578125) * 125.0;
    return float3(dot(rec2020, float3(1.660491, -0.587641, -0.072850)),
        dot(rec2020, float3(-0.124550, 1.132900, -0.008350)),
        dot(rec2020, float3(-0.018151, -0.100579, 1.118730)));
}
float4 PS(Vertex input) : SV_TARGET {
    float3 sum = 0;
    // Sixteen bilinear taps cover the destination pixel footprint on the GPU.
    // This avoids point-sampled flicker from thin text and video detail.
    [unroll] for (int y = 0; y < 4; ++y)
        [unroll] for (int x = 0; x < 4; ++x) {
            float2 offset = (float2(x, y) - 1.5) * frameSettings.xy * 0.25;
            float3 value = sourceImage.SampleLevel(linearSampler, sourceUV(input.uv + offset), 0).rgb;
            if (frameSettings.w > 1.5) value = pqToScRgb(value);
            sum += value;
        }
    float3 rgb = sum / 16.0;
    if (frameSettings.w > 0.5) {
        // FP16 duplication is linear scRGB (1.0 = 80 nits). Normalize by the
        // Windows SDR white setting, then use a hue-preserving highlight
        // shoulder and encode to SDR sRGB for the non-HDR RGB strip.
        rgb = max(rgb, 0) / max(colorSettings.x, 1.0);
        float peak = max(rgb.r, max(rgb.g, rgb.b));
        if (peak > 0.8) rgb *= (0.8 + 0.2 * (1 - exp(-(peak - 0.8) / 0.2))) / peak;
        rgb = toSrgb(rgb);
    }
    return float4(saturate(rgb), 1);
}
)hlsl";

struct CaptureConstants { float frame[4]; float color[4]; };

static HRESULT CompileShaders(ComPtr<ID3DBlob>& vertex, ComPtr<ID3DBlob>& pixel) {
    ComPtr<ID3DBlob> errors;
    HRESULT hr = D3DCompile(ShaderCode, sizeof(ShaderCode) - 1, "EdgeGlow shader", nullptr, nullptr,
        "VS", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_ENABLE_STRICTNESS, 0, &vertex, &errors);
    if (FAILED(hr)) return hr;
    errors.Reset();
    return D3DCompile(ShaderCode, sizeof(ShaderCode) - 1, "EdgeGlow shader", nullptr, nullptr,
        "PS", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_ENABLE_STRICTNESS, 0, &pixel, &errors);
}

static HRESULT ReadSdrWhiteLevel(const WCHAR* displayName, float& value) {
    UINT32 pathCount = 0, modeCount = 0;
    LONG status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(), nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    for (UINT32 i = 0; i < pathCount; ++i) {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source = {};
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME; source.header.size = sizeof(source);
        source.header.adapterId = paths[i].sourceInfo.adapterId; source.header.id = paths[i].sourceInfo.id;
        if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS || _wcsicmp(source.viewGdiDeviceName, displayName)) continue;
        DISPLAYCONFIG_SDR_WHITE_LEVEL white = {};
        white.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL; white.header.size = sizeof(white);
        white.header.adapterId = paths[i].targetInfo.adapterId; white.header.id = paths[i].targetInfo.id;
        status = DisplayConfigGetDeviceInfo(&white.header);
        if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
        value = std::max(1.0f, std::min(10.0f, white.SDRWhiteLevel / 1000.0f));
        return S_OK;
    }
    return DXGI_ERROR_NOT_FOUND;
}

// Stable C ABI shared with the managed diagnostics adapter.
struct CaptureMetadata {
    UINT64 captureGeneration, imageGeneration, colorMetadataGeneration;
    int currentGenerationValid, textureFormat, outputColorSpace, conversionKind;
    float sdrWhiteMultiplier;
    int reserved;
};
static_assert(sizeof(CaptureMetadata) == 48, "Capture metadata ABI size changed");

struct NativeCapture {
    RECT bounds;
    UINT width, height;
    DXGI_OUTPUT_DESC outputDesc = {};
    ComPtr<IDXGIFactory1> factory;
    LUID adapterLuid = {};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> sourceCopy, reduced, staging;
    ComPtr<ID3D11ShaderResourceView> sourceView;
    ComPtr<ID3D11RenderTargetView> targetView;
    ComPtr<ID3D11VertexShader> vertexShader;
    ComPtr<ID3D11PixelShader> pixelShader;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<ID3D11Buffer> constants;
    D3D11_TEXTURE2D_DESC copiedDesc = {};
    bool acquired = false, haveImage = false, hdr = false, toneMapped = false;
    bool currentImageValid = false, protectedBlocked = false, colorDirty = false;
    UINT64 captureGeneration = 1, imageGeneration = 0, colorMetadataGeneration = 0;
    DXGI_COLOR_SPACE_TYPE colorSpace = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
    int conversionKind = 0;
    float sdrWhite = 1.0f;
    HRESULT lastError = S_OK;
    ULONGLONG retryAt = 0, colorCheckAt = 0, factoryRefreshAt = 0;
    ComPtr<IDXGIOutput6> output6;
    NativeCapture(RECT rect, UINT w, UINT h) : bounds(rect), width(w), height(h) {}
    ~NativeCapture() { Reset(); }

    void Reset() {
        if (acquired && duplication) duplication->ReleaseFrame();
        acquired = false;
        if (context) context->ClearState();
        sourceView.Reset(); targetView.Reset(); sourceCopy.Reset(); reduced.Reset(); staging.Reset();
        vertexShader.Reset(); pixelShader.Reset(); sampler.Reset(); rasterizer.Reset(); constants.Reset();
        duplication.Reset(); output6.Reset(); factory.Reset(); context.Reset(); device.Reset();
        copiedDesc = {};
        haveImage = currentImageValid = colorDirty = false;
        ++captureGeneration;
    }

    HRESULT Fail(HRESULT error) {
        Reset(); lastError = error; retryAt = GetTickCount64() + 750; return error;
    }

    HRESULT RefreshColorInfo(bool force = false) {
        ULONGLONG now = GetTickCount64();
        if (!force && now < colorCheckAt) return S_OK;
        colorCheckAt = now + 2000;
        // IsCurrent is checked at most every2s. Re-enumerate at least every10s
        // as a bound on metadata staleness even without a factory invalidation.
        if (!factory || !factory->IsCurrent() || now >= factoryRefreshAt) {
            ComPtr<IDXGIFactory1> refreshedFactory;
            HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&refreshedFactory));
            if (FAILED(hr)) return hr;
            ComPtr<IDXGIOutput> refreshedOutput;
            for (UINT a = 0; !refreshedOutput; ++a) {
                ComPtr<IDXGIAdapter1> adapter;
                hr = refreshedFactory->EnumAdapters1(a, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(hr)) return hr;
                DXGI_ADAPTER_DESC1 adapterDesc = {}; hr = adapter->GetDesc1(&adapterDesc);
                if (FAILED(hr)) return hr;
                for (UINT o = 0; ; ++o) {
                    ComPtr<IDXGIOutput> output;
                    hr = adapter->EnumOutputs(o, &output);
                    if (hr == DXGI_ERROR_NOT_FOUND) break;
                    if (FAILED(hr)) return hr;
                    DXGI_OUTPUT_DESC desc = {}; hr = output->GetDesc(&desc);
                    if (FAILED(hr)) return hr;
                    if (_wcsicmp(desc.DeviceName, outputDesc.DeviceName)) continue;
                    if (!desc.AttachedToDesktop || !EqualRect(&desc.DesktopCoordinates, &bounds)
                        || adapterDesc.AdapterLuid.LowPart != adapterLuid.LowPart || adapterDesc.AdapterLuid.HighPart != adapterLuid.HighPart)
                        return DXGI_ERROR_ACCESS_LOST;
                    refreshedOutput = output; outputDesc = desc; break;
                }
            }
            if (!refreshedOutput) return DXGI_ERROR_ACCESS_LOST;
            output6.Reset(); refreshedOutput.As(&output6);
            factory = refreshedFactory; factoryRefreshAt = now + 10000;
        }
        DXGI_COLOR_SPACE_TYPE nextSpace = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        if (output6) {
            DXGI_OUTPUT_DESC1 desc = {};
            HRESULT hr = output6->GetDesc1(&desc); if (FAILED(hr)) return hr;
            nextSpace = desc.ColorSpace;
        }
        bool nextHdr = nextSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 || nextSpace == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709;
        float nextWhite = 1.0f;
        if (nextHdr) { HRESULT hr = ReadSdrWhiteLevel(outputDesc.DeviceName, nextWhite); if (FAILED(hr)) return hr; }
        ApplyColorMetadata(nextSpace, nextHdr, nextWhite);
        return S_OK;
    }

    void ApplyColorMetadata(DXGI_COLOR_SPACE_TYPE nextSpace, bool nextHdr, float nextWhite) {
        if (colorMetadataGeneration == 0 || colorSpace != nextSpace || hdr != nextHdr || std::fabs(sdrWhite - nextWhite) > 0.0001f) {
            // Cached texels were acquired under the previous transfer/gamut
            // convention. A format such as R10 alone does not establish that
            // those same bytes can be decoded under new output metadata.
            bool encodingChanged = colorSpace != nextSpace || hdr != nextHdr;
            if (encodingChanged && sourceCopy) {
                currentImageValid = false;
                colorDirty = false;
            }
            colorSpace = nextSpace; hdr = nextHdr; sdrWhite = nextWhite;
            ++colorMetadataGeneration;
            // A white-level-only adjustment retains the input convention and
            // may reprocess a valid current-generation source without a present.
            if (!encodingChanged && currentImageValid && sourceCopy && imageGeneration == captureGeneration) colorDirty = true;
        }
    }

    CaptureMetadata Metadata() const {
        CaptureMetadata value = { captureGeneration, imageGeneration, colorMetadataGeneration,
            currentImageValid ? 1 : 0, static_cast<int>(copiedDesc.Format), static_cast<int>(colorSpace), conversionKind, sdrWhite, 0 };
        return value;
    }

    HRESULT Initialize() {
        HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr)) return hr;
        ComPtr<IDXGIAdapter1> selectedAdapter;
        ComPtr<IDXGIOutput> selectedOutput;
        for (UINT a = 0; !selectedOutput; ++a) {
            ComPtr<IDXGIAdapter1> adapter;
            hr = factory->EnumAdapters1(a, &adapter);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) return hr;
            for (UINT o = 0; ; ++o) {
                ComPtr<IDXGIOutput> output;
                hr = adapter->EnumOutputs(o, &output);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(hr)) return hr;
                DXGI_OUTPUT_DESC desc = {};
                if (SUCCEEDED(output->GetDesc(&desc)) && desc.AttachedToDesktop && EqualRect(&desc.DesktopCoordinates, &bounds)) {
                    selectedAdapter = adapter; selectedOutput = output; outputDesc = desc; break;
                }
            }
        }
        if (!selectedOutput) return DXGI_ERROR_NOT_FOUND;
        DXGI_ADAPTER_DESC1 adapterDesc = {};
        hr = selectedAdapter->GetDesc1(&adapterDesc); if (FAILED(hr)) return hr;
        adapterLuid = adapterDesc.AdapterLuid;
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
        D3D_FEATURE_LEVEL obtained;
        hr = D3D11CreateDevice(selectedAdapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
            &device, &obtained, &context);
        if (FAILED(hr)) return hr;
        selectedOutput.As(&output6);
        colorCheckAt = 0; factoryRefreshAt = GetTickCount64() + 10000;
        hr = RefreshColorInfo(true); if (FAILED(hr)) return hr;
        ComPtr<IDXGIOutput5> output5;
        if (SUCCEEDED(selectedOutput.As(&output5))) {
            const DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM };
            hr = output5->DuplicateOutput1(device.Get(), 0, ARRAYSIZE(formats), formats, &duplication);
        } else {
            ComPtr<IDXGIOutput1> output1;
            hr = selectedOutput.As(&output1);
            if (SUCCEEDED(hr)) hr = output1->DuplicateOutput(device.Get(), &duplication);
        }
        if (FAILED(hr)) return hr;
        return CreatePipeline();
    }

    HRESULT CreatePipeline() {
        ComPtr<ID3DBlob> vs, ps;
        HRESULT hr = CompileShaders(vs, ps);
        if (FAILED(hr)) return hr;
        hr = device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &vertexShader);
        if (FAILED(hr)) return hr;
        hr = device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &pixelShader);
        if (FAILED(hr)) return hr;
        D3D11_SAMPLER_DESC sample = {};
        sample.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sample.AddressU = sample.AddressV = sample.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sample.MaxLOD = D3D11_FLOAT32_MAX; sample.MaxAnisotropy = 1; sample.ComparisonFunc = D3D11_COMPARISON_NEVER;
        hr = device->CreateSamplerState(&sample, &sampler);
        if (FAILED(hr)) return hr;
        D3D11_RASTERIZER_DESC raster = {};
        raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
        hr = device->CreateRasterizerState(&raster, &rasterizer);
        if (FAILED(hr)) return hr;
        D3D11_BUFFER_DESC buffer = {};
        buffer.ByteWidth = sizeof(CaptureConstants); buffer.Usage = D3D11_USAGE_DEFAULT; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = device->CreateBuffer(&buffer, nullptr, &constants);
        if (FAILED(hr)) return hr;
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = width; desc.Height = height; desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        hr = device->CreateTexture2D(&desc, nullptr, &reduced);
        if (FAILED(hr)) return hr;
        hr = device->CreateRenderTargetView(reduced.Get(), nullptr, &targetView);
        if (FAILED(hr)) return hr;
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        return device->CreateTexture2D(&desc, nullptr, &staging);
    }

    HRESULT Render(ID3D11Texture2D* image, BYTE* destination) {
        D3D11_TEXTURE2D_DESC desc = {}; image->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM && desc.Format != DXGI_FORMAT_R16G16B16A16_FLOAT && desc.Format != DXGI_FORMAT_R10G10B10A2_UNORM)
            return DXGI_ERROR_UNSUPPORTED;
        if (!sourceCopy || copiedDesc.Width != desc.Width || copiedDesc.Height != desc.Height || copiedDesc.Format != desc.Format) {
            sourceView.Reset(); sourceCopy.Reset();
            copiedDesc = desc; copiedDesc.Usage = D3D11_USAGE_DEFAULT;
            copiedDesc.CPUAccessFlags = 0; copiedDesc.MiscFlags = 0;
            copiedDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE; copiedDesc.MipLevels = copiedDesc.ArraySize = 1;
            HRESULT hr = device->CreateTexture2D(&copiedDesc, nullptr, &sourceCopy);
            if (FAILED(hr)) return hr;
            hr = device->CreateShaderResourceView(sourceCopy.Get(), nullptr, &sourceView);
            if (FAILED(hr)) return hr;
        }
        // Full-size image remains exclusively on the GPU.
        context->CopyResource(sourceCopy.Get(), image);
        return RenderCached(destination);
    }

    HRESULT RenderCached(BYTE* destination) {
        if (!sourceCopy || !sourceView) return E_UNEXPECTED;
        DXGI_FORMAT format = copiedDesc.Format;
        float toneMode = format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 1.0f :
            ((colorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 && format == DXGI_FORMAT_R10G10B10A2_UNORM) ? 2.0f : 0.0f);
        if (format == DXGI_FORMAT_R10G10B10A2_UNORM && toneMode == 0 && colorSpace != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709) {
            conversionKind = 4; return DXGI_ERROR_UNSUPPORTED;
        }
        conversionKind = toneMode > 0 ? static_cast<int>(toneMode) : (hdr ? 3 : 0);
        toneMapped = toneMode > 0;
        CaptureConstants data = {{ 1.0f / width, 1.0f / height, static_cast<float>(outputDesc.Rotation), toneMode }, { sdrWhite, 0, 0, 0 }};
        context->UpdateSubresource(constants.Get(), 0, nullptr, &data, 0, 0);
        ID3D11Buffer* constantBuffers[] = { constants.Get() };
        ID3D11RenderTargetView* targets[] = { targetView.Get() };
        ID3D11ShaderResourceView* sources[] = { sourceView.Get() };
        ID3D11SamplerState* samplers[] = { sampler.Get() };
        D3D11_VIEWPORT viewport = { 0, 0, static_cast<float>(width), static_cast<float>(height), 0, 1 };
        context->OMSetRenderTargets(1, targets, nullptr);
        context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        context->RSSetState(rasterizer.Get()); context->RSSetViewports(1, &viewport);
        context->IASetInputLayout(nullptr); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertexShader.Get(), nullptr, 0);
        context->PSSetShader(pixelShader.Get(), nullptr, 0);
        context->PSSetConstantBuffers(0, 1, constantBuffers);
        context->PSSetSamplers(0, 1, samplers); context->PSSetShaderResources(0, 1, sources);
        context->Draw(3, 0);
        ID3D11ShaderResourceView* empty[] = { nullptr }; context->PSSetShaderResources(0, 1, empty);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        context->CopyResource(staging.Get(), reduced.Get());
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        HRESULT hr = context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return hr;
        for (UINT y = 0; y < height; ++y)
            std::memcpy(destination + y * width * 4, static_cast<const BYTE*>(mapped.pData) + y * mapped.RowPitch, width * 4);
        context->Unmap(staging.Get(), 0);
        colorDirty = false;
        return S_OK;
    }

    int Idle(BYTE* destination, int* flags, int* error) {
        if (colorDirty && currentImageValid && imageGeneration == captureGeneration && !protectedBlocked) {
            HRESULT hr = RenderCached(destination);
            if (FAILED(hr)) { *error = Fail(hr); return -1; }
            *flags = (hdr ? 1 : 0) | (toneMapped ? 2 : 0); *error = S_OK;
            return 2; // valid current-generation pixels reprocessed, not a new desktop image
        }
        *flags = (hdr ? 1 : 0) | (toneMapped ? 2 : 0) | (protectedBlocked ? 4 : 0);
        *error = static_cast<int>(lastError);
        return 0;
    }

    int Read(BYTE* destination, int bytes, int* flags, int* error) {
        *flags = (hdr ? 1 : 0) | (toneMapped ? 2 : 0) | (protectedBlocked ? 4 : 0); *error = static_cast<int>(lastError);
        if (!destination || bytes < static_cast<int>(width * height * 4)) { *error = E_INVALIDARG; return -1; }
        if (!duplication) {
            if (GetTickCount64() < retryAt) return -1;
            HRESULT hr = Initialize();
            if (FAILED(hr)) { *error = Fail(hr); return -1; }
        }
        if (acquired) {
            HRESULT hr = duplication->ReleaseFrame(); acquired = false;
            if (FAILED(hr)) { *error = Fail(hr); return -1; }
        }
        HRESULT hr = RefreshColorInfo();
        if (FAILED(hr)) { *error = Fail(hr); return -1; }
        DXGI_OUTDUPL_FRAME_INFO info = {};
        ComPtr<IDXGIResource> resource;
        hr = duplication->AcquireNextFrame(haveImage ? 0 : 16, &info, &resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return Idle(destination, flags, error);
        if (FAILED(hr)) { *error = Fail(hr); return -1; }
        acquired = true;
        if (info.ProtectedContentMaskedOut) {
            protectedBlocked = true; currentImageValid = false;
            *flags |= 4; *error = S_OK; return -2;
        }
        // Pointer-only frames cannot clear protection or recover a generation.
        if (info.LastPresentTime.QuadPart == 0) return Idle(destination, flags, error);
        ComPtr<ID3D11Texture2D> image;
        hr = resource.As(&image);
        if (SUCCEEDED(hr)) hr = Render(image.Get(), destination);
        if (FAILED(hr)) { *error = Fail(hr); return -1; }
        haveImage = currentImageValid = true; protectedBlocked = false;
        imageGeneration = captureGeneration; lastError = S_OK; *error = S_OK;
        *flags = (hdr ? 1 : 0) | (toneMapped ? 2 : 0);
        return 1;
    }

};

extern "C" __declspec(dllexport) void* __cdecl EG_Create(int x, int y, int width, int height, int smallWidth, int smallHeight) {
    if (width <= 0 || height <= 0 || smallWidth <= 0 || smallHeight <= 0 || smallWidth > 512 || smallHeight > 512) return nullptr;
    RECT bounds = { x, y, x + width, y + height };
    try { return new NativeCapture(bounds, smallWidth, smallHeight); } catch (...) { return nullptr; }
}
extern "C" __declspec(dllexport) int __cdecl EG_Read(void* handle, BYTE* buffer, int bytes, int* flags, int* error) {
    if (!handle || !flags || !error) return -1;
    try { return static_cast<NativeCapture*>(handle)->Read(buffer, bytes, flags, error); }
    catch (const std::bad_alloc&) { *error = static_cast<int>(static_cast<NativeCapture*>(handle)->Fail(E_OUTOFMEMORY)); return -1; }
    catch (...) { *error = static_cast<int>(static_cast<NativeCapture*>(handle)->Fail(E_FAIL)); return -1; }
}
extern "C" __declspec(dllexport) void __cdecl EG_Metadata(void* handle, CaptureMetadata* metadata) {
    if (handle && metadata) *metadata = static_cast<NativeCapture*>(handle)->Metadata();
}
extern "C" __declspec(dllexport) void __cdecl EG_Destroy(void* handle) { delete static_cast<NativeCapture*>(handle); }
// Shader compilation checks do not initialize a capture session or read pixels.
extern "C" __declspec(dllexport) int __cdecl EG_ShaderSelfTest() {
    try { ComPtr<ID3DBlob> vertex, pixel; return static_cast<int>(CompileShaders(vertex, pixel)); }
    catch (...) { return static_cast<int>(E_FAIL); }
}


// All pixels below are generated in memory. WARP is Microsoft's software D3D
// renderer; this test never duplicates an output or reads a desktop surface.
extern "C" __declspec(dllexport) int __cdecl EG_RenderSelfTest() {
    try {
        RECT bounds = { 0, 0, 64, 32 };
        NativeCapture test(bounds, 16, 8);
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL level;
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &test.device, &level, &test.context);
        if (FAILED(hr)) return hr;
        hr = test.CreatePipeline(); if (FAILED(hr)) return hr;
        std::vector<BYTE> pixels(64 * 32 * 4, 255), result(16 * 8 * 4);
        const BYTE quadrants[4][3] = { {0,0,255}, {0,255,0}, {255,0,0}, {255,255,255} };
        for (int y = 0; y < 32; ++y) for (int x = 0; x < 64; ++x) {
            int q = (y >= 16 ? 2 : 0) + (x >= 32 ? 1 : 0);
            for (int c = 0; c < 3; ++c) pixels[(y * 64 + x) * 4 + c] = quadrants[q][c];
        }
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = 64; desc.Height = 32; desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.Usage = D3D11_USAGE_DEFAULT;
        D3D11_SUBRESOURCE_DATA data = { pixels.data(), 64 * 4, 0 };
        ComPtr<ID3D11Texture2D> image;
        hr = test.device->CreateTexture2D(&desc, &data, &image); if (FAILED(hr)) return hr;
        const int expected[4][4] = { {0,1,2,3}, {2,0,3,1}, {3,2,1,0}, {1,3,0,2} };
        for (int rotation = 0; rotation < 4; ++rotation) {
            test.outputDesc.Rotation = static_cast<DXGI_MODE_ROTATION>(rotation + 1);
            hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
            for (int q = 0; q < 4; ++q) {
                int x = (q & 1) ? 12 : 4, y = (q & 2) ? 6 : 2;
                for (int c = 0; c < 3; ++c)
                    if (std::abs(static_cast<int>(result[(y * 16 + x) * 4 + c]) - quadrants[expected[rotation][q]][c]) > 1)
                        return static_cast<int>(0x8004E001u + rotation);
            }
        }
        image.Reset(); test.outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
        // FP16 scRGB 0.5 / Windows white level2 -> linear0.25 -> sRGB137.
        std::vector<USHORT> floating(64 * 32 * 4, 0x3800);
        for (int pixel = 0; pixel < 64 * 32; ++pixel) floating[pixel * 4 + 3] = 0x3c00;
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        data.pSysMem = floating.data(); data.SysMemPitch = 64 * 8;
        hr = test.device->CreateTexture2D(&desc, &data, &image); if (FAILED(hr)) return hr;
        test.sdrWhite = 2.0f;
        hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
        if (!test.toneMapped) return static_cast<int>(0x8004E005u);
        for (int c = 0; c < 3; ++c)
            if (std::abs(static_cast<int>(result[c]) - 137) > 1) return static_cast<int>(0x8004E006u);
        // True black remains black through the HDR transfer.
        for (int pixel = 0; pixel < 64 * 32; ++pixel) for (int c = 0; c < 3; ++c) floating[pixel * 4 + c] = 0;
        test.context->UpdateSubresource(image.Get(), 0, nullptr, floating.data(), 64 * 8, 0);
        hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
        for (int c = 0; c < 3; ++c) if (result[c] != 0) return static_cast<int>(0x8004E007u);
        // HDR10 neutral10nits (PQ code307/1023) -> linear0.125 -> sRGB99.
        image.Reset();
        std::vector<UINT> pqPixels(64 * 32, 307u | (307u << 10) | (307u << 20) | (3u << 30));
        desc.Format = DXGI_FORMAT_R10G10B10A2_UNORM;
        data.pSysMem = pqPixels.data(); data.SysMemPitch = 64 * 4;
        hr = test.device->CreateTexture2D(&desc, &data, &image); if (FAILED(hr)) return hr;
        test.hdr = true; test.colorSpace = DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020; test.sdrWhite = 1.0f;
        hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
        for (int c = 0; c < 3; ++c)
            if (std::abs(static_cast<int>(result[c]) - 99) > 2) return static_cast<int>(0x8004E008u);
        int checks = 7;
        // Fixed independent numeric reference vectors, tolerance2 encoded steps.
        const USHORT linearCases[5][3] = { {0x3c00,0,0}, {0x3c00,0x3400,0}, {0x4400,0xb800,0x3400}, {0x3400,0x3c00,0x3800}, {0xb800,0xb800,0xb800} };
        const BYTE expectedRgb[5][3] = { {247,0,0}, {247,132,0}, {255,0,71}, {132,247,181}, {0,0,0} };
        image.Reset(); desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        data.pSysMem = floating.data(); data.SysMemPitch = 64 * 8;
        hr = test.device->CreateTexture2D(&desc, &data, &image); if (FAILED(hr)) return hr;
        for (int sample = 0; sample < 5; ++sample) {
            for (int pixel = 0; pixel < 64 * 32; ++pixel) for (int c = 0; c < 3; ++c) floating[pixel * 4 + c] = linearCases[sample][c];
            test.context->UpdateSubresource(image.Get(), 0, nullptr, floating.data(), 64 * 8, 0);
            hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
            for (int c = 0; c < 3; ++c) if (std::abs(static_cast<int>(result[2-c]) - expectedRgb[sample][c]) > 2) return static_cast<int>(0x8004E020u + sample);
            ++checks;
        }
        // Changing the Windows SDR-white metadata with no new desktop pixels
        // must reprocess the valid cached GPU source exactly once.
        for (int pixel = 0; pixel < 64 * 32; ++pixel) for (int c = 0; c < 3; ++c) floating[pixel * 4 + c] = 0x3800;
        test.context->UpdateSubresource(image.Get(), 0, nullptr, floating.data(), 64 * 8, 0);
        test.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, true, 1.0f);
        hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
        test.currentImageValid = test.haveImage = true; test.imageGeneration = test.captureGeneration;
        UINT64 originalGeneration = test.captureGeneration, originalMetadata = test.colorMetadataGeneration;
        test.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, true, 2.0f);
        int flags = 0, error = 0;
        if (test.Idle(result.data(), &flags, &error) != 2 || result[0] < 136 || result[0] > 138
            || test.colorMetadataGeneration != originalMetadata + 1 || test.imageGeneration != originalGeneration
            || test.Idle(result.data(), &flags, &error) != 0) return static_cast<int>(0x8004E030u);
        ++checks;
        // A protected/invalid generation is never used for metadata re-render.
        test.currentImageValid = false; test.protectedBlocked = true;
        test.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, true, 3.0f);
        if (test.Idle(result.data(), &flags, &error) != 0 || !(flags & 4) || test.colorDirty) return static_cast<int>(0x8004E031u);
        ++checks;
        test.protectedBlocked = false;
        // Eight-bit RGB remains encoded RGB even on an HDR output; this
        // ambiguous transfer is explicitly named in diagnostics, not tone-mapped.
        image.Reset(); desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        for (int pixel = 0; pixel < 64 * 32; ++pixel) { pixels[pixel*4] = 23; pixels[pixel*4+1] = 80; pixels[pixel*4+2] = 170; }
        data.pSysMem = pixels.data(); data.SysMemPitch = 64 * 4;
        hr = test.device->CreateTexture2D(&desc, &data, &image); if (FAILED(hr)) return hr;
        hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
        if (result[0] != 170 || result[1] != 80 || result[2] != 23 || test.conversionKind != 3 || test.toneMapped) return static_cast<int>(0x8004E032u);
        ++checks;
        // HDR10 colored primaries exercise PQ decoding, gamut conversion,
        // clipping of negative out-of-gamut components and channel order.
        image.Reset(); desc.Format = DXGI_FORMAT_R10G10B10A2_UNORM;
        data.pSysMem = pqPixels.data(); data.SysMemPitch = 64 * 4;
        hr = test.device->CreateTexture2D(&desc, &data, &image); if (FAILED(hr)) return hr;
        test.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, true, 1.0f);
        const BYTE expectedPq[3][3] = { {126,0,0}, {0,105,0}, {0,0,105} };
        for (int primary = 0; primary < 3; ++primary) {
            std::fill(pqPixels.begin(), pqPixels.end(), (307u << (primary * 10)) | (3u << 30));
            test.context->UpdateSubresource(image.Get(), 0, nullptr, pqPixels.data(), 64 * 4, 0);
            hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
            for (int c = 0; c < 3; ++c) if (std::abs(static_cast<int>(result[2-c]) - expectedPq[primary][c]) > 2) return static_cast<int>(0x8004E033u + primary);
            ++checks;
        }
        test.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, false, 1.0f);
        std::fill(pqPixels.begin(), pqPixels.end(), 307u | (307u << 10) | (307u << 20) | (3u << 30));
        test.context->UpdateSubresource(image.Get(), 0, nullptr, pqPixels.data(), 64 * 4, 0);
        hr = test.Render(image.Get(), result.data()); if (FAILED(hr)) return hr;
        if (result[0] != 77 || result[1] != 77 || result[2] != 77 || test.conversionKind != 0) return static_cast<int>(0x8004E036u);
        ++checks;
        test.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, true, 1.0f);
        if (test.RenderCached(result.data()) != DXGI_ERROR_UNSUPPORTED || test.conversionKind != 4) return static_cast<int>(0x8004E037u);
        ++checks;
        return checks;
    } catch (...) { return static_cast<int>(E_FAIL); }
}
