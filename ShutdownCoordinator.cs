using System;
namespace EdgeGlow
{
    // Shared by the WinForms lifetime and isolated child-process lifetime tests.
    public sealed class ShutdownCoordinator
    {
        private readonly GlowEngine engine;
        public bool Requested { get; private set; }
        public bool Forced { get; private set; }
        public bool Completed { get { return Requested && engine.ShutdownCompleted; } }
        public bool Pending { get { return Requested && !Completed && !Forced; } }
        public ShutdownCoordinator(GlowEngine value)
        {
            if (value == null) throw new ArgumentNullException("value");
            engine = value;
        }
        public void Request() { Requested = true; engine.RequestStop(); }
        public bool WaitForCompletion(int timeoutMs) { Request(); return engine.WaitForShutdown(timeoutMs); }
        // Explicit owner exit, fatal-cleanup timeout and isolated tests use this.
        // Diagnostic/test callers use exit 2 for abandoned cleanup. Explicit owner
        // exit uses 0 to stay stopped; neither code establishes release delivery.
        public void ForceExit() { ForceExit(false); }
        public void ForceExit(bool expectedExit)
        {
            Request(); Forced = true;
            // An owner-requested exit must remain stopped when Windows recovery is enabled.
            Environment.Exit(expectedExit ? 0 : 2);
        }
    }
}
