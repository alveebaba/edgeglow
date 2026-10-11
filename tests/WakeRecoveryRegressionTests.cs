// Matched baseline/candidate harness. Production GlowEngine + GoveeLan;
// generated pixels, deterministic time, and in-memory sockets ONLY.
// No new candidate-only public member is needed to compile this witness.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EdgeGlow
{
    internal static class WakeRecoveryRegressionTests
    {
        private static int passed, failed, assertions;
        private static string output, current;
        private static void Check(bool ok, string message)
        { assertions++; if (!ok) throw new InvalidOperationException(message); }
        public static int Main(string[] args)
        {
            if (args.Length != 1) { Console.Error.WriteLine("Supply a NEW isolated trace directory."); return 2; }
            output = Path.GetFullPath(args[0]);
            if (Directory.Exists(output)) { Console.Error.WriteLine("Trace directory already exists."); return 2; }
            Directory.CreateDirectory(output);
            Run("lost_enable_on_status", delegate { SilentDrop("enable", true); });
            Run("lost_power_original_on", delegate { SilentDrop("turn1", true); });
            Run("lost_power_original_unknown", delegate { SilentDrop("turn1", null); });
            Run("lost_power_original_off", delegate { SilentDrop("turn1", false); });
            Run("all_silent_drops_finite", AllDrops);
            Run("power_then_mode_model", PowerThenMode);
            Run("healthy_playback_after_wake", HealthyPlayback);
            Run("fresh_then_thrown_wake_then_cached", ThrownThenCached);
            Run("short_capture_miss_does_not_repeat_off", CaptureMiss);
            Run("deadline_serviced_during_capture_failure", Deadline);
            Run("late_replies_before_first_wake", LateReplies);
            Run("off_on_between_datagrams_requires_fresh_image", RenewedTransition);
            foreach (string action in new string[] { "pause", "off", "lock", "sleep", "exit", "identity", "control-version", "cancel-receipt", "endpoint-move", "retired-generation" })
                for (int i = 1; i <= 2; i++) {
                    string a = action; int boundary = i;
                    Run("cancel_" + a + "_boundary_" + i, delegate { CancelBoundary(a, boundary); });
                }
            Run("competitor_observed_before_retry", Competitor);
            Run("selected_monitor_removed_before_retry", MissingMonitor);
            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + assertions + " assertions.");
            Console.WriteLine("MODELED delivery/firmware only; no LAN, capture, GUI, settings, startup or device access.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            current = name;
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static Fixture Open(bool? original) { return new Fixture(Path.Combine(output, current + ".tsv"), original); }
        private static void SilentDrop(string kind, bool? original)
        {
            using (Fixture f = Open(original)) {
                f.Block(); f.DropFirst = kind; f.Resume();
                Check(!f.Visible, "The fixture did not suppress first-wake visible output.");
                f.Step(.5); f.Step(.51); f.Step(1.01);
                Check(f.Visible, "Silent local success never regained modeled visible output.");
                Check(f.WakeCalls == 3 && f.Count("enable") == 3 && f.Count("turn1") == 3, "Wake episode did not use exactly three bounded pairs.");
                f.AssertSpacing(); f.AssertOriginalRelease(original);
            }
        }
        private static void AllDrops()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.DropAll = true; f.Resume();
                for (int i = 0; i < 40; i++) f.Step(1.01);
                Check(!f.Visible, "All drops were reported as modeled visible output.");
                Check(f.WakeCalls == 3 && f.Count("enable") == 3 && f.Count("turn1") == 3, "Wake loss escaped the finite pair budget.");
                Check(!f.Lan.HasStandby(Fixture.Id), "Exhausted wake retained power authority.");
                Check(!f.Engine.State.Sending, "Newer persistent off status was ignored after budget exhaustion.");
                f.AssertSpacing();
            }
        }
        private static void PowerThenMode()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.ResetModeOnPower = true; f.Resume();
                Check(f.Visible, "Hypothetical mode reset on turn1 erased the final stream-enable.");
                Check(f.First("turn1") < f.First("enable"), "Wake pair was not power-then-mode.");
            }
        }
        private static void HealthyPlayback()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.Resume(); f.Step(1.01); f.Step(1.01);
                int enables = f.Count("enable"), ons = f.Count("turn1");
                for (int i = 0; i < 600; i++) f.Step(1);
                Check(f.Visible && f.Count("enable") == enables && f.Count("turn1") == ons, "Ordinary cached playback reinitialized mode.");
                Check(f.Engine.State.FreshFrames == 2, "Static-playback fixture accidentally kept supplying fresh frames.");
            }
        }
        private static void ThrownThenCached()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.ThrowWakeOnce = true; f.Resume();
                Check(!f.Engine.State.AutomaticStandby && f.WakeCalls == 1, "Thrown wake was not separated from standby.");
                f.Step(.5); Check(f.WakeCalls == 1, "Thrown wake ignored spacing.");
                f.Step(.51);
                Check(f.Visible && f.WakeCalls == 2 && f.Engine.State.FreshFrames == 2, "One fresh image did not survive a thrown wake on a healthy cache.");
            }
        }
        private static void CaptureMiss()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.DropFirst = "enable"; f.Resume();
                f.Capture.Failing = true; f.Step(.2);
                Check(f.Count("turn0") == 0, "Keeping wake authority caused another off on a short capture miss.");
                f.Capture.Failing = false; f.Step(1.01);
                Check(f.Visible, "A short capture miss stranded wake reacquisition.");
            }
        }
        private static void Deadline()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.DropAll = true; f.Resume(); f.Capture.Failing = true; f.Step(6.01);
                Check(f.WakeCalls == 1 && !f.Lan.HasStandby(Fixture.Id), "Capture failure extended wake authority past its deadline.");
                f.Capture.Failing = false; f.Step(.1);
                Check(f.WakeCalls == 1 && f.Count("enable") == 1, "Expired wake leaked into generic off recovery.");
            }
        }
        private static void LateReplies()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.Replies = false; f.Step(25);
                f.Engine.SetDisplayPowerState(1); f.Capture.Fresh = true; f.Step(1);
                Check(f.WakeCalls == 0, "Stale replies authorized wake.");
                f.Replies = true; f.Scan(); f.Status(); f.Step(.1);
                Check(f.Visible && f.WakeCalls == 1, "Late network before first wake incorrectly consumed its budget.");
            }
        }
        private static void RenewedTransition()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.Boundary = 2; f.AtBoundary = delegate {
                    f.Engine.SetDisplayPowerState(0); f.Engine.SetDisplayPowerState(1);
                }; f.Resume(); int colors = f.Count("colors");
                f.Step(1.01); f.Step(1.01);
                Check(f.Count("colors") == colors, "Off/On between wake datagrams reused an image from before the transition.");
                f.Capture.Fresh = true; f.Step(.1);
                // The old episode must stay canceled. New ordinary control remains
                // separate from proof of physical power/stream restoration.
                Check(f.WakeCalls == 1, "A canceled episode replayed after renewed display control.");
            }
        }
        private static void CancelBoundary(string action, int boundary)
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.Boundary = boundary; f.AtBoundary = delegate { f.Change(action); }; f.Resume();
                Check(f.Count("enable") + f.Count("turn1") == boundary - 1, "Canceled boundary emitted a later wake command.");
                Check(f.Count("colors") == 0, "Canceled activation sent colors with a stale token or control version.");
                if (action == "exit") {
                    int ons = f.Count("turn1"), enables = f.Count("enable");
                    Check(f.Engine.WaitForShutdown(4000), "Isolated exit did not finish cleanup.");
                    Check(f.WakeCalls == 1 && f.Count("turn1") == ons && f.Count("enable") == enables, "Exit emitted a new wake.");
                }
                if (action == "pause" || action == "off" || action == "lock" || action == "sleep" || action == "identity") {
                    f.Step(1.01); f.Step(1.01);
                    Check(f.WakeCalls == 1, "Blocked/deselected/exit state replayed a stale wake.");
                }
            }
        }
        private static void Competitor()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.Resume(); f.Competing = true; f.Engine.InvalidateDisplayTopology(); f.Step(1.01);
                Check(f.WakeCalls == 1 && !f.Lan.HasStandby(Fixture.Id), "Detected competitor did not revoke wake authority.");
            }
        }
        private static void MissingMonitor()
        {
            using (Fixture f = Open(true)) {
                f.Block(); f.Resume(); f.Bounds = Rectangle.Empty; f.Engine.InvalidateDisplayTopology(); f.Step(1.01);
                Check(f.WakeCalls == 1 && f.Count("turn0") > 0 && !f.Engine.State.Sending, "Missing selected screen did not replace wake with standby.");
            }
        }
        private sealed class Pixels : IEngineCapture, IEngineCaptureMetadata
        {
            private readonly CaptureFrame frame;
            public bool Fresh = true, Failing;
            public Pixels() {
                byte[] bytes = new byte[256 * 107 * 4];
                for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 80; bytes[i + 1] = 130; bytes[i + 2] = 40; bytes[i + 3] = 255; }
                frame = new CaptureFrame(256, 107, bytes);
            }
            public bool HasNewFrame { get; private set; }
            public bool HasFreshDesktopFrame { get { return HasNewFrame; } }
            public bool IsReprocessedFrame { get { return false; } }
            public bool ProtectedContentMasked { get { return false; } }
            public string Description { get { return "Generated, current-generation fixture"; } }
            public string LastError { get { return Failing ? "Injected capture failure" : ""; } }
            public CaptureFrame Capture(Rectangle ignored) { HasNewFrame = Fresh && !Failing; if (!Failing) Fresh = false; return frame; }
            public void Dispose() { } // Factory reuses this explicit fake, not a native cache.
        }
        private sealed class Entry { public double Time; public string Kind; }
        private sealed class Fixture : IDisposable
        {
            public const string Id = "SYNTHETIC-G1", Ip = "192.168.50.20";
            public readonly GoveeLan Lan;
            public readonly GlowEngine Engine;
            public readonly Pixels Capture = new Pixels();
            public Rectangle Bounds = new Rectangle(0, 0, 3440, 1440);
            public bool Replies = true, Competing, ThrowWakeOnce, DropAll, ResetModeOnPower, Power = true, Mode, Visible;
            public string DropFirst = "";
            public int WakeCalls, Boundary;
            public Action AtBoundary;
            private int boundaryCalls;
            private double time = 100;
            private readonly object clockGate = new object();
            private readonly string trace;
            private bool recording;
            private string ip = Ip;
            private LanInterface[] interfaces;
            private readonly List<Entry> entries = new List<Entry>();
            private readonly List<double> wakeTimes = new List<double>();
            private readonly List<string> lines = new List<string>();
            private readonly object traceGate = new object();
            private void Trace(string message) { lock (traceGate) lines.Add(message); }
            private readonly List<MemorySocket> sockets = new List<MemorySocket>();
            private double Now { get { lock (clockGate) return time; } }
            private DateTime Utc { get { return new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).AddSeconds(Now); } }
            public Fixture(string path, bool? original) {
                trace = path; interfaces = new LanInterface[] { Interface("fake") };
                Lan = new GoveeLan(delegate(bool reply) { MemorySocket socket = new MemorySocket(this); sockets.Add(socket); return socket; },
                    delegate { return interfaces; }, delegate { return Utc; });
                Lan.Start(); Scan(); Power = original ?? true; if (original.HasValue) Status();
                Engine = new GlowEngine(Config(Id), new EngineServices { Transport = new Forward(this), CreateCapture = delegate { return Capture; },
                    UtcNow = delegate { return Utc; }, MonotonicSeconds = delegate { return Now; },
                    SelectBounds = delegate(AppSettings ignored) { return Bounds; }, CompetitorRunning = delegate { return Competing; },
                    Log = delegate(string message) { Trace("LOG\t" + message); }, ListenSystemEvents = false });
                Engine.Step(); Check(Engine.State.Sending, "Initial generated frame failed to activate.");
            }
            private static AppSettings Config(string identity) { return new AppSettings { DeviceId = identity, Monitor = "SYNTHETIC", AutoSync = true,
                StartWithWindows = false, Fps = 24, SmoothMs = 75, ZoneCount = 18, Rotation = 15, Reverse = false,
                EdgePercent = 20, AreaMode = "Full", Brightness = 80, Saturation = 140, ColorGammaPercent = 100, FavorVividColors = true, IgnoreBlackPixels = true }; }
            private static LanInterface Interface(string name) { return new LanInterface { Id = name, Address = IPAddress.Parse("192.168.50.10"),
                Mask = new byte[] { 255, 255, 255, 0 }, Broadcast = IPAddress.Parse("192.168.50.255"), Route = "192.168.50.1" }; }
            public void Step(double dt) {
                lock (clockGate) time += dt;
                if (Replies) Status(); Engine.Step();
                Trace(Now.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\tSTATE\t" + Engine.State.OutputPolicy + "\tmodeledVisible=" + Visible);
            }
            public void Block() { Engine.SetDisplayPowerState(0); Step(.1); Check(!Power && !Mode, "Fixture standby was not established."); recording = true; lock (traceGate) entries.Clear(); Visible = false; }
            public void Resume() { Engine.SetDisplayPowerState(1); Capture.Fresh = true; Step(.1); }
            public void Scan() { DeviceInfo d = Lan.AcceptDatagram(Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"ip\":\"" + ip + "\",\"device\":\"" + Id + "\",\"sku\":\"H6609\"}}}"), new IPEndPoint(IPAddress.Parse(ip), 4003), Lan.Generation); Check(d != null, "In-memory scan rejected."); Lan.SetTarget(d); }
            public void Status() { Lan.AcceptDatagram(Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":" + (Power ? "1" : "0") + ",\"brightness\":17}}}"), new IPEndPoint(IPAddress.Parse(ip), 4003), Lan.Generation); }
            public int Count(string kind) { lock (traceGate) { int n = 0; foreach (Entry e in entries) if (e.Kind == kind) n++; return n; } }
            public int First(string kind) { lock (traceGate) { for (int i = 0; i < entries.Count; i++) if (entries[i].Kind == kind) return i; return Int32.MaxValue; } }
            public void AssertSpacing() { for (int i = 1; i < wakeTimes.Count; i++) Check(wakeTimes[i] - wakeTimes[i - 1] >= 1, "Wake calls closer than one monotonic second."); }
            public void AssertOriginalRelease(bool? original) {
                int off = Count("turn0"); Engine.Paused = true; Step(.1);
                Check(Count("turn0") - off == (original == false ? 1 : 0), "Final release changed the original power policy.");
            }
            public void Change(string action) {
                if (action == "pause") Engine.Paused = true;
                else if (action == "off") Engine.SetDisplayPowerState(0);
                else if (action == "lock") Engine.SetSessionBlocked(true, false);
                else if (action == "sleep") Engine.SetSessionBlocked(false, true);
                else if (action == "exit") Engine.RequestStop();
                else if (action == "identity") Engine.Update(Config("OTHER-G1"));
                else if (action == "control-version") Engine.CancelTest();
                else if (action == "cancel-receipt") Lan.CancelStandby();
                else if (action == "endpoint-move") { ip = "192.168.50.21"; Scan(); }
                else if (action == "retired-generation") { interfaces = new LanInterface[] { Interface("replacement") }; Lan.MaintainTransport(true); Scan(); }
            }
            public void ReceiveSend(byte[] bytes, IPEndPoint destination) {
                string json = Encoding.UTF8.GetString(bytes);
                string kind = json.Contains("\"value\":0") ? "turn0" : json.Contains("\"value\":1") ? "turn1" :
                    json.Contains(GoveeLan.EnablePacket) ? "enable" : json.Contains(GoveeLan.DisablePacket) ? "disable" : "colors";
                if (recording) { lock (traceGate) entries.Add(new Entry { Time = Now, Kind = kind }); }
                bool drop = recording && ((DropAll && (kind == "turn1" || kind == "enable")) || (DropFirst == kind && Count(kind) == 1));
                Trace(Now.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\t" + kind + "\t" + destination.Address + "\tlocalSuccess=true\tmodeledDrop=" + drop);
                if (drop) return;
                if (kind == "turn0") { Power = false; Visible = false; }
                if (kind == "turn1") { Power = true; if (ResetModeOnPower) { Mode = false; Visible = false; } }
                if (kind == "enable") Mode = true;
                if (kind == "disable") { Mode = false; Visible = false; }
                if (kind == "colors") Visible = Power && Mode;
                Check(!json.Contains("\"cmd\":\"brightness\""), "Unexpected brightness command.");
            }
            public event Action<DeviceInfo> DeviceSeen { add { Lan.DeviceSeen += value; } remove { Lan.DeviceSeen -= value; } }
            public event Action<string> Error { add { Lan.Error += value; } remove { Lan.Error -= value; } }
            public DeviceInfo CurrentTarget { get { return Lan.CurrentTarget; } }
            public EndpointToken CurrentEndpoint { get { return Lan.CurrentEndpoint; } }
            public DateTime LastSeenUtc { get { return Lan.LastSeenUtc; } }
            public int Generation { get { return Lan.Generation; } }
            public void Start() { Lan.Start(); }
            public void Discover() { if (Replies) Scan(); }
            public void Poll() { if (Replies) Status(); }
            public void SetTarget(DeviceInfo target) { Lan.SetTarget(target); }
            public void Enable(EndpointToken endpoint, Func<bool> allow) { Lan.Enable(endpoint, allow); }
            public void Disable(EndpointToken endpoint) { Lan.Disable(endpoint); }
            public void Standby(EndpointToken endpoint, Func<bool> allow) { Lan.Standby(endpoint, allow); }
            public void Wake(EndpointToken endpoint, Func<bool> allow) {
                WakeCalls++; wakeTimes.Add(Now);
                if (ThrowWakeOnce) { ThrowWakeOnce = false; throw new IOException("Injected pre-datagram wake failure"); }
                Lan.Wake(endpoint, delegate {
                    boundaryCalls++;
                    if (AtBoundary != null && boundaryCalls == Boundary) { Action action = AtBoundary; AtBoundary = null; action(); }
                    return allow == null || allow();
                });
            }
            public bool HasStandby(string identity) { return Lan.HasStandby(identity); }
            public void CancelStandby() { Lan.CancelStandby(); }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> allow) { Lan.SendColors(endpoint, colors, flag, allow); }
            public void Dispose() { Engine.Dispose(); Lan.Dispose(); string[] snapshot; lock (traceGate) snapshot = lines.ToArray(); File.WriteAllLines(trace, snapshot); }
        }
        // Separate disposal ownership: the engine disposes only the transport;
        // the fixture disposes the engine and then writes its trace.
        private sealed class Forward : IEngineTransport, IEngineStandbyTransport
        {
            private readonly Fixture f;
            public Forward(Fixture fixture) { f = fixture; }
            public event Action<DeviceInfo> DeviceSeen { add { f.DeviceSeen += value; } remove { f.DeviceSeen -= value; } }
            public event Action<string> Error { add { f.Error += value; } remove { f.Error -= value; } }
            public DeviceInfo CurrentTarget { get { return f.CurrentTarget; } }
            public EndpointToken CurrentEndpoint { get { return f.CurrentEndpoint; } }
            public DateTime LastSeenUtc { get { return f.LastSeenUtc; } }
            public int Generation { get { return f.Generation; } }
            public void Start() { f.Start(); }
            public void Discover() { f.Discover(); }
            public void Poll() { f.Poll(); }
            public void SetTarget(DeviceInfo d) { f.SetTarget(d); }
            public void Enable(EndpointToken e, Func<bool> a) { f.Enable(e, a); }
            public void Disable(EndpointToken e) { f.Disable(e); }
            public void Standby(EndpointToken e, Func<bool> a) { f.Standby(e, a); }
            public void Wake(EndpointToken e, Func<bool> a) { f.Wake(e, a); }
            public bool HasStandby(string id) { return f.HasStandby(id); }
            public void CancelStandby() { f.CancelStandby(); }
            public void SendColors(EndpointToken e, Color[] c, bool flag, Func<bool> a) { f.SendColors(e, c, flag, a); }
            public void Dispose() { f.Lan.Dispose(); }
        }
        private sealed class MemorySocket : ILanDatagramSocket
        {
            private readonly Fixture owner;
            private readonly ManualResetEvent closed = new ManualResetEvent(false);
            private volatile bool disposed;
            public MemorySocket(Fixture owner) { this.owner = owner; }
            public void Send(byte[] bytes, IPEndPoint endpoint) { if (disposed) throw new ObjectDisposedException("in-memory socket"); owner.ReceiveSend(bytes, endpoint); }
            public byte[] Receive(out IPEndPoint source) { source = null; closed.WaitOne(10); if (disposed) throw new ObjectDisposedException("in-memory socket"); throw new SocketException((int)SocketError.TimedOut); }
            public void Join(LanInterface local) { }
            public void ConfigureMulticast(LanInterface local) { }
            public void Dispose() { disposed = true; closed.Set(); }
        }
    }
}
