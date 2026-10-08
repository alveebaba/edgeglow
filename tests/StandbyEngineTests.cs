using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    // These deterministic tests compile the production managed components.
    // The clock, capture, monitor selection and LAN transport are fakes: passing
    // does not establish Windows notifications, UDP delivery or physical output.
    public static class StandbyEngineTests
    {
        private static int passed, failed, assertions;
        public static int Main(string[] args)
        {
            Run("Display off stops capture, on waits for fresh frame and wakes once", DisplayOffOn);
            Run("Dim, duplicate and invalid display notifications are quiet", DimAndDuplicates);
            Run("A healthy static cached desktop stays active for ten minutes", StaticCachedDesktop);
            Run("Short capture errors hold colors and reset the no-image timer", ShortFailure);
            Run("Ten continuous seconds of capture errors request standby", ContinuousErrors);
            Run("Ten continuous seconds of null captures request standby", ContinuousMissing);
            Run("A valid black picture is content and does not request standby", ValidBlackPicture);
            Run("Cached and reprocessed frames cannot wake automatic standby", StaleCachedWake);
            Run("One fresh image permits a failed wake to retry on healthy cached frames", FreshImageSurvivesWakeRetry);
            Run("A single fresh image survives clock progress during the initial wake cooldown", FreshImageSurvivesRecoveryClockProgress);
            Run("Off then On during capture cannot publish stale fresh wake evidence", CaptureTransitionCannotPublishFreshWakeEvidence);
            Run("A renewed blocked transition invalidates earlier fresh wake evidence", RenewedBlockClearsFreshLatch);
            Run("Changing the selected device at the off boundary cancels off to the old device", ChangeSelectionAtOffBoundary);
            Run("Changing the selected device at the wake boundary cancels wake to the old device", ChangeSelectionAtWakeBoundary);
            Run("Display on cannot clear overlapping sleep and lock blockers", OverlappingBlocks);
            Run("Manual pause cancels automatic wake ownership", ManualPause);
            Run("A competing controller prevents automatic wake", CompetitorBlocksWake);
            Run("Selected monitor removal stops capture and return requires fresh frame", MissingSelectedMonitor);
            Run("Failed standby retries are spaced and stop at three attempts", FailedStandbyRetries);
            Run("Display on at the standby send boundary cancels the off command", CancelOffByOn);
            Run("Manual pause at the standby send boundary cancels the off command", CancelOffByPause);
            Run("Display off at the wake send boundary cancels wake and colors", CancelWakeByOff);
            Run("Initially paused or never activated instances do not power-control", NoOwnedInitialState);
            Run("Automatic off cancels zone identification and expiry cannot rekindle it", ZoneCanceledByStandby);
            Run("An active instance resumes ambient colors rather than its canceled zone test", ActiveZoneCanceledByStandby);
            Console.WriteLine("Standby engine regression: " + passed + " passed, " + failed + " failed, " + assertions + " assertions.");
            Console.WriteLine("Evidence qualification: managed production code with fakes; no GUI, native capture, real LAN, settings, startup or physical acceptance test.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        private static void NoWake(Fixture f, string cause)
        {
            Check(f.Transport.SuccessfulWakes == 0, cause + " emitted a wake");
            Check(!f.Engine.State.Sending, cause + " published active output");
        }
        private static void DisplayOffOn()
        {
            using (Fixture f = Fixture.Running()) {
                int captures = f.Capture.Calls, colors = f.Transport.Colors.Count;
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 1, "display off did not send standby once");
                Check(f.Capture.Calls == captures, "display off captured the desktop");
                Check(f.Transport.Colors.Count == colors && !f.Engine.State.Sending, "display off streamed colors");
                Check(f.Engine.State.AutomaticStandby, "display off did not publish automatic standby");
                f.Engine.SetDisplayPowerState(0); f.Step(4);
                Check(f.Transport.StandbyEntries == 1 && f.Capture.Calls == captures, "duplicate off retried or captured");
                f.Engine.SetDisplayPowerState(1); f.Step(.05);
                NoWake(f, "display on with an old cached frame");
                f.Capture.Present(Color.FromArgb(180, 20, 70)); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "fresh frame did not resume output");
                Check(f.Transport.EnableEntries == 1, "wake also issued a separate enable");
                Check(!f.Engine.State.AutomaticStandby && f.Transport.Colors.Count == colors + 1, "wake failed to clear standby or send colors");
                Check(f.Transport.Colors[f.Transport.Colors.Count - 1].Length == 18, "wake changed the zone count");
                f.Step(1.1);
                Check(f.Transport.SuccessfulWakes == 1 && f.Transport.EnableEntries == 1, "healthy wake repeated activation");
            }
        }
        private static void DimAndDuplicates()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(2); f.Step(.05);
                Check(f.Engine.State.Sending && f.Transport.StandbyEntries == 0, "dim incorrectly turned off lighting");
                f.Engine.SetDisplayPowerState(2); f.Engine.SetDisplayPowerState(-1); f.Engine.SetDisplayPowerState(3); f.Step(1.1);
                Check(f.Transport.StandbyEntries == 0 && f.Transport.WakeEntries == 0 && f.Transport.EnableEntries == 1, "quiet notifications restarted or powered lighting");
                Check(f.Engine.State.DisplayPowerState == 2, "invalid notification changed the recorded power state");
                f.Engine.SetDisplayPowerState(1); f.Engine.SetDisplayPowerState(1); f.Step(.05);
                Check(f.Transport.StandbyEntries == 0 && f.Transport.WakeEntries == 0 && f.Transport.EnableEntries == 1, "dim to on restarted an active stream");
            }
        }
        private static void StaticCachedDesktop()
        {
            using (Fixture f = Fixture.Running()) {
                for (int i = 0; i < 600; i++) f.Step(1);
                Check(f.Engine.State.Sending, "static desktop became paused");
                Check(f.Transport.StandbyEntries == 0 && f.Transport.WakeEntries == 0, "static desktop was classified as no picture");
                Check(f.Transport.EnableEntries == 1, "static desktop repeated activation");
                Check(f.Engine.State.FreshFrames == 1 && f.Engine.State.IdleResults >= 600, "fixture did not exercise healthy cached frames");
                Check(f.Transport.Colors.Count > 500, "static desktop did not maintain the normal stream heartbeat");
            }
        }
        private static void ShortFailure()
        {
            using (Fixture f = Fixture.Running()) {
                f.Capture.Fail("Synthetic capture error"); f.Step(.1); f.Step(.5);
                Check(f.Transport.StandbyEntries == 0 && f.Engine.State.Sending, "a short capture error turned off lights");
                int colors = f.Transport.Colors.Count;
                f.Step(.5);
                Check(f.Transport.Colors.Count > colors, "short error did not hold ambient colors");
                f.Capture.Present(Color.FromArgb(30, 140, 80)); f.Step(.1);
                f.Capture.Fail("Synthetic second episode"); f.Step(5); f.Step(9.9);
                Check(f.Transport.StandbyEntries == 0, "healthy recovery did not reset the continuous-error timer");
                f.Capture.Present(Color.FromArgb(30, 140, 80)); f.Step(.05);
                Check(f.Transport.StandbyEntries == 0 && f.Engine.State.Sending, "short error recovery was not seamless");
            }
        }
        private static void ContinuousErrors() { ContinuousFailure(true); }
        private static void ContinuousMissing() { ContinuousFailure(false); }
        private static void ContinuousFailure(bool error)
        {
            using (Fixture f = Fixture.Running()) {
                if (error) f.Capture.Fail("Synthetic persistent error"); else f.Capture.Missing();
                f.Step(.1); f.Step(GlowEngine.NoPictureDelaySeconds - .1);
                Check(f.Transport.StandbyEntries == 0 && f.Engine.State.Sending, "no-picture delay elapsed early");
                f.Step(.2);
                Check(f.Transport.SuccessfulStandbys == 1 && f.Engine.State.AutomaticStandby, "persistent unavailable picture did not request standby");
                Check(!f.Engine.State.Sending, "persistent unavailable picture still sent ambient colors");
                int colors = f.Transport.Colors.Count;
                f.Step(2);
                Check(f.Transport.StandbyEntries == 1 && f.Transport.Colors.Count == colors, "standby kept sending power or stale colors");
                f.Capture.Cached(); f.Step(.05); NoWake(f, "capture recovery containing only a cached image");
                f.Capture.Present(Color.FromArgb(190, 50, 15)); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "fresh capture after unavailable-picture standby did not wake");
                Check(f.Transport.EnableEntries == 1, "capture recovery also used ordinary enable");
            }
        }
        private static void ValidBlackPicture()
        {
            using (Fixture f = new Fixture(true)) {
                f.Capture.Present(Color.Black); f.Step();
                Check(f.Engine.State.Sending && f.Engine.State.LastColorPeak == 0, "valid black frame was not emitted as black content");
                for (int i = 0; i < 30; i++) f.Step(1);
                Check(f.Transport.StandbyEntries == 0 && f.Engine.State.Sending, "valid black cached picture became no-picture standby");
                Check(f.Engine.State.MissingFrames == 0 && f.Engine.State.CaptureErrors == 0, "valid black frame was counted as unavailable");
            }
        }
        private static void StaleCachedWake()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05); f.Engine.SetDisplayPowerState(1);
                f.Capture.Cached(); f.Step(.05); NoWake(f, "cached desktop");
                f.Capture.Reprocess(); f.Step(1.1); NoWake(f, "reprocessed old desktop");
                Check(f.Transport.Colors.Count == 1, "a stale reprocessed frame was emitted");
                f.Capture.Present(Color.FromArgb(220, 20, 10)); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "fresh metadata was not sufficient to resume");
            }
        }

        private static void FreshImageSurvivesWakeRetry()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Engine.SetDisplayPowerState(1); f.Capture.Cached(); f.Step(.05);
                NoWake(f, "cached image before the first fresh image");
                Check(f.Transport.WakeEntries == 0, "stale-before-fresh image entered wake");
                f.Transport.FailWakeCount = 1;
                // A command may take real time; the deterministic clock models it
                // without sleeping, capture, networking or another worker.
                f.Transport.BeforeWakeOnce = delegate { f.Clock.Advance(.02); };
                f.Capture.Present(Color.Red); f.Step(.05);
                Check(f.Transport.WakeEntries == 1 && f.Transport.FailedWakes == 1 &&
                    f.Transport.SuccessfulWakes == 0 && f.Engine.State.AutomaticStandby,
                    "fixture did not retain standby after one failed authorized wake");
                Check(f.Transport.Colors.Count == 1 && !f.Engine.State.Sending, "failed wake streamed colors");
                f.Step(.5);
                Check(f.Transport.WakeEntries == 1 && !f.Engine.State.Sending, "wake ignored its retry cooldown");
                f.Step(.51);
                Check(f.Transport.WakeEntries == 2 && f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending,
                    "healthy cached frames after one fresh image did not retry wake");
                Check(f.Engine.State.FreshFrames == 2, "retry fixture accidentally supplied another fresh image");
                Check(f.Transport.WakeTimes[1] - f.Transport.WakeTimes[0] >= 1,
                    "wake retry did not observe the one-second spacing");
                Check(f.Transport.Colors.Count == 2 && f.Transport.EnableEntries == 1,
                    "wake retry emitted an extra enable or failed to resume colors");
            }
        }
        private static void FreshImageSurvivesRecoveryClockProgress()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red);
                // Step has already captured its monotonic "now" when it asks for
                // UTC. Later ResetPowerEpisode sees this advanced clock, placing
                // nextActivation after Step's earlier timestamp.
                f.Clock.AdvanceAtNextUtc = .02; f.Step(.05);
                Check(f.Transport.WakeEntries <= 1 && f.Engine.State.FreshFrames == 2,
                    "clock progress caused repeated wake attempts or lost the single fresh image");
                f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending &&
                    f.Engine.State.FreshFrames == 2 && f.Transport.EnableEntries == 1,
                    "initial activation cooldown lost the one fresh image before a static cached desktop");
            }
        }
        private static void CaptureTransitionCannotPublishFreshWakeEvidence()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red);
                f.Capture.BeforeReturnOnce = delegate {
                    f.Engine.SetDisplayPowerState(0);
                    f.Engine.SetDisplayPowerState(1);
                };
                f.Step(.05);
                Check(f.Transport.WakeEntries == 0 && f.Transport.SuccessfulWakes == 0 &&
                    f.Transport.Colors.Count == 1 && !f.Engine.State.Sending,
                    "a recovery image captured across Off then On entered wake or streamed colors");
                // This frame now lives in the capture cache, but it belongs to the
                // invalidated transition. Waiting out all cooldowns cannot make
                // it current evidence for the later display-on transition.
                f.Step(1.1); f.Step(1.1);
                Check(f.Transport.WakeEntries == 0 && f.Transport.SuccessfulWakes == 0 &&
                    f.Transport.Colors.Count == 1 && !f.Engine.State.Sending,
                    "cached evidence from the interrupted capture later rekindled the strip");
                f.Capture.Present(Color.Blue); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending &&
                    f.Transport.Colors.Count == 2 && f.Transport.EnableEntries == 1,
                    "a genuinely new recovery image could not resume after an interrupted capture");
                foreach (Color color in f.Transport.Colors[1])
                    Check(color.B > color.R && color.B > color.G, "recovery reused the stale red image instead of the new blue image");
            }
        }
        private static void RenewedBlockClearsFreshLatch()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red);
                f.Transport.FailWakeCount = 1; f.Step(.05);
                Check(f.Transport.FailedWakes == 1 && f.Transport.SuccessfulWakes == 0, "fixture did not fail its first wake");
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 2, "renewed blocker did not send another off");
                f.Engine.SetDisplayPowerState(1); f.Capture.Cached(); f.Step(1.1);
                Check(f.Transport.WakeEntries == 1 && !f.Engine.State.Sending,
                    "fresh image from before the renewed block authorized a later wake");
                f.Capture.Present(Color.Blue); f.Step(.05);
                Check(f.Transport.WakeEntries == 2 && f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending,
                    "renewed block could not recover from a new fresh image");
            }
        }
        private static void ChangeSelectionAtOffBoundary()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.BeforeStandbyOnce = delegate {
                    AppSettings replacement = Config(true); replacement.DeviceId = "TEST-G1-B";
                    f.Engine.Update(replacement);
                };
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.StandbyEntries == 1 && f.Transport.CanceledStandbys == 1 &&
                    f.Transport.SuccessfulStandbys == 0,
                    "deselecting A at the off boundary did not cancel off to A");
                Check(f.Transport.SuccessfulWakes == 0 && f.Transport.Colors.Count == 1,
                    "deselecting A at the off boundary emitted wake or colors to A");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05); f.Step(6);
                Check(f.Transport.SuccessfulStandbys == 0 && f.Transport.WakeEntries == 0 &&
                    f.Transport.EnableEntries == 1 && f.Transport.Colors.Count == 1 && !f.Engine.State.Sending,
                    "deselected A received off, wake, activation or colors on later updates");
                Check(!f.Engine.State.AutomaticStandby && !f.Transport.HasStandby(FakeTransport.Id),
                    "deselected standby episode retained wake authority for A");
            }
        }
        private static void ChangeSelectionAtWakeBoundary()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 1, "fixture did not establish automatic standby for A");
                f.Transport.BeforeWakeOnce = delegate {
                    AppSettings replacement = Config(true); replacement.DeviceId = "TEST-G1-B";
                    f.Engine.Update(replacement);
                };
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05);
                Check(f.Transport.WakeEntries == 1 && f.Transport.CanceledWakes == 1 &&
                    f.Transport.SuccessfulWakes == 0, "deselecting A at the wake boundary did not cancel wake to A");
                Check(f.Transport.SuccessfulStandbys == 1 && f.Transport.Colors.Count == 1,
                    "deselecting A at the wake boundary emitted off or colors to A");
                f.Step(.05); f.Step(6);
                Check(f.Transport.SuccessfulStandbys == 1 && f.Transport.WakeEntries == 1 &&
                    f.Transport.SuccessfulWakes == 0 && f.Transport.EnableEntries == 1 &&
                    f.Transport.Colors.Count == 1 && !f.Engine.State.Sending,
                    "deselected A received new off, wake, activation or colors on later updates");
                Check(!f.Engine.State.AutomaticStandby && !f.Transport.HasStandby(FakeTransport.Id),
                    "wake boundary deselection retained automatic ownership for A");
            }
        }
        private static void OverlappingBlocks()
        {
            using (Fixture f = Fixture.Running()) {
                int captures = f.Capture.Calls;
                f.Engine.SetDisplayPowerState(0); f.Engine.SetSessionBlocked(true, true); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 1, "overlapping blockers did not power off once");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05);
                NoWake(f, "display on while Windows is locked and sleeping");
                f.Engine.SetSessionBlocked(false, true); f.Step(.05); NoWake(f, "session unlock while sleeping");
                Check(f.Capture.Calls == captures && f.Transport.StandbyEntries == 1, "an overlapping blocker captured or repeated standby");
                f.Capture.Cached(); f.Engine.SetSessionBlocked(false, false); f.Step(.05); NoWake(f, "resume with a cached frame");
                f.Capture.Present(Color.Red); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "cleared overlapping blockers did not resume");
                f.Engine.SetDisplayPowerState(0); f.Engine.SetSessionBlocked(false, true); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 2, "second standby episode did not power off");
                f.Engine.SetSessionBlocked(false, false); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && !f.Engine.State.Sending, "resume cleared an outstanding display-off reason");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Blue); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 2 && f.Engine.State.Sending, "last overlapping reason did not resume");
            }
        }
        private static void ManualPause()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05); f.Engine.Paused = true;
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05);
                NoWake(f, "manual pause");
                Check(!f.Engine.State.AutomaticStandby && f.Transport.EnableEntries == 1, "manual pause retained automatic wake or reactivated");
                f.Step(11);
                NoWake(f, "manual pause after waiting");
                f.Engine.Paused = false; f.Capture.Present(Color.Red); f.Step(.05);
                Check(f.Engine.State.Sending && f.Transport.EnableEntries == 2 && f.Transport.SuccessfulWakes == 0, "explicit resume did not use ordinary activation");
            }
        }
        private static void CompetitorBlocksWake()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Competitor = true; f.Engine.InvalidateDisplayTopology(); f.Engine.SetDisplayPowerState(1);
                f.Capture.Present(Color.Green); f.Step(.05);
                NoWake(f, "competing controller");
                Check(!f.Engine.State.AutomaticStandby && f.Transport.EnableEntries == 1, "competitor retained automatic wake or reactivated");
                f.Competitor = false; f.Engine.InvalidateDisplayTopology(); f.Capture.Present(Color.Green); f.Step(.05);
                Check(f.Engine.State.Sending && f.Transport.EnableEntries == 2 && f.Transport.SuccessfulWakes == 0, "competitor exit did not resume through ordinary activation");
            }
        }
        private static void MissingSelectedMonitor()
        {
            using (Fixture f = Fixture.Running()) {
                int captures = f.Capture.Calls;
                f.Bounds = Rectangle.Empty; f.Engine.InvalidateDisplayTopology(); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 1 && f.Capture.Calls == captures, "selected monitor removal did not stop capture and enter standby");
                f.Bounds = new Rectangle(0, 0, 3440, 1440); f.Engine.InvalidateDisplayTopology(); f.Step(.05);
                NoWake(f, "monitor return with an old cached frame");
                f.Capture.Present(Color.FromArgb(150, 60, 120)); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "returned selected monitor did not resume on a fresh image");
            }
        }
        private static void FailedStandbyRetries()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.FailStandbyCount = 10;
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.StandbyEntries == 1 && f.Transport.SuccessfulStandbys == 0, "first failed standby attempt was not recorded");
                f.Step(.5); Check(f.Transport.StandbyEntries == 1, "standby retried before one second");
                f.Step(.51); Check(f.Transport.StandbyEntries == 2, "second standby attempt was not due");
                f.Step(.5); Check(f.Transport.StandbyEntries == 2, "second retry was too early");
                f.Step(.51); Check(f.Transport.StandbyEntries == 3, "third standby attempt was not due");
                for (int i = 0; i < 15; i++) f.Step(1);
                Check(f.Transport.StandbyEntries == 3, "failed standby exceeded its three-attempt ceiling");
                Check(f.Transport.StandbyTimes[1] - f.Transport.StandbyTimes[0] >= 1 &&
                    f.Transport.StandbyTimes[2] - f.Transport.StandbyTimes[1] >= 1, "retry timestamps violate the one-second spacing");
                Check(f.Engine.State.AutomaticStandby && !f.Engine.State.Sending, "failed off command resumed capture output");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "off-send failure prevented a later owner resume");
            }
        }
        private static void CancelOffByOn()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.BeforeStandbyOnce = delegate { f.Engine.SetDisplayPowerState(1); };
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.StandbyEntries == 1 && f.Transport.SuccessfulStandbys == 0 && f.Transport.CanceledStandbys == 1, "display on did not cancel off at serialized authorization");
                Check(f.Transport.Colors.Count == 1, "canceled off emitted new colors in the same update");
                f.Capture.Cached(); f.Step(.05); NoWake(f, "canceled off with cached image");
                f.Capture.Present(Color.Red); f.Step(.05);
                Check(f.Engine.State.Sending && f.Transport.SuccessfulWakes == 0 && f.Transport.EnableEntries == 1, "canceled off before any attempt did not retain its existing active stream");
                Check(!f.Engine.State.AutomaticStandby && !f.Transport.HasStandby(FakeTransport.Id) &&
                    f.Transport.SuccessfulStandbys == 0 && f.Transport.WakeEntries == 0, "canceled-before-off recovery retained standby or issued power control");
                Check(f.Transport.Colors.Count == 2, "fresh red image did not produce a new color output after canceled off");
                foreach (Color color in f.Transport.Colors[1])
                    Check(color.R > color.G && color.R > color.B, "fresh red recovery emitted old or non-red ambient colors");
            }
        }
        private static void CancelOffByPause()
        {
            using (Fixture f = Fixture.Running()) {
                f.Transport.BeforeStandbyOnce = delegate { f.Engine.Paused = true; };
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 0 && f.Transport.CanceledStandbys == 1, "manual pause did not cancel off at serialized authorization");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05);
                NoWake(f, "manual pause after canceled off");
                Check(!f.Engine.State.AutomaticStandby && f.Transport.EnableEntries == 1, "canceled off retained automatic wake after pause");
            }
        }
        private static void CancelWakeByOff()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red);
                f.Transport.BeforeWakeOnce = delegate { f.Engine.SetDisplayPowerState(0); };
                f.Step(.05);
                Check(f.Transport.WakeEntries == 1 && f.Transport.SuccessfulWakes == 0 && f.Transport.CanceledWakes == 1, "display off did not cancel in-flight wake");
                Check(f.Transport.Colors.Count == 1 && !f.Engine.State.Sending, "canceled wake streamed colors");
                int captures = f.Capture.Calls; f.Step(.05);
                Check(f.Capture.Calls == captures && f.Transport.SuccessfulWakes == 0 && f.Transport.SuccessfulStandbys == 2,
                    "renewed display-off after canceled wake did not send off again without capture or wake");
                f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 2 && f.Capture.Calls == captures && f.Transport.SuccessfulWakes == 0,
                    "duplicate display-off after canceled wake repeated off or resumed capture/wake");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(1.1);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending, "fresh later display-on did not recover canceled wake");
            }
        }
        private static void NoOwnedInitialState()
        {
            using (Fixture f = new Fixture(false)) {
                f.Step(); f.Engine.SetDisplayPowerState(0); f.Step(.05);
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(11);
                Check(f.Transport.EnableEntries == 0 && f.Transport.StandbyEntries == 0 && f.Transport.WakeEntries == 0 &&
                    f.Transport.Releases == 0 && f.Transport.Colors.Count == 0, "initially paused instance sent control without owning activation");
                Check(f.Capture.Calls == 0, "initially paused instance captured");
            }
            using (Fixture f = new Fixture(true)) {
                f.Capture.Missing(); f.Step(); f.Step(11);
                Check(f.Transport.EnableEntries == 0 && f.Transport.StandbyEntries == 0, "no first picture powered off an unowned strip");
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.StandbyEntries == 0 && f.Transport.WakeEntries == 0 && f.Transport.Colors.Count == 0, "display off controlled an instance with no activation");
            }
        }
        private static void ZoneCanceledByStandby()
        {
            using (Fixture f = new Fixture(false)) {
                f.Engine.TestRawZone(2); f.Step();
                Check(f.Transport.Colors.Count == 1 && f.Engine.State.Sending && f.Engine.State.LastColorWasTest, "fixture did not identify a paused zone");
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                Check(f.Transport.SuccessfulStandbys == 1 && !f.Engine.State.Sending, "display off did not stop owned zone identification");
                f.Engine.TestRawZone(3); f.Step(.05);
                Check(f.Transport.Colors.Count == 1 && f.Transport.SuccessfulWakes == 0, "blocked zone request rekindled lights");
                f.Engine.SetDisplayPowerState(1); f.Capture.Present(Color.Red); f.Step(.05); f.Step(8.1);
                Check(f.Transport.SuccessfulWakes == 0 && f.Transport.Colors.Count == 1 && !f.Engine.State.Sending, "canceled paused zone woke or emitted on expiry");
            }
        }
        private static void ActiveZoneCanceledByStandby()
        {
            using (Fixture f = Fixture.Running()) {
                f.Engine.TestRawZone(2); f.Step(.05);
                Check(f.Engine.State.LastColorWasTest, "fixture did not send the zone highlight");
                f.Engine.SetDisplayPowerState(0); f.Step(.05);
                int colors = f.Transport.Colors.Count; f.Engine.TestRawZone(3); f.Step(.05);
                Check(f.Transport.Colors.Count == colors, "zone identification was accepted while display off");
                f.Engine.SetDisplayPowerState(1); f.Capture.Cached(); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 0, "test history woke display standby without a fresh image");
                f.Capture.Present(Color.FromArgb(15, 25, 180)); f.Step(.05);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending && !f.Engine.State.LastColorWasTest, "wake resumed canceled identification instead of ambient output");
                f.Step(8.1);
                Check(f.Transport.SuccessfulWakes == 1 && f.Engine.State.Sending && !f.Engine.State.LastColorWasTest, "old identification expiry interrupted or rekindled output");
            }
        }
        private static AppSettings Config(bool autoSync)
        {
            return new AppSettings { DeviceId = FakeTransport.Id, DeviceIp = "", Monitor = "FAKE-DISPLAY", AutoSync = autoSync,
                StartWithWindows = false, AreaMode = "Cinema16x9", ZoneCount = 18, EdgePercent = 20, Brightness = 80,
                Saturation = 140, SmoothMs = 75, Fps = 24, IgnoreBlackPixels = true, Rotation = 15, Reverse = false, StreamFlag = false };
        }
        private sealed class Clock
        {
            public double Seconds = 100, AdvanceAtNextUtc;
            public DateTime UtcNow {
                get {
                    double advance = AdvanceAtNextUtc; AdvanceAtNextUtc = 0; Seconds += advance;
                    return new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds);
                }
            }
            public void Advance(double seconds) { Seconds += seconds; }
        }
        private sealed class FakeCapture : IEngineCapture, IEngineCaptureMetadata
        {
            private CaptureFrame frame;
            private bool fresh = true, missing, reprocessed;
            private string error = "";
            public int Calls, Disposals;
            public Action BeforeReturnOnce;
            public FakeCapture() { Present(Color.FromArgb(40, 90, 120)); }
            public bool HasNewFrame { get; private set; }
            public bool HasFreshDesktopFrame { get; private set; }
            public bool IsReprocessedFrame { get; private set; }
            public bool ProtectedContentMasked { get { return false; } }
            public string Description { get { return "Synthetic managed capture"; } }
            public string LastError { get { return error; } }
            public void Present(Color color)
            {
                byte[] pixels = new byte[256 * 107 * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
                frame = new CaptureFrame(256, 107, pixels); fresh = true; missing = false; reprocessed = false; error = "";
            }
            public void Fail(string message) { error = message; missing = false; fresh = false; reprocessed = false; }
            public void Missing() { missing = true; error = ""; fresh = false; reprocessed = false; }
            public void Cached() { error = ""; missing = false; fresh = false; reprocessed = false; }
            public void Reprocess() { error = ""; missing = false; fresh = false; reprocessed = true; }
            public CaptureFrame Capture(Rectangle bounds)
            {
                Calls++; HasFreshDesktopFrame = !missing && error.Length == 0 && fresh;
                IsReprocessedFrame = !missing && error.Length == 0 && reprocessed;
                HasNewFrame = HasFreshDesktopFrame || IsReprocessedFrame;
                fresh = false; reprocessed = false;
                Action callback = BeforeReturnOnce; BeforeReturnOnce = null; if (callback != null) callback();
                return missing ? null : frame;
            }
            // Factory reuse is deliberate: no native resources are created in this harness.
            public void Dispose() { Disposals++; }
        }
        private sealed class FakeTransport : IEngineTransport, IEngineStandbyTransport
        {
            public const string Id = "TEST-G1";
            private readonly Clock clock;
            private DeviceInfo target;
            public int EnableEntries, StandbyEntries, WakeEntries, SuccessfulStandbys, SuccessfulWakes, CanceledStandbys, CanceledWakes, Releases, Cancels;
            public int FailStandbyCount, FailWakeCount, FailedWakes;
            public Action BeforeStandbyOnce, BeforeWakeOnce;
            private string standbyId = "";
            public readonly List<Color[]> Colors = new List<Color[]>();
            public readonly List<double> StandbyTimes = new List<double>();
            public readonly List<double> WakeTimes = new List<double>();
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public FakeTransport(Clock value) { clock = value; }
            public int Generation { get { return 1; } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, Generation); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public void Start() { }
            public void Discover() { PublishFresh(); }
            public void Poll() { PublishFresh(); }
            public void SetTarget(DeviceInfo value) { target = value == null ? null : value.Copy(); }
            private void PublishFresh()
            {
                DeviceInfo reply = target == null ? new DeviceInfo() : target.Copy();
                reply.Id = Id; reply.Sku = "H6609"; reply.Ip = "192.0.2.44"; reply.LastSeenUtc = clock.UtcNow; reply.IdentityGeneration = Generation;
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            private static void Authorized(Func<bool> authorize)
            { if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic serialized authorization canceled"); }
            public void Enable(EndpointToken endpoint, Func<bool> authorize)
            { EnableEntries++; Authorized(authorize); }
            public void Disable(EndpointToken endpoint) { Releases++; }
            public void Standby(EndpointToken endpoint, Func<bool> authorize)
            {
                StandbyEntries++; StandbyTimes.Add(clock.Seconds);
                Action callback = BeforeStandbyOnce; BeforeStandbyOnce = null; if (callback != null) callback();
                try { Authorized(authorize); }
                catch (OperationCanceledException) { CanceledStandbys++; throw; }
                standbyId = endpoint.Id;
                if (FailStandbyCount > 0) { FailStandbyCount--; throw new InvalidOperationException("Synthetic standby send failure"); }
                SuccessfulStandbys++;
            }
            public void Wake(EndpointToken endpoint, Func<bool> authorize)
            {
                WakeEntries++; WakeTimes.Add(clock.Seconds);
                Action callback = BeforeWakeOnce; BeforeWakeOnce = null; if (callback != null) callback();
                try { Authorized(authorize); }
                catch (OperationCanceledException) { CanceledWakes++; throw; }
                if (FailWakeCount > 0) { FailWakeCount--; FailedWakes++; throw new InvalidOperationException("Synthetic wake send failure"); }
                SuccessfulWakes++;
            }
            public bool HasStandby(string deviceId) { return String.Equals(standbyId, deviceId, StringComparison.OrdinalIgnoreCase); }
            public void CancelStandby() { Cancels++; standbyId = ""; }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize)
            {
                Authorized(authorize);
                Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "production packet builder yielded no output");
                Colors.Add((Color[])colors.Clone());
            }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly FakeCapture Capture = new FakeCapture();
            public readonly FakeTransport Transport;
            public readonly GlowEngine Engine;
            public Rectangle Bounds = new Rectangle(0, 0, 3440, 1440);
            public bool Competitor;
            public Fixture(bool autoSync)
            {
                Transport = new FakeTransport(Clock);
                Engine = new GlowEngine(Config(autoSync), new EngineServices { Transport = Transport, CreateCapture = delegate { return Capture; },
                    UtcNow = delegate { return Clock.UtcNow; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return Bounds; }, CompetitorRunning = delegate { return Competitor; },
                    Log = delegate(string ignored) { }, ListenSystemEvents = false });
            }
            public static Fixture Running()
            {
                Fixture f = new Fixture(true); f.Step();
                Check(f.Engine.State.Sending && f.Transport.EnableEntries == 1 && f.Transport.Colors.Count == 1, "fixture initial stream did not activate once");
                return f;
            }
            public void Step() { Engine.Step(); }
            public void Step(double seconds) { Clock.Advance(seconds); Engine.Step(); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
