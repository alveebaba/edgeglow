// Real production-component regressions. Only clock, capture I/O and transport I/O
// are fake. Never call Program.Main, ScreenSampler's native constructor, Storage.Load,
// registry startup helpers, SystemEvents subscriptions, sockets or the Windows desktop.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace EdgeGlow
{
    internal static class RealComponentRegressionTests
    {
        private static int passed, failed, assertions;
        private static string temporaryRoot;

        public static int Main(string[] args)
        {
            if (args.Length != 1) { Console.Error.WriteLine("Provide one isolated temporary directory."); return 2; }
            temporaryRoot = Path.GetFullPath(args[0]);
            string tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!temporaryRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
            { Console.Error.WriteLine("Tests only write settings under an isolated system temporary directory."); return 2; }
            Directory.CreateDirectory(temporaryRoot);
            Run("R01 real isolated settings loader and paused startup", SettingsIsolation);
            Run("R02/R03 real floating filter convergence matrix", SmoothingConvergence);
            Run("R04 one-byte settled output and failed-heartbeat retry", HeartbeatAndFailure);
            Run("R05/R06 real detector through engine: static crop/expand/recrop", StaticCropTransitions);
            Run("R07/R08 invalid crop evidence and dark scene preservation", InvalidCropEvidence);
            Run("R09 reply timeout then pause preserves release obligation", ReplyTimeoutPause);
            Run("R09 reply timeout then exit still attempts release", ReplyTimeoutExit);
            Run("R09 release exception retains bounded pending release", ReleaseFailure);
            Run("R09 endpoint change never redirects prior release", EndpointChange);
            Run("R09 exhausted release retries resume after transport recovery", ReleaseExhaustionRecovery);
            Run("R09 endpoint recovery retires prior ownership", EndpointChangeAfterOutage);
            Run("R09 partial activation then endpoint change releases attempted owner", PartialActivationEndpointChange);
            Run("R11 ninety-second healthy session has no periodic preparation", HealthySession);
            Run("R12 calibration from paused requires no capture", CalibrationWhilePaused);
            Run("R12/R13 calibration expiry during capture failure restores ambient", CalibrationExpiry);
            Run("R13 calibration replacement and explicit cancellation", CalibrationReplacement);
            Run("R13 pause/lock/suspend cancel calibration before error heartbeat", CalibrationGuards);
            Run("R14 no ambient activation before first usable frame", FirstFrameGate);
            Run("R20 fresh/idle/error/send metrics stay distinct", Metrics);
            Run("R10 activation failures are bounded and explicit resume retries", BoundedActivationRetries);
            Run("Competitor and pause-before-first-image guards", CompetitorAndFirstFramePause);
            Run("R13 expired calibration never returns after send error", CalibrationSendFailure);
            Run("R14/R20 real sampler through engine preserves stale health and metadata age", RealSamplerThroughEngine);
            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + assertions + " assertions.");
            Console.WriteLine("No desktop capture, light commands, LAN sockets, GUI, live settings or registry access.");
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException(message); }

        private static void EqualColor(Color actual, Color expected, string message)
        { Check(actual.R == expected.R && actual.G == expected.G && actual.B == expected.B,
            message + " expected=" + expected + " actual=" + actual); }

        private static void EqualColors(Color[] actual, Color[] expected, string message)
        {
            Check(actual != null && expected != null && actual.Length == expected.Length, message + " length");
            for (int i = 0; i < actual.Length; i++) EqualColor(actual[i], expected[i], message + " zone " + i);
        }

        private static AppSettings Config(bool automatic)
        {
            AppSettings c = new AppSettings();
            c.DeviceId = "TEST-G1"; c.DeviceIp = ""; c.Monitor = "FAKE-DISPLAY";
            c.AutoSync = automatic; c.StartWithWindows = false; c.AreaMode = "Full";
            c.Brightness = 100; c.Saturation = 100; c.SmoothMs = 0; c.Fps = 24;
            // Keep the legacy ten-zone scenarios independent of public defaults.
            c.ZoneCount = 10; c.EdgePercent = 8; c.IgnoreBlackPixels = false;
            return c;
        }

        private static Color[] SolidColors(Color color)
        { Color[] a = new Color[10]; for (int i = 0; i < a.Length; i++) a[i] = color; return a; }

        private static CaptureFrame SolidFrame(Color color)
        { return Picture(256, 107, new Rectangle(0, 0, 256, 107), color); }

        private static CaptureFrame Pillarbox()
        { return Picture(256, 107, new Rectangle(33, 0, 190, 107), Color.FromArgb(90, 160, 210)); }

        private static CaptureFrame Picture(int width, int height, Rectangle region, Color color)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                {
                    int p = (y * width + x) * 4;
                    pixels[p] = color.B; pixels[p + 1] = color.G; pixels[p + 2] = color.R; pixels[p + 3] = 255;
                }
            return new CaptureFrame(width, height, pixels);
        }

        private static void SettingsIsolation()
        {
            string[] names = { "missing", "malformed", "null", "partial", "paused" };
            string[] json = { null, "{not json", "null", "{\"Brightness\":42}", "{\"AutoSync\":false,\"StartWithWindows\":false}" };
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(temporaryRoot, names[i] + ".json");
                if (json[i] != null) File.WriteAllText(path, json[i]);
                string warning;
                AppSettings loaded = Storage.LoadFrom(path, out warning);
                Check(!loaded.AutoSync, names[i] + " must start paused");
                Check(!loaded.StartWithWindows, names[i] + " must not silently opt into startup");
                if (i == 1 || i == 2) Check(!String.IsNullOrEmpty(warning), names[i] + " must report load failure");
                loaded.DeviceId = "TEST-G1";
                using (Fixture f = new Fixture(loaded))
                {
                    f.Source.Present(SolidFrame(Color.Red)); f.Step(); f.Step(1);
                    Check(f.Engine.Paused, names[i] + " engine pause state");
                    Check(f.CaptureCreates == 0 && f.Source.Reads == 0, names[i] + " captured before resume");
                    Check(f.Transport.EnableAttempts == 0 && f.Transport.SuccessfulColors.Count == 0,
                        names[i] + " sent controls before resume");
                }
            }
            string optInPath = Path.Combine(temporaryRoot, "explicit-opt-in.json");
            File.WriteAllText(optInPath, "{\"AutoSync\":true,\"StartWithWindows\":false}");
            string optInWarning;
            AppSettings optIn = Storage.LoadFrom(optInPath, out optInWarning);
            Check(optIn.AutoSync && !optIn.StartWithWindows, "explicit AutoSync opt-in must remain independent of startup");
        }

        private static void SmoothingConvergence()
        {
            foreach (int ms in new int[] { 150, 1000 })
            {
                ColorSmoother filter = new ColorSmoother();
                filter.Update(SolidColors(Color.FromArgb(100, 100, 100)), 1, 0);
                Color[] output = null;
                for (int i = 0; i < 5000; i++) output = filter.Update(SolidColors(Color.Black), 1.0 / 24, ms);
                EqualColors(output, SolidColors(Color.Black), "5,000-frame 100 to zero at smoothness " + ms);
            }
            int[] targets = { 0, 1, 2, 3, 100, 254, 255 };
            foreach (int fps in new int[] { 10, 24, 40 })
            foreach (int ms in new int[] { 0, 50, 150, 1000 })
            foreach (int target in targets)
            foreach (bool irregular in new bool[] { false, true })
            {
                Color from = Color.FromArgb(255 - target, target, 255);
                Color to = Color.FromArgb(target, 255 - target, target / 2);
                ColorSmoother filter = new ColorSmoother();
                Color previous = filter.Update(SolidColors(from), 1, 0)[0];
                for (int step = 0; step < 600; step++)
                {
                    double dt = irregular ? new double[] { .001, .02, .05, .14, .005 }[step % 5] : 1.0 / fps;
                    Color now = filter.Update(SolidColors(to), dt, ms)[0];
                    Check(Between(now.R, previous.R, to.R) && Between(now.G, previous.G, to.G)
                        && Between(now.B, previous.B, to.B), "filter overshoot or reversed constant-target convergence");
                    if (ms == 0) EqualColor(now, to, "zero smoothing must apply current target immediately");
                    previous = now;
                }
                EqualColor(previous, to, "filter failed to settle fps=" + fps + " ms=" + ms + " target=" + target);
            }
        }

        private static bool Between(int value, int start, int target)
        { return value >= Math.Min(start, target) && value <= Math.Max(start, target); }

        private static void HeartbeatAndFailure()
        {
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Source.Present(SolidFrame(Color.FromArgb(100, 100, 100))); f.Step();
                int initial = f.Transport.SuccessfulColors.Count;
                Check(initial > 0, "initial output absent");
                f.Source.Present(SolidFrame(Color.FromArgb(101, 101, 101))); f.Step(.2);
                f.Step(.79);
                Check(f.Transport.SuccessfulColors.Count == initial, "one-unit output forced premature send");
                f.Step(.03);
                EqualColor(f.LastColors[0], Color.FromArgb(101, 101, 101), "heartbeat omitted settled one-unit change");
                f.Source.Present(SolidFrame(Color.FromArgb(102, 102, 102)));
                f.Transport.FailColorCount = 1; f.Step(1.1);
                int successes = f.Transport.SuccessfulColors.Count;
                f.Step(.2);
                Check(f.Transport.SuccessfulColors.Count > successes, "failed send advanced successful-send deadline");
                EqualColor(f.LastColors[0], Color.FromArgb(102, 102, 102), "retry lost the current desired byte");
            }
        }

        private static void StaticCropTransitions()
        {
            AppSettings config = Config(true); config.AreaMode = "Auto";
            using (Fixture f = new Fixture(config))
            {
                f.Source.Present(Pillarbox()); f.Step();
                Check(f.Engine.State.ActiveArea.Width == 256, "crop should not immediately trust one image");
                f.Ticks(36, 1.0 / 24);
                Check(f.Source.FreshReads == 1, "fixture accidentally supplied multiple presents");
                Check(f.Engine.State.ActiveArea == new Rectangle(33, 0, 190, 107), "idle picture never finished crop");
                f.Source.Present(SolidFrame(Color.CornflowerBlue)); f.Step(1.0 / 24); f.Ticks(12, 1.0 / 24);
                Check(f.Engine.State.ActiveArea == new Rectangle(0, 0, 256, 107), "single full frame never finished expansion");
                f.Source.Present(Pillarbox()); f.Step(1.0 / 24); f.Ticks(36, 1.0 / 24);
                Check(f.Engine.State.ActiveArea == new Rectangle(33, 0, 190, 107), "idle recrop did not settle");
            }
        }

        private static void InvalidCropEvidence()
        {
            AppSettings config = Config(true); config.AreaMode = "Auto";
            using (Fixture f = new Fixture(config))
            {
                f.Source.Present(Pillarbox()); f.Step();
                f.Source.Fail("Synthetic protected content"); f.Ticks(30, 1.0 / 24);
                Check(f.Engine.State.ActiveArea.IsEmpty || f.Engine.State.ActiveArea.Width == 256,
                    "invalid/protected evidence completed crop");
                f.Source.Present(Pillarbox()); f.Step(.1); f.Ticks(36, 1.0 / 24);
                Check(f.Engine.State.ActiveArea.Width == 190, "healthy crop failed after invalid evidence");
                f.Source.Present(SolidFrame(Color.Black)); f.Step(.1); f.Ticks(36, 1.0 / 24);
                Check(f.Engine.State.ActiveArea.Width == 190, "ambiguous dark frame removed stable crop");
                EqualColors(f.LastColors, SolidColors(Color.Black), "dark scene failed to produce black");
            }
        }

        private static void ReplyTimeoutPause()
        {
            using (Fixture f = Fixture.Running())
            {
                f.Transport.RepliesEnabled = false; f.Step(21);
                Check(f.Transport.ReleaseAttempts.Count == 0, "mere reply loss disrupted mode");
                f.Engine.Paused = true; f.Step(.1);
                Check(f.Transport.ReleaseAttempts.Count > 0, "pause forgot release after reply timeout");
                Check(f.Transport.ReleaseAttempts[0].Id == "TEST-G1", "release lost validated device identity");
            }
        }

        private static void ReplyTimeoutExit()
        {
            Fixture f = Fixture.Running();
            f.Transport.RepliesEnabled = false; f.Step(21); f.Engine.Dispose();
            Check(f.Transport.ReleaseAttempts.Count > 0, "exit forgot release after reply timeout");
            int count = f.Transport.ReleaseAttempts.Count; f.Engine.Dispose();
            Check(f.Transport.ReleaseAttempts.Count == count, "repeated dispose released twice");
        }

        private static void ReleaseFailure()
        {
            using (Fixture f = Fixture.Running())
            {
                f.Transport.FailReleaseCount = 1; f.Engine.Paused = true; f.Step(.1);
                Check(f.Transport.ReleaseAttempts.Count == 1, "expected initial release attempt");
                for (int i = 0; i < 6; i++) f.Step(2);
                Check(f.Transport.SuccessfulReleases == 1, "pending release did not recover from send exception");
                Check(f.Transport.ReleaseAttempts.Count <= 4, "release retry was unbounded");
            }
        }

        private static void EndpointChange()
        {
            using (Fixture f = Fixture.Running())
            {
                string oldIp = f.Transport.CurrentTarget.Ip;
                f.Transport.ChangeEndpoint("192.168.50.45"); f.Step(.1);
                Check(f.Transport.ReleaseAttempts.Count > 0, "endpoint handoff silently abandoned old ownership");
                Check(f.Transport.ReleaseAttempts[0].Ip == oldIp, "old session release was redirected to new endpoint");
            }
        }


        private static void ReleaseExhaustionRecovery()
        {
            using (Fixture f = Fixture.Running())
            {
                f.Transport.FailReleaseCount = 3; f.Engine.Paused = true;
                f.Step(.1); f.Step(1.1); f.Step(1.1);
                Check(f.Transport.ReleaseAttempts.Count == 3 && f.Engine.State.ReleasePending,
                    "expected bounded failed release obligation");
                f.Transport.RepliesEnabled = false; f.Step(21);
                f.Transport.RepliesEnabled = true; f.Transport.Rebind(); f.Transport.Discover();
                f.Step(.1); f.Step(1.1);
                Check(f.Transport.SuccessfulReleases == 1, "revalidated transport recovery never retried pending paused release");
                Check(f.Transport.SuccessfulColors.Count == 1, "paused recovery resumed ambient output");
            }
        }

        private static void EndpointChangeAfterOutage()
        {
            using (Fixture f = Fixture.Running())
            {
                string oldIp = f.Transport.CurrentTarget.Ip;
                f.Transport.RepliesEnabled = false; f.Step(21);
                f.Transport.RepliesEnabled = true;
                f.Transport.ChangeEndpoint("192.168.50.45"); f.Step(.1);
                Check(f.Transport.ReleaseAttempts.Count > 0,
                    "unreachable-to-new-endpoint transition abandoned old ownership");
                Check(f.Transport.ReleaseAttempts[0].Ip == oldIp,
                    "recovery release targeted new endpoint instead of owned prior endpoint");
            }
        }

        private static void Metrics()
        {
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Source.Present(SolidFrame(Color.CadetBlue)); f.Step();
                f.Ticks(599, 1.0 / 24);
                EngineState state = f.Engine.State;
                Check(state.LoopIterations == 600 && state.CaptureAttempts == 600, "capture attempts confused with loop/image count");
                Check(state.FreshFrames == 1 && state.Frames == 1 && state.IdleResults == 599,
                    "idle frames reported as fresh images");
                Check(state.Datagrams == f.Transport.SuccessfulColors.Count && state.Datagrams < state.LoopIterations / 2,
                    "suppressed color sends still counted as datagrams");
                f.Engine.Update(Config(true)); f.Step(1.0 / 24);
                Check(f.Engine.State.FreshFrames == 1, "settings-only resample manufactured fresh image");
                f.Source.Fail("Synthetic recovery"); f.Ticks(3, .1);
                Check(f.Engine.State.CaptureErrors == 3 && f.Engine.State.FreshFrames == 1,
                    "capture error counter or fresh count wrong");
                Check(f.Engine.State.OutputPolicy.IndexOf("holding", StringComparison.OrdinalIgnoreCase) >= 0,
                    "capture failure not reported as held output");
                f.Source.Present(SolidFrame(Color.Teal)); f.Step(.1); f.Step(.1);
                Check(f.Engine.State.FreshFrames == 2 && f.Engine.State.CaptureErrors == 3,
                    "final healthy idle erased capture history");
            }
        }


        private static void PartialActivationEndpointChange()
        {
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Source.Present(SolidFrame(Color.Teal));
                f.Transport.FailEnableCount = 1; f.Step();
                string oldIp = f.Transport.CurrentTarget.Ip;
                Check(f.Transport.EnableAttempts == 1 && f.LastColors == null,
                    "fixture must fail after attempting first activation");
                f.Transport.ChangeEndpoint("192.168.50.45"); f.Step(1.1);
                Check(f.Transport.ReleaseAttempts.Count > 0,
                    "partially successful activation obligation was abandoned on target change");
                Check(f.Transport.ReleaseAttempts[0].Ip == oldIp,
                    "partial activation release used the replacement target");
                int releaseIndex = f.Transport.Commands.FindIndex(delegate(Command c) { return c.Kind == "release" && c.Ip == oldIp; });
                int activateIndex = f.Transport.Commands.FindIndex(delegate(Command c) { return c.Kind == "enable" && c.Ip == "192.168.50.45"; });
                Check(activateIndex > releaseIndex, "replacement activation preceded old endpoint release attempt");
            }
        }


        private static void BoundedActivationRetries()
        {
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Transport.FailEnableCount = 10;
                f.Source.Present(SolidFrame(Color.Teal)); f.Step(); f.Ticks(25, .2);
                Check(f.Transport.EnableAttempts == 3, "activation failures did not stop at three bounded attempts");
                Check(f.Transport.SuccessfulColors.Count == 0, "color stream proceeded after failed activation");
                f.Engine.Paused = true; f.Step(.1);
                Check(f.Transport.ReleaseAttempts.Count > 0, "partially attempted activation lost release obligation");
                f.Transport.FailEnableCount = 0; f.Engine.Paused = false;
                f.Source.Present(SolidFrame(Color.Teal)); f.Step(1.1);
                Check(f.Transport.EnableAttempts == 4 && f.LastColors != null, "explicit resume did not reset exhausted activation");
            }
        }

        private static void CompetitorAndFirstFramePause()
        {
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Competitor = true; f.Source.Present(SolidFrame(Color.Red)); f.Step();
                f.Engine.TestZone(2); f.Step(.1);
                Check(f.Transport.EnableAttempts == 0 && f.Transport.SuccessfulColors.Count == 0
                    && f.CaptureCreates == 0, "competing controller did not block capture/activation/calibration");
                f.Competitor = false; f.Step(3.1);
                Check(f.Transport.EnableAttempts == 1, "normal sync failed after competitor departed");
                f.Competitor = true; f.Step(3.1);
                Check(f.Transport.ReleaseAttempts.Count == 1, "new competitor did not release existing stream");
            }
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Step(); f.Engine.Paused = true; f.Step(.1);
                int reads = f.Source.Reads;
                f.Source.Present(SolidFrame(Color.Red)); f.Step(2);
                Check(f.Source.Reads == reads && f.Transport.EnableAttempts == 0
                    && f.Transport.SuccessfulColors.Count == 0, "pause before first image allowed later implicit activation");
                Check(f.Engine.State.MissingFrames == 1 && f.Engine.State.CaptureErrors == 0,
                    "healthy first-ever idle was classified as a capture error");
            }
        }

        private static void CalibrationSendFailure()
        {
            using (Fixture f = Fixture.Running())
            {
                Color[] ambient = (Color[])f.LastColors.Clone();
                f.Engine.TestZone(1); f.Step(.1);
                f.Transport.FailColorCount = 1; f.Step(1.1);
                f.Source.Fail("Synthetic capture unavailable after network error");
                int count = f.Transport.SuccessfulColors.Count;
                f.Step(7.1); f.Step(1.1);
                Check(f.Transport.SuccessfulColors.Count > count, "test expiry did not recover from failed test send");
                for (int i = count; i < f.Transport.SuccessfulColors.Count; i++)
                    EqualColors(f.Transport.SuccessfulColors[i].Colors, ambient, "expired pattern reappeared after send error");
            }
        }

        private static void RealSamplerThroughEngine()
        {
            foreach (bool protect in new bool[] { false, true })
            {
                using (Fixture f = new Fixture(Config(true)))
                {
                    ScriptedBackend backend = new ScriptedBackend();
                    backend.Next = NativeResult(1, 1, 1, true);
                    ScreenSampler sampler = new ScreenSampler(
                        delegate(Rectangle bounds, int width, int height) { return backend; },
                        delegate { return (long)(f.Clock.Seconds * 1000); });
                    ManagedCapture adapter = new ManagedCapture(sampler);
                    f.CaptureOverride = delegate { return adapter; };
                    f.Step();
                    Check(f.Engine.State.FreshFrames == 1, "real sampler initial frame not counted");
                    Color[] ambient = (Color[])f.LastColors.Clone();
                    backend.Next = NativeResult(protect ? -2 : -1, protect ? 1 : 2, 1, false);
                    if (protect) backend.Next.Flags = 4;
                    else backend.Next.HResult = unchecked((int)0x887A0026);
                    f.Step(.1);
                    backend.Next = NativeResult(0, protect ? 1 : 2, 1, false);
                    f.Ticks(3, 1.1);
                    Check(f.Engine.State.FreshFrames == 1 && f.Engine.State.CaptureErrors == 4,
                        "real sampler idle cleared invalid generation/protection");
                    Check(f.Engine.State.OutputPolicy.IndexOf("holding", StringComparison.OrdinalIgnoreCase) >= 0,
                        "real sampler stale pixels were called healthy cached output");
                    EqualColors(f.LastColors, ambient, "error introduced substitute black or changed held output");
                    if (protect) Check(f.Engine.State.ProtectedResults == 4, "protected state/counter vanished on idle");
                    backend.Next = NativeResult(1, 2, 2, true); f.Step(.1);
                    Check(f.Engine.State.FreshFrames == 2, "current-generation fresh image failed to recover");
                    DateTime lastFresh = f.Engine.State.LastUsableImageUtc;
                    backend.Next = NativeResult(2, 2, 2, true); f.Step(.2);
                    Check(f.Engine.State.ReprocessedFrames == 1 && f.Engine.State.FreshFrames == 2
                        && f.Engine.State.LastUsableImageUtc == lastFresh,
                        "metadata-only rerender manufactured a fresh desktop image or timestamp");
                }
            }
        }

        private static CaptureReadResult NativeResult(int result, long captureGeneration, long imageGeneration, bool valid)
        {
            return new CaptureReadResult { Result = result, CaptureGeneration = captureGeneration,
                ImageGeneration = imageGeneration, CurrentGenerationValid = valid, TextureFormat = 87 };
        }

        private sealed class ScriptedBackend : IScreenCaptureBackend
        {
            public CaptureReadResult Next;
            public CaptureReadResult Read(byte[] destination)
            {
                if (Next.Result == 1 || Next.Result == 2)
                    for (int i = 0; i < destination.Length; i += 4)
                    { destination[i] = 90; destination[i + 1] = 60; destination[i + 2] = 30; destination[i + 3] = 255; }
                return Next;
            }
            public void Dispose() { }
        }

        private sealed class ManagedCapture : IEngineCapture, IEngineCaptureMetadata
        {
            private readonly ScreenSampler sampler;
            public ManagedCapture(ScreenSampler source) { sampler = source; }
            public CaptureFrame Capture(Rectangle bounds) { return sampler.Capture(bounds); }
            public bool HasNewFrame { get { return sampler.HasNewFrame; } }
            public bool HasFreshDesktopFrame { get { return sampler.HasFreshDesktopFrame; } }
            public bool IsReprocessedFrame { get { return sampler.IsReprocessedFrame; } }
            public bool ProtectedContentMasked { get { return sampler.ProtectedContentMasked; } }
            public string Description { get { return "Synthetic native boundary; real production sampler"; } }
            public string LastError { get { return sampler.LastError; } }
            public void Dispose() { sampler.Dispose(); }
        }

        private static void HealthySession()
        {
            using (Fixture f = Fixture.Running())
            {
                int enables = f.Transport.EnableAttempts;
                for (int i = 0; i < 90; i++) f.Step(1.001);
                Check(f.Transport.EnableAttempts == enables, "healthy session repeated full preparation");
                Check(f.Transport.ReleaseAttempts.Count == 0, "healthy session released control");
                Check(f.Transport.SuccessfulColors.Count >= 90 && f.Transport.SuccessfulColors.Count <= 92,
                    "stable-color heartbeat did not follow one-second policy");
            }
        }

        private static void CalibrationWhilePaused()
        {
            using (Fixture f = new Fixture(Config(false)))
            {
                f.Source.Fail("Capture deliberately forbidden");
                f.Engine.TestZone(3); f.Step();
                Check(f.CaptureCreates == 0 && f.Source.Reads == 0, "calibration touched capture");
                Check(IsPattern(f.LastColors, 3), "dim physical zone pattern absent");
                int colorCount = f.Transport.SuccessfulColors.Count;
                f.Step(8.1);
                Check(f.Transport.ReleaseAttempts.Count > 0, "paused calibration failed to release at expiry");
                Check(f.Transport.SuccessfulColors.Count == colorCount, "expired paused pattern was resent");
            }
        }

        private static void CalibrationExpiry()
        {
            using (Fixture f = Fixture.Running())
            {
                Color[] ambient = (Color[])f.LastColors.Clone();
                int reads = f.Source.Reads;
                f.Engine.TestZone(4); f.Step(.1);
                Check(IsPattern(f.LastColors, 4), "running calibration missing");
                Check(f.Source.Reads == reads, "running calibration still captured");
                f.Source.Fail("Synthetic capture failure through test expiry");
                int beforeExpiry = f.Transport.SuccessfulColors.Count;
                f.Step(8.1); f.Ticks(3, 1.1);
                Check(f.Transport.SuccessfulColors.Count > beforeExpiry, "expiry never restored ambient heartbeat");
                for (int i = beforeExpiry; i < f.Transport.SuccessfulColors.Count; i++)
                    EqualColors(f.Transport.SuccessfulColors[i].Colors, ambient, "expired calibration escaped into heartbeat");
            }
        }

        private static void CalibrationReplacement()
        {
            using (Fixture f = new Fixture(Config(false)))
            {
                f.Engine.TestZone(1); f.Step(); f.Step(7);
                f.Engine.TestZone(6); f.Step(.1); f.Step(2);
                Check(IsPattern(f.LastColors, 6), "replacement test inherited old deadline or zone");
                int count = f.Transport.SuccessfulColors.Count;
                f.Engine.CancelTest(); f.Step(.1); f.Step(2);
                Check(f.Transport.SuccessfulColors.Count == count, "cancelled test was retransmitted");
                Check(f.Transport.ReleaseAttempts.Count > 0, "cancelled paused test did not release");
            }
        }

        private static void CalibrationGuards()
        {
            foreach (int guard in new int[] { 0, 1, 2 })
            {
                using (Fixture f = Fixture.Running())
                {
                    f.Engine.TestZone(2); f.Step(.1);
                    int count = f.Transport.SuccessfulColors.Count;
                    f.Source.Fail("Capture remains unavailable");
                    if (guard == 0) f.Engine.Paused = true;
                    else f.Engine.SetSessionBlocked(guard == 1, guard == 2);
                    f.Step(.1); f.Step(9);
                    Check(f.Transport.SuccessfulColors.Count == count, "pause/lock/suspend retransmitted calibration");
                    Check(f.Transport.ReleaseAttempts.Count > 0, "pause/lock/suspend did not attempt release");
                }
            }
        }

        private static bool IsPattern(Color[] colors, int zone)
        {
            if (colors == null || colors.Length != 10) return false;
            for (int i = 0; i < 10; i++)
            {
                Color expected = i == zone ? Color.FromArgb(100, 35, 5) : Color.Black;
                if (colors[i].R != expected.R || colors[i].G != expected.G || colors[i].B != expected.B) return false;
            }
            return true;
        }

        private static void FirstFrameGate()
        {
            using (Fixture f = new Fixture(Config(true)))
            {
                f.Step(); f.Ticks(10, .1);
                Check(f.Transport.EnableAttempts == 0 && f.Transport.SuccessfulColors.Count == 0,
                    "first-ever idle caused ambient activation or substitute black");
                f.Source.Present(SolidFrame(Color.Teal)); f.Step(.1);
                Check(f.Transport.EnableAttempts == 1 && f.Transport.SuccessfulColors.Count > 0,
                    "first valid image failed to activate once");
            }
        }

        private sealed class Clock
        {
            public double Seconds = 100;
            public DateTime UtcNow { get { return new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } }
            public void Advance(double seconds) { if (seconds < 0) throw new ArgumentOutOfRangeException("seconds"); Seconds += seconds; }
        }

        private sealed class FakeCapture : IEngineCapture
        {
            private CaptureFrame frame;
            private bool pendingFresh;
            private string error = "";
            public int Reads, FreshReads, Disposals;
            public bool HasNewFrame { get; private set; }
            public string LastError { get { return error; } }
            public void Present(CaptureFrame next) { frame = next; pendingFresh = true; error = ""; }
            public void Fail(string reason) { error = reason; pendingFresh = false; }
            public CaptureFrame Capture(Rectangle ignored)
            {
                Reads++; HasNewFrame = pendingFresh && error.Length == 0;
                if (HasNewFrame) FreshReads++;
                pendingFresh = false; return frame;
            }
            public void Dispose() { Disposals++; }
        }

        private sealed class Command
        {
            public double At;
            public string Kind, Id, Ip;
            public int Generation;
            public bool Success;
            public Color[] Colors;
            public byte[] WireFrame;
        }

        private sealed class FakeTransport : IEngineTransport
        {
            private readonly Clock clock;
            private DeviceInfo target;
            private string endpoint = "192.168.50.44";
            public bool RepliesEnabled = true;
            public int EnableAttempts, SuccessfulReleases, FailReleaseCount, FailColorCount, FailEnableCount;
            public int Generation { get; private set; }
            public readonly List<Command> Commands = new List<Command>();
            public readonly List<Command> SuccessfulColors = new List<Command>();
            public readonly List<Command> ReleaseAttempts = new List<Command>();
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public FakeTransport(Clock time) { clock = time; Generation = 1; }
            public DeviceInfo CurrentTarget { get { return Copy(target); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, Generation); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public void Start() { }
            public void Discover()
            {
                if (!RepliesEnabled) return;
                DeviceInfo reply = new DeviceInfo { Id = "TEST-G1", Sku = "H6609", Ip = endpoint,
                    LastSeenUtc = clock.UtcNow, IdentityGeneration = Generation };
                Action<DeviceInfo> handler = DeviceSeen;
                if (handler != null) handler(reply);
            }
            public void Poll() { if (RepliesEnabled && target != null && target.IdentityGeneration == Generation) target.LastSeenUtc = clock.UtcNow; }
            public void SetTarget(DeviceInfo d) { target = Copy(d); }
            public void ChangeEndpoint(string ip) { endpoint = ip; Discover(); }
            // A rebind replaces the transport, not the accepted identity observation.
            // Discover supplies a fresh observation in the new epoch; prior tokens stay immutable.
            public void Rebind()
            {
                Generation++;
                if (target != null) {
                    target.IdentityGeneration = -1; target.LastSeenUtc = DateTime.MinValue;
                    target.LastStatusUtc = DateTime.MinValue; target.PowerOn = null; target.Brightness = null;
                }
            }
            public void Enable(EndpointToken owned, Func<bool> authorize)
            {
                if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic authorization revoked");
                EnableAttempts++;
                bool succeeds = FailEnableCount <= 0;
                if (!succeeds) FailEnableCount--;
                Commands.Add(Record("enable", owned.Snapshot, succeeds, null));
                if (!succeeds) throw new IOException("Synthetic partial activation failure");
            }
            public void Disable(EndpointToken owned)
            {
                bool succeeds = FailReleaseCount <= 0;
                if (!succeeds) FailReleaseCount--;
                Command command = Record("release", owned.Snapshot, succeeds, null);
                Commands.Add(command); ReleaseAttempts.Add(command);
                if (!succeeds) throw new IOException("Synthetic release failure");
                SuccessfulReleases++;
            }
            public void SendColors(EndpointToken owned, Color[] colors, bool flag, Func<bool> authorize)
            {
                if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic authorization revoked");
                bool succeeds = FailColorCount <= 0;
                if (!succeeds) FailColorCount--;
                Command command = Record("colors", owned.Snapshot, succeeds, colors);
                command.WireFrame = GoveeLan.BuildFrame(colors, flag);
                Commands.Add(command);
                if (!succeeds) throw new IOException("Synthetic color failure");
                SuccessfulColors.Add(command);
            }
            private Command Record(string kind, DeviceInfo device, bool success, Color[] colors)
            {
                return new Command { At = clock.Seconds, Kind = kind, Id = device == null ? null : device.Id,
                    Ip = device == null ? null : device.Ip, Generation = Generation, Success = success,
                    Colors = colors == null ? null : (Color[])colors.Clone() };
            }
            private static DeviceInfo Copy(DeviceInfo source)
            {
                return source == null ? null : source.Copy();
            }
            public void Dispose() { }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly FakeCapture Source = new FakeCapture();
            public readonly FakeTransport Transport;
            public readonly GlowEngine Engine;
            public int CaptureCreates;
            public bool Competitor;
            public Func<IEngineCapture> CaptureOverride;
            public Color[] LastColors
            {
                get { return Transport.SuccessfulColors.Count == 0 ? null : Transport.SuccessfulColors[Transport.SuccessfulColors.Count - 1].Colors; }
            }
            public Fixture(AppSettings config)
            {
                Transport = new FakeTransport(Clock);
                EngineServices services = new EngineServices();
                services.Transport = Transport;
                services.CreateCapture = delegate { CaptureCreates++; return CaptureOverride == null ? Source : CaptureOverride(); };
                services.UtcNow = delegate { return Clock.UtcNow; };
                services.MonotonicSeconds = delegate { return Clock.Seconds; };
                services.SelectBounds = delegate(AppSettings ignored) { return new Rectangle(0, 0, 3440, 1440); };
                services.CompetitorRunning = delegate { return Competitor; };
                services.Log = delegate(string ignored) { };
                services.ListenSystemEvents = false;
                Engine = new GlowEngine(config, services);
            }
            public static Fixture Running()
            {
                Fixture fixture = new Fixture(Config(true));
                fixture.Source.Present(SolidFrame(Color.FromArgb(40, 90, 120))); fixture.Step();
                Check(fixture.Transport.EnableAttempts == 1 && fixture.LastColors != null, "fixture did not activate");
                return fixture;
            }
            public void Step() { Engine.Step(); }
            public void Step(double seconds) { Clock.Advance(seconds); Engine.Step(); }
            public void Ticks(int count, double seconds) { for (int i = 0; i < count; i++) Step(seconds); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
