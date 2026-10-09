using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace EdgeGlow
{
    // One replacement process after a caught failure, with no resident supervisor.
    internal sealed class AppFailureRecovery
    {
        internal const int MaximumAttempts = 3, BackoffMilliseconds = 10000;
        internal const double HealthyResetSeconds = 600;
        private readonly object gate = new object();
        private readonly int incomingAttempt;
        private readonly Func<double> clock;
        private readonly Func<int, bool, int> launch;
        private readonly Action<string> log;
        private bool attempted, suppressed;
        private double runningSince = -1;

        internal AppFailureRecovery(int attempt, Func<double> clock, Func<int, bool, int> launch, Action<string> log)
        {
            if (clock == null || launch == null) throw new ArgumentNullException("Recovery dependencies");
            incomingAttempt = Math.Max(0, Math.Min(MaximumAttempts, attempt));
            this.clock = clock; this.launch = launch; this.log = log;
        }
        internal static AppFailureRecovery CreateDefault(string executable, int incomingAttempt, Action<string> log)
        {
            Stopwatch uptime = Stopwatch.StartNew();
            return new AppFailureRecovery(incomingAttempt, delegate { return uptime.Elapsed.TotalSeconds; },
                delegate(int next, bool paused) {
                    using (Process child = Process.Start(CreateStartInfo(executable, next, paused))) {
                        if (child == null) throw new InvalidOperationException("No replacement process was returned.");
                        return child.Id;
                    }
                }, log);
        }
        internal static ProcessStartInfo CreateStartInfo(string executable, int attempt, bool paused)
        {
            return new ProcessStartInfo {
                FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable),
                Arguments = "--tray --recovery-attempt=" + attempt.ToString(CultureInfo.InvariantCulture) + (paused ? " --recovery-paused" : ""),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
        }
        internal static int ReadAttempt(string[] args)
        {
            int result = 0; bool found = false;
            foreach (string arg in args) {
                if (!arg.StartsWith("--recovery-attempt=", StringComparison.Ordinal)) continue;
                int value;
                if (found || !Int32.TryParse(arg.Substring(19), NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                    value < 1 || value > MaximumAttempts) return MaximumAttempts;
                found = true; result = value;
            }
            return result;
        }
        internal static void RestorePause(GlowEngine engine, bool? paused)
        { if (paused.HasValue) engine.Paused = paused.Value; }
        internal static void DelayBeforeStart(int attempt, Action<int> delay)
        { if (attempt > 0) delay(BackoffMilliseconds); }
        internal void MarkRunning()
        { lock (gate) { runningSince = clock(); } }
        internal void Suppress()
        { lock (gate) { suppressed = true; } }
        private void Log(string message)
        { try { if (log != null) log(message); } catch { } }
        internal bool TryRecover(bool expectedExit, bool paused)
        {
            lock (gate) {
                if (expectedExit) suppressed = true;
                if (suppressed || attempted) return false;
                attempted = true;
                try {
                    double now = clock();
                    int used = runningSince >= 0 && now >= runningSince + HealthyResetSeconds ? 0 : incomingAttempt;
                    if (used >= MaximumAttempts) { Log("Managed-failure recovery stopped: three retries exhausted for this failure burst."); return false; }
                    int next = used + 1;
                    int pid = launch(next, paused);
                    Log("Managed-failure replacement launched: pid=" + pid + "; attempt=" + next + "/" + MaximumAttempts +
                        "; startupDelayMs=" + BackoffMilliseconds + "; paused=" + paused + ". Process launch does not establish capture or visible output.");
                    return true;
                } catch (Exception ex) {
                    Log("Managed-failure replacement could not start; no further attempt in this process: " + ex);
                    return false;
                }
            }
        }
    }
}
