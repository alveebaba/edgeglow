using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace EdgeGlow
{
    // Compile with the actual Engine.cs, Capture.cs, Network.cs and Settings.cs.
    // The injected dependencies open no sockets, windows, registry or settings files.
    public static class OutputDiagnosticsTests
    {
        private static int passed;
        public static int Main(string[] args)
        {
            try {
                Run("successful color history, black boundaries and heartbeat", ColorHistory);
                Run("failed and canceled sends do not commit diagnostics", FailedAndCanceledColors);
                Run("activation attempts differ from successful activation", ActivationFailure);
                Run("fresh off reports deduplicate by status timestamp", FreshOffReports);
                Run("pause and resume separate send-gap histories", PauseResumeGap);
                Run("calibration labels output and separates ambient gaps", CalibrationRole);
                Run("output anomaly logs throttle without losing counters", ThrottledOutputLogs);
                Run("diagnostics never invoke the disk log sink", ThrowingDiagnosticSink);
                Run("diagnostic history is bounded and snapshots are independent", BoundedDiagnosticHistory);
                Run("legitimate black remains accepted with black filtering", LegitimateBlack);
                Console.WriteLine("PASS: " + passed + " output diagnostic regression cases. Actual production components; injected clock/capture/transport; no sockets or physical strip claims.");
                return 0;
            } catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
        }
        private static void Run(string name, Action test)
        { test(); passed++; Console.WriteLine("PASS: " + name); }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Close(double actual, double expected, string message)
        { Check(Math.Abs(actual - expected) < 0.00001, message + ": expected " + expected + ", got " + actual); }
        private static List<string> OutputEvents(EngineState state)
        {
            List<string> events = new List<string>();
            foreach (string text in state.OutputDiagnosticEvents) if (text.IndexOf("Lighting output diagnostic:", StringComparison.Ordinal) >= 0) events.Add(text);
            return events;
        }
        private static DateTime OutputEventUtc(string text)
        {
            const string marker = "local send UTC ";
            int start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            int end = text.IndexOf(',', start);
            return DateTime.Parse(text.Substring(start, end - start), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        }
        private static AppSettings Config()
        {
            return new AppSettings { DeviceId = "TEST-G1", DeviceIp = "", Monitor = "FAKE-DISPLAY",
                AutoSync = true, StartWithWindows = false, AreaMode = "Full", ZoneCount = 18,
                Brightness = 100, Saturation = 100, SmoothMs = 0, Fps = 24, IgnoreBlackPixels = false };
        }
        private static CaptureFrame Frame(Color color)
        {
            byte[] pixels = new byte[256 * 107 * 4];
            for (int i = 0; i < pixels.Length; i += 4) {
                pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
            }
            return new CaptureFrame(256, 107, pixels);
        }
        private static void Counts(Fixture f, long datagrams, long black, long nearBlack, int peak, bool test)
        {
            EngineState s = f.Engine.State;
            Check(s.Datagrams == datagrams && f.Transport.Colors.Count == datagrams, "actual successful local color send count");
            Check(s.AllBlackColorDatagrams == black, "exact-black counter");
            Check(s.NearBlackColorDatagrams == nearBlack, "near-black counter uses maximum channel <= 3");
            Check(s.LastColorPeak == peak && s.LastColorWasTest == test, "last successful color peak/source");
            Check(s.LastColorSendUtc == f.Transport.Colors[f.Transport.Colors.Count - 1].Utc, "last successful send timestamp");
        }
        private static void UnchangedColorAccounting(EngineState before, EngineState after)
        {
            Check(after.Datagrams == before.Datagrams && after.AllBlackColorDatagrams == before.AllBlackColorDatagrams &&
                after.NearBlackColorDatagrams == before.NearBlackColorDatagrams && after.LastColorPeak == before.LastColorPeak &&
                after.LastColorWasTest == before.LastColorWasTest && after.LastColorSendUtc == before.LastColorSendUtc &&
                after.LastNearBlackSendUtc == before.LastNearBlackSendUtc && after.LastAllBlackSendUtc == before.LastAllBlackSendUtc &&
                after.LastNearBlackWasTest == before.LastNearBlackWasTest && after.LastAllBlackWasTest == before.LastAllBlackWasTest &&
                after.MaximumColorSendGapUtc == before.MaximumColorSendGapUtc && after.MaximumColorSendGapWasTest == before.MaximumColorSendGapWasTest, "no color accounting commit without a successful local send");
            Close(after.LastColorSendGapSeconds, before.LastColorSendGapSeconds, "unchanged last gap");
            Close(after.MaximumColorSendGapSeconds, before.MaximumColorSendGapSeconds, "unchanged maximum gap");
        }
        private static void ColorHistory()
        {
            using (Fixture f = Fixture.Running()) {
                Counts(f, 1, 0, 0, 120, false);
                EngineState initial = f.Engine.State;
                Check(initial.LastNearBlackSendUtc == DateTime.MinValue, "bright startup invents no near-black timestamp");
                Check(initial.LastAllBlackSendUtc == DateTime.MinValue && initial.MaximumColorSendGapUtc == DateTime.MinValue, "bright first send invents no black or maximum-gap timestamp");
                Check(initial.ActivationAttemptsTotal == 1 && initial.SessionGeneration == 1, "first activation accounting");
                Check(initial.LastActivationAttemptUtc == f.Clock.UtcNow && initial.LastActivationSucceededUtc == f.Clock.UtcNow, "first activation timestamps");
                Check(!String.IsNullOrEmpty(initial.LastActivationReason), "activation reason is meaningful");
                Close(initial.LastColorSendGapSeconds, 0, "first send has no predecessor gap");
                f.Source.Present(Frame(Color.Black)); f.Step(.1);
                Counts(f, 2, 1, 1, 0, false);
                Check(f.Engine.State.LastNearBlackSendUtc == f.Clock.UtcNow, "black-send timestamp");
                Check(f.Engine.State.LastAllBlackSendUtc == f.Clock.UtcNow && !f.Engine.State.LastAllBlackWasTest && !f.Engine.State.LastNearBlackWasTest, "exact black retains ambient source label");
                Close(f.Engine.State.LastColorSendGapSeconds, .1, "bright to black gap");
                EngineState black = f.Engine.State;
                f.Step(.2); UnchangedColorAccounting(black, f.Engine.State);
                f.Step(.81); Counts(f, 3, 2, 2, 0, false);
                Close(f.Engine.State.LastColorSendGapSeconds, 1.01, "black heartbeat gap");
                Close(f.Engine.State.MaximumColorSendGapSeconds, 1.01, "maximum active-stream gap");
                DateTime blackAt = f.Engine.State.LastAllBlackSendUtc;
                DateTime maxAt = f.Engine.State.MaximumColorSendGapUtc;
                Check(maxAt == f.Clock.UtcNow, "maximum gap records its event timestamp");
                f.Source.Present(Frame(Color.FromArgb(3, 2, 1))); f.Step(.1);
                Counts(f, 4, 2, 3, 3, false);
                DateTime nearAt = f.Engine.State.LastNearBlackSendUtc;
                Check(f.Engine.State.LastAllBlackSendUtc == blackAt && f.Engine.State.MaximumColorSendGapUtc == maxAt, "near-black and smaller gap preserve exact-black and maximum-gap event history");
                f.Source.Present(Frame(Color.FromArgb(4, 2, 1))); f.Step(.1);
                Check(f.Engine.State.Datagrams == 4, "one-channel one-unit difference remains deduplicated");
                f.Step(.91); Counts(f, 5, 2, 3, 4, false);
                Check(f.Engine.State.LastNearBlackSendUtc == nearAt, "maximum channel 4 is not near-black");
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step(.1);
                Counts(f, 6, 2, 3, 120, false);
                Check(f.Engine.State.LastAllBlackSendUtc == blackAt && f.Engine.State.LastNearBlackSendUtc == nearAt, "later bright output preserves both historical dark events");
                Check(initial.Datagrams == 1 && initial.AllBlackColorDatagrams == 0 && initial.LastColorPeak == 120, "published state history remains immutable");
                Check(black.Datagrams == 2 && black.AllBlackColorDatagrams == 1, "prior black snapshot remains immutable");
            }
        }
        private static void FailedAndCanceledColors()
        {
            using (Fixture f = Fixture.Running()) {
                EngineState bright = f.Engine.State;
                f.Source.Present(Frame(Color.Black)); f.Transport.FailColorCount = 1; f.Step(.1);
                UnchangedColorAccounting(bright, f.Engine.State);
                Check(f.Transport.FailedColors == 1, "synthetic failed send reached the boundary");
                f.Step(.05); Counts(f, 2, 1, 1, 0, false);
                Close(f.Engine.State.LastColorSendGapSeconds, .15, "failed send did not replace successful-send predecessor");
                EngineState black = f.Engine.State;
                f.Source.Present(Frame(Color.FromArgb(3, 2, 1))); f.Transport.CancelColorCount = 1; f.Step(.1);
                UnchangedColorAccounting(black, f.Engine.State);
                Check(f.Transport.CanceledColors == 1, "synthetic canceled send reached the boundary");
                f.Step(.05); Counts(f, 3, 1, 2, 3, false);
                Close(f.Engine.State.LastColorSendGapSeconds, .15, "canceled send did not replace successful-send predecessor");
                Check(f.Engine.State.LastNearBlackSendUtc == f.Clock.UtcNow, "retry commits current near-black timestamp");
            }
        }
        private static void ActivationFailure()
        {
            using (Fixture f = new Fixture(Config())) {
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Transport.FailEnableCount = 1; f.Step();
                EngineState failed = f.Engine.State;
                Check(failed.ActivationAttemptsTotal == 1 && f.Transport.EnableAttempts == 1, "failed activation is still an attempt");
                Check(failed.SessionGeneration == 0 && failed.Datagrams == 0 && failed.LastActivationSucceededUtc == DateTime.MinValue, "failed activation invents no success/output");
                Check(failed.LastActivationAttemptUtc == f.Clock.UtcNow && !String.IsNullOrEmpty(failed.LastActivationReason), "failed attempt timestamp/reason");
                f.Step(.2); Check(f.Engine.State.ActivationAttemptsTotal == 1, "activation retry deadline is unchanged");
                f.Step(.81); Counts(f, 1, 0, 0, 120, false);
                Check(f.Engine.State.ActivationAttemptsTotal == 2 && f.Engine.State.SessionGeneration == 1, "retry distinguishes total attempts from successes");
                Check(f.Engine.State.LastActivationAttemptUtc == f.Clock.UtcNow && f.Engine.State.LastActivationSucceededUtc == f.Clock.UtcNow, "successful retry timestamps");
                Check(failed.LastActivationSucceededUtc == DateTime.MinValue, "failed activation snapshot stays immutable");
            }
        }
        private static void FreshOffReports()
        {
            using (Fixture f = Fixture.Running()) {
                f.Clock.Advance(.1); f.Transport.ReportPower(false, f.Clock.UtcNow); f.Step();
                EngineState first = f.Engine.State;
                Check(first.PowerOffReports == 1 && first.LastPowerOffReportUtc == f.Clock.UtcNow, "fresh off report counted");
                Check(first.ActivationAttemptsTotal == 1 && first.SessionGeneration == 1, "fresh off observation preserves the existing one-second activation deadline");
                f.Step(.2); Check(f.Engine.State.PowerOffReports == 1 && f.Engine.State.LastPowerOffReportUtc == first.LastPowerOffReportUtc, "same off-status timestamp does not recount");
                Check(f.Engine.State.ActivationAttemptsTotal == 1, "off recovery does not bypass activation cooldown");
                f.Step(.71);
                Check(f.Engine.State.PowerOffReports == 1 && f.Engine.State.ActivationAttemptsTotal == 2 && f.Engine.State.SessionGeneration == 2, "off recovery activates after the existing deadline without recounting cached status");
                f.Clock.Advance(.1); f.Transport.ReportPower(false, f.Clock.UtcNow); f.Step();
                Check(f.Engine.State.PowerOffReports == 2 && f.Engine.State.LastPowerOffReportUtc == f.Clock.UtcNow, "new off-status timestamp counts within the same episode");
                f.Clock.Advance(.1); f.Transport.ReportPower(true, f.Clock.UtcNow); f.Step();
                Check(f.Engine.State.PowerOffReports == 2, "on status is not an off report");
                f.Clock.Advance(.1); f.Transport.ReportPower(false, f.Clock.UtcNow); f.Step();
                Check(f.Engine.State.PowerOffReports == 3, "new off episode preserves cumulative counter");
                DateTime last = f.Engine.State.LastPowerOffReportUtc;
                f.Transport.ReportPower(false, f.Clock.UtcNow.AddSeconds(1)); f.Step(.1);
                Check(f.Engine.State.PowerOffReports == 3 && f.Engine.State.LastPowerOffReportUtc == last, "future off status cannot advance diagnostics");
                Check(first.PowerOffReports == 1, "off-report snapshot remains immutable");
            }
        }
        private static void PauseResumeGap()
        {
            using (Fixture f = Fixture.Running()) {
                f.Step(2); Counts(f, 2, 0, 0, 120, false);
                Close(f.Engine.State.MaximumColorSendGapSeconds, 2, "pre-pause maximum gap");
                f.Engine.Paused = true; f.Step();
                EngineState paused = f.Engine.State;
                f.Step(20); UnchangedColorAccounting(paused, f.Engine.State);
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Engine.Paused = false; f.Step(.1);
                Counts(f, 3, 0, 0, 120, false);
                Check(f.Engine.State.SessionGeneration == 2 && f.Engine.State.ActivationAttemptsTotal == 2, "resume establishes a new stream");
                Close(f.Engine.State.LastColorSendGapSeconds, 0, "inactive pause is excluded from resume gap");
                Close(f.Engine.State.MaximumColorSendGapSeconds, 0, "activation resets maximum gap scope");
                Check(f.Engine.State.MaximumColorSendGapUtc == DateTime.MinValue, "activation resets maximum-gap timestamp");
                Check(!f.Engine.State.MaximumColorSendGapWasTest, "activation resets maximum-gap source label");
                f.Step(1.2); Close(f.Engine.State.LastColorSendGapSeconds, 1.2, "new stream heartbeat gap");
                Close(f.Engine.State.MaximumColorSendGapSeconds, 1.2, "new stream maximum gap");
                Check(f.Engine.State.MaximumColorSendGapUtc == f.Clock.UtcNow, "new stream gap has current event timestamp");
            }
        }
        private static void CalibrationRole()
        {
            using (Fixture f = Fixture.Running()) {
                f.Source.Present(Frame(Color.Black)); f.Step(.1);
                DateTime darkAt = f.Engine.State.LastNearBlackSendUtc;
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step(.1);
                f.Engine.TestZone(3); f.Step(.2);
                Counts(f, 4, 1, 1, 100, true);
                Check(f.Engine.State.LastNearBlackSendUtc == darkAt && f.Engine.State.LastAllBlackSendUtc == darkAt && !f.Engine.State.LastNearBlackWasTest && !f.Engine.State.LastAllBlackWasTest, "calibration source does not relabel historical ambient-black events");
                Close(f.Engine.State.LastColorSendGapSeconds, 0, "ambient predecessor excluded from calibration gap");
                f.Step(1.1); Counts(f, 5, 1, 1, 100, true);
                Close(f.Engine.State.LastColorSendGapSeconds, 1.1, "calibration heartbeat gap");
                Check(f.Engine.State.MaximumColorSendGapWasTest && f.Engine.State.MaximumColorSendGapUtc == f.Clock.UtcNow, "new calibration maximum retains its test role and timestamp");
                f.Engine.CancelTest(); f.Step(.1); Counts(f, 6, 1, 1, 120, false);
                Close(f.Engine.State.LastColorSendGapSeconds, 0, "calibration predecessor excluded from restored ambient gap");
                Check(f.Engine.State.MaximumColorSendGapWasTest, "restored ambient role does not relabel the historical calibration maximum");
                Check(f.Engine.State.SessionGeneration == 1, "role switch does not reinitialize a healthy stream");
            }
        }
        private static void ThrottledOutputLogs()
        {
            using (Fixture f = Fixture.Running()) {
                f.Source.Present(Frame(Color.Black)); f.Step(.1);
                Check(OutputEvents(f.Engine.State).Count == 1, "first near-black transition records an event");
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step(.1);
                f.Source.Present(Frame(Color.Black)); f.Step(.1);
                Counts(f, 4, 2, 2, 0, false);
                Check(OutputEvents(f.Engine.State).Count == 1, "rapid near-black transition events are throttled");
                f.Step(2); Counts(f, 5, 3, 3, 0, false);
                List<string> events = OutputEvents(f.Engine.State);
                Check(events.Count == 2, ">=1.5 second successful-send gap records an event after throttle interval");
                Close(f.Engine.State.LastColorSendGapSeconds, 2, "long gap metric is exact");
                for (int i = 1; i < events.Count; i++) Check((OutputEventUtc(events[i]) - OutputEventUtc(events[i - 1])).TotalSeconds >= 2, "output anomaly events cannot exceed one per two seconds");
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step(.1);
                f.Source.Present(Frame(Color.Black)); f.Step(.1);
                Counts(f, 7, 4, 4, 0, false);
                Check(OutputEvents(f.Engine.State).Count == 2 && f.Engine.State.LastNearBlackSendUtc == f.Clock.UtcNow, "throttle never hides counts or latest near-black timestamp");
            }
        }
        private static void ThrowingDiagnosticSink()
        {
            using (Fixture f = new Fixture(Config())) {
                f.ThrowDiagnosticLogs = true;
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step();
                Counts(f, 1, 0, 0, 120, false);
                Check(f.Engine.State.ActivationAttemptsTotal == 1 && f.Engine.State.SessionGeneration == 1 && f.Engine.State.LastActivationSucceededUtc == f.Clock.UtcNow, "throwing activation log cannot interrupt successful activation accounting");
                f.Source.Present(Frame(Color.Black)); f.Step(.1); Counts(f, 2, 1, 1, 0, false);
                Check(OutputEvents(f.Engine.State).Count == 1, "black output event exists in memory");
                f.Clock.Advance(.1); f.Transport.ReportPower(false, f.Clock.UtcNow); f.Step();
                Counts(f, 2, 1, 1, 0, false);
                Check(f.Engine.State.PowerOffReports == 1 && f.Engine.State.ActivationAttemptsTotal == 1, "off report commits while existing activation cooldown remains in force");
                f.Step(.81);
                Counts(f, 3, 2, 2, 0, false);
                Check(f.Engine.State.PowerOffReports == 1 && f.Engine.State.ActivationAttemptsTotal == 2 && f.Engine.State.SessionGeneration == 2, "throwing off/activation sink cannot interrupt recovery accounting");
                f.Engine.Paused = true; f.Step();
                Check(f.Transport.SuccessfulReleases == 1 && !f.Engine.State.ReleasePending, "throwing release sink cannot interrupt release");
                Check(f.DiagnosticLogThrows == 0, "new diagnostics must never reach services.Log");
                Check(f.LogSinkCalls > 0, "existing non-diagnostic log path is still connected to injected sink");
                Check(f.Engine.State.OutputDiagnosticEvents.Length >= 8, "activation, black, off, recovery and release events retained in memory");
            }
        }
        private static void BoundedDiagnosticHistory()
        {
            using (Fixture f = Fixture.Running()) {
                EngineState initial = f.Engine.State;
                string[] firstEvents = initial.OutputDiagnosticEvents;
                string first = firstEvents[0];
                for (int i = 0; i < 70; i++) {
                    f.Source.Present(Frame(Color.Black)); f.Step(2.1);
                    f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step(2.1);
                }
                Counts(f, 141, 70, 70, 120, false);
                string[] retained = f.Engine.State.OutputDiagnosticEvents;
                Check(retained.Length == 64, "RAM diagnostic history is capped at 64 events");
                Check(!Object.ReferenceEquals(firstEvents, retained) && initial.OutputDiagnosticEvents.Length == firstEvents.Length && firstEvents[0] == first, "later events do not mutate a previously published history snapshot");
                Check(OutputEvents(f.Engine.State).Count == 64, "old activation events are evicted before newest output events");
                DateTime prior = DateTime.MinValue;
                foreach (string text in retained) { DateTime next = OutputEventUtc(text); Check(next > prior, "retained output history stays in event order"); prior = next; }
                Check(prior == f.Engine.State.LastColorSendUtc, "bounded history retains newest successful output event");
            }
        }
        private static void LegitimateBlack()
        {
            AppSettings config = Config(); config.IgnoreBlackPixels = true;
            using (Fixture f = new Fixture(config)) {
                f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step();
                f.Source.Present(Frame(Color.Black)); f.Step(.1); Counts(f, 2, 1, 1, 0, false);
                foreach (Color color in f.Transport.Colors[1].Colors) Check(color.R == 0 && color.G == 0 && color.B == 0, "legitimate whole-black frame still reaches the send boundary");
                f.Step(1.01); Counts(f, 3, 2, 2, 0, false);
                Check(f.Engine.State.CaptureErrors == 0 && f.Engine.State.FreshFrames == 2, "black frame is successful image evidence, never a synthetic capture failure");
            }
        }
        private sealed class Clock
        {
            public double Seconds = 100;
            public DateTime UtcNow { get { return new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } }
            public void Advance(double seconds) { Check(seconds >= 0, "clock cannot go backward"); Seconds += seconds; }
        }
        private sealed class FakeCapture : IEngineCapture
        {
            private CaptureFrame frame;
            private bool fresh;
            public bool HasNewFrame { get; private set; }
            public string LastError { get { return ""; } }
            public void Present(CaptureFrame next) { frame = next; fresh = true; }
            public CaptureFrame Capture(Rectangle ignored) { HasNewFrame = fresh; fresh = false; return frame; }
            public void Dispose() { }
        }
        private sealed class LocalColorSend
        { public DateTime Utc; public Color[] Colors; }
        private sealed class FakeTransport : IEngineTransport
        {
            private readonly Clock clock;
            private DeviceInfo target;
            public int FailColorCount, CancelColorCount, FailEnableCount, FailedColors, CanceledColors, EnableAttempts, SuccessfulReleases;
            public readonly List<LocalColorSend> Colors = new List<LocalColorSend>();
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public FakeTransport(Clock value) { clock = value; }
            public int Generation { get { return 1; } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, Generation); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public void Start() { }
            public void Discover()
            {
                DeviceInfo reply = target == null ? new DeviceInfo { Id = "TEST-G1", Sku = "H6609", Ip = "192.0.2.44", IdentityGeneration = Generation } : target.Copy();
                reply.LastSeenUtc = clock.UtcNow;
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            public void Poll() { if (target != null) target.LastSeenUtc = clock.UtcNow; }
            public void SetTarget(DeviceInfo value) { target = value == null ? null : value.Copy(); }
            public void ReportPower(bool on, DateTime observed) { Check(target != null, "power observation requires discovered identity"); target.PowerOn = on; target.LastStatusUtc = observed; target.LastSeenUtc = clock.UtcNow; }
            public void Enable(EndpointToken owned, Func<bool> authorize)
            {
                if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic activation authorization canceled");
                EnableAttempts++;
                if (FailEnableCount > 0) { FailEnableCount--; throw new IOException("Synthetic activation failure"); }
            }
            public void Disable(EndpointToken owned) { SuccessfulReleases++; }
            public void SendColors(EndpointToken owned, Color[] colors, bool flag, Func<bool> authorize)
            {
                if (authorize != null && !authorize()) { CanceledColors++; throw new OperationCanceledException("Synthetic color authorization canceled"); }
                if (CancelColorCount > 0) { CancelColorCount--; CanceledColors++; throw new OperationCanceledException("Synthetic canceled color send"); }
                if (FailColorCount > 0) { FailColorCount--; FailedColors++; throw new IOException("Synthetic local color send failure"); }
                // Exercise the real packet builder without instantiating GoveeLan or a socket.
                Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "actual packet builder accepted no output");
                Colors.Add(new LocalColorSend { Utc = clock.UtcNow, Colors = (Color[])colors.Clone() });
            }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly FakeCapture Source = new FakeCapture();
            public readonly FakeTransport Transport;
            public readonly GlowEngine Engine;
            public bool ThrowDiagnosticLogs;
            public int DiagnosticLogThrows, LogSinkCalls;
            public Fixture(AppSettings config)
            {
                Transport = new FakeTransport(Clock);
                Engine = new GlowEngine(config, new EngineServices {
                    Transport = Transport, CreateCapture = delegate { return Source; },
                    UtcNow = delegate { return Clock.UtcNow; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return new Rectangle(0, 0, 3440, 1440); },
                    CompetitorRunning = delegate { return false; }, ListenSystemEvents = false,
                    Log = delegate(string message) {
                        LogSinkCalls++;
                        if (ThrowDiagnosticLogs && (message.StartsWith("Lighting ", StringComparison.Ordinal) || message.StartsWith("Strip reported power off:", StringComparison.Ordinal))) {
                            DiagnosticLogThrows++; throw new IOException("Synthetic diagnostic sink failure");
                        }
                    }
                });
            }
            public static Fixture Running()
            {
                Fixture f = new Fixture(Config()); f.Source.Present(Frame(Color.FromArgb(40, 90, 120))); f.Step();
                Check(f.Transport.Colors.Count == 1, "running fixture first local color send"); return f;
            }
            public void Step() { Engine.Step(); }
            public void Step(double elapsed) { Clock.Advance(elapsed); Engine.Step(); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
