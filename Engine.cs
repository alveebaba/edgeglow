using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EdgeGlow
{
    public sealed class EngineState
    {
        public string Message = "Starting local connection...", Detail = "", OutputPolicy = "paused";
        public bool Sending, ReleasePending, ShutdownIncomplete;
        public Color[] Colors = new Color[10];
        public double WorkMs, ImageAgeSeconds;
        public Rectangle ActiveArea;
        public int FrameWidth, FrameHeight, SessionGeneration;
        public string Ip = "", DeviceId = "";
        public DateTime LastReply, LastUsableImageUtc;
        // Frames is retained as a compatibility alias for fresh desktop images.
        public long Frames, LoopIterations, CaptureAttempts, FreshFrames, IdleResults, CaptureErrors, MissingFrames, ProtectedResults, ReprocessedFrames, Datagrams;
        // Diagnostics describe local sends, never acknowledged physical output.
        public long AllBlackColorDatagrams, NearBlackColorDatagrams, ActivationAttemptsTotal, PowerOffReports;
        public int LastColorPeak;
        public bool LastColorWasTest, LastNearBlackWasTest, LastAllBlackWasTest, MaximumColorSendGapWasTest;
        public DateTime LastColorSendUtc, LastNearBlackSendUtc, LastAllBlackSendUtc, MaximumColorSendGapUtc, LastActivationAttemptUtc, LastActivationSucceededUtc, LastPowerOffReportUtc;
        public double LastColorSendGapSeconds, MaximumColorSendGapSeconds;
        public string LastActivationReason = "";
        public string[] OutputDiagnosticEvents = new string[0];
    }

    public interface IEngineCapture : IDisposable
    {
        CaptureFrame Capture(Rectangle bounds);
        bool HasNewFrame { get; }
        string LastError { get; }
    }
    public interface IEngineCaptureMetadata
    {
        bool HasFreshDesktopFrame { get; }
        bool IsReprocessedFrame { get; }
        bool ProtectedContentMasked { get; }
        string Description { get; }
    }
    public interface IEngineTransport : IDisposable
    {
        event Action<DeviceInfo> DeviceSeen;
        event Action<string> Error;
        DeviceInfo CurrentTarget { get; }
        EndpointToken CurrentEndpoint { get; }
        DateTime LastSeenUtc { get; }
        int Generation { get; }
        void Start();
        void Discover();
        void Poll();
        void SetTarget(DeviceInfo device);
        void Enable(EndpointToken endpoint, Func<bool> authorize);
        void Disable(EndpointToken endpoint);
        void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize);
    }

    public sealed class EngineServices
    {
        public IEngineTransport Transport;
        public Func<IEngineCapture> CreateCapture;
        public Func<DateTime> UtcNow;
        public Func<double> MonotonicSeconds;
        public Func<AppSettings, Rectangle> SelectBounds;
        public Func<bool> CompetitorRunning;
        public Action<string> Log;
        public bool ListenSystemEvents;
        public static EngineServices CreateDefault()
        {
            Stopwatch clock = Stopwatch.StartNew();
            return new EngineServices {
                Transport = new LanAdapter(), CreateCapture = delegate { return new CaptureAdapter(); },
                UtcNow = delegate { return DateTime.UtcNow; }, MonotonicSeconds = delegate { return clock.Elapsed.TotalSeconds; },
                SelectBounds = delegate(AppSettings config) {
                    Screen chosen = Screen.PrimaryScreen;
                    foreach (Screen display in Screen.AllScreens) if (display.DeviceName == config.Monitor) chosen = display;
                    return chosen.Bounds;
                },
                CompetitorRunning = delegate {
                    foreach (string name in new string[] { "GoveeDesktop", "Govee Desktop" }) {
                        Process[] list = Process.GetProcessesByName(name);
                        bool found = list.Length != 0;
                        foreach (Process process in list) process.Dispose();
                        if (found) return true;
                    }
                    return false;
                },
                Log = Storage.Log, ListenSystemEvents = true
            };
        }
        private sealed class CaptureAdapter : IEngineCapture, IEngineCaptureMetadata
        {
            private readonly ScreenSampler sampler = new ScreenSampler();
            public CaptureFrame Capture(Rectangle bounds) { return sampler.Capture(bounds); }
            public bool HasNewFrame { get { return sampler.HasNewFrame; } }
            public bool HasFreshDesktopFrame { get { return sampler.HasFreshDesktopFrame; } }
            public bool IsReprocessedFrame { get { return sampler.IsReprocessedFrame; } }
            public bool ProtectedContentMasked { get { return sampler.ProtectedContentMasked; } }
            public string LastError { get { return sampler.LastError; } }
            public string Description { get { return "HDR " + sampler.IsHdr + ", conversion " + sampler.ConversionMode +
                ", format " + sampler.NativeTextureFormat + ", color space " + sampler.NativeOutputColorSpace +
                ", SDR white " + sampler.SdrWhiteLevelNits + " nits"; } }
            public void Dispose() { sampler.Dispose(); }
        }
        public sealed class LanAdapter : IEngineTransport
        {
            private readonly GoveeLan lan;
            public LanAdapter() : this(new GoveeLan()) { }
            public LanAdapter(GoveeLan transport) { lan = transport; }
            public event Action<DeviceInfo> DeviceSeen { add { lan.DeviceSeen += value; } remove { lan.DeviceSeen -= value; } }
            public event Action<string> Error { add { lan.Error += value; } remove { lan.Error -= value; } }
            public DeviceInfo CurrentTarget { get { return lan.CurrentTarget; } }
            public EndpointToken CurrentEndpoint { get { return lan.CurrentEndpoint; } }
            public DateTime LastSeenUtc { get { return lan.LastSeenUtc; } }
            public int Generation { get { return lan.Generation; } }
            public void Start() { lan.Start(); }
            public void Discover() { lan.Discover(); }
            public void Poll() { lan.Poll(); }
            public void SetTarget(DeviceInfo device) { lan.SetTarget(device); }
            public void Enable(EndpointToken endpoint, Func<bool> authorize) { lan.Enable(endpoint, authorize); }
            public void Disable(EndpointToken endpoint) { lan.Disable(endpoint); }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize) { lan.SendColors(endpoint, colors, flag, authorize); }
            public void Dispose() { lan.Dispose(); }
        }
    }

    // Floating state is retained between frames. Rounding happens only at output.
    public sealed class ColorSmoother
    {
        private double[] channels;
        public void Reset() { channels = null; }
        public Color[] Update(Color[] target, double elapsedSeconds, int smoothingMs)
        {
            if (target == null) throw new ArgumentNullException("target");
            bool initialize = channels == null || channels.Length != target.Length * 3;
            if (initialize) channels = new double[target.Length * 3];
            double dt = Math.Max(0, Math.Min(1, elapsedSeconds));
            double alpha = smoothingMs <= 0 ? 1 : 1 - Math.Exp(-dt / (smoothingMs / 1000.0));
            Color[] result = new Color[target.Length];
            for (int i = 0; i < target.Length; i++) {
                int[] desired = new int[] { target[i].R, target[i].G, target[i].B };
                int[] output = new int[3];
                for (int c = 0; c < 3; c++) {
                    int index = 3 * i + c;
                    double value = initialize ? desired[c] : channels[index] + (desired[c] - channels[index]) * alpha;
                    if (Math.Abs(desired[c] - value) < .01) value = desired[c];
                    channels[index] = value;
                    output[c] = Math.Max(0, Math.Min(255, (int)Math.Round(value)));
                }
                result[i] = Color.FromArgb(output[0], output[1], output[2]);
            }
            return result;
        }
    }

    public sealed class GlowEngine : IDisposable
    {
        private sealed class ReleaseObligation
        {
            public EndpointToken Endpoint;
            public bool Requested;
            public int Attempts;
            public double NextAttempt;
        }
        private volatile AppSettings settings;
        private volatile EngineState state = new EngineState();
        private volatile bool quit, paused, sessionLocked, suspended;
        private readonly object tickGate = new object(), controlGate = new object(), lifetimeGate = new object();
        private readonly EngineServices services;
        private readonly IEngineTransport lan;
        private readonly List<ReleaseObligation> obligations = new List<ReleaseObligation>();
        private readonly ColorSmoother smoother = new ColorSmoother();
        private ActiveAreaDetector detector = new ActiveAreaDetector();
        private IEngineCapture capture;
        private Thread worker;
        private bool started, disposed, competitor, sessionActive, previouslyReachable;
        private volatile bool shutdownCompleted;
        private bool testClearPending, testRaw, offEpisode, hasActivated;
        private int resumeVersion, appliedResumeVersion, offRecoveryAttempts;
        private long controlVersion;
        private int discoveryRequested;
        private string powerEpisodeKey = "";
        private DateTime processedPowerStatus;
        private EndpointToken sessionEndpoint;
        private int testZone = -1, transportGeneration, sessionGeneration, activationAttempts;
        private double testUntil, scanAt, pollAt, inspectAt, nextStart, nextActivation, sentAt = -1000, lastSmoothAt;
        private double lastImageAt = -1;
        private DateTime lastImageUtc, lastActivationStatus;
        private string networkError = "", lastCaptureError = "", priorMonitor = "", priorMode = "", sessionKey = "";
        private string discoveredDeviceId;
        private Rectangle bounds = Rectangle.Empty, activeArea = Rectangle.Empty;
        private Color[] desired, previous, lastAmbient, lastLogicalAmbient;
        private bool previousWasTest;
        private AppSettings sampledSettings;
        private long loops, attempts, freshFrames, idleResults, captureErrors, missingFrames, protectedResults, reprocessedFrames, datagrams;
        private int frameWidth, frameHeight;
        private long allBlackColorDatagrams, nearBlackColorDatagrams, activationAttemptsTotal, powerOffReports;
        private int lastColorPeak;
        private bool lastColorWasTest, lastNearBlackWasTest, lastAllBlackWasTest, maximumColorSendGapWasTest;
        private readonly Queue<string> outputDiagnosticEvents = new Queue<string>();
        private DateTime lastColorSendUtc, lastNearBlackSendUtc, lastAllBlackSendUtc, maximumColorSendGapUtc, lastActivationAttemptUtc, lastActivationSucceededUtc, lastPowerOffReportUtc;
        private double lastColorSendGapSeconds, maximumColorSendGapSeconds, outputAnomalyLogAt = -1000;
        private string lastActivationReason = "";

        public EngineState State { get { return state; } }
        public bool ShutdownCompleted { get { return shutdownCompleted; } }
        public bool Paused {
            get { return paused; }
            set {
                lock (controlGate) {
                    paused = value; controlVersion++;
                    if (!value) resumeVersion++;
                    else CancelTestLocked();
                }
            }
        }
        public GlowEngine(AppSettings config) : this(config, EngineServices.CreateDefault()) { }
        public GlowEngine(AppSettings config, EngineServices dependencies)
        {
            if (config == null || dependencies == null || dependencies.Transport == null ||
                dependencies.CreateCapture == null || dependencies.UtcNow == null || dependencies.MonotonicSeconds == null ||
                dependencies.SelectBounds == null || dependencies.CompetitorRunning == null) throw new ArgumentException("Engine dependencies are incomplete.");
            settings = config.Copy(); settings.Validate(); paused = !settings.AutoSync;
            state = new EngineState { Colors = new Color[settings.ZoneCount] };
            services = dependencies; lan = services.Transport;
            lastSmoothAt = services.MonotonicSeconds();
            lan.Error += OnNetworkError; lan.DeviceSeen += OnDeviceSeen;
            if (services.ListenSystemEvents) {
                SystemEvents.SessionSwitch += OnSession;
                SystemEvents.PowerModeChanged += OnPower;
            }
        }
        private void Log(string text) { if (services.Log != null) services.Log(text); }
        private void OnNetworkError(string message) { if (networkError != message) Log(message); networkError = message; }
        private void OnDeviceSeen(DeviceInfo device)
        {
            AppSettings config = settings;
            if (device == null || device.Sku != "H6609" || String.IsNullOrWhiteSpace(device.Id)) return;
            string selectedId = config.DeviceId;
            if (String.IsNullOrEmpty(selectedId)) {
                // Keep the first discovered G1 for this run. Explicit settings
                // always take precedence without changing the settings snapshot.
                selectedId = Interlocked.CompareExchange(ref discoveredDeviceId, device.Id, null) ?? device.Id;
            }
            if (!String.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase)) return;
            lan.SetTarget(device); networkError = "";
        }
        public void Update(AppSettings config)
        {
            AppSettings next = config.Copy(); next.Validate();
            if (next.ZoneCount != settings.ZoneCount) throw new InvalidOperationException("Changing the zone layout requires restarting EdgeGlow.");
            settings = next;
        }
        public void Start()
        {
            lock (lifetimeGate) {
                if (disposed || quit) throw new ObjectDisposedException("GlowEngine");
                if (worker != null) return;
                worker = new Thread(Run) { IsBackground = false, Name = "EdgeGlow screen sync" }; worker.Start();
            }
        }
        public void Discover() { if (!quit) Interlocked.Exchange(ref discoveryRequested, 1); }
        public void TestZone(int zone) { BeginIdentification(zone, false); }
        public void TestRawZone(int zone) { BeginIdentification(zone, true); }
        private void BeginIdentification(int zone, bool raw)
        {
            lock (controlGate) {
                if (quit) return;
                testZone = Math.Max(0, Math.Min(settings.ZoneCount - 1, zone)); testUntil = services.MonotonicSeconds() + 8;
                testRaw = raw; controlVersion++; testClearPending = false;
            }
        }
        private void CancelTestLocked()
        {
            if (testZone >= 0) testClearPending = true;
            testZone = -1; testUntil = 0; controlVersion++;
        }
        public void CancelTest() { lock (controlGate) { CancelTestLocked(); } }
        private void ExpireTestLocked()
        {
            if (testZone >= 0 && services.MonotonicSeconds() >= testUntil) CancelTestLocked();
        }
        private bool Authorize(long version, bool testing, EndpointToken endpoint, bool activation)
        {
            lock (controlGate) {
                ExpireTestLocked();
                if (quit || sessionLocked || suspended || competitor || version != controlVersion) return false;
                if (testing ? testZone < 0 : paused || testZone >= 0) return false;
                // The production transport invokes this at its serialized send boundary.
                if (activation) RememberActivation(endpoint);
                return true;
            }
        }
        public void SetSessionBlocked(bool locked, bool sleep)
        {
            sessionLocked = locked; suspended = sleep;
            if (locked || sleep) CancelTest();
        }
        private void OnSession(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.RemoteDisconnect || e.Reason == SessionSwitchReason.ConsoleDisconnect) SetSessionBlocked(true, suspended);
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.RemoteConnect || e.Reason == SessionSwitchReason.ConsoleConnect) SetSessionBlocked(false, suspended);
        }
        private void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) SetSessionBlocked(sessionLocked, true);
            if (e.Mode == PowerModes.Resume) SetSessionBlocked(sessionLocked, false);
        }
        private static string Key(EndpointToken endpoint) { return endpoint == null ? "" : (endpoint.Id ?? "").ToUpperInvariant() + "|" + endpoint.Ip; }
        private void RecordNetworkError(Exception ex) { OnNetworkError(ex.GetType().Name + ": " + ex.Message); }
        private void ResetPixels()
        {
            desired = null; sampledSettings = null; smoother.Reset();
            detector.ClearPendingEvidence();
            if (capture != null) { capture.Dispose(); capture = null; }
        }
        private void RememberActivation(EndpointToken endpoint)
        {
            string key = Key(endpoint);
            foreach (ReleaseObligation item in obligations) if (Key(item.Endpoint) == key) {
                item.Endpoint = endpoint; item.Requested = false; item.Attempts = 0; return;
            }
            obligations.Add(new ReleaseObligation { Endpoint = endpoint });
        }
        private void RequestRelease()
        {
            foreach (ReleaseObligation item in obligations) if (!item.Requested) { item.Requested = true; item.Attempts = 0; item.NextAttempt = 0; }
            sessionActive = false; sessionKey = ""; activationAttempts = 0;
        }
        private void TryReleases(double now, bool force)
        {
            for (int i = obligations.Count - 1; i >= 0; i--) {
                ReleaseObligation item = obligations[i];
                if (!item.Requested || item.Attempts >= 3 || (!force && now < item.NextAttempt)) continue;
                item.Attempts++; item.NextAttempt = now + 1;
                try {
                    DiagnosticLog("Lighting release attempt: endpoint " + Key(item.Endpoint) + ", attempt " + item.Attempts);
                    lan.Disable(item.Endpoint); obligations.RemoveAt(i);
                    DiagnosticLog("Lighting release locally sent; delivery unconfirmed: " + Key(item.Endpoint));
                }
                catch (Exception ex) { RecordNetworkError(ex); }
            }
        }
        private void ResetPowerEpisode(string cause)
        {
            offEpisode = false; offRecoveryAttempts = 0; hasActivated = false;
            processedPowerStatus = DateTime.MinValue; lastActivationStatus = DateTime.MinValue;
            activationAttempts = 0; nextActivation = services.MonotonicSeconds();
            Log("Activation recovery budget reset: " + cause);
        }
        private void ObservePower(DeviceInfo target, DateTime utc)
        {
            if (target.LastStatusUtc <= processedPowerStatus || target.LastStatusUtc > utc ||
                (utc - target.LastStatusUtc).TotalSeconds > 5 || !target.PowerOn.HasValue) return;
            processedPowerStatus = target.LastStatusUtc;
            if (target.PowerOn == true) {
                offEpisode = false; offRecoveryAttempts = 0;
            } else {
                powerOffReports++; lastPowerOffReportUtc = target.LastStatusUtc;
                if (!offEpisode) {
                    offEpisode = true; offRecoveryAttempts = 0;
                    DiagnosticLog("Strip reported power off: fresh status " + target.LastStatusUtc.ToString("o") + ", endpoint " + target.Id + "|" + target.Ip);
                }
                if (hasActivated && target.LastStatusUtc > lastActivationStatus) {
                    sessionActive = false;
                    nextActivation = Math.Max(nextActivation, services.MonotonicSeconds());
                }
            }
        }
        private string ActivationWaitDetail
        {
            get { return offEpisode && hasActivated && offRecoveryAttempts >= 3 ?
                "The strip still reports off. Recovery limit reached; pause and resume to retry." :
                "Activation retry pending; pause and resume to retry after three failures. " + networkError; }
        }
        private bool Activate(EndpointToken endpoint, double now, long version, bool testing)
        {
            string key = Key(endpoint);
            if (sessionActive && sessionKey == key && sessionEndpoint.Generation == endpoint.Generation) return true;
            bool offRecovery = offEpisode && hasActivated;
            if (now < nextActivation || activationAttempts >= 3 || (offRecovery && offRecoveryAttempts >= 3)) return false;
            activationAttempts++; nextActivation = now + 1;
            if (offRecovery) offRecoveryAttempts++;
            activationAttemptsTotal++; lastActivationAttemptUtc = services.UtcNow();
            lastActivationReason = testing ? "zone identification" : offRecovery ? "fresh device-off status" : datagrams == 0 ? "first usable image" : "resume or endpoint/reply/transport recovery";
            DiagnosticLog("Lighting stream activation attempt: " + lastActivationReason + ", endpoint " + key + ", transport generation " + endpoint.Generation);
            lan.Enable(endpoint, delegate { return Authorize(version, testing, endpoint, true); });
            sessionActive = true; sessionKey = key; sessionEndpoint = endpoint; sessionGeneration++;
            hasActivated = true; lastActivationStatus = endpoint.Snapshot.LastStatusUtc;
            lastActivationSucceededUtc = services.UtcNow(); maximumColorSendGapSeconds = 0; maximumColorSendGapUtc = DateTime.MinValue; maximumColorSendGapWasTest = false; lastColorSendGapSeconds = 0;
            DiagnosticLog("Lighting stream activation locally sent; delivery unconfirmed: session " + sessionGeneration + ", reason " + lastActivationReason);
            previous = null; previousWasTest = false; sentAt = -1000;
            activationAttempts = 0; // Local success does not reset the unresolved off-episode budget.
            return true;
        }
        private static int LogicalIndex(int wireIndex, AppSettings config)
        { return ((config.Reverse ? config.ZoneCount - wireIndex : wireIndex) + config.Rotation) % config.ZoneCount; }
        private static Color[] MapToWire(Color[] logical, AppSettings config)
        {
            Color[] wire = new Color[config.ZoneCount];
            for (int i = 0; i < wire.Length; i++) wire[i] = logical[LogicalIndex(i, config)];
            return wire;
        }
        private static Color[] MapToLogical(Color[] wire, AppSettings config)
        {
            Color[] logical = new Color[config.ZoneCount];
            for (int i = 0; i < wire.Length; i++) logical[LogicalIndex(i, config)] = wire[i];
            return logical;
        }
        private bool Send(EndpointToken endpoint, Color[] wire, bool test, AppSettings config, double now, long version)
        {
            if (wire == null) return false;
            if (Different(wire, previous) || test != previousWasTest || now - sentAt >= 1) {
                lan.SendColors(endpoint, wire, config.StreamFlag, delegate { return Authorize(version, test, endpoint, false); });
                double committedAt = services.MonotonicSeconds();
                RecordColorSend(wire, test, committedAt);
                previous = (Color[])wire.Clone(); previousWasTest = test; sentAt = committedAt; datagrams++;
            }
            return true;
        }
        private void DiagnosticLog(string message)
        {
            // RAM only: disk logging must never delay a color, activation or release command.
            if (outputDiagnosticEvents.Count >= 64) outputDiagnosticEvents.Dequeue();
            outputDiagnosticEvents.Enqueue(services.UtcNow().ToString("o") + " " + message);
        }
        private static int ColorPeak(Color[] colors)
        {
            int peak = 0;
            if (colors != null) foreach (Color color in colors) peak = Math.Max(peak, Math.Max(color.R, Math.Max(color.G, color.B)));
            return peak;
        }
        private void RecordColorSend(Color[] wire, bool test, double committedAt)
        {
            int peak = ColorPeak(wire);
            bool sameHistory = previous != null && test == previousWasTest && sentAt >= 0;
            double gap = sameHistory ? Math.Max(0, committedAt - sentAt) : 0;
            bool enteredNearBlack = peak <= 3 && (!sameHistory || lastColorPeak > 3);
            lastColorSendUtc = services.UtcNow(); lastColorSendGapSeconds = gap;
            if (gap > maximumColorSendGapSeconds) { maximumColorSendGapSeconds = gap; maximumColorSendGapUtc = lastColorSendUtc; maximumColorSendGapWasTest = test; }
            lastColorPeak = peak; lastColorWasTest = test;
            if (peak == 0) { allBlackColorDatagrams++; lastAllBlackSendUtc = lastColorSendUtc; lastAllBlackWasTest = test; }
            if (peak <= 3) { nearBlackColorDatagrams++; lastNearBlackSendUtc = lastColorSendUtc; lastNearBlackWasTest = test; }
            // Counters are exact; only event messages are throttled. No per-frame file write.
            if ((enteredNearBlack || gap >= 1.5) && committedAt - outputAnomalyLogAt >= 2) {
                outputAnomalyLogAt = committedAt;
                DiagnosticLog(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Lighting output diagnostic: local send UTC {0:o}, source {1}, wire peak {2}/255, target peak {3}/255, gap {4:F3}s, image age {5:F3}s, capture errors {6}, session {7}. Delivery unconfirmed.",
                    lastColorSendUtc, test ? "zone identification" : "ambient", peak, ColorPeak(desired), gap,
                    lastImageAt < 0 ? -1 : Math.Max(0, committedAt - lastImageAt), captureErrors, sessionGeneration));
            }
        }
        private void FinishIdentification(EndpointToken endpoint, bool reachable, bool intentionalPause, AppSettings config, long version)
        {
            bool pending;
            lock (controlGate) pending = testClearPending && testZone < 0;
            if (!pending) return;
            bool replaced = false;
            if (!intentionalPause && reachable && sessionActive && sessionKey == Key(endpoint) && lastAmbient != null) {
                try { replaced = Send(endpoint, lastAmbient, false, config, services.MonotonicSeconds(), version); }
                catch (Exception ex) { RecordNetworkError(ex); }
            }
            if (!replaced) {
                RequestRelease(); TryReleases(services.MonotonicSeconds(), false);
                previous = null; previousWasTest = false;
            }
            // Any failed release is retained in the obligation ledger, not a one-tick flag.
            lock (controlGate) if (controlVersion == version) testClearPending = false;
        }
        private void Publish(string message, string detail, string policy, DeviceInfo target, Color[] colors, bool sending, Stopwatch work)
        {
            double now = services.MonotonicSeconds();
            state = new EngineState {
                Message = message, Detail = detail ?? "", OutputPolicy = policy, Sending = sending,
                ReleasePending = obligations.Count != 0, Colors = colors == null ? new Color[settings.ZoneCount] : (Color[])colors.Clone(),
                Ip = target == null ? settings.DeviceIp : target.Ip, DeviceId = target == null ? settings.DeviceId : target.Id,
                LastReply = lan.LastSeenUtc, LastUsableImageUtc = lastImageUtc, ImageAgeSeconds = lastImageAt < 0 ? -1 : Math.Max(0, now - lastImageAt),
                ActiveArea = activeArea, FrameWidth = frameWidth, FrameHeight = frameHeight, WorkMs = work.Elapsed.TotalMilliseconds,
                Frames = freshFrames, LoopIterations = loops, CaptureAttempts = attempts, FreshFrames = freshFrames,
                IdleResults = idleResults, CaptureErrors = captureErrors, MissingFrames = missingFrames, ProtectedResults = protectedResults, ReprocessedFrames = reprocessedFrames, Datagrams = datagrams, SessionGeneration = sessionGeneration,
                AllBlackColorDatagrams = allBlackColorDatagrams, NearBlackColorDatagrams = nearBlackColorDatagrams, LastColorPeak = lastColorPeak, LastColorWasTest = lastColorWasTest, LastNearBlackWasTest = lastNearBlackWasTest, LastAllBlackWasTest = lastAllBlackWasTest,
                LastColorSendUtc = lastColorSendUtc, LastNearBlackSendUtc = lastNearBlackSendUtc, LastAllBlackSendUtc = lastAllBlackSendUtc, MaximumColorSendGapUtc = maximumColorSendGapUtc, MaximumColorSendGapWasTest = maximumColorSendGapWasTest, LastColorSendGapSeconds = lastColorSendGapSeconds, MaximumColorSendGapSeconds = maximumColorSendGapSeconds,
                ActivationAttemptsTotal = activationAttemptsTotal, LastActivationAttemptUtc = lastActivationAttemptUtc, LastActivationSucceededUtc = lastActivationSucceededUtc, LastActivationReason = lastActivationReason,
                PowerOffReports = powerOffReports, LastPowerOffReportUtc = lastPowerOffReportUtc, OutputDiagnosticEvents = outputDiagnosticEvents.ToArray()
            };
        }
        public void Step()
        {
            lock (tickGate) {
                if (disposed || quit) return;
                Stopwatch work = Stopwatch.StartNew();
                AppSettings config = settings;
                double now = services.MonotonicSeconds();
                DateTime utc = services.UtcNow();
                loops++;
                try {
                    if (!started) {
                        if (now < nextStart) return;
                        nextStart = now + 2;
                        lan.Start(); started = true; transportGeneration = lan.Generation; networkError = "";
                    }
                    if (now >= inspectAt || priorMonitor != config.Monitor) {
                        competitor = services.CompetitorRunning(); inspectAt = now + 3;
                        Rectangle nextBounds = services.SelectBounds(config);
                        if (bounds != nextBounds || priorMonitor != config.Monitor) {
                            ResetPixels(); detector = new ActiveAreaDetector(); bounds = nextBounds;
                        }
                        priorMonitor = config.Monitor;
                    }
                    if (priorMode != config.AreaMode) { detector = new ActiveAreaDetector(); priorMode = config.AreaMode; desired = null; smoother.Reset(); }
                    bool requestedDiscovery = Interlocked.Exchange(ref discoveryRequested, 0) != 0;
                    if (now >= scanAt || requestedDiscovery) {
                        scanAt = now + (lan.LastSeenUtc < utc.AddSeconds(-15) ? 5 : 30);
                        try { lan.Discover(); } catch (Exception ex) { RecordNetworkError(ex); }
                    }
                    if (now >= pollAt && lan.CurrentTarget != null) {
                        pollAt = now + 5; try { lan.Poll(); } catch (Exception ex) { RecordNetworkError(ex); }
                    }
                    EndpointToken endpoint = lan.CurrentEndpoint;
                    DeviceInfo target = endpoint == null ? null : endpoint.Snapshot;
                    // Discover/Poll or the receiver can publish a reply after the step began.
                    // Check its timestamp against a clock sampled after this snapshot.
                    utc = services.UtcNow();
                    bool reachable = target != null && target.Sku == "H6609" &&
                        (String.IsNullOrEmpty(config.DeviceId) || String.Equals(target.Id, config.DeviceId, StringComparison.OrdinalIgnoreCase)) && target.LastSeenUtc > utc.AddSeconds(-20) && target.LastSeenUtc <= utc;
                    int resume;
                    lock (controlGate) resume = resumeVersion;
                    string nextPowerKey = endpoint == null ? "" : Key(endpoint) + "|" + endpoint.Generation;
                    if (reachable && (powerEpisodeKey != nextPowerKey || appliedResumeVersion != resume)) {
                        ResetPowerEpisode(appliedResumeVersion != resume ? "explicit resume" : "validated endpoint/transport changed");
                        powerEpisodeKey = nextPowerKey; appliedResumeVersion = resume;
                    }
                    bool recoveredTransport = reachable && (!previouslyReachable || transportGeneration != endpoint.Generation);
                    if (recoveredTransport) {
                        sessionActive = false; activationAttempts = 0; nextActivation = now;
                        transportGeneration = endpoint.Generation;
                        foreach (ReleaseObligation item in obligations) if (item.Requested) { item.Attempts = 0; item.NextAttempt = now; }
                    }
                    if (reachable) foreach (ReleaseObligation item in obligations) {
                        if (Key(item.Endpoint) != Key(endpoint) && !item.Requested) {
                            item.Requested = true; item.Attempts = 0; item.NextAttempt = now;
                        }
                    }
                    if (reachable && sessionKey.Length != 0 && sessionKey != Key(endpoint)) {
                        // Reachability loss must not hide the old endpoint's release obligation.
                        RequestRelease(); previous = null; nextActivation = now;
                    }
                    int zone; long version; bool rawTest;
                    lock (controlGate) { ExpireTestLocked(); zone = testZone; rawTest = testRaw; version = controlVersion; }
                    bool testing = zone >= 0;
                    bool intentionalPause = (paused && !testing) || sessionLocked || suspended || competitor;
                    FinishIdentification(endpoint, reachable, intentionalPause, config, version);
                    if (intentionalPause) {
                        if (sessionLocked || suspended || competitor) CancelTest();
                        RequestRelease(); TryReleases(now, false); ResetPixels();
                        lastAmbient = null; lastLogicalAmbient = null; previous = null; previousWasTest = false; lastSmoothAt = now;
                        string message = sessionLocked || suspended ? "Paused while Windows is locked or sleeping" :
                            competitor ? "Paused: Govee Desktop is running" : "Paused";
                        Publish(message, networkError, obligations.Count == 0 ? "paused" : "release pending (delivery unconfirmed)", target, null, false, work);
                        previouslyReachable = reachable;
                        return;
                    }
                    // Requested cleanup stays due even while incoming replies are stale.
                    // The ledger's next-attempt time prevents duplicate expiry retries.
                    TryReleases(now, false);
                    if (!reachable) {
                        // Reply freshness and the obligation to release are independent.
                        sessionActive = false; activationAttempts = 0;
                        ResetPixels(); lastSmoothAt = now; previouslyReachable = false;
                        Publish("Reconnecting to your G1...", networkError, "holding; device replies missing", target, lastLogicalAmbient, false, work);
                        return;
                    }
                    previouslyReachable = true;
                    ObservePower(target, utc);
                    if (testing) {
                        Color[] testColors = new Color[config.ZoneCount];
                        for (int i = 0; i < config.ZoneCount; i++) testColors[i] = i == zone ? Color.FromArgb(100, 35, 5) : Color.Black;
                        Color[] testWire = rawTest ? testColors : MapToWire(testColors, config);
                        Color[] testLogical = rawTest ? MapToLogical(testColors, config) : testColors;
                        if (Activate(endpoint, now, version, testing)) {
                            Send(endpoint, testWire, true, config, now, version);
                            Publish((rawTest ? "Identifying raw strip output " : "Testing screen zone ") + (zone + 1), "Identification ends after eight seconds.", "calibration", target, testLogical, true, work);
                        } else Publish("Waiting to activate zone identification", networkError, ActivationWaitDetail, target, null, false, work);
                        lastSmoothAt = now;
                        return;
                    }
                    CaptureFrame frame = null;
                    string captureError = "";
                    try {
                        if (capture == null) capture = services.CreateCapture();
                        attempts++; frame = capture.Capture(bounds); captureError = capture.LastError ?? "";
                    } catch (Exception ex) {
                        captureError = ex.GetType().Name + ": " + ex.Message;
                        if (capture != null) { try { capture.Dispose(); } catch { } capture = null; }
                    }
                    if (frame == null || captureError.Length != 0) {
                        if (captureError.Length != 0) captureErrors++; else missingFrames++;
                        IEngineCaptureMetadata failedMetadata = capture as IEngineCaptureMetadata;
                        if (failedMetadata != null && failedMetadata.ProtectedContentMasked) protectedResults++;
                        detector.ClearPendingEvidence(); lastSmoothAt = now;
                        if (captureError.Length != 0 && captureError != lastCaptureError) Log("Capture recovery: " + captureError);
                        lastCaptureError = captureError;
                        // Ambient history is separate from calibration: an expired test can never be heartbeated.
                        if (sessionActive && lastAmbient != null) Send(endpoint, lastAmbient, false, config, now, version);
                        else if (previousWasTest) { RequestRelease(); TryReleases(now, false); previous = null; previousWasTest = false; }
                        Publish(lastAmbient == null ? "Waiting for the first screen frame..." : "Keeping last colors while screen capture recovers",
                            captureError, lastAmbient == null ? "waiting for usable image" : "holding last ambient colors", target, lastLogicalAmbient, sessionActive && lastAmbient != null, work);
                        return;
                    }
                    lastCaptureError = "";
                    IEngineCaptureMetadata metadata = capture as IEngineCaptureMetadata;
                    bool fresh = metadata == null ? capture.HasNewFrame : metadata.HasFreshDesktopFrame;
                    if (fresh) { freshFrames++; lastImageAt = now; lastImageUtc = utc; }
                    else if (metadata != null && metadata.IsReprocessedFrame) reprocessedFrames++;
                    else idleResults++;
                    // Healthy cached image evidence advances crop stability even when no desktop present occurs.
                    Rectangle active = detector.Update(frame, config.AreaMode);
                    if (capture.HasNewFrame || desired == null || active != activeArea || !Object.ReferenceEquals(sampledSettings, config)) {
                        desired = ColorProcessor.Sample(frame, active, config.EdgePercent / 100.0, config.Brightness / 100.0, config.Saturation / 100.0, config.ZoneCount, config.IgnoreBlackPixels);
                        sampledSettings = config;
                    }
                    activeArea = active; frameWidth = frame.Width; frameHeight = frame.Height;
                    Color[] filtered = smoother.Update(desired, now - lastSmoothAt, config.SmoothMs);
                    lastSmoothAt = now;
                    Color[] wire = MapToWire(filtered, config);
                    lastAmbient = (Color[])wire.Clone();
                    lastLogicalAmbient = (Color[])filtered.Clone();
                    if (Activate(endpoint, now, version, testing)) {
                        Send(endpoint, wire, false, config, now, version);
                        Publish("Screen colors are being sent", (metadata == null ? "" : metadata.Description + ". ") + "Strip output is not acknowledged by the LAN protocol.",
                            fresh ? "streaming fresh image" : "streaming healthy cached image", target, filtered, true, work);
                    } else Publish("Waiting to activate screen lighting", networkError, ActivationWaitDetail, target, filtered, false, work);
                }
                catch (OperationCanceledException) {
                    long currentVersion; lock (controlGate) { ExpireTestLocked(); currentVersion = controlVersion; }
                    FinishIdentification(lan.CurrentEndpoint, false, true, config, currentVersion);
                    Publish(quit ? "Stopping..." : "Control changed; waiting for the next update", "", "control request canceled", lan.CurrentTarget, lastLogicalAmbient, false, work);
                }
                catch (Exception ex) {
                    RecordNetworkError(ex);
                    Publish(started ? "Recovering connection; keeping last colors" : "Waiting for local connection (retrying)",
                        networkError, "transport retry pending", lan.CurrentTarget, lastLogicalAmbient, false, work);
                }
            }
        }
        private void Run()
        {
            try {
                while (!quit) {
                    Stopwatch timer = Stopwatch.StartNew(); Step();
                    int remaining = (int)Math.Max(1, (paused ? 100 : 1000.0 / settings.Fps) - timer.Elapsed.TotalMilliseconds);
                    while (remaining > 0 && !quit) { int delay = Math.Min(remaining, 50); Thread.Sleep(delay); remaining -= delay; }
                }
            } finally { lock (tickGate) { Shutdown(); } }
        }
        private void Shutdown()
        {
            if (disposed) return;
            disposed = true; CancelTest(); RequestRelease();
            // UDP is unacknowledged. At most three local attempts, never a claim of delivery.
            for (int i = 0; i < 3; i++) TryReleases(services.MonotonicSeconds(), true);
            if (capture != null) { try { capture.Dispose(); } catch (Exception ex) { Log(ex.Message); } capture = null; }
            lan.DeviceSeen -= OnDeviceSeen; lan.Error -= OnNetworkError;
            try { lan.Dispose(); } catch (Exception ex) { Log("Local transport cleanup failed: " + ex.Message); }
            state = new EngineState { Message = "Stopped", Detail = obligations.Count == 0 ? "Release command sent where required; delivery is unconfirmed." : "A release attempt failed; device state is unconfirmed.", ReleasePending = obligations.Count != 0, OutputPolicy = "stopped" };
            shutdownCompleted = true;
        }
        private static bool Different(Color[] a, Color[] b)
        {
            if (b == null || a.Length != b.Length) return true;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i].R - b[i].R) > 1 || Math.Abs(a[i].G - b[i].G) > 1 || Math.Abs(a[i].B - b[i].B) > 1) return true;
            return false;
        }
        public void RequestStop()
        {
            lock (lifetimeGate) {
                if (quit) return;
                quit = true; CancelTest();
                if (services.ListenSystemEvents) {
                    SystemEvents.SessionSwitch -= OnSession;
                    SystemEvents.PowerModeChanged -= OnPower;
                }
                state = new EngineState { Message = "Stopping; waiting for lighting cleanup", ShutdownIncomplete = true, ReleasePending = true, OutputPolicy = "shutdown pending" };
                if (worker == null) {
                    worker = new Thread(delegate() { lock (tickGate) { Shutdown(); } }) { IsBackground = false, Name = "EdgeGlow cleanup" };
                    worker.Start();
                }
            }
        }
        public bool WaitForShutdown(int timeoutMs)
        {
            RequestStop();
            Thread active; lock (lifetimeGate) active = worker;
            if (active == Thread.CurrentThread) return shutdownCompleted;
            bool done = active == null || active.Join(timeoutMs);
            if (!done) Log("Shutdown remains pending; the foreground worker and application coordinator retain the process lifetime.");
            return done && shutdownCompleted;
        }
        public void Dispose() { WaitForShutdown(4000); }
    }
}
