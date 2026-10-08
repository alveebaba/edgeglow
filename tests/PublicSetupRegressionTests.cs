using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    // Uses the actual public Engine/Capture/Network/Settings source. Dependencies
    // below never open sockets, windows, registry or settings files or start threads.
    public static class PublicSetupRegressionTests
    {
        private static int passed, failed;
        public static int Main(string[] args)
        {
            Run("public defaults start paused with no lighting commands", PausedDefaults);
            Run("invalid discovery identities cannot select the first strip", InvalidDiscovery);
            Run("first discovered G1 stays selected when another replies", FirstDiscoverySticks);
            Run("selected G1 may change address and identity letter case", SameDeviceReaddress);
            Run("configured identity takes precedence over discovery", ConfiguredIdentity);
            Run("explicit settings update overrides the discovered identity", UpdatedIdentity);
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed. Public setup with production components and fake clock/capture/transport; no physical strip claim.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + name + " -- " + ex.Message); }
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static AppSettings RunningConfig()
        { AppSettings config = new AppSettings(); config.AutoSync = true; return config; }
        private static DeviceInfo Device(Clock clock, string id, string ip, string sku)
        { return new DeviceInfo { Id = id, Ip = ip, Sku = sku, LastSeenUtc = clock.UtcNow, IdentityGeneration = 1 }; }
        private static void PausedDefaults()
        {
            AppSettings defaults = new AppSettings(); defaults.Validate();
            Check(defaults.DeviceId == "" && defaults.DeviceIp == "" && defaults.Monitor == "", "public defaults must contain no saved device identity, address or monitor");
            Check(!defaults.AutoSync && !defaults.StartWithWindows, "first launch must not silently opt into syncing or Windows startup");
            Check(defaults.ZoneCount == 18 && defaults.Rotation == 0 && defaults.EdgePercent == 20 && defaults.Fps == 24 &&
                defaults.SmoothMs == 150 && defaults.Brightness == 65 && defaults.Saturation == 125 && defaults.IgnoreBlackPixels,
                "validated public first-launch tuning or 18-zone layout changed");
            using (Fixture f = new Fixture(defaults, true)) {
                f.Step(); f.Step(10);
                Check(f.Engine.Paused && !f.Engine.State.Sending, "valid discovered strip must not resume an initially paused engine");
                Check(f.Transport.CurrentTarget != null && f.Transport.CurrentTarget.Id == "A", "paused engine can discover its first G1");
                Check(f.CaptureCreates == 0 && f.Capture.Reads == 0 && f.Transport.Enables.Count == 0 &&
                    f.Transport.Colors.Count == 0 && f.Transport.Releases.Count == 0, "first launch performed capture or lighting control before resume");
                f.Engine.Paused = false; f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.Enables.Count == 1 && f.Transport.Colors.Count == 1,
                    "explicit resume must activate the selected strip with current screen colors");
                Check(f.Transport.Colors[0].Colors.Length == 18, "public default sends an 18-zone frame");
                Check(defaults.DeviceId == "" && defaults.DeviceIp == "", "discovery must not mutate caller-owned settings");
            }
        }
        private static void InvalidDiscovery()
        {
            using (Fixture f = new Fixture(RunningConfig(), false)) {
                f.Transport.Emit(null);
                f.Transport.Emit(Device(f.Clock, null, "192.0.2.1", "H6609"));
                f.Transport.Emit(Device(f.Clock, "", "192.0.2.1", "H6609"));
                f.Transport.Emit(Device(f.Clock, "   ", "192.0.2.1", "H6609"));
                f.Transport.Emit(Device(f.Clock, "WRONG-SKU", "192.0.2.1", "H0000"));
                Check(f.Transport.CurrentTarget == null && f.Transport.TargetUpdates == 0, "invalid reply selected or pinned a strip");
                f.Transport.Emit(Device(f.Clock, "A", "192.0.2.10", "H6609")); f.Step();
                Check(f.Engine.State.Sending && f.Transport.CurrentTarget.Id == "A" && f.Transport.Enables.Count == 1,
                    "valid G1 must still be selectable after invalid replies");
            }
        }
        private static void FirstDiscoverySticks()
        {
            using (Fixture f = Fixture.Running()) {
                int updates = f.Transport.TargetUpdates;
                f.Transport.Emit(Device(f.Clock, "B", "192.0.2.20", "H6609")); f.Step(.1);
                Check(f.Transport.CurrentTarget.Id == "A" && f.Transport.CurrentTarget.Ip == "192.0.2.10" && f.Transport.TargetUpdates == updates,
                    "later G1 discovery retargeted the first-selected strip");
                Check(f.Engine.State.Sending && f.Engine.State.DeviceId == "A" && f.Transport.Enables.Count == 1,
                    "other G1 reply interrupted or reactivated the selected strip");
                f.Engine.Discover(); f.Step(.1);
                Check(f.Transport.CurrentTarget.Id == "A" && f.Transport.Enables.Count == 1, "rediscovery must preserve the selected strip");
            }
        }
        private static void SameDeviceReaddress()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.Emit(Device(f.Clock, "a", "192.0.2.11", "H6609")); f.Step(.1);
                Check(f.Transport.CurrentTarget.Id == "a" && f.Transport.CurrentTarget.Ip == "192.0.2.11", "same identity's DHCP address change was rejected");
                Check(f.Engine.State.Sending && f.Transport.Enables.Count == 2 && f.Transport.Enables[1].Ip == "192.0.2.11",
                    "accepted address change did not establish the new endpoint stream");
                Check(f.Transport.Releases.Count == 1 && f.Transport.Releases[0].Ip == "192.0.2.10",
                    "address change lost the prior endpoint release obligation");
                Check(f.Transport.Colors[f.Transport.Colors.Count - 1].Ip == "192.0.2.11", "screen colors went to the prior address after DHCP change");
                f.Transport.Emit(Device(f.Clock, "B", "192.0.2.20", "H6609")); f.Step(.1);
                Check(f.Transport.CurrentTarget.Ip == "192.0.2.11" && f.Transport.Enables.Count == 2, "new address lost the selected identity pin");
            }
        }
        private static void ConfiguredIdentity()
        {
            AppSettings config = RunningConfig(); config.DeviceId = "B";
            using (Fixture f = new Fixture(config, true)) {
                f.Step();
                Check(f.Transport.CurrentTarget == null && f.Transport.Enables.Count == 0 && f.Transport.Colors.Count == 0,
                    "first other G1 discovery overrode the configured identity");
                f.Transport.Emit(Device(f.Clock, "B", "192.0.2.20", "H6609")); f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.CurrentTarget.Id == "B" && f.Transport.Enables.Count == 1,
                    "configured G1 failed to become active");
                f.Transport.Emit(Device(f.Clock, "A", "192.0.2.10", "H6609")); f.Step(.1);
                Check(f.Transport.CurrentTarget.Id == "B" && f.Transport.Enables.Count == 1, "later other discovery bypassed configured identity");
            }
        }
        private static void UpdatedIdentity()
        {
            using (Fixture f = Fixture.Running()) {
                AppSettings updated = RunningConfig(); updated.DeviceId = "B";
                f.Engine.Update(updated);
                f.Transport.Emit(Device(f.Clock, "B", "192.0.2.20", "H6609")); f.Step(.1);
                Check(f.Engine.State.Sending && f.Transport.CurrentTarget.Id == "B" && f.Transport.Enables.Count == 2,
                    "explicit settings update did not override first-discovered identity");
                Check(f.Transport.Enables[1].Id == "B" && f.Transport.Colors[f.Transport.Colors.Count - 1].Id == "B",
                    "updated selection's lighting commands targeted the original strip");
                f.Transport.Emit(Device(f.Clock, "A", "192.0.2.10", "H6609")); f.Step(.1);
                Check(f.Transport.CurrentTarget.Id == "B" && f.Transport.Enables.Count == 2, "discovered identity overrode explicit updated selection");
            }
        }
        private sealed class Clock
        {
            public double Seconds = 100;
            public DateTime UtcNow { get { return new DateTime(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } }
            public void Advance(double seconds) { Check(seconds >= 0, "clock cannot go backward"); Seconds += seconds; }
        }
        private sealed class FakeCapture : IEngineCapture
        {
            private readonly CaptureFrame frame;
            private bool fresh = true;
            public int Reads;
            public FakeCapture()
            {
                byte[] pixels = new byte[256 * 107 * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 120; pixels[i + 1] = 90; pixels[i + 2] = 40; pixels[i + 3] = 255; }
                frame = new CaptureFrame(256, 107, pixels);
            }
            public bool HasNewFrame { get; private set; }
            public string LastError { get { return ""; } }
            public CaptureFrame Capture(Rectangle ignored) { Reads++; HasNewFrame = fresh; fresh = false; return frame; }
            public void Dispose() { }
        }
        private sealed class ColorCommand
        { public string Id, Ip; public Color[] Colors; }
        private sealed class FakeTransport : IEngineTransport
        {
            private readonly Clock clock;
            private DeviceInfo target;
            public bool AutoReplies;
            public int TargetUpdates;
            public readonly List<DeviceInfo> Enables = new List<DeviceInfo>(), Releases = new List<DeviceInfo>();
            public readonly List<ColorCommand> Colors = new List<ColorCommand>();
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public FakeTransport(Clock value, bool automatic) { clock = value; AutoReplies = automatic; }
            public int Generation { get { return 1; } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, Generation); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public void Start() { }
            public void Discover() { if (AutoReplies) Emit(Device(clock, "A", "192.0.2.10", "H6609")); }
            public void Poll() { if (AutoReplies && target != null) { DeviceInfo reply = target.Copy(); reply.LastSeenUtc = clock.UtcNow; Emit(reply); } }
            public void Emit(DeviceInfo reply) { Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply); }
            public void SetTarget(DeviceInfo value) { TargetUpdates++; target = value == null ? null : value.Copy(); }
            public void Enable(EndpointToken owned, Func<bool> authorize)
            { if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic activation canceled"); Enables.Add(owned.Snapshot); }
            public void Disable(EndpointToken owned) { Releases.Add(owned.Snapshot); }
            public void SendColors(EndpointToken owned, Color[] colors, bool flag, Func<bool> authorize)
            {
                if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic color send canceled");
                Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "production packet builder returned no output");
                Colors.Add(new ColorCommand { Id = owned.Id, Ip = owned.Ip, Colors = (Color[])colors.Clone() });
            }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly FakeCapture Capture = new FakeCapture();
            public readonly FakeTransport Transport;
            public readonly GlowEngine Engine;
            public int CaptureCreates;
            public Fixture(AppSettings config, bool autoReplies)
            {
                Transport = new FakeTransport(Clock, autoReplies);
                Engine = new GlowEngine(config, new EngineServices { Transport = Transport, CreateCapture = delegate { CaptureCreates++; return Capture; },
                    UtcNow = delegate { return Clock.UtcNow; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return new Rectangle(0, 0, 3440, 1440); }, CompetitorRunning = delegate { return false; },
                    Log = delegate(string ignored) { }, ListenSystemEvents = false });
            }
            public static Fixture Running()
            {
                Fixture f = new Fixture(RunningConfig(), true); f.Step();
                Check(f.Engine.State.Sending && f.Transport.CurrentTarget.Id == "A" && f.Transport.Enables.Count == 1 && f.Transport.Colors.Count == 1,
                    "fixture first discovery failed to start one active stream"); return f;
            }
            public void Step() { Engine.Step(); }
            public void Step(double seconds) { Clock.Advance(seconds); Engine.Step(); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
