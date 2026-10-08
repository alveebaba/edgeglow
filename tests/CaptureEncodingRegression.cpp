// Synthetic C06 regression: includes the exact production implementation copied
// by run-capture-encoding-tests.ps1. No output duplication or desktop reads.
#include "NativeCaptureUnderTest.cpp"
#include <cstdio>
#include <stdexcept>

static void RequireHr(HRESULT hr) { if (FAILED(hr)) throw std::runtime_error("Synthetic D3D setup/render failed"); }
struct Fixture {
    NativeCapture capture;
    ComPtr<ID3D11Texture2D> texture;
    std::vector<BYTE> output;
    Fixture() : capture(RECT{0,0,64,32}, 16, 8), output(16*8*4, 0) {
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL obtained;
        RequireHr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &capture.device, &obtained, &capture.context));
        RequireHr(capture.CreatePipeline());
    }
    void AcceptRendered() {
        RequireHr(capture.Render(texture.Get(), output.data()));
        // Same accepted-image bookkeeping as NativeCapture.Read after Render.
        // No desktop API is invoked to manufacture the generated test image.
        capture.haveImage = capture.currentImageValid = true;
        capture.protectedBlocked = false;
        capture.imageGeneration = capture.captureGeneration;
    }
    void Fresh10(UINT red, UINT green, UINT blue) {
        texture.Reset();
        std::vector<UINT> texels(64*32, red | (green << 10) | (blue << 20) | (3u << 30));
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width=64; desc.Height=32; desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_R10G10B10A2_UNORM; desc.Usage=D3D11_USAGE_DEFAULT;
        D3D11_SUBRESOURCE_DATA data = { texels.data(), 64*4, 0 };
        RequireHr(capture.device->CreateTexture2D(&desc, &data, &texture));
        AcceptRendered();
    }
    void FreshLinear() {
        texture.Reset(); std::vector<USHORT> texels(64*32*4);
        for (int i=0;i<64*32;++i) { texels[i*4]=0x3800; texels[i*4+1]=0x3c00; texels[i*4+2]=0x3000; texels[i*4+3]=0x3c00; }
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width=64; desc.Height=32; desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_R16G16B16A16_FLOAT; desc.Usage=D3D11_USAGE_DEFAULT;
        D3D11_SUBRESOURCE_DATA data = { texels.data(), 64*8, 0 };
        RequireHr(capture.device->CreateTexture2D(&desc, &data, &texture)); AcceptRendered();
    }
    int Idle() { int flags=0, error=0; return capture.Idle(output.data(), &flags, &error); }
};

static int failures=0, cases=0;
static void Check(const char* name, bool okay) { ++cases; std::printf("%s %s\n", okay?"PASS":"FAIL",name); if(!okay) ++failures; }
static void PrintPixel(const char* name, Fixture& f, int idle) {
    std::printf("%s: idle=%d valid=%d RGB=(%u,%u,%u) imageGeneration=%llu captureGeneration=%llu conversion=%d metadataRevision=%llu\n",
        name,idle,f.capture.currentImageValid?1:0, f.output[2],f.output[1],f.output[0],
        f.capture.imageGeneration,f.capture.captureGeneration,f.capture.conversionKind,f.capture.colorMetadataGeneration);
}

static void Transition(bool fromPq) {
    Fixture f;
    DXGI_COLOR_SPACE_TYPE before = fromPq ? DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
    DXGI_COLOR_SPACE_TYPE after = fromPq ? DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 : DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
    f.capture.ApplyColorMetadata(before,fromPq,1.0f);
    // Saturated colored inputs expose transfer and gamut changes, unlike gray.
    if(fromPq) f.Fresh10(700,100,50); else f.Fresh10(100,650,350);
    std::vector<BYTE> held=f.output;
    UINT64 imageGeneration=f.capture.imageGeneration, captureGeneration=f.capture.captureGeneration;
    PrintPixel(fromPq?"PQ retained before SDR metadata":"SDR retained before PQ metadata",f,-99);
    f.capture.ApplyColorMetadata(after,!fromPq,1.0f);
    int idle=f.Idle();
    PrintPixel(fromPq?"PQ cache after SDR metadata":"SDR cache after PQ metadata",f,idle);
    Check(fromPq?"C06_PQ_TO_SDR_CACHE_INVALIDATED":"C06_SDR_TO_PQ_CACHE_INVALIDATED",
        idle==0 && !f.capture.currentImageValid && f.output==held && f.capture.imageGeneration==imageGeneration && f.capture.captureGeneration==captureGeneration);
    int again=f.Idle();
    Check(fromPq?"PQ_TO_SDR_REPEATED_IDLE_CANNOT_RECOVER":"SDR_TO_PQ_REPEATED_IDLE_CANNOT_RECOVER",
        again==0 && !f.capture.currentImageValid && f.output==held);
    // A real newly supplied generated image with the new convention may resume.
    f.Fresh10(409,204,818);
    Fixture reference; reference.capture.ApplyColorMetadata(after,!fromPq,1.0f); reference.Fresh10(409,204,818);
    Check(fromPq?"PQ_TO_SDR_FRESH_IMAGE_ACCEPTED":"SDR_TO_PQ_FRESH_IMAGE_ACCEPTED",
        f.capture.currentImageValid && f.output==reference.output && f.capture.conversionKind==(fromPq?0:2) && f.Idle()==0);
}

static void CompatibleWhiteAndGuards() {
    Fixture f; f.capture.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,true,1.0f); f.FreshLinear();
    UINT64 image=f.capture.imageGeneration, generation=f.capture.captureGeneration, metadata=f.capture.colorMetadataGeneration;
    std::vector<BYTE> previous=f.output;
    f.capture.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,true,2.0f);
    Check("SCRGB_WHITE_LEVEL_ONLY_REPROCESSES_COMPATIBLE_CACHE",f.Idle()==2 && f.capture.currentImageValid && f.output!=previous && f.capture.imageGeneration==image && f.capture.captureGeneration==generation && f.capture.colorMetadataGeneration==metadata+1);
    previous=f.output; metadata=f.capture.colorMetadataGeneration;
    f.capture.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,true,2.0f);
    Check("EQUAL_METADATA_DOES_NOT_INVENT_FRAME",f.Idle()==0 && f.output==previous && f.capture.colorMetadataGeneration==metadata);
    f.capture.currentImageValid=false; f.capture.protectedBlocked=true;
    f.capture.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,true,3.0f);
    Check("PROTECTED_CACHE_CANNOT_REPROCESS",f.Idle()==0 && !f.capture.currentImageValid && f.output==previous && f.capture.imageGeneration==image);
    f.capture.protectedBlocked=false; f.capture.currentImageValid=true;
    f.capture.captureGeneration=7; f.capture.imageGeneration=6;
    f.capture.ApplyColorMetadata(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,true,4.0f);
    Check("OLD_GENERATION_CACHE_CANNOT_REPROCESS",f.Idle()==0 && f.output==previous && f.capture.imageGeneration==6);
}

int main() {
    try {
        std::puts("Generated textures + software WARP only. No desktop capture, LAN, lights, GUI, or startup changes.");
        int legacy=EG_RenderSelfTest();
        std::printf("Existing production WARP checks returned %d\n",legacy);
        Check("EXISTING_TWENTY_WARP_CONTROLS",legacy==20);
        Transition(true); Transition(false); CompatibleWhiteAndGuards();
    } catch(const std::exception& e) { ++failures; std::printf("ERROR %s\n",e.what()); }
    std::printf("RESULT cases=%d failures=%d\n",cases,failures);
    return failures?1:0;
}
