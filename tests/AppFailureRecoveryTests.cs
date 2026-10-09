using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;

namespace EdgeGlow
{
    internal static class AppFailureRecoveryTests
    {
        private static int assertions, passed;
        private static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        private static void Run(string name, Action test)
        { test(); passed++; Console.WriteLine("PASS " + name); }
        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--tray") >= 0 && AppFailureRecovery.ReadAttempt(args) > 0) return Child(args);
            try {
                Run("R01_recovery_arguments_are_bounded", Arguments);
                Run("R02_one_replacement_per_process", Once);
                Run("R03_three_retry_budget_and_healthy_reset", Budget);
                Run("R04_explicit_exit_suppresses_pending_recovery", ExpectedExit);
                Run("R05_launch_failure_is_logged_without_loop", LaunchFailure);
                Run("R06_hidden_launch_and_pause_transfer", LaunchInfo);
                Run("R07_concurrent_failure_routes_launch_once", Concurrent);
                Run("R08_real_hidden_child_delays_before_singleton", RealChild);
                Run("R09_recovery_restores_both_runtime_pause_states", PauseState);
                Console.WriteLine("PASS " + passed + " failure recovery cases; " + assertions + " assertions. Synthetic process only; no owner capture, LAN, GUI, settings, or startup operation.");
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        }
        private static void Arguments()
        {
            Check(AppFailureRecovery.ReadAttempt(new string[0]) == 0, "Normal startup acquired retry budget.");
            Check(AppFailureRecovery.ReadAttempt(new string[] { "--tray", "--recovery-attempt=1" }) == 1, "First retry not parsed.");
            Check(AppFailureRecovery.ReadAttempt(new string[] { "--recovery-attempt=3" }) == 3, "Last retry not parsed.");
            foreach (string value in new string[] { "-1", "0", "4", "garbage", "999999999999999999" })
                Check(AppFailureRecovery.ReadAttempt(new string[] { "--recovery-attempt=" + value }) == 3, "Invalid attempt bypassed retry ceiling: " + value);
            Check(AppFailureRecovery.ReadAttempt(new string[] { "--recovery-attempt=1", "--recovery-attempt=1" }) == 3, "Duplicate flags bypassed ceiling.");
            int calls = 0, delay = 0;
            AppFailureRecovery.DelayBeforeStart(0, delegate(int ms) { calls++; delay = ms; });
            Check(calls == 0, "Normal startup delayed.");
            AppFailureRecovery.DelayBeforeStart(1, delegate(int ms) { calls++; delay = ms; });
            Check(calls == 1 && delay == 10000, "Recovery backoff differs.");
        }
        private static void Once()
        {
            int count = 0, attempt = 0; bool paused = false;
            AppFailureRecovery policy = new AppFailureRecovery(1, delegate { return 0; }, delegate(int next, bool hold) { count++; attempt = next; paused = hold; return 123; }, null);
            Check(policy.TryRecover(false, true), "Replacement not attempted.");
            Check(!policy.TryRecover(false, false), "Second failure route launched again.");
            Check(count == 1 && attempt == 2 && paused, "Budget/pause transfer differs.");
        }
        private static void Budget()
        {
            double now = 0; int count = 0, nextAttempt = 0;
            AppFailureRecovery exhausted = new AppFailureRecovery(3, delegate { return now; }, delegate(int next, bool paused) { count++; return 1; }, null);
            now = 10000; Check(!exhausted.TryRecover(false, false) && count == 0, "Failed initialization reset retry budget.");
            now = 0;
            AppFailureRecovery beforeReset = new AppFailureRecovery(3, delegate { return now; }, delegate(int next, bool paused) { count++; return 1; }, null);
            beforeReset.MarkRunning(); now = 599.9;
            Check(!beforeReset.TryRecover(false, false) && count == 0, "Retry budget reset early.");
            now = 0;
            AppFailureRecovery healthy = new AppFailureRecovery(3, delegate { return now; }, delegate(int next, bool paused) { count++; nextAttempt = next; return 1; }, null);
            healthy.MarkRunning(); now = 600;
            Check(healthy.TryRecover(false, false) && count == 1 && nextAttempt == 1, "Healthy ten-minute reset did not reopen first retry.");
            for (int used = 0; used < 3; used++) {
                int seen = 0;
                AppFailureRecovery burst = new AppFailureRecovery(used, delegate { return 0; }, delegate(int next, bool paused) { seen = next; return 1; }, null);
                Check(burst.TryRecover(false, false) && seen == used + 1, "Retry chain skipped its bounded next attempt.");
            }
        }
        private static void ExpectedExit()
        {
            int count = 0;
            AppFailureRecovery owner = new AppFailureRecovery(0, delegate { return 0; }, delegate { count++; return 1; }, null);
            owner.MarkRunning(); owner.Suppress();
            Check(!owner.TryRecover(false, false) && count == 0, "Owner Exit after fatal detection still launched replacement.");
            AppFailureRecovery windows = new AppFailureRecovery(0, delegate { return 0; }, delegate { count++; return 1; }, null);
            Check(!windows.TryRecover(true, false), "Expected Windows shutdown launched replacement.");
            Check(!windows.TryRecover(false, false) && count == 0, "Later failure cleared expected exit suppression.");
        }
        private static void LaunchFailure()
        {
            int count = 0; List<string> logs = new List<string>();
            AppFailureRecovery policy = new AppFailureRecovery(0, delegate { return 0; }, delegate { count++; throw new InvalidOperationException("synthetic Process.Start failure"); }, logs.Add);
            Check(!policy.TryRecover(false, false), "Failed launch was reported successful.");
            Check(!policy.TryRecover(false, false) && count == 1, "Failed launch entered a retry loop.");
            Check(logs.Count == 1 && logs[0].Contains("synthetic Process.Start failure"), "Launch failure record missing.");
        }
        private static void LaunchInfo()
        {
            ProcessStartInfo info = AppFailureRecovery.CreateStartInfo(@"C:\A folder\EdgeGlow.exe", 2, true);
            Check(info.FileName == @"C:\A folder\EdgeGlow.exe" && info.WorkingDirectory == @"C:\A folder", "Executable path with spaces is not a direct launch.");
            Check(!info.UseShellExecute && info.CreateNoWindow && info.WindowStyle == ProcessWindowStyle.Hidden, "Recovery launch can show a shell/window.");
            Check(info.Arguments == "--tray --recovery-attempt=2 --recovery-paused", "Pause/startup flags differ.");
            Check(!AppFailureRecovery.CreateStartInfo(@"C:\A folder\EdgeGlow.exe", 1, false).Arguments.Contains("paused"), "Active sync was forced paused.");
        }
        private static void Concurrent()
        {
            int count = 0; Thread[] threads = new Thread[8];
            AppFailureRecovery policy = new AppFailureRecovery(0, delegate { return 0; }, delegate { Interlocked.Increment(ref count); return 1; }, null);
            for (int i = 0; i < threads.Length; i++) { threads[i] = new Thread(delegate() { policy.TryRecover(false, false); }); threads[i].Start(); }
            foreach (Thread thread in threads) Check(thread.Join(2000), "Concurrent policy route hung.");
            Check(count == 1, "Concurrent fatal paths launched more than one child.");
        }
        private static void PauseState()
        {
            foreach (bool autoSync in new bool[] { false, true }) {
                using (GlowEngine engine = new GlowEngine(new AppSettings { AutoSync = autoSync }, new EngineServices {
                    Transport = new NoControls(), CreateCapture = delegate { throw new InvalidOperationException("No capture in pause fixture."); },
                    UtcNow = delegate { return DateTime.UtcNow; }, MonotonicSeconds = delegate { return 1; },
                    SelectBounds = delegate { return Rectangle.Empty; }, CompetitorRunning = delegate { return false; }, ListenSystemEvents = false
                })) {
                    AppFailureRecovery.RestorePause(engine, null); Check(engine.Paused == !autoSync, "Ordinary launch changed configured AutoSync.");
                    AppFailureRecovery.RestorePause(engine, false); Check(!engine.Paused, "Recovered running state was lost when saved AutoSync was false.");
                    AppFailureRecovery.RestorePause(engine, true); Check(engine.Paused, "Recovered manual pause was lost.");
                }
            }
        }
        private sealed class NoControls : IEngineTransport
        {
            public event Action<DeviceInfo> DeviceSeen { add { } remove { } }
            public event Action<string> Error { add { } remove { } }
            public DeviceInfo CurrentTarget { get { return null; } }
            public EndpointToken CurrentEndpoint { get { return null; } }
            public DateTime LastSeenUtc { get { return DateTime.MinValue; } }
            public int Generation { get { return 1; } }
            public void Start() { } public void Discover() { } public void Poll() { }
            public void SetTarget(DeviceInfo device) { }
            public void Enable(EndpointToken endpoint, Func<bool> authorize) { throw new InvalidOperationException("No control."); }
            public void Disable(EndpointToken endpoint) { throw new InvalidOperationException("No control."); }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize) { throw new InvalidOperationException("No control."); }
            public void Dispose() { }
        }
        private static int Child(string[] args)
        {
            try {
                int attempt = AppFailureRecovery.ReadAttempt(args);
                AppFailureRecovery.DelayBeforeStart(attempt, Thread.Sleep);
                string name = Environment.GetEnvironmentVariable("EDGEGLOW_TEST_RECOVERY_MUTEX");
                string receipt = Environment.GetEnvironmentVariable("EDGEGLOW_TEST_RECOVERY_RECEIPT");
                bool created;
                using (Mutex singleton = new Mutex(true, name, out created)) {
                    File.WriteAllLines(receipt, new string[] { "attempt=" + attempt, "paused=" + (Array.IndexOf(args, "--recovery-paused") >= 0), "singleton=" + created, "pid=" + Process.GetCurrentProcess().Id });
                    if (created) singleton.ReleaseMutex();
                }
                return 0;
            } catch { return 2; }
        }
        private static void RealChild()
        {
            string receipt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "synthetic-recovery-child-" + Guid.NewGuid().ToString("N") + ".txt");
            string name = "Local\\EdgeGlow.Recovery.Test." + Guid.NewGuid().ToString("N");
            Process child = null; bool created;
            Stopwatch elapsed = Stopwatch.StartNew();
            try {
                using (Mutex parentSingleton = new Mutex(true, name, out created)) {
                    Check(created, "Isolated test singleton already exists.");
                    AppFailureRecovery policy = new AppFailureRecovery(0, delegate { return elapsed.Elapsed.TotalSeconds; }, delegate(int next, bool paused) {
                        ProcessStartInfo info = AppFailureRecovery.CreateStartInfo(Assembly.GetExecutingAssembly().Location, next, paused);
                        info.EnvironmentVariables["EDGEGLOW_TEST_RECOVERY_RECEIPT"] = receipt;
                        info.EnvironmentVariables["EDGEGLOW_TEST_RECOVERY_MUTEX"] = name;
                        child = Process.Start(info); return child.Id;
                    }, null);
                    policy.MarkRunning();
                    Check(policy.TryRecover(false, true), "Real synthetic child could not launch.");
                    Thread.Sleep(200); Check(!File.Exists(receipt), "Child attempted singleton before recovery backoff.");
                    parentSingleton.ReleaseMutex();
                }
                Check(child.WaitForExit(15000), "Synthetic child did not complete bounded backoff.");
                Check(child.ExitCode == 0 && File.Exists(receipt), "Synthetic child failed.");
                string text = File.ReadAllText(receipt);
                Check(elapsed.ElapsedMilliseconds >= 9500, "Actual child did not wait about ten seconds.");
                Check(text.Contains("attempt=1") && text.Contains("paused=True") && text.Contains("singleton=True"), "Synthetic relaunch lost pause/budget or raced parent singleton.");
            } finally { if (child != null) { if (!child.HasExited) child.Kill(); child.Dispose(); } }
        }
    }
}
