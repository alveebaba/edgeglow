using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    // Scripted native boundary with generated desktop pixels. No DLL/desktop/LAN.
    internal static class MaskedCaptureRegressionTests
    {
        private static readonly Rectangle Bounds = new Rectangle(0, 0, 3440, 1440);
        private static int cases, assertions;
        public static int Main()
        {
            try
            {
                int original = CaptureRecoveryTests.Run();
                Console.WriteLine("Original capture recovery cases=" + original);
                Run("FRESH_MASKED_DESKTOP_USABLE", FreshMaskedDesktop);
                Run("HEALTHY_MASKED_STATIC_CACHE", StaticMaskedCache);
                Run("MASKED_RERENDER_PRESERVES_AGE", MaskedRerender);
                Run("ONLY_FRESH_UNMASKED_CLEARS_WARNING", OnlyFreshUnmaskedClears);
                Run("LEGACY_PROTECTED_FAILURE_STAYS_INVALID", LegacyFailure);
                Run("ACCESS_LOST_REQUIRES_FRESH_PIXELS", CaptureFailure);
                Run("WRONG_GENERATION_MASKED_PIXELS_REJECTED", WrongGeneration);
                Run("POINTER_MASK_PRESERVES_HEALTHY_CACHE", PointerTransition);
                Run("INITIAL_MASKED_POINTER_CANNOT_CREATE_FRAME", InitialPointer);
                Console.WriteLine("RESULT newCases=" + cases + " originalCases=" + original + " assertions=" + assertions + " failures=0");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        }
        private static void Run(string name, Action test) { test(); ++cases; Console.WriteLine("PASS " + name); }
        private static void Check(bool okay, string message) { ++assertions; if (!okay) throw new InvalidOperationException(message); }
        private static CaptureReadResult Read(int result, long generation, long image, bool valid, int flags)
        {
            return new CaptureReadResult { Result=result, CaptureGeneration=generation, ImageGeneration=image,
                CurrentGenerationValid=valid, Flags=flags, TextureFormat=87, SdrWhiteMultiplier=1 };
        }
        private sealed class Backend : IScreenCaptureBackend
        {
            public readonly Queue<CaptureReadResult> Reads = new Queue<CaptureReadResult>();
            public Backend(params CaptureReadResult[] reads) { foreach (CaptureReadResult read in reads) Reads.Enqueue(read); }
            public CaptureReadResult Read(byte[] pixels)
            {
                CaptureReadResult read=Reads.Dequeue();
                if (read.Result==1 || read.Result==2)
                    for(int i=0;i<pixels.Length;i+=4)
                    {
                        // Simulate Windows' already masked result, not DRM content.
                        bool black=(i/4)%256>=64 && (i/4)%256<192;
                        pixels[i]=black?(byte)0:(byte)32; pixels[i+1]=black?(byte)0:(byte)64;
                        pixels[i+2]=black?(byte)0:(byte)160; pixels[i+3]=255;
                    }
                return read;
            }
            public void Dispose() { }
        }
        private static ScreenSampler Sampler(Backend backend) { return new ScreenSampler(delegate(Rectangle b,int w,int h){return backend;},delegate{return 100;}); }
        private static void FreshMaskedDesktop()
        {
            using(ScreenSampler s=Sampler(new Backend(Read(1,1,1,true,4))))
            {
                CaptureFrame frame=s.Capture(Bounds);
                Check(frame!=null && s.HasFreshDesktopFrame && s.HasNewFrame && s.HasCurrentGenerationFrame,"Masked fresh pixels must be accepted");
                Check(s.ProtectedContentMasked && s.LastError=="" && s.LastHResult==0,"Informational mask must not be a capture error");
                Check(frame.Bgra[2]==160 && frame.Bgra[1]==64 && frame.Bgra[0]==32,"Unprotected desktop color changed");
                Check(frame.Bgra[128*4]==0 && frame.Bgra[128*4+1]==0 && frame.Bgra[128*4+2]==0,"Excluded pixels must stay black");
            }
        }
        private static void StaticMaskedCache()
        {
            Backend b=new Backend(Read(1,1,1,true,4));
            for(int i=0;i<300;i++)b.Reads.Enqueue(Read(0,1,1,true,4));
            using(ScreenSampler s=Sampler(b))
            {
                CaptureFrame f=s.Capture(Bounds); DateTime fresh=s.LastUsableFrameUtc;
                for(int i=0;i<300;i++)
                {
                    Check(Object.ReferenceEquals(f,s.Capture(Bounds)) && s.HasCurrentGenerationFrame && !s.HasNewFrame,"Static masked cache was discarded");
                    Check(s.ProtectedContentMasked && s.LastError=="" && s.LastUsableFrameUtc==fresh,"Idle changed mask/error/fresh timestamp");
                }
            }
        }
        private static void MaskedRerender()
        {
            long clock=10; CaptureReadResult redraw=Read(2,1,1,true,7); redraw.SdrWhiteMultiplier=2; redraw.ColorMetadataGeneration=2;
            Backend b=new Backend(Read(1,1,1,true,7),redraw,Read(0,1,1,true,7));
            using(ScreenSampler s=new ScreenSampler(delegate(Rectangle r,int w,int h){return b;},delegate{return clock;}))
            {
                s.Capture(Bounds); DateTime fresh=s.LastUsableFrameUtc; clock=210; s.Capture(Bounds);
                Check(s.HasNewFrame && s.IsReprocessedFrame && !s.HasFreshDesktopFrame && s.HasCurrentGenerationFrame,"Masked color conversion rejected");
                Check(s.ProtectedContentMasked && s.LastError=="" && s.FrameAgeMilliseconds==200 && s.LastUsableFrameUtc==fresh,"Rerender invented freshness or cleared mask");
                Check(s.SdrWhiteLevelNits==160 && s.ColorMetadataGeneration==2,"White metadata lost");
                clock=410; s.Capture(Bounds); Check(s.HasCurrentGenerationFrame && s.FrameAgeMilliseconds==400 && !s.HasNewFrame,"Idle after masked rerender failed");
            }
        }
        private static void OnlyFreshUnmaskedClears()
        {
            using(ScreenSampler s=Sampler(new Backend(Read(1,1,1,true,4),Read(0,1,1,true,0),Read(2,1,1,true,0),Read(1,1,1,true,0))))
            {
                s.Capture(Bounds); s.Capture(Bounds); Check(s.ProtectedContentMasked && s.HasCurrentGenerationFrame,"Idle cleared mask warning");
                s.Capture(Bounds); Check(s.ProtectedContentMasked && s.HasCurrentGenerationFrame,"Conversion-only result cleared mask warning");
                s.Capture(Bounds); Check(!s.ProtectedContentMasked && s.HasFreshDesktopFrame && s.LastError=="","Fresh unmasked result did not clear warning");
            }
        }
        private static void LegacyFailure()
        {
            using(ScreenSampler s=Sampler(new Backend(Read(1,1,1,true,4),Read(-2,1,1,false,4),Read(0,1,1,true,4),Read(2,1,1,true,4),Read(1,1,1,true,4))))
            {
                s.Capture(Bounds); s.Capture(Bounds); string error=s.LastError;
                Check(s.ProtectedContentMasked && error.Length>0 && !s.HasCurrentGenerationFrame,"Legacy -2 should remain a real failure");
                s.Capture(Bounds); Check(!s.HasCurrentGenerationFrame && s.LastError==error,"Idle recovered legacy -2");
                s.Capture(Bounds); Check(!s.HasCurrentGenerationFrame && !s.HasNewFrame && s.LastError==error,"Rerender recovered legacy -2");
                s.Capture(Bounds); Check(s.HasCurrentGenerationFrame && s.HasFreshDesktopFrame && s.ProtectedContentMasked && s.LastError=="","Fresh masked image failed to recover legacy failure");
            }
        }
        private static void CaptureFailure()
        {
            CaptureReadResult failure=Read(-1,2,1,false,4); failure.HResult=unchecked((int)0x887A0026);
            using(ScreenSampler s=Sampler(new Backend(Read(1,1,1,true,4),failure,Read(0,2,1,false,4),Read(2,2,2,true,4),Read(1,2,2,true,4))))
            {
                s.Capture(Bounds); s.Capture(Bounds); string error=s.LastError;
                Check(!s.HasCurrentGenerationFrame && s.LastHResult==failure.HResult && error.Length>0,"Negative masked capture failure was accepted");
                Check(s.ProtectedContentMasked && error.Contains("GPU capture is reconnecting") && error.Contains("0x887A0026"),"Informational mask hid the actual capture error");
                s.Capture(Bounds); s.Capture(Bounds); Check(!s.HasCurrentGenerationFrame && !s.HasNewFrame && s.LastError==error,"Pointer/rerender recovered capture failure");
                s.Capture(Bounds); Check(s.HasFreshDesktopFrame && s.HasCurrentGenerationFrame && s.ImageGeneration==2 && s.LastError=="","Fresh new-generation masked pixels failed to recover");
            }
        }
        private static void WrongGeneration()
        {
            using(ScreenSampler s=Sampler(new Backend(Read(1,1,1,true,0),Read(1,2,1,true,4))))
            {
                s.Capture(Bounds); s.Capture(Bounds);
                Check(!s.HasCurrentGenerationFrame && !s.HasNewFrame && s.CaptureGeneration==2 && s.ImageGeneration==1,"Mask bypassed generation check");
            }
        }
        private static void PointerTransition()
        {
            long clock=0; Backend b=new Backend(Read(1,1,1,true,0),Read(0,1,1,true,4),Read(0,1,1,true,0),Read(0,1,1,false,4),Read(0,1,1,true,4),Read(1,1,1,true,4));
            using(ScreenSampler s=new ScreenSampler(delegate(Rectangle r,int w,int h){return b;},delegate{return clock;}))
            {
                s.Capture(Bounds); clock=15000; s.Capture(Bounds);
                Check(s.ProtectedContentMasked && s.HasCurrentGenerationFrame && !s.HasNewFrame && s.LastError=="" && s.FrameAgeMilliseconds==15000,"Pointer metadata invalidated unchanged desktop pixels");
                s.Capture(Bounds); Check(s.ProtectedContentMasked && s.HasCurrentGenerationFrame && s.LastError=="","Pointer-only unmasked result cleared warning or lost valid cache");
                s.Capture(Bounds); Check(!s.HasCurrentGenerationFrame,"Actually invalid native metadata was accepted");
                s.Capture(Bounds); Check(s.ProtectedContentMasked && !s.HasCurrentGenerationFrame,"Pointer-only result recovered invalid cache");
                s.Capture(Bounds); Check(s.HasFreshDesktopFrame && s.HasCurrentGenerationFrame && s.ProtectedContentMasked && s.LastError=="","Fresh masked transition failed");
            }
        }
        private static void InitialPointer()
        {
            using(ScreenSampler s=Sampler(new Backend(Read(0,1,0,false,4),Read(2,1,1,true,4))))
            {
                Check(s.Capture(Bounds)==null && !s.HasCurrentGenerationFrame && !s.HasNewFrame,"Initial pointer invented black frame");
                Check(s.Capture(Bounds)==null && !s.HasCurrentGenerationFrame && !s.HasNewFrame && s.FrameAgeMilliseconds==-1,"Initial rerender invented fresh pixels");
            }
        }
    }
}
