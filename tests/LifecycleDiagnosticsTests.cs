using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace EdgeGlow
{
    internal static class LifecycleDiagnosticsTests
    {
        private static int assertions, passed;
        private static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        private static void Run(string name, Action test)
        { test(); passed++; Console.WriteLine("PASS " + name); }
        private static int Main()
        {
            try {
                Run("L01_display_notifications_log_only_on_worker", DisplayEvents);
                Run("L02_system_event_queue_is_bounded", BoundedEvents);
                Run("L03_session_and_power_notifications_are_preserved", PowerEvents);
                Run("L04_worker_fault_is_retained_and_cleanup_finishes", WorkerFault);
                Run("L05_cleanup_fault_ends_worker_without_unhandled_exception", CleanupFault);
                Run("L06_broken_diagnostic_sink_does_not_escape_fault_handler", BrokenSink);
                Run("L07_expected_owner_exit_survives_cleanup_failure", OwnerExitBoundary);
                Console.WriteLine("PASS " + passed + " lifecycle diagnostic cases; " + assertions + " assertions. No native capture, LAN, GUI, settings, or startup operation.");
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly List<string> Logs = new List<string>();
            public readonly FakeTransport Transport = new FakeTransport();
            public readonly GlowEngine Engine;
            public Fixture(Func<double> clock, Action<string> sink)
            {
                Engine = new GlowEngine(new AppSettings { AutoSync = false }, new EngineServices {
                    Transport = Transport, CreateCapture = delegate { throw new InvalidOperationException("Capture must not run in these fixtures."); },
                    UtcNow = delegate { return new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc); },
                    MonotonicSeconds = clock ?? delegate { return 1; },
                    SelectBounds = delegate { return new Rectangle(0, 0, 3440, 1440); }, CompetitorRunning = delegate { return false; },
                    Log = sink ?? delegate(string text) { Logs.Add(text); }, ListenSystemEvents = false
                });
            }
            public void Dispose() { Engine.Dispose(); }
        }
        private static int Count(List<string> values, string text)
        { return values.FindAll(delegate(string value) { return value.Contains(text); }).Count; }
        private static void DisplayEvents()
        {
            using (Fixture f = new Fixture(null, null)) {
                f.Engine.SetDisplayPowerState(0); f.Engine.SetDisplayPowerState(1); f.Engine.SetDisplayPowerState(2);
                f.Engine.SetDisplayPowerState(2); f.Engine.SetDisplayPowerState(-1); f.Engine.SetDisplayPowerState(3);
                Check(f.Logs.Count == 0, "Display callback synchronously called the log sink.");
                f.Engine.Step();
                Check(Count(f.Logs, "Windows display power state:") == 3, "Transitions were lost or duplicate/invalid states were logged.");
                Check(f.Logs[0].Contains("Off (0)") && f.Logs[1].Contains("On (1)") && f.Logs[2].Contains("Dimmed (2)"), "Display transition order differs.");
                Check(f.Logs[0].Contains("receivedUtc="), "Callback arrival time missing.");
                Check(f.Engine.State.DisplayPowerState == 2, "Published display state differs.");
                int firstCount = f.Logs.Count; f.Engine.Step(); Check(f.Logs.Count == firstCount, "System events were logged again.");
            }
        }
        private static void BoundedEvents()
        {
            using (Fixture f = new Fixture(null, null)) {
                for (int i = 0; i < 40; i++) f.Engine.SetDisplayPowerState(i % 2);
                Check(f.Logs.Count == 0, "Bounded callback queue wrote synchronously.");
                f.Engine.Step();
                Check(Count(f.Logs, "Windows display power state:") == 32, "Event capacity is not 32.");
                Check(Count(f.Logs, "dropped 8 older entries") == 1, "Dropped event evidence missing.");
                Check(f.Engine.State.DisplayPowerState == 1, "Latest display state was lost.");
            }
        }
        private static void Notify(GlowEngine engine, string method, EventArgs args)
        { typeof(GlowEngine).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine, new object[] { null, args }); }
        private static void PowerEvents()
        {
            using (Fixture f = new Fixture(null, null)) {
                Notify(f.Engine, "OnSession", new SessionSwitchEventArgs(SessionSwitchReason.SessionLock));
                Notify(f.Engine, "OnPower", new PowerModeChangedEventArgs(PowerModes.Suspend));
                Notify(f.Engine, "OnPower", new PowerModeChangedEventArgs(PowerModes.Resume));
                Notify(f.Engine, "OnSession", new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock));
                Check(f.Logs.Count == 0, "System event handler synchronously called log sink.");
                f.Engine.Step();
                Check(Count(f.Logs, "Windows session notification: SessionLock") == 1, "Lock event missing.");
                Check(Count(f.Logs, "Windows power notification: Suspend") == 1, "Suspend event missing.");
                Check(Count(f.Logs, "Windows power notification: Resume") == 1, "Resume event missing.");
                Check(Count(f.Logs, "Windows session notification: SessionUnlock") == 1, "Unlock event missing.");
                Check(f.Engine.Paused && !f.Engine.State.AutomaticStandby, "Diagnostics changed manual pause semantics.");
            }
        }
        private static void WaitForFault(GlowEngine engine)
        { Check(SpinWait.SpinUntil(delegate { return engine.WorkerFault != null; }, 2000), "Worker fault did not become visible."); }
        private static void WorkerFault()
        {
            int calls = 0;
            using (Fixture f = new Fixture(delegate { if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("synthetic worker clock fault"); return 1; }, null)) {
                f.Engine.Start(); WaitForFault(f.Engine);
                Check(f.Engine.WaitForShutdown(2000), "Worker failure did not finish bounded cleanup.");
                Check(f.Engine.WorkerFault.Message == "synthetic worker clock fault", "Primary worker fault was lost.");
                Check(Count(f.Logs, "Fatal engine failure during screen worker") == 1, "Worker failure was not logged through injected sink.");
                Check(f.Transport.Disposed && f.Engine.ShutdownCompleted, "Transport cleanup did not finish.");
            }
        }
        private static void CleanupFault()
        {
            int calls = 0;
            using (Fixture f = new Fixture(delegate { if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("synthetic cleanup clock fault"); return 1; }, null)) {
                f.Engine.RequestStop(); WaitForFault(f.Engine);
                Check(!f.Engine.WaitForShutdown(2000), "Incomplete cleanup was falsely reported complete.");
                Check(f.Engine.WorkerFault.Message == "synthetic cleanup clock fault", "Cleanup fault was lost.");
                Check(Count(f.Logs, "Fatal engine failure during worker cleanup") == 1, "Cleanup failure missing from injected log.");
            }
        }
        private static void BrokenSink()
        {
            int calls = 0;
            using (Fixture f = new Fixture(delegate { if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("primary synthetic fault"); return 1; },
                delegate { throw new InvalidOperationException("synthetic diagnostic sink failure"); })) {
                f.Engine.Start(); WaitForFault(f.Engine);
                Check(f.Engine.WaitForShutdown(2000), "Broken fault log sink prevented worker cleanup.");
                Check(f.Engine.WorkerFault.Message == "primary synthetic fault", "Broken log sink replaced primary fault.");
            }
        }
        private static void OwnerExitBoundary()
        {
            int calls = 0; bool exitRequested = true;
            using (Fixture f = new Fixture(delegate { if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("owner exit cleanup failure"); return 1; }, null)) {
                f.Engine.RequestStop(); WaitForFault(f.Engine);
                Check(!f.Engine.WaitForShutdown(2000), "Owner cleanup failure was falsely complete.");
                Check(!Program.ShouldFailForWorkerFault(f.Engine.WorkerFault, exitRequested, false), "Owner exit cleanup failure was promoted to unexpected failure/restart.");
                Check(Program.ShouldFailForWorkerFault(f.Engine.WorkerFault, false, false), "The same fault without an exit request failed to request recovery.");
                Check(!Program.ShouldFailForWorkerFault(f.Engine.WorkerFault, true, true), "An existing fatal exit was requested again.");
            }
        }
        private sealed class FakeTransport : IEngineTransport
        {
            public bool Disposed;
            public event Action<DeviceInfo> DeviceSeen { add { } remove { } }
            public event Action<string> Error { add { } remove { } }
            public DeviceInfo CurrentTarget { get { return null; } }
            public EndpointToken CurrentEndpoint { get { return null; } }
            public DateTime LastSeenUtc { get { return DateTime.MinValue; } }
            public int Generation { get { return 1; } }
            public void Start() { }
            public void Discover() { }
            public void Poll() { }
            public void SetTarget(DeviceInfo device) { }
            public void Enable(EndpointToken endpoint, Func<bool> authorize) { throw new InvalidOperationException("Control must not run."); }
            public void Disable(EndpointToken endpoint) { throw new InvalidOperationException("Control must not run."); }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize) { throw new InvalidOperationException("Control must not run."); }
            public void Dispose() { Disposed = true; }
        }
    }
}
