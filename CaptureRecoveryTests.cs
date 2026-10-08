using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    // Real ScreenSampler state machine, scripted native boundary, no DLL load.
    public static class CaptureRecoveryTests
    {
        private static readonly Rectangle Bounds = new Rectangle(0, 0, 3440, 1440);
        public static int Run()
        {
            InitialIdle(); ErrorStaysSticky(); ProtectionStaysSticky(); NewGenerationRequiresFreshPixels();
            RerenderPreservesImageAge(); RerenderCannotRecover(); MonitorChangeInvalidates(); PendingEvidenceCanBeCleared();
            return 8;
        }
        private static CaptureReadResult Result(int result, long generation, long imageGeneration, bool valid)
        {
            return new CaptureReadResult { Result = result, CaptureGeneration = generation, ImageGeneration = imageGeneration,
                CurrentGenerationValid = valid, TextureFormat = 87, OutputColorSpace = 0, SdrWhiteMultiplier = 1 };
        }
        private sealed class Scripted : IScreenCaptureBackend
        {
            public readonly Queue<CaptureReadResult> Results = new Queue<CaptureReadResult>();
            public bool Disposed;
            public Scripted(params CaptureReadResult[] reads) { foreach (CaptureReadResult read in reads) Results.Enqueue(read); }
            public CaptureReadResult Read(byte[] destination)
            {
                CaptureReadResult read = Results.Dequeue();
                if (read.Result == 1 || read.Result == 2)
                    for (int i = 0; i < destination.Length; i += 4) { destination[i] = 12; destination[i + 1] = 34; destination[i + 2] = 56; destination[i + 3] = 255; }
                return read;
            }
            public void Dispose() { Disposed = true; }
        }
        private static ScreenSampler Sampler(Scripted backend)
        {
            return new ScreenSampler(delegate(Rectangle bounds, int width, int height) { return backend; }, delegate { return 100; });
        }
        private static void InitialIdle()
        {
            using (ScreenSampler sampler = Sampler(new Scripted(Result(0, 1, 0, false))))
            {
                Check(sampler.Capture(Bounds) == null, "Initial idle must not invent a black image");
                Check(!sampler.HasNewFrame && !sampler.HasCurrentGenerationFrame && sampler.FrameAgeMilliseconds == -1, "Initial idle validity");
            }
        }
        private static void ErrorStaysSticky()
        {
            CaptureReadResult failure = Result(-1, 2, 1, false); failure.HResult = unchecked((int)0x887A0026);
            using (ScreenSampler sampler = Sampler(new Scripted(Result(1, 1, 1, true), failure, Result(0, 2, 1, false), Result(0, 2, 1, false), Result(1, 2, 2, true))))
            {
                CaptureFrame frame = sampler.Capture(Bounds); sampler.Capture(Bounds);
                string error = sampler.LastError;
                for (int i = 0; i < 2; i++)
                {
                    Check(Object.ReferenceEquals(frame, sampler.Capture(Bounds)), "Hold last image on recovery idle");
                    Check(sampler.LastError == error && sampler.LastHResult == failure.HResult && !sampler.HasCurrentGenerationFrame, "Idle/pointer result cannot clear failure");
                }
                sampler.Capture(Bounds);
                Check(sampler.HasCurrentGenerationFrame && sampler.HasFreshDesktopFrame && sampler.LastError == "" && sampler.ImageGeneration == 2, "New generation fresh image recovers");
            }
        }
        private static void ProtectionStaysSticky()
        {
            CaptureReadResult masked = Result(-2, 1, 1, false); masked.Flags = 4;
            using (ScreenSampler sampler = Sampler(new Scripted(Result(1, 1, 1, true), masked, Result(0, 1, 1, false), Result(1, 1, 1, true))))
            {
                sampler.Capture(Bounds); sampler.Capture(Bounds); sampler.Capture(Bounds);
                Check(sampler.ProtectedContentMasked && sampler.LastError.Length > 0 && !sampler.HasCurrentGenerationFrame, "Protection persists across timeout");
                sampler.Capture(Bounds);
                Check(!sampler.ProtectedContentMasked && sampler.LastError == "" && sampler.HasCurrentGenerationFrame, "Usable unmasked image clears protection");
            }
        }
        private static void NewGenerationRequiresFreshPixels()
        {
            using (ScreenSampler sampler = Sampler(new Scripted(Result(1, 1, 1, true), Result(0, 2, 1, false))))
            {
                sampler.Capture(Bounds); sampler.Capture(Bounds);
                Check(!sampler.HasCurrentGenerationFrame && sampler.LastError.Length > 0 && sampler.ImageGeneration == 1 && sampler.CaptureGeneration == 2, "Generation changes invalidate old pixels even without explicit error");
            }
        }
        private static void RerenderPreservesImageAge()
        {
            long clock = 10;
            CaptureReadResult rerender = Result(2, 1, 1, true); rerender.Flags = 3; rerender.ConversionKind = 1; rerender.SdrWhiteMultiplier = 2; rerender.ColorMetadataGeneration = 2;
            Scripted backend = new Scripted(Result(1, 1, 1, true), rerender, Result(0, 1, 1, true));
            using (ScreenSampler sampler = new ScreenSampler(delegate(Rectangle bounds, int width, int height) { return backend; }, delegate { return clock; }))
            {
                sampler.Capture(Bounds); clock = 210; sampler.Capture(Bounds);
                Check(sampler.HasNewFrame && sampler.IsReprocessedFrame && !sampler.HasFreshDesktopFrame && sampler.FrameAgeMilliseconds == 200, "Color rerender is not a new desktop frame");
                Check(sampler.SdrWhiteLevelNits == 160 && sampler.ColorMetadataGeneration == 2 && sampler.NativeTextureFormat == 87, "Color metadata reaches managed diagnostics");
                clock = 410; sampler.Capture(Bounds);
                Check(!sampler.HasNewFrame && sampler.HasCurrentGenerationFrame && sampler.FrameAgeMilliseconds == 400, "Healthy idle remains valid and ages");
            }
        }
        private static void RerenderCannotRecover()
        {
            using (ScreenSampler sampler = Sampler(new Scripted(Result(1, 1, 1, true), Result(-1, 2, 1, false), Result(2, 2, 2, true))))
            {
                sampler.Capture(Bounds); sampler.Capture(Bounds); sampler.Capture(Bounds);
                Check(!sampler.HasNewFrame && !sampler.HasCurrentGenerationFrame && sampler.LastError.Length > 0, "Cached rerender cannot establish replacement pixels after failure");
            }
        }
        private static void MonitorChangeInvalidates()
        {
            Scripted old = new Scripted(Result(1, 1, 1, true)), next = new Scripted(Result(0, 1, 0, false));
            int count = 0;
            using (ScreenSampler sampler = new ScreenSampler(delegate(Rectangle bounds, int width, int height) { return count++ == 0 ? old : next; }, delegate { return 0; }))
            {
                sampler.Capture(Bounds);
                Check(sampler.Capture(new Rectangle(3440, -1021, 1440, 2560)) == null && old.Disposed && !sampler.HasCurrentGenerationFrame && sampler.CaptureGeneration == 2, "Monitor switch needs its own pixels");
            }
        }
        private static void PendingEvidenceCanBeCleared()
        {
            CaptureFrame movie = new CaptureFrame(256, 107, new byte[256 * 107 * 4]);
            for (int y = 0; y < 107; y++) for (int x = 33; x < 223; x++)
            { int i = (y * 256 + x) * 4; movie.Bgra[i] = 60; movie.Bgra[i + 1] = 100; movie.Bgra[i + 2] = 180; }
            ActiveAreaDetector detector = new ActiveAreaDetector();
            Rectangle full = new Rectangle(0, 0, 256, 107), actual = full;
            for (int i = 0; i < 11; i++) actual = detector.Update(movie, "Auto");
            detector.ClearPendingEvidence();
            for (int i = 0; i < 11; i++) Check(detector.Update(movie, "Auto") == full, "Interrupted crop evidence restarts delay");
            actual = detector.Update(movie, "Auto"); detector.ClearPendingEvidence();
            Check(actual.Width < full.Width && detector.Update(movie, "Auto") == actual, "Clear pending preserves accepted crop");
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
