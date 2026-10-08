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
        // Only an explicit owner action (or isolated test child) invokes force exit.
        // Exit code 2 identifies abandoned cleanup; no release-delivery claim is made.
        public void ForceExit() { Request(); Forced = true; Environment.Exit(2); }
    }
}
