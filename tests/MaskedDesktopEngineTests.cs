using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    // Real ScreenSampler, crop/color processing and GlowEngine. Only the native
    // read boundary, clock, monitor selection and transport are injected. These
    // checks do not exercise Windows capture, protected playback or real lights.
    public static class MaskedDesktopEngineTests
    {
        private static int passed, failed, assertions;
        private static readonly Color DesktopColor = Color.FromArgb(200, 40, 20);
        public static int Main(string[] args)
        {
            Run("Masked desktop edges remain correct through thirty seconds of cached output", HealthyMaskedDesktop);
            Run("A first mask notice on a pointer-only result preserves a healthy static desktop", FirstPointerMaskNotice);
            Run("Fresh masked changes update colors and fresh unmasked pixels clear the notice", FreshMaskedChanges);
            Run("Masked color rerender preserves desktop freshness and image age", MaskedRerender);
            Run("Legacy negative protection still enters standby and needs fresh pixels", LegacyNegativeResult);
            Run("Masked metadata cannot validate an obsolete image generation", InvalidGeneration);
            Run("Initial masked pointer-only results cannot invent a desktop", InitialPointerOnly);
            Run("A valid fully black masked image is content rather than unavailable capture", ValidBlackMaskedPicture);
            Run("Display off blocks capture and cached masked pixels cannot wake", DisplayOffFreshWake);
            Run("Masked pixels cannot override overlapping lock and sleep blockers", SessionBlocks);
            Run("Missing selected monitor blocks capture and requires fresh masked pixels on return", MissingMonitor);
            Run("Manual pause and explicit engine stop retain authority over masked frames", PauseAndStop);
            Console.WriteLine("Masked desktop engine regression: " + passed + " passed, " + failed + " failed, " + assertions + " assertions.");
            Console.WriteLine("Evidence qualification: real managed sampler/engine with synthetic pixels and metadata, fake time and in-memory transport; no native DLL, Windows capture, protected playback, LAN delivery, GUI, settings, startup or physical acceptance.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        private static void EqualColors(Color[] colors, Color expected, string cause)
        {
            Check(colors != null && colors.Length == 18, cause + " lost the eighteen-zone layout");
            for (int i = 0; i < colors.Length; i++)
                Check(colors[i].R == expected.R && colors[i].G == expected.G && colors[i].B == expected.B,
                    cause + " changed visible desktop color at zone " + (i + 1));
        }
        private static void NoWake(Fixture f, string cause)
        {
            Check(f.Transport.Wakes == 0 && !f.Engine.State.Sending, cause + " woke or streamed lighting");
        }
        private static void HealthyMaskedDesktop()
        {
            using (Fixture f = Fixture.Running()) {
                Check(f.LastSampler.ProtectedContentMasked && f.LastSampler.LastError == "", "masked fresh image was treated as an error");
                EqualColors(f.Transport.LastColors, DesktopColor, "initial masked frame");
                Check(f.LastSampler.CaptureGeneration == f.LastSampler.ImageGeneration && f.LastSampler.HasCurrentGenerationFrame,
                    "fresh masked image did not establish valid generation");
                Check(f.LastFrame.Bgra[(53 * f.LastFrame.Width + 128) * 4 + 2] == 0,
                    "the synthetic protected region did not remain black");
                for (int i = 0; i < 30; i++) {
                    f.Step(1);
                    Check(f.Engine.State.Sending && !f.Engine.State.AutomaticStandby, "healthy masked cache entered standby at second " + (i + 1));
                    EqualColors(f.Transport.LastColors, DesktopColor, "cached masked frame");
                }
                Check(f.Engine.State.CaptureErrors == 0 && f.Engine.State.MissingFrames == 0, "healthy masked desktop accumulated unavailable-image errors");
                Check(f.Engine.State.FreshFrames == 1 && f.Engine.State.IdleResults == 30, "fixture failed to exercise one fresh frame followed by thirty cached reads");
                Check(f.Engine.State.ProtectedResults == 31, "informational mask metadata was lost on valid reads");
                Check(f.LastSampler.FrameAgeMilliseconds == 30000 && !f.LastSampler.HasFreshDesktopFrame, "cached reads refreshed the image timestamp");
                Check(f.Transport.Standbys == 0 && f.Transport.Wakes == 0 && f.Transport.Enables == 1,
                    "healthy masked desktop emitted a power transition");
                Check(f.Transport.Colors.Count == 31, "healthy masked desktop failed to maintain its normal stream heartbeat");
            }
        }
        private static void FreshMaskedChanges()
        {
            using (Fixture f = Fixture.Running()) {
                Color blue = Color.FromArgb(20, 40, 200);
                f.Backend.Color = blue; f.Backend.Set(1, 1, 1, true, 4); f.Step(.1);
                EqualColors(f.Transport.LastColors, blue, "fresh masked change");
                Check(f.Engine.State.FreshFrames == 2 && f.Engine.State.CaptureErrors == 0, "fresh masked change was not usable");
                f.Backend.Set(0, 1, 1, true, 0); f.Step(.1);
                Check(f.LastSampler.ProtectedContentMasked, "an idle metadata change cleared the accepted image's mask notice");
                f.Backend.Set(1, 1, 1, true, 0); f.Step(.1);
                Check(!f.LastSampler.ProtectedContentMasked && f.LastSampler.LastError == "", "fresh unmasked pixels failed to clear the notice");
                Check(f.Transport.Standbys == 0 && f.Transport.Enables == 1, "mask notice transitions interrupted output");
            }
        }
        private static void FirstPointerMaskNotice()
        {
            using (Fixture f = new Fixture()) {
                f.Backend.Set(1, 1, 1, true, 0); f.Step();
                Check(f.Engine.State.Sending && !f.LastSampler.ProtectedContentMasked, "fixture did not begin with a valid unmasked desktop");
                f.Backend.Set(0, 1, 1, true, 4);
                for (int i = 0; i < 30; i++) f.Step(1);
                Check(f.LastSampler.ProtectedContentMasked && f.LastSampler.HasCurrentGenerationFrame && f.LastSampler.LastError == "",
                    "first pointer-only mask notice invalidated otherwise valid desktop pixels");
                Check(f.Engine.State.Sending && f.Engine.State.CaptureErrors == 0 && f.Engine.State.MissingFrames == 0 && f.Transport.Standbys == 0,
                    "informational pointer-only mask notice caused false no-picture standby");
                Check(f.Engine.State.FreshFrames == 1 && f.Engine.State.IdleResults == 30 && f.LastSampler.FrameAgeMilliseconds == 30000,
                    "pointer-only mask notice invented a fresh desktop image or refreshed image age");
                EqualColors(f.Transport.LastColors, DesktopColor, "pointer-only mask notice");
            }
        }
        private static void MaskedRerender()
        {
            using (Fixture f = Fixture.Running()) {
                DateTime freshUtc = f.Engine.State.LastUsableImageUtc;
                f.Backend.Set(2, 1, 1, true, 7);
                f.Backend.Next.ConversionKind = 1; f.Backend.Next.ColorMetadataGeneration = 2; f.Backend.Next.SdrWhiteMultiplier = 2;
                f.Step(.25);
                Check(f.LastSampler.IsReprocessedFrame && !f.LastSampler.HasFreshDesktopFrame && f.LastSampler.HasNewFrame,
                    "masked rerender manufactured a fresh image or was not accepted");
                Check(f.LastSampler.ProtectedContentMasked && f.LastSampler.LastError == "" && f.LastSampler.HasCurrentGenerationFrame,
                    "masked rerender invalidated a healthy image");
                Check(f.LastSampler.ColorMetadataGeneration == 2 && f.LastSampler.SdrWhiteLevelNits == 160, "rerender metadata was not propagated");
                Check(f.LastSampler.FrameAgeMilliseconds == 250 && f.Engine.State.LastUsableImageUtc == freshUtc,
                    "masked rerender refreshed the desktop timestamp");
                for (int i = 0; i < 30; i++) f.Step(1);
                Check(f.Engine.State.FreshFrames == 1 && f.Engine.State.ReprocessedFrames == 1 && f.Engine.State.IdleResults == 30,
                    "masked rerender/cache counts do not match the scripted boundary");
                Check(f.Engine.State.CaptureErrors == 0 && f.Transport.Standbys == 0 && f.Engine.State.Sending,
                    "healthy masked rerender/cache triggered no-picture standby");
                EqualColors(f.Transport.LastColors, DesktopColor, "masked rerender");
            }
        }
        private static void LegacyNegativeResult()
        {
            using (Fixture f = Fixture.Running()) {
                f.Backend.Set(-2, 1, 1, false, 4); f.Step(.1);
                Check(f.LastSampler.LastError.Length > 0 && !f.LastSampler.HasCurrentGenerationFrame, "legacy negative protection was accepted as healthy");
                EqualColors(f.Transport.LastColors, DesktopColor, "short negative-result hold");
                f.Backend.Set(0, 1, 1, false, 4);
                f.Step(9.9); Check(f.Transport.Standbys == 0, "negative-result standby delay elapsed early");
                f.Step(.2);
                Check(f.Transport.Standbys == 1 && f.Engine.State.AutomaticStandby && !f.Engine.State.Sending,
                    "ten continuous seconds of genuinely invalid capture did not enter standby");
                int colors = f.Transport.Colors.Count;
                f.Backend.Set(0, 2, 2, true, 4); f.Step(1.1); NoWake(f, "legacy recovery idle");
                f.Backend.Set(2, 2, 2, true, 4); f.Step(1.1); NoWake(f, "legacy recovery rerender");
                Check(f.Transport.Colors.Count == colors, "invalid recovery emitted substitute colors");
                f.Backend.Set(1, 2, 2, true, 4); f.Step(.1);
                Check(f.Transport.Wakes == 1 && f.Engine.State.Sending && f.Engine.State.FreshFrames == 2,
                    "fresh masked image could not recover negative-result standby");
                EqualColors(f.Transport.LastColors, DesktopColor, "legacy recovery fresh image");
            }
        }
        private static void InvalidGeneration()
        {
            using (Fixture f = Fixture.Running()) {
                f.Backend.Set(1, 2, 1, true, 4); f.Step(.1);
                Check(!f.LastSampler.HasCurrentGenerationFrame && f.LastSampler.LastError.Length > 0,
                    "masked fresh result accepted mismatched image generation");
                f.Backend.Set(0, 2, 2, true, 4); f.Step(5); f.Step(5.1);
                Check(f.Transport.Standbys == 1 && f.Engine.State.FreshFrames == 1,
                    "masked idle revalidated obsolete pixels or prevented invalid-image standby");
                f.Backend.Set(2, 2, 2, true, 4); f.Step(1.1); NoWake(f, "new-generation rerender");
                f.Backend.Set(1, 2, 2, true, 4); f.Step(.1);
                Check(f.Transport.Wakes == 1 && f.Engine.State.Sending, "new-generation fresh masked pixels did not recover");
            }
        }
        private static void InitialPointerOnly()
        {
            using (Fixture f = new Fixture()) {
                f.Backend.Set(0, 1, 0, false, 4); f.Step(); f.Step(11);
                Check(f.Engine.State.FreshFrames == 0 && !f.Engine.State.Sending && f.Transport.Enables == 0 && f.Transport.Standbys == 0,
                    "initial masked pointer-only metadata invented capture or power ownership");
                Check(!f.LastSampler.HasCurrentGenerationFrame && f.LastSampler.FrameAgeMilliseconds == -1 && f.LastFrame == null,
                    "initial pointer-only read invented pixel history");
                f.Backend.Set(2, 1, 1, true, 4); f.Step(.1);
                Check(!f.Engine.State.Sending && f.Engine.State.FreshFrames == 0, "initial rerender invented a fresh image");
                f.Backend.Set(1, 1, 1, true, 4); f.Step(.1);
                Check(f.Engine.State.Sending && f.Engine.State.FreshFrames == 1 && f.Transport.Enables == 1,
                    "first real masked image failed to activate");
            }
        }
        private static void ValidBlackMaskedPicture()
        {
            using (Fixture f = new Fixture()) {
                f.Backend.Color = Color.Black; f.Step();
                for (int i = 0; i < 20; i++) f.Step(1);
                Check(f.Engine.State.Sending && f.Engine.State.LastColorPeak == 0 && f.Transport.Standbys == 0,
                    "valid black masked image was mistaken for unavailable capture");
                Check(f.Engine.State.CaptureErrors == 0 && f.Engine.State.MissingFrames == 0, "black masked content created capture errors");
                EqualColors(f.Transport.LastColors, Color.Black, "black masked desktop");
            }
        }
        private static void DisplayOffFreshWake()
        {
            using (Fixture f = Fixture.Running()) {
                int calls = f.Backend.Calls, colors = f.Transport.Colors.Count;
                f.Engine.SetDisplayPowerState(0); f.Step(.1); f.Step(2);
                Check(f.Transport.Standbys == 1 && f.Backend.Calls == calls && f.Transport.Colors.Count == colors,
                    "display off captured, streamed or repeated power-off");
                f.Engine.SetDisplayPowerState(1); f.Backend.Set(0, 1, 1, true, 4); f.Step(.1); NoWake(f, "display-on masked cache");
                f.Backend.Set(2, 1, 1, true, 4); f.Step(1.1); NoWake(f, "display-on masked rerender");
                f.Backend.Set(1, 1, 1, true, 4); f.Step(.1);
                Check(f.Transport.Wakes == 1 && f.Engine.State.Sending && f.Transport.Enables == 1,
                    "display on did not resume exactly once after fresh masked pixels");
            }
        }
        private static void SessionBlocks()
        {
            using (Fixture f = Fixture.Running()) {
                int calls = f.Backend.Calls;
                f.Engine.SetSessionBlocked(true, true); f.Step(.1);
                Check(f.Transport.Standbys == 1 && f.Backend.Calls == calls, "overlapping session blockers failed to stop capture");
                f.Backend.Set(1, 1, 1, true, 4); f.Engine.SetSessionBlocked(false, true); f.Step(1.1);
                NoWake(f, "unlocked but sleeping with fresh masked pixels");
                Check(f.Backend.Calls == calls && f.Transport.Standbys == 1, "a remaining sleep blocker captured or repeated power-off");
                f.Engine.SetSessionBlocked(false, false); f.Backend.Set(0, 1, 1, true, 4); f.Step(.1); NoWake(f, "session resume cached pixels");
                f.Backend.Set(1, 1, 1, true, 4); f.Step(.1);
                Check(f.Transport.Wakes == 1 && f.Engine.State.Sending, "cleared session blockers failed to resume on fresh masked pixels");
            }
        }
        private static void MissingMonitor()
        {
            using (Fixture f = Fixture.Running()) {
                int calls = f.Backend.Calls;
                f.Bounds = Rectangle.Empty; f.Engine.InvalidateDisplayTopology(); f.Step(.1);
                Check(f.Transport.Standbys == 1 && f.Backend.Calls == calls, "missing selected display failed to stop capture");
                f.Bounds = Fixture.FullBounds; f.Engine.InvalidateDisplayTopology(); f.Backend.Set(0, 1, 1, true, 4); f.Step(.1);
                NoWake(f, "returned monitor masked cache");
                f.Backend.Set(1, 1, 1, true, 4); f.Step(.1);
                Check(f.Transport.Wakes == 1 && f.Engine.State.Sending, "returned selected display failed to resume on fresh masked pixels");
            }
        }
        private static void PauseAndStop()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.1); f.Engine.Paused = true;
                int calls = f.Backend.Calls, colors = f.Transport.Colors.Count;
                f.Engine.SetDisplayPowerState(1); f.Backend.Set(1, 1, 1, true, 4); f.Step(.1); f.Step(11);
                Check(f.Engine.Paused && !f.Engine.State.AutomaticStandby && !f.Engine.State.Sending && f.Transport.Wakes == 0,
                    "masked fresh pixels overrode manual pause or retained automatic wake");
                Check(f.Backend.Calls == calls && f.Transport.Colors.Count == colors, "manual pause captured or streamed pixels");
                f.Engine.Paused = false; f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.Enables == 2 && f.Transport.Wakes == 0, "explicit resume failed to use ordinary activation");
                f.Engine.RequestStop();
                Check(f.Engine.WaitForShutdown(4000), "explicit stop did not finish bounded managed cleanup");
                calls = f.Backend.Calls; colors = f.Transport.Colors.Count;
                int powers = f.Transport.Enables + f.Transport.Wakes + f.Transport.Standbys;
                f.Backend.Set(1, 1, 1, true, 4); f.Step(11);
                Check(f.Engine.State.Message == "Stopped" && f.Backend.Calls == calls && f.Transport.Colors.Count == colors,
                    "explicit stop was restarted by fresh masked pixels");
                Check(f.Transport.Enables + f.Transport.Wakes + f.Transport.Standbys == powers, "explicit stop issued a new power transition");
            }
        }
        private static CaptureReadResult Result(int result, long generation, long imageGeneration, bool valid, int flags)
        {
            return new CaptureReadResult { Result = result, CaptureGeneration = generation, ImageGeneration = imageGeneration,
                CurrentGenerationValid = valid, Flags = flags, TextureFormat = 87, OutputColorSpace = 0, SdrWhiteMultiplier = 1 };
        }
        private sealed class Clock
        {
            public double Seconds = 100;
            public DateTime UtcNow { get { return new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } }
        }
        private sealed class ScriptedBackend : IScreenCaptureBackend
        {
            public CaptureReadResult Next = Result(1, 1, 1, true, 4);
            public Color Color = DesktopColor;
            public int Calls, Disposals;
            public void Set(int result, long generation, long imageGeneration, bool valid, int flags)
            { Next = Result(result, generation, imageGeneration, valid, flags); }
            public CaptureReadResult Read(byte[] destination)
            {
                Calls++;
                CaptureReadResult read = Next;
                if (read.Result == 1 || read.Result == 2) {
                    // A black masked center with colored unprotected edges. The
                    // complete returned image is the only input to production code.
                    int width = 256, height = destination.Length / (width * 4);
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                        Color pixel = x >= 86 && x < 170 && y >= 36 && y < 71 ? System.Drawing.Color.Black : Color;
                        int i = (y * width + x) * 4;
                        destination[i] = pixel.B; destination[i + 1] = pixel.G; destination[i + 2] = pixel.R; destination[i + 3] = 255;
                    }
                    Next = Result(0, read.CaptureGeneration, read.ImageGeneration, read.CurrentGenerationValid, read.Flags);
                    Next.ColorMetadataGeneration = read.ColorMetadataGeneration; Next.ConversionKind = read.ConversionKind;
                    Next.SdrWhiteMultiplier = read.SdrWhiteMultiplier;
                }
                return read;
            }
            public void Dispose() { Disposals++; }
        }
        private sealed class ManagedCapture : IEngineCapture, IEngineCaptureMetadata
        {
            private readonly Fixture fixture;
            private readonly ScreenSampler sampler;
            public ManagedCapture(Fixture owner, ScreenSampler source) { fixture = owner; sampler = source; }
            public CaptureFrame Capture(Rectangle bounds) { return fixture.LastFrame = sampler.Capture(bounds); }
            public bool HasNewFrame { get { return sampler.HasNewFrame; } }
            public bool HasFreshDesktopFrame { get { return sampler.HasFreshDesktopFrame; } }
            public bool IsReprocessedFrame { get { return sampler.IsReprocessedFrame; } }
            public bool ProtectedContentMasked { get { return sampler.ProtectedContentMasked; } }
            public string LastError { get { return sampler.LastError; } }
            public string Description { get { return "Synthetic safe masked desktop; real managed sampler"; } }
            public void Dispose() { sampler.Dispose(); }
        }
        private sealed class Transport : IEngineTransport, IEngineStandbyTransport
        {
            public const string Id = "MASKED-DESKTOP-TEST";
            private readonly Clock clock;
            private DeviceInfo target;
            private string standbyId = "";
            public int Enables, Standbys, Wakes, Releases;
            public readonly List<Color[]> Colors = new List<Color[]>();
            public Color[] LastColors { get { return Colors.Count == 0 ? null : Colors[Colors.Count - 1]; } }
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public Transport(Clock value) { clock = value; }
            public int Generation { get { return 1; } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, Generation); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public void Start() { }
            public void Discover() { PublishFresh(); }
            public void Poll() { PublishFresh(); }
            public void SetTarget(DeviceInfo value) { target = value == null ? null : value.Copy(); }
            private void PublishFresh()
            {
                DeviceInfo reply = target == null ? new DeviceInfo() : target.Copy();
                reply.Id = Id; reply.Sku = "H6609"; reply.Ip = "192.0.2.44"; reply.LastSeenUtc = clock.UtcNow; reply.IdentityGeneration = Generation;
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            private static void Authorize(Func<bool> authorize)
            { if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic control authorization canceled"); }
            public void Enable(EndpointToken endpoint, Func<bool> authorize) { Authorize(authorize); Enables++; }
            public void Disable(EndpointToken endpoint) { Releases++; }
            public void Standby(EndpointToken endpoint, Func<bool> authorize) { Authorize(authorize); Standbys++; standbyId = endpoint.Id; }
            public void Wake(EndpointToken endpoint, Func<bool> authorize) { Authorize(authorize); Wakes++; }
            public bool HasStandby(string id) { return String.Equals(standbyId, id, StringComparison.OrdinalIgnoreCase); }
            public void CancelStandby() { standbyId = ""; }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize)
            {
                Authorize(authorize);
                Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "production color packet builder produced no frame");
                Colors.Add((Color[])colors.Clone());
            }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public static readonly Rectangle FullBounds = new Rectangle(0, 0, 3440, 1440);
            public readonly Clock Clock = new Clock();
            public readonly ScriptedBackend Backend = new ScriptedBackend();
            public readonly Transport Transport;
            public readonly GlowEngine Engine;
            public Rectangle Bounds = FullBounds;
            public ScreenSampler LastSampler;
            public CaptureFrame LastFrame;
            public Fixture()
            {
                Transport = new Transport(Clock);
                AppSettings config = new AppSettings { DeviceId = Transport.Id, DeviceIp = "", Monitor = "FAKE-DISPLAY", AutoSync = true,
                    StartWithWindows = false, AreaMode = "Auto", ZoneCount = 18, EdgePercent = 20, Brightness = 100,
                    Saturation = 100, SmoothMs = 0, Fps = 24, IgnoreBlackPixels = true, Rotation = 0, Reverse = false, StreamFlag = false };
                Engine = new GlowEngine(config, new EngineServices {
                    Transport = Transport, CreateCapture = delegate {
                        LastSampler = new ScreenSampler(delegate(Rectangle bounds, int width, int height) { return Backend; },
                            delegate { return (long)Math.Round(Clock.Seconds * 1000); });
                        return new ManagedCapture(this, LastSampler);
                    }, UtcNow = delegate { return Clock.UtcNow; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return Bounds; }, CompetitorRunning = delegate { return false; },
                    Log = delegate(string ignored) { }, ListenSystemEvents = false
                });
            }
            public static Fixture Running()
            {
                Fixture f = new Fixture(); f.Step();
                Check(f.Engine.State.Sending && f.Transport.Enables == 1 && f.Transport.Colors.Count == 1, "fixture did not establish its initial stream");
                return f;
            }
            public void Step() { Engine.Step(); }
            public void Step(double seconds) { Clock.Seconds += seconds; Engine.Step(); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
