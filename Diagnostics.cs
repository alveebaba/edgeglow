using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace EdgeGlow
{
    internal static class Diagnostics
    {
        private static double Percentile(List<double> sorted, double percentile) { return sorted[Math.Max(0, Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * percentile) - 1))]; }
        public static void Run(bool includeLan = true)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("EdgeGlow local diagnostics " + DateTime.UtcNow.ToString("o"));
            try
            {
                report.AppendLine(ProtocolTests.Run()); report.AppendLine(CaptureTests.Run());
                foreach (Screen s in Screen.AllScreens) report.AppendLine(s.DeviceName + " " + s.Bounds + " primary=" + s.Primary);
                using (ScreenSampler sampler = new ScreenSampler())
                {
                    long memory = GC.GetTotalMemory(false);
                    ActiveAreaDetector detector = new ActiveAreaDetector();
                    Rectangle active = Rectangle.Empty; CaptureFrame frame = null;
                    int fresh = 0, missing = 0, idle = 0, errors = 0, protectedResults = 0, reprocessed = 0; double totalWork = 0, maxWork = 0;
                    List<double> warmFreshWork = new List<double>();
                    AppSettings settings = Storage.Load();
                    Screen chosen = Screen.PrimaryScreen;
                    foreach (Screen display in Screen.AllScreens) if (display.DeviceName == settings.Monitor) chosen = display;
                    report.AppendLine("Configured capture source " + chosen.DeviceName + " " + chosen.Bounds + "; area mode " + settings.AreaMode);
                    for (int i = 0; i < 150; i++)
                    {
                        Stopwatch work = Stopwatch.StartNew();
                        frame = sampler.Capture(chosen.Bounds);
                        bool usable = frame != null && String.IsNullOrEmpty(sampler.LastError);
                        if (!usable) {
                            if (frame == null) missing++;
                            if (!String.IsNullOrEmpty(sampler.LastError)) errors++;
                            if (sampler.ProtectedContentMasked) protectedResults++;
                            detector.ClearPendingEvidence();
                        }
                        else
                        {
                            if (sampler.HasFreshDesktopFrame) fresh++;
                            else if (sampler.IsReprocessedFrame) reprocessed++;
                            else idle++;
                            active = detector.Update(frame, settings.AreaMode);
                            ColorProcessor.Sample(frame, active, settings.EdgePercent / 100.0, settings.Brightness / 100.0, settings.Saturation / 100.0, settings.ZoneCount, settings.IgnoreBlackPixels);
                        }
                        double ms = work.Elapsed.TotalMilliseconds; totalWork += ms; maxWork = Math.Max(ms, maxWork);
                        if (usable && sampler.HasFreshDesktopFrame && fresh > 3) warmFreshWork.Add(ms);
                        Thread.Sleep((int)Math.Max(1, 1000.0 / 24 - ms));
                    }
                    report.AppendLine("GPU capture/detect/sample 150 attempts: " + (totalWork / 150).ToString("F2") + " ms work/attempt, max " + maxWork.ToString("F2") + " ms");
                    report.AppendLine("Fresh desktop images " + fresh + "; healthy idle " + idle + "; metadata reprocessed " + reprocessed + "; missing images " + missing + "; errors " + errors + "; protected results " + protectedResults);
                    report.AppendLine("HDR " + sampler.IsHdr + "; conversion " + sampler.ConversionMode + "; texture format " + sampler.NativeTextureFormat + "; output color space " + sampler.NativeOutputColorSpace + "; SDR white nits " + sampler.SdrWhiteLevelNits + "; capture generation " + sampler.CaptureGeneration + "; image generation " + sampler.ImageGeneration + "; LastError " + sampler.LastError);
                    warmFreshWork.Sort();
                    report.AppendLine(warmFreshWork.Count == 0 ? "No warm fresh-image timings available; idle reuse is not a moving-video benchmark." :
                        "Warm fresh-image work (first three fresh images excluded): n=" + warmFreshWork.Count + "; p50=" + Percentile(warmFreshWork, .50).ToString("F2") + "ms; p95=" + Percentile(warmFreshWork, .95).ToString("F2") + "ms; p99=" + Percentile(warmFreshWork, .99).ToString("F2") + "ms.");
                    report.AppendLine("This diagnostic checks capture/detect/sample only. Engine integration and physical lighting acceptance are separate tests.");
                    if (frame != null && String.IsNullOrEmpty(sampler.LastError)) report.AppendLine("Downsample " + frame.Width + "x" + frame.Height + "; active " + active + "; managed memory delta " + (GC.GetTotalMemory(false) - memory));
                    else throw new InvalidOperationException("No valid captured frame.");
                    report.AppendLine("No screenshots or pixel data were written. No lighting commands sent.");
                }
                if (includeLan)
                using (GoveeLan lan = new GoveeLan())
                {
                    object sync = new object();
                    lan.DeviceSeen += delegate(DeviceInfo d) { lock (sync) report.AppendLine("LAN reply: " + d.Sku + " " + d.Ip + " " + d.Id); };
                    lan.Error += delegate(string error) { lock (sync) report.AppendLine("LAN: " + error); };
                    lan.Start(); for (int i = 0; i < 3; i++) { lan.Discover(); Thread.Sleep(1800); }
                }
            }
            catch (Exception ex) { report.AppendLine("ERROR: " + ex.ToString()); Environment.ExitCode = 1; }
            Directory.CreateDirectory(Storage.DirectoryPath); File.WriteAllText(Path.Combine(Storage.DirectoryPath, includeLan ? "diagnostics.txt" : "capture-test.txt"), report.ToString());
        }
    }
}
