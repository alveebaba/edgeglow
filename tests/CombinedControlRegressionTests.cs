// Combined regressions: production GlowEngine + production GoveeLan.
// Only physical socket/interface/capture/time boundaries are injected.
// Baseline/current test bodies and negative expectations are identical.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EdgeGlow
{
    internal static class CombinedControlRegressionTests
    {
        private const string A = "192.168.50.20", B = "192.168.50.21", Device = "SYNTHETIC-G1";
        private static string traceRoot;
        private static int passed, failed;
        public static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--child") return Child(args[1], args[2], args.Length > 3, args.Length > 3 && args[3] == "owner-force");
            if (args.Length != 1) { Console.Error.WriteLine("Provide isolated evidence directory."); return 2; }
            traceRoot = Path.GetFullPath(args[0]); Directory.CreateDirectory(traceRoot);
            Run("C01_capture_retarget", CaptureRetarget);
            Run("C01_between_mode_and_color", BetweenModeAndColor);
            Run("C03_expiry_after_reply_loss", ExpiryAfterReplyLoss);
            Run("C04_known_off_episode", delegate { PersistentOff(false); });
            Run("C04_known_on_episode", delegate { PersistentOff(true); });
            Run("C04_unknown_original_episode", delegate { PersistentOff(null); });
            Run("C03_deadline_at_send_boundary", DeadlineAtSendBoundary);
            Run("C03_no_ambient_expiry", delegate { NoAmbientExpiry(false); });
            Run("C03_paused_start_expiry", delegate { NoAmbientExpiry(true); });
            Run("C03_cancel_after_reply_loss", CancellationAfterReplyLoss);
            Run("C03_lock_at_send_boundary", delegate { CancelAtSendBoundary(false); });
            Run("C03_suspend_at_send_boundary", delegate { CancelAtSendBoundary(true); });
            Run("C04_observed_on_and_resume_reset", PowerEpisodeReset);
            Run("C05_fast_shutdown", FastShutdown);
            Run("C05_discovery_returns_while_blocked", DiscoverReturnsWhileWorkerBlocked);
            Run("C05_process_lifetime", delegate { ProcessLifetime(false); });
            Run("C05_explicit_force_exit", delegate { ProcessLifetime(true); });
            Run("C05_owner_force_exit_stays_stopped", delegate { ProcessLifetime(true, true); });
            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
            Console.WriteLine("Actual engine/network/parser; in-memory datagrams and generated pixels only. No desktop, GUI, LAN ports, settings or registry.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action action)
        {
            currentCase = name;
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static string currentCase;
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static bool Wait(Func<bool> condition, int milliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < milliseconds) Thread.Sleep(10);
            return condition();
        }
        private static Fixture NewFixture(bool? originalPower)
        { return new Fixture(Path.Combine(traceRoot, currentCase + ".trace.tsv"), originalPower); }

        private static void CaptureRetarget()
        {
            using (Fixture f = NewFixture(true))
            {
                f.Capture.BeforeReturn = delegate { f.Scan(Device, B); };
                f.Step();
                f.Engine.Paused = true; f.Step(.1);
                Check(f.Trace.Count("enable", B) == 0 || f.Trace.Count("disable", B) > 0,
                    "B was activated during A-owned tick but pause never attempted B release");
                Check(f.Trace.Count("colors", B) == 0 || f.Trace.Count("disable", B) > 0,
                    "B received colors without retained B release obligation");
                f.NoteState();
            }
        }
        private static void BetweenModeAndColor()
        {
            using (Fixture f = NewFixture(true))
            {
                f.Forward.AfterEnable = delegate { f.Scan(Device, B); };
                f.Step();
                Check(f.Trace.Count("enable", A) == 1, "fixture did not activate A before the retarget barrier");
                Check(f.Trace.Count("colors", B) == 0, "post-activation colors silently followed a new global target");
                f.Engine.Paused = true; f.Step(.1); f.NoteState();
            }
        }
        private static void ExpiryAfterReplyLoss()
        {
            using (Fixture f = NewFixture(true))
            {
                f.Step(); f.Clock.Set(17); f.Engine.TestZone(3); f.Step();
                Check(f.Trace.PatternCount > 0, "fixture did not establish identification at t=17");
                f.Clock.Set(20.1); f.Step();
                int colors = f.Trace.Count("colors", null);
                f.Clock.Set(25.1); f.Step(); f.Clock.Set(26.2); f.Step();
                Check(f.Trace.Count("disable", A) > 0, "identification expiry was consumed during lost-reply return without release");
                Check(f.Trace.Count("colors", null) == colors, "expired identification or ambient reactivation was newly sent while unreachable");
                f.NoteState();
            }
        }
        private static void PersistentOff(bool? originalPower)
        {
            using (Fixture f = NewFixture(originalPower))
            {
                f.Step();
                int initial = f.Trace.Count("enable", A);
                for (int i = 1; i <= 20; i++) { f.Clock.Set(i * 5); f.Status(false); f.Step(); }
                int recovery = f.Trace.Count("enable", A) - initial;
                Check(recovery <= 3, "one persistent observed-off episode performed " + recovery + " recovery activations; cap is three");
                Check(!f.Engine.State.Sending, "off episode exhaustion still reports active sending");
                Check(f.Trace.Count("brightness", null) == 0, "recovery changed global brightness");
                if (!originalPower.HasValue) Check(f.Trace.Count("turn1", null) == 0, "unknown original power was retroactively invented as off");
                f.NoteState();
            }
        }


        private static void DeadlineAtSendBoundary()
        {
            using (Fixture f = NewFixture(true)) {
                f.Engine.TestZone(2);
                f.Forward.AfterEnable = delegate { f.Clock.Set(9); };
                f.Step();
                Check(f.Trace.Count("enable", A) == 1, "deadline barrier did not follow actual activation");
                Check(f.Trace.PatternCount == 0, "identification expired during activation but was newly sent afterward");
                Check(f.Trace.Count("disable", A) > 0, "expired first identification left partial activation unreleased");
            }
        }
        private static void NoAmbientExpiry(bool paused)
        {
            using (Fixture f = NewFixture(true)) {
                f.Engine.Paused = paused; f.Clock.Set(17); f.Engine.TestZone(1); f.Step();
                Check(f.Trace.PatternCount == 1, "no-ambient calibration was not sent");
                f.Clock.Set(20.1); f.Step(); f.Clock.Set(25.1); f.Step();
                Check(f.Trace.Count("disable", A) > 0, "no-ambient override expiry did not release retained endpoint");
                Check(f.Trace.PatternCount == 1, "no-ambient expired pattern was resent");
            }
        }
        private static void CancellationAfterReplyLoss()
        {
            using (Fixture f = NewFixture(true)) {
                f.Step(); f.Clock.Set(17); f.Engine.TestZone(4); f.Step();
                f.Clock.Set(20.1); f.Step(); f.Engine.CancelTest(); f.Step();
                Check(f.Trace.Count("disable", A) > 0, "explicit cancellation was lost behind unreachable early return");
            }
        }
        private static void CancelAtSendBoundary(bool suspended)
        {
            using (Fixture f = NewFixture(true)) {
                f.Engine.TestZone(4);
                f.Forward.AfterEnable = delegate { f.Engine.SetSessionBlocked(!suspended, suspended); };
                f.Step();
                Check(f.Trace.PatternCount == 0, "blocked session authorized new identification after activation boundary");
                f.Step(.1);
                Check(f.Trace.Count("disable", A) > 0, "blocked partial session was not released");
            }
        }
        private static void PowerEpisodeReset()
        {
            using (Fixture f = NewFixture(true)) {
                f.Step();
                for (int i=1;i<=20;i++) { f.Clock.Set(i*5); f.Status(false); f.Step(); }
                f.Clock.Set(105);f.Status(true);f.Step();
                int afterOn=f.Trace.Count("enable", A);
                f.Clock.Set(110);f.Status(false);f.Step();
                Check(f.Trace.Count("enable",A)==afterOn+1, "genuine observed on-to-off transition did not permit a new episode");
                for (int i=1;i<=20;i++) { f.Clock.Set(110+i*5);f.Status(false);f.Step(); }
                Check(f.Trace.Count("enable",A)<=afterOn+3, "new off episode was not finite");
                int beforeResume=f.Trace.Count("enable",A);
                f.Engine.Paused=true;f.Step(.1);f.Engine.Paused=false;f.Step(.1);
                Check(f.Trace.Count("enable",A)==beforeResume+1,"explicit resume did not reset exhausted episode");
            }
        }

        private static void DiscoverReturnsWhileWorkerBlocked()
        {
            using(Fixture f=NewFixture(true))
            using(ManualResetEvent entered=new ManualResetEvent(false))
            using(ManualResetEvent release=new ManualResetEvent(false))
            using(ManualResetEvent returned=new ManualResetEvent(false)) {
                f.Engine.Start();
                Check(Wait(delegate { return f.Trace.Count("enable",A)>0; },2000),"worker did not establish session");
                f.Capture.BeforeReturn=delegate { entered.Set();release.WaitOne(); };
                Check(entered.WaitOne(2000),"capture barrier not entered");
                Thread ui=new Thread(new ThreadStart(delegate { f.Engine.Discover(); returned.Set(); })) { IsBackground=true };
                bool prompt=false;
                try { ui.Start();prompt=returned.WaitOne(250);f.Trace.Record("discover-return","",prompt,"UI-equivalent call with worker blocked"); }
                finally { release.Set();ui.Join(2000); }
                Check(prompt,"public discovery blocked on worker tick and prevented ordinary close interaction");
            }
        }
        private static void FastShutdown()
        {
            using (Fixture f=NewFixture(true)) {
                f.Engine.Start();
                Check(Wait(delegate { return f.Trace.Count("enable", A)>0; },2000), "fast-shutdown worker did not establish session");
#if PHASE_C_BASELINE
                f.Engine.Dispose();
#else
                ShutdownCoordinator coordinator=new ShutdownCoordinator(f.Engine);coordinator.Request();
                Check(coordinator.WaitForCompletion(2000) && coordinator.Completed && !coordinator.Pending,"fast shutdown remained pending");
#endif
                Check(f.Trace.Count("disable",A)==1,"fast shutdown did not attempt exactly one release");
                Check(f.Trace.Count("capture-dispose",null)==1,"fast shutdown did not dispose one owned capture");
                Check(f.Trace.Count("socket-dispose",null)==2,"fast shutdown did not dispose both owned sockets");
            }
        }

        private static void ProcessLifetime(bool force)
        {
            ProcessLifetime(force, false);
        }
        private static void ProcessLifetime(bool force, bool expectedExit)
        {
            string prefix = "Local\\EdgeGlowSynthetic-" + Guid.NewGuid().ToString("N");
            string childTrace = Path.Combine(traceRoot, currentCase+"_child.trace.tsv");
            using (EventWaitHandle entered = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-entered"))
            using (EventWaitHandle release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-release"))
            using (EventWaitHandle stopping = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-stopping"))
            using (Process child = new Process())
            {
                child.StartInfo = new ProcessStartInfo {
                    FileName = System.Reflection.Assembly.GetExecutingAssembly().Location,
                    Arguments = "--child \"" + prefix + "\" \"" + childTrace + "\"" + (force ? (expectedExit ? " owner-force" : " force") : ""),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                child.Start();
                bool stayedAlive = false, completed = false;
                try {
                    Check(entered.WaitOne(3000), "child worker never reached injected blocking capture");
                    Check(stopping.WaitOne(3000), "child never requested shutdown");
                    if (force) {
                        Check(child.WaitForExit(2000), "explicit force-exit did not terminate the blocked synthetic child");
                        Check(child.ExitCode==(expectedExit ? 0 : 2), "forced exit code did not preserve owner intent or incomplete-test distinction");
                        Check(File.ReadAllText(childTrace).IndexOf("force-request",StringComparison.Ordinal)>=0,"force path was not explicit in trace");
                        return;
                    }
                    stayedAlive = !child.WaitForExit(5200);
                    release.Set();
                    completed = child.WaitForExit(5000);
                    if (!completed) { child.Kill(); child.WaitForExit(2000); }
                    File.WriteAllText(Path.Combine(traceRoot, currentCase+"_child.stdout.txt"), child.StandardOutput.ReadToEnd());
                    File.WriteAllText(Path.Combine(traceRoot, currentCase+"_child.stderr.txt"), child.StandardError.ReadToEnd());
                    Check(stayedAlive, "ordinary main lifetime ended after four-second join while cleanup remained on background worker");
                    Check(completed && child.ExitCode == 0, "child did not complete cleanly after barrier release");
                    string[] lines = File.ReadAllLines(childTrace);
                    bool requested = false, released = false, disposedCapture = false;
                    foreach (string line in lines) {
                        if (line.IndexOf("\tstop-request\t", StringComparison.Ordinal) >= 0) requested = true;
                        if (requested && (line.IndexOf("\tcolors\t", StringComparison.Ordinal) >= 0 ||
                            line.IndexOf("\tenable\t", StringComparison.Ordinal) >= 0))
                            throw new InvalidOperationException("new control write was authorized after observed shutdown cancellation");
                        if (line.IndexOf("\tdisable\t", StringComparison.Ordinal) >= 0) released = true;
                        if (line.IndexOf("\tcapture-dispose\t", StringComparison.Ordinal) >= 0) disposedCapture = true;
                    }
                    Check(released && disposedCapture, "child ended without release attempt and owned capture cleanup");
                } finally {
                    release.Set();
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); }
                    File.WriteAllText(Path.Combine(traceRoot,currentCase+"_child.exit.txt"),child.ExitCode.ToString());
                    File.WriteAllText(Path.Combine(traceRoot,currentCase+"_child.final.stdout.txt"),child.StandardOutput.ReadToEnd());
                    File.WriteAllText(Path.Combine(traceRoot,currentCase+"_child.final.stderr.txt"),child.StandardError.ReadToEnd());
                }
            }
        }
        private static int Child(string prefix, string output, bool force, bool expectedExit)
        {
            EventWaitHandle entered = EventWaitHandle.OpenExisting(prefix + "-entered");
            EventWaitHandle release = EventWaitHandle.OpenExisting(prefix + "-release");
            EventWaitHandle stopping = EventWaitHandle.OpenExisting(prefix + "-stopping");
            Fixture f = new Fixture(output, true);
            f.Engine.Start();
            if (!Wait(delegate { return f.Trace.Count("enable", A) > 0; }, 2000)) return 31;
            f.Capture.BeforeReturn = delegate {
                entered.Set(); release.WaitOne(); f.Clock.Set(10); f.Capture.Color = Color.CornflowerBlue;
            };
            if (!entered.WaitOne(3000)) return 32;
            f.Trace.Record("stop-request", "", true, "ordinary application exit requested");
            stopping.Set();
#if PHASE_C_BASELINE
            f.Engine.Dispose();
#else
            ShutdownCoordinator coordinator = new ShutdownCoordinator(f.Engine);
            coordinator.Request();
            if (force) { f.Trace.Record("force-request", "", false, "explicit incomplete exit; no delivery or cleanup claim"); if (expectedExit) coordinator.ForceExit(true); else coordinator.ForceExit(); }
            bool complete = coordinator.WaitForCompletion(4000);
            f.Trace.Record("coordinator-return", "", complete, complete ? "completed" : "pending; foreground worker retains lifetime");
#endif
            f.Trace.Record("main-return", "", true, "synthetic foreground main returning");
            // Deliberately no second Dispose or finally wait: mirrors actual app-main return.
            // Fixed production worker/coordinator must retain the process while cleanup is pending.
            return 0;
        }

        private sealed class TestClock
        {
            private long ticks;
            public double Seconds { get { return Interlocked.Read(ref ticks) / (double)TimeSpan.TicksPerSecond; } }
            public DateTime UtcNow { get { return new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(Interlocked.Read(ref ticks)); } }
            public void Set(double seconds) { Interlocked.Exchange(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond)); }
            public void Advance(double seconds) { Interlocked.Add(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond)); }
        }
        private sealed class TraceLog
        {
            private readonly object gate = new object();
            private readonly string path;
            private readonly TestClock clock;
            private long sequence;
            private readonly List<string[]> rows = new List<string[]>();
            public Func<int> Generation;
            public TraceLog(string output, TestClock time) {
                path = output; clock = time;
                File.WriteAllText(path, "sequence\ttime\tthread\tgeneration\tclass\tendpoint\tlocal-result\tdetail\n");
            }
            public void Record(string kind, string endpoint, bool success, string detail) {
                lock (gate) {
                    int generation = Generation == null ? 0 : Generation();
                    string[] row = { (++sequence).ToString(), clock.Seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        Thread.CurrentThread.ManagedThreadId.ToString(), generation.ToString(), kind, endpoint,
                        success ? "local-success-unacknowledged" : "local-failure", (detail ?? "").Replace("\r", "").Replace("\n", " ") };
                    rows.Add(row); File.AppendAllText(path, String.Join("\t", row) + "\n");
                }
            }
            public int Count(string kind, string endpoint) {
                lock (gate) { int n = 0; foreach (string[] row in rows)
                    if (row[4] == kind && (endpoint == null || row[5] == endpoint) && row[6].StartsWith("local-success")) n++; return n; }
            }
            public int PatternCount { get { lock (gate) { int n=0; foreach(string[] r in rows) if(r[4]=="colors" && r[7].IndexOf("pattern",StringComparison.Ordinal)>=0)n++; return n; } } }
        }
        private sealed class GeneratedCapture : IEngineCapture
        {
            private readonly TraceLog trace;
            public Action BeforeReturn;
            public Color Color = Color.FromArgb(40, 90, 120);
            public GeneratedCapture(TraceLog output) { trace = output; }
            public bool HasNewFrame { get { return true; } }
            public string LastError { get { return ""; } }
            public CaptureFrame Capture(Rectangle ignored) {
                Action action = Interlocked.Exchange(ref BeforeReturn, null); if (action != null) action();
                byte[] pixels = new byte[256 * 107 * 4];
                for (int i=0;i<pixels.Length;i+=4) { pixels[i]=Color.B;pixels[i+1]=Color.G;pixels[i+2]=Color.R;pixels[i+3]=255; }
                return new CaptureFrame(256,107,pixels);
            }
            public void Dispose() { trace.Record("capture-dispose", "", true, "owned capture released"); }
        }
        // Only forwards actual production calls. Hooks are scheduling barriers, not transport logic.
        private sealed class Forwarder : IEngineTransport
        {
            public readonly GoveeLan Lan;
            public Action AfterEnable;
            #if !PHASE_C_BASELINE
            private readonly EngineServices.LanAdapter adapter;
#endif
            public Forwarder(GoveeLan lan) { Lan=lan;
#if !PHASE_C_BASELINE
                adapter=new EngineServices.LanAdapter(lan);
#endif
            }
            public event Action<DeviceInfo> DeviceSeen { add { Lan.DeviceSeen+=value; } remove { Lan.DeviceSeen-=value; } }
            public event Action<string> Error { add { Lan.Error+=value; } remove { Lan.Error-=value; } }
            public DeviceInfo CurrentTarget { get { return Lan.CurrentTarget; } }
            public DateTime LastSeenUtc { get { return Lan.LastSeenUtc; } }
            public int Generation { get { return Lan.Generation; } }
            public void Start() { Lan.Start(); }
            public void Discover() { Lan.Discover(); }
            public void Poll() { Lan.Poll(); }
            public void SetTarget(DeviceInfo device) { Lan.SetTarget(device); }
#if PHASE_C_BASELINE
            public void Enable() { Lan.Enable(); Barrier(); }
            public void Disable(DeviceInfo device) { Lan.Disable(device); }
            public void SendColors(Color[] colors,bool flag) { Lan.SendColors(colors,flag); }
#else
            public EndpointToken CurrentEndpoint { get { return adapter.CurrentEndpoint; } }
            public void Enable(EndpointToken token,Func<bool> maySend) { adapter.Enable(token,maySend); Barrier(); }
            public void Disable(EndpointToken token) { adapter.Disable(token); }
            public void SendColors(EndpointToken token,Color[] colors,bool flag,Func<bool> maySend) { adapter.SendColors(token,colors,flag,maySend); }
#endif
            private void Barrier() { Action action=Interlocked.Exchange(ref AfterEnable,null);if(action!=null)action(); }
            public void Dispose() { Lan.Dispose(); }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly TestClock Clock=new TestClock();
            public readonly TraceLog Trace;
            public readonly GoveeLan Lan;
            public readonly Forwarder Forward;
            public readonly GeneratedCapture Capture;
            public readonly GlowEngine Engine;
            private readonly List<MemorySocket> sockets=new List<MemorySocket>();
            public Fixture(string traceFile,bool? power) {
                Trace=new TraceLog(traceFile,Clock);
                LanInterface local=new LanInterface { Id="generated-test-interface",Address=IPAddress.Parse("192.168.50.10"),
                    Mask=new byte[]{255,255,255,0},Broadcast=IPAddress.Parse("192.168.50.255"),Route="192.168.50.1" };
                Lan=new GoveeLan(delegate(bool reply) { MemorySocket s=new MemorySocket(Trace);lock(sockets)sockets.Add(s);return s; },
                    delegate { return new LanInterface[]{local}; },delegate { return Clock.UtcNow; });
                Trace.Generation=delegate { return Lan.Generation; };
                Lan.Start();
                DeviceInfo device=Scan(Device,A);Lan.SetTarget(device);
                if(power.HasValue)Status(power.Value);
                Forward=new Forwarder(Lan);Capture=new GeneratedCapture(Trace);
                AppSettings settings=new AppSettings { DeviceId=Device,DeviceIp=A,AutoSync=true,StartWithWindows=false,
                    AreaMode="Full",SmoothMs=0,Brightness=100,Saturation=100,Fps=24,Monitor="GENERATED" };
                Engine=new GlowEngine(settings,new EngineServices {
                    Transport=Forward,CreateCapture=delegate { return Capture; },UtcNow=delegate { return Clock.UtcNow; },
                    MonotonicSeconds=delegate { return Clock.Seconds; },SelectBounds=delegate(AppSettings c) { return new Rectangle(0,0,3440,1440); },
                    CompetitorRunning=delegate { return false; },Log=delegate(string s) { Trace.Record("engine-log","",true,s); },ListenSystemEvents=false
                });
            }
            public DeviceInfo Scan(string id,string ip) {
                string json="{\"msg\":{\"cmd\":\"scan\",\"data\":{\"device\":\""+id+"\",\"sku\":\"H6609\",\"ip\":\""+ip+"\"}}}";
                Trace.Record("scan-input",ip,true,id);
                return Lan.AcceptDatagram(Encoding.UTF8.GetBytes(json),new IPEndPoint(IPAddress.Parse(ip),4003),Lan.Generation);
            }
            public void Status(bool on) {
                string json="{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":"+(on?"1":"0")+",\"brightness\":17}}}";
                Trace.Record("status-input",A,true,on?"on":"off");
                Check(Lan.AcceptDatagram(Encoding.UTF8.GetBytes(json),new IPEndPoint(IPAddress.Parse(A),4003),Lan.Generation)!=null,"real parser rejected fixture status");
            }
            public void Step() { Engine.Step(); }
            public void Step(double seconds) { Clock.Advance(seconds);Engine.Step(); }
            public void NoteState() { Trace.Record("engine-state","",true,Engine.State.Message+" | "+Engine.State.OutputPolicy+" | pending="+Engine.State.ReleasePending); }
            public void Dispose() { Engine.Dispose();NoteState(); }
        }
        private sealed class MemorySocket : ILanDatagramSocket
        {
            private readonly TraceLog trace;
            private readonly ManualResetEvent closed=new ManualResetEvent(false);
            private int disposed;
            public MemorySocket(TraceLog log) { trace=log; }
            public void Send(byte[] bytes,IPEndPoint destination) {
                if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("memory socket");
                string json=Encoding.UTF8.GetString(bytes),kind="other",detail=json;
                if(json.IndexOf("\"cmd\":\"scan\"",StringComparison.Ordinal)>=0)kind="scan";
                else if(json.IndexOf("\"cmd\":\"devStatus\"",StringComparison.Ordinal)>=0)kind="poll";
                else if(json.IndexOf(GoveeLan.EnablePacket,StringComparison.Ordinal)>=0)kind="enable";
                else if(json.IndexOf(GoveeLan.DisablePacket,StringComparison.Ordinal)>=0)kind="disable";
                else if(json.IndexOf("\"cmd\":\"brightness\"",StringComparison.Ordinal)>=0)kind="brightness";
                else if(json.IndexOf("\"cmd\":\"turn\"",StringComparison.Ordinal)>=0)kind=json.IndexOf("\"value\":1",StringComparison.Ordinal)>=0?"turn1":"turn0";
                else if(json.IndexOf("\"cmd\":\"razer\"",StringComparison.Ordinal)>=0) {
                    kind="colors";
                    int p=json.IndexOf("\"pt\":\"",StringComparison.Ordinal)+6;
                    int end=json.IndexOf('"',p);byte[] wire=Convert.FromBase64String(json.Substring(p,end-p));
                    int lit=0;for(int i=0;i<10;i++)if(wire[6+i*3]!=0||wire[7+i*3]!=0||wire[8+i*3]!=0)lit++;
                    detail=(lit==1?"pattern ":"ambient ")+json;
                }
                trace.Record(kind,destination.Address.ToString(),true,detail);
            }
            public byte[] Receive(out IPEndPoint source) {
                source=null;closed.WaitOne(25);
                if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("memory socket");
                throw new SocketException((int)SocketError.TimedOut);
            }
            public void Join(LanInterface local) { }
            public void ConfigureMulticast(LanInterface local) { }
            public void Dispose() { if(Interlocked.Exchange(ref disposed,1)==0) { closed.Set();trace.Record("socket-dispose","",true,"owned socket closed"); } }
        }
    }
}
