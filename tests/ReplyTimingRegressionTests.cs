using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    // Run the identical test body against before-Engine.cs and candidate Engine.cs.
    // Production capture/color/packet types are compiled in; dependencies below
    // open no sockets, windows, registry or settings files and start no threads.
    public static class ReplyTimingRegressionTests
    {
        private static int passed, failed;
        public static int Main(string[] args)
        {
            Run("Poll reply published after step-start UTC", PollRace);
            Run("Discover reply published after step-start UTC", DiscoverRace);
            Run("receiver reply published while taking endpoint snapshot", EndpointRace);
            Run("truly future reply timestamp remains rejected", FutureTimestamp);
            Run("fresh timestamp does not bypass identity or SKU", IdentityGuards);
            Run("genuinely stale reply waits and legitimate reply reactivates", StaleThenLegitimate);
            Run("fresh off status during Poll preserves activation retry spacing", FreshPowerOff);
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed. All cases ran; production components with injected clock/capture/transport; no physical strip claim.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + name + " -- " + ex.Message); }
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void PollRace()
        { Race("Poll", 5.01, delegate(Fixture f) { f.Transport.PollAdvanceOnce = true; }); }
        private static void DiscoverRace()
        { Race("Discover", .1, delegate(Fixture f) { f.Transport.DiscoverAdvanceOnce = true; f.Engine.Discover(); }); }
        private static void EndpointRace()
        { Race("CurrentEndpoint", .1, delegate(Fixture f) { f.Transport.EndpointAdvanceOnce = true; }); }
        private static void Race(string origin, double elapsed, Action<Fixture> arm)
        {
            using (Fixture f = Fixture.Running()) {
                int initialEnables = f.Transport.EnableAttempts;
                int initialSession = f.Engine.State.SessionGeneration;
                arm(f); f.Step(elapsed);
                EngineState race = f.Engine.State;
                int raceEnables = f.Transport.EnableAttempts;
                // Always execute the next tick before asserting: preserve the
                // baseline witness of the subsequent unnecessary activation.
                f.Step(.01);
                EngineState next = f.Engine.State;
                int nextEnables = f.Transport.EnableAttempts;
                Console.WriteLine("WITNESS " + origin + ": initial enables=" + initialEnables + ", race enables=" + raceEnables +
                    ", race sending=" + race.Sending + ", next enables=" + nextEnables + ", next sending=" + next.Sending +
                    ", initial/race/next sessions=" + initialSession + "/" + race.SessionGeneration + "/" + next.SessionGeneration +
                    ", reply publications after step-start=" + f.Transport.DelayedPublications);
                Check(f.Transport.DelayedPublications == 1, "fixture did not publish exactly one reply after the step's initial UTC");
                Check(race.Sending && next.Sending, "legitimate newly published reply caused a false unreachable tick");
                Check(raceEnables == initialEnables && nextEnables == initialEnables &&
                    race.SessionGeneration == initialSession && next.SessionGeneration == initialSession,
                    "fresh same-identity/endpoint/generation reply triggered unnecessary stream activation");
                Check(f.Transport.SuccessfulReleases == 0, "race tick introduced a release");
            }
        }
        private static void FutureTimestamp()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.AutoReplies = false;
                int enables = f.Transport.EnableAttempts, colors = f.Transport.ColorSends, releases = f.Transport.SuccessfulReleases;
                f.Transport.Observation(FakeTransport.Id, "H6609", f.Clock.UtcNow.AddSeconds(5));
                f.Step(.1); Check(!f.Engine.State.Sending, "genuinely future reply was admitted");
                NoWrites(f, enables, colors, releases, "future reply");
                f.Step(.1); Check(!f.Engine.State.Sending, "still-future reply became reachable on next tick");
                NoWrites(f, enables, colors, releases, "still-future reply");
                f.Transport.PublishFresh(); f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.EnableAttempts == enables + 1 && f.Transport.ColorSends == colors + 1,
                    "legitimate replacement reply did not establish a new active stream");
            }
        }
        private static void IdentityGuards()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.AutoReplies = false;
                int enables = f.Transport.EnableAttempts, colors = f.Transport.ColorSends, releases = f.Transport.SuccessfulReleases;
                f.Transport.Observation("OTHER-G1", "H6609", f.Clock.UtcNow); f.Step(.1);
                Check(!f.Engine.State.Sending, "fresh clock bypassed configured device identity");
                NoWrites(f, enables, colors, releases, "wrong identity");
                f.Transport.Observation(FakeTransport.Id, "H0000", f.Clock.UtcNow); f.Step(.1);
                Check(!f.Engine.State.Sending, "fresh clock bypassed H6609 SKU restriction");
                NoWrites(f, enables, colors, releases, "wrong SKU");
                f.Transport.PublishFresh(); f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.EnableAttempts == enables + 1, "valid identity did not recover after rejected observations");
            }
        }
        private static void StaleThenLegitimate()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.AutoReplies = false;
                int enables = f.Transport.EnableAttempts, colors = f.Transport.ColorSends, releases = f.Transport.SuccessfulReleases;
                f.Transport.Observation(FakeTransport.Id, "H6609", f.Clock.UtcNow.AddSeconds(-21));
                f.Step(.1); Check(!f.Engine.State.Sending, "21-second-old reply should be unreachable");
                NoWrites(f, enables, colors, releases, "stale reply");
                f.Step(.1); NoWrites(f, enables, colors, releases, "continued stale reply");
                f.Transport.PublishFresh(); f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.EnableAttempts == enables + 1 && f.Transport.ColorSends == colors + 1,
                    "genuine reply-loss recovery must still reactivate and send current screen colors");
                f.Step(.1); Check(f.Transport.EnableAttempts == enables + 1, "healthy post-recovery tick reactivated again");
            }
        }
        private static void FreshPowerOff()
        {
            using (Fixture f = Fixture.Running()) {
                // The separate PollRace case covers an asynchronously newer
                // timestamp. Here fixed clock time isolates off-status policy.
                f.Transport.PollOffOnce = true; f.Step(5.01);
                Check(f.Engine.State.PowerOffReports == 1 && f.Engine.State.LastPowerOffReportUtc == f.Clock.UtcNow,
                    "fresh power-off status delivered by Poll was not observed");
                Check(f.Transport.EnableAttempts == 2 && f.Engine.State.SessionGeneration == 2 && f.Engine.State.Sending,
                    "fresh off status after the first cooldown did not recover the active stream");
                f.Clock.Advance(.2); f.Transport.PublishPower(false); f.Step();
                Check(f.Engine.State.PowerOffReports == 2 && f.Transport.EnableAttempts == 2 && !f.Engine.State.Sending,
                    "second fresh off observation bypassed existing one-second activation deadline");
                f.Step(.81);
                Check(f.Engine.State.PowerOffReports == 2 && f.Transport.EnableAttempts == 3 && f.Engine.State.SessionGeneration == 3 && f.Engine.State.Sending,
                    "off recovery after its deadline failed or recounted a cached status");
                f.Step(.1); Check(f.Transport.EnableAttempts == 3 && f.Engine.State.PowerOffReports == 2,
                    "cached off status caused another activation or observation count");
            }
        }
        private static void NoWrites(Fixture f, int enables, int colors, int releases, string cause)
        {
            Check(f.Transport.EnableAttempts == enables && f.Transport.ColorSends == colors && f.Transport.SuccessfulReleases == releases,
                cause + " must not issue activation, color or release commands");
        }
        private static AppSettings Config()
        {
            return new AppSettings { DeviceId = FakeTransport.Id, DeviceIp = "", Monitor = "FAKE-DISPLAY", AutoSync = true,
                StartWithWindows = false, AreaMode = "Cinema16x9", ZoneCount = 18, EdgePercent = 20, Brightness = 80,
                Saturation = 140, SmoothMs = 75, Fps = 24, IgnoreBlackPixels = true, Rotation = 15, Reverse = false, StreamFlag = false };
        }
        private sealed class Clock
        {
            public double Seconds = 100;
            public DateTime UtcNow { get { return new DateTime(2026, 10, 8, 4, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } }
            public void Advance(double seconds) { Check(seconds >= 0, "clock cannot move backward"); Seconds += seconds; }
        }
        private sealed class FakeCapture : IEngineCapture
        {
            private readonly CaptureFrame frame;
            private bool fresh = true;
            public FakeCapture()
            {
                byte[] pixels = new byte[256 * 107 * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 120; pixels[i + 1] = 90; pixels[i + 2] = 40; pixels[i + 3] = 255; }
                frame = new CaptureFrame(256, 107, pixels);
            }
            public bool HasNewFrame { get; private set; }
            public string LastError { get { return ""; } }
            public CaptureFrame Capture(Rectangle ignored) { HasNewFrame = fresh; fresh = false; return frame; }
            public void Dispose() { }
        }
        private sealed class FakeTransport : IEngineTransport
        {
            public const string Id = "TEST-G1";
            private readonly Clock clock;
            private DeviceInfo target;
            public bool AutoReplies = true, PollAdvanceOnce, DiscoverAdvanceOnce, EndpointAdvanceOnce, PollOffOnce;
            public int EnableAttempts, ColorSends, SuccessfulReleases, DelayedPublications;
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public FakeTransport(Clock value) { clock = value; }
            public int Generation { get { return 1; } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint
            {
                get {
                    if (EndpointAdvanceOnce) { EndpointAdvanceOnce = false; clock.Advance(.001); DelayedPublications++; PublishFresh(); }
                    return target == null ? null : new EndpointToken(target, Generation);
                }
            }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public void Start() { }
            public void Discover()
            {
                if (!AutoReplies) return;
                if (DiscoverAdvanceOnce) { DiscoverAdvanceOnce = false; clock.Advance(.001); DelayedPublications++; }
                PublishFresh();
            }
            public void Poll()
            {
                if (!AutoReplies) return;
                if (PollAdvanceOnce) { PollAdvanceOnce = false; clock.Advance(.001); DelayedPublications++; }
                if (PollOffOnce) { PollOffOnce = false; PublishPower(false); }
                else PublishFresh();
            }
            public void SetTarget(DeviceInfo value) { target = value == null ? null : value.Copy(); }
            public void Observation(string id, string sku, DateTime seen)
            { target = new DeviceInfo { Id = id, Sku = sku, Ip = "192.0.2.44", LastSeenUtc = seen, IdentityGeneration = Generation }; }
            public void PublishFresh()
            {
                DeviceInfo reply = target == null ? new DeviceInfo() : target.Copy();
                reply.Id = Id; reply.Sku = "H6609"; reply.Ip = "192.0.2.44"; reply.LastSeenUtc = clock.UtcNow; reply.IdentityGeneration = Generation;
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            public void PublishPower(bool on)
            {
                PublishFresh(); Check(target != null, "power publication needs discovered identity");
                DeviceInfo reply = target.Copy(); reply.PowerOn = on; reply.LastStatusUtc = clock.UtcNow;
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            public void Enable(EndpointToken owned, Func<bool> authorize)
            { if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic activation canceled"); EnableAttempts++; }
            public void Disable(EndpointToken owned) { SuccessfulReleases++; }
            public void SendColors(EndpointToken owned, Color[] colors, bool flag, Func<bool> authorize)
            {
                if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic color send canceled");
                Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "production packet builder yielded no output"); ColorSends++;
            }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly FakeCapture Capture = new FakeCapture();
            public readonly FakeTransport Transport;
            public readonly GlowEngine Engine;
            public Fixture()
            {
                Transport = new FakeTransport(Clock);
                Engine = new GlowEngine(Config(), new EngineServices { Transport = Transport, CreateCapture = delegate { return Capture; },
                    UtcNow = delegate { return Clock.UtcNow; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return new Rectangle(0, 0, 3440, 1440); }, CompetitorRunning = delegate { return false; },
                    Log = delegate(string ignored) { }, ListenSystemEvents = false });
            }
            public static Fixture Running()
            {
                Fixture f = new Fixture(); f.Step();
                Check(f.Engine.State.Sending && f.Transport.EnableAttempts == 1 && f.Transport.ColorSends == 1, "fixture initial stream did not activate once");
                return f;
            }
            public void Step() { Engine.Step(); }
            public void Step(double seconds) { Clock.Advance(seconds); Engine.Step(); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
