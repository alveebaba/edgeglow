using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;

namespace EdgeGlow
{
    // Output calibration is tested against independent encoded-byte arithmetic.
    // Engine checks inject pixels/time/transport and never open the owner app or LAN.
    public static class OutputGammaTests
    {
        private static int passed, failed, assertions;
        private static string isolatedSettingsDirectory;
        public static int Main(string[] args)
        {
            isolatedSettingsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-gamma-settings");
            Run("Neutral gamma preserves all existing sampling overloads and packets", NeutralCompatibility);
            Run("Gamma follows rounded brightness and saturation output", EncodedByteOrder);
            Run("Warm and deep red reduce secondary light while pure red remains pure", RedExamples);
            Run("Gamma preserves endpoints and never raises an encoded channel", EndpointsAndMonotonicity);
            Run("Gamma bounds and nonfinite values have a neutral safe fallback", GammaBounds);
            Run("Concurrent gamma samples keep the requested calibration", ConcurrentGammaSamples);
            Run("Old JSON, validation and copies preserve neutral default and calibration", SettingsCompatibility);
            Run("Serialized calibration round-trips through isolated settings loading", IsolatedSettingsRoundTrip);
            Run("Engine applies gamma before smoothing and physical zone mapping", EngineOrderAndMapping);
            Console.WriteLine("Output gamma regression: " + passed + " passed, " + failed + " failed, " + assertions + " assertions.");
            Console.WriteLine("Evidence qualification: production managed color/settings/engine code with synthetic pixels, fake clock and in-memory transport; serializer files are isolated beside this test executable. No owner settings, GUI, native capture, LAN, startup or physical color acceptance operation.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void Check(bool value, string reason)
        { System.Threading.Interlocked.Increment(ref assertions); if (!value) throw new InvalidOperationException(reason); }
        private static void EqualColor(Color actual, Color expected, string reason)
        { Check(actual.ToArgb() == expected.ToArgb(), reason + ": got " + actual + ", expected " + expected); }
        private static void EqualColors(Color[] actual, Color[] expected, string reason)
        {
            Check(actual != null && actual.Length == expected.Length, reason + " length");
            for (int i = 0; i < actual.Length; i++) EqualColor(actual[i], expected[i], reason + " zone " + i);
        }
        private static void EqualBytes(byte[] actual, byte[] expected, string reason)
        {
            Check(actual.Length == expected.Length, reason + " length");
            for (int i = 0; i < actual.Length; i++) Check(actual[i] == expected[i], reason + " byte " + i);
        }
        private static CaptureFrame Solid(Color color)
        {
            byte[] pixels = new byte[24 * 12 * 4];
            for (int i = 0; i < pixels.Length; i += 4) {
                pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
            }
            return new CaptureFrame(24, 12, pixels);
        }
        private static CaptureFrame Pattern(int variant)
        {
            byte[] pixels = new byte[60 * 30 * 4];
            for (int y = 0; y < 30; y++) for (int x = 0; x < 60; x++) {
                int i = (y * 60 + x) * 4;
                pixels[i] = (byte)((x * 11 + y * 3 + variant * 61) % 256);
                pixels[i + 1] = (byte)((x * 3 + y * 13 + variant * 31) % 256);
                pixels[i + 2] = (byte)((x * 7 + y * 5 + variant * 97) % 256);
                if ((x + 3 * y) % 9 == 0) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                pixels[i + 3] = 255;
            }
            return new CaptureFrame(60, 30, pixels);
        }
        private static Rectangle Full(CaptureFrame frame) { return new Rectangle(0, 0, frame.Width, frame.Height); }
        private static Color[] Sample(CaptureFrame frame, int zones, bool ignoreDark, double brightness, double saturation, double gamma)
        { return ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones, ignoreDark, gamma); }
        private static int Byte(double value) { return (int)Math.Round(Math.Max(0, Math.Min(255, value))); }
        private static int GammaByte(int encoded, double gamma)
        {
            if (Double.IsNaN(gamma) || Double.IsInfinity(gamma)) gamma = 1;
            gamma = Math.Max(1, Math.Min(2.2, gamma));
            return gamma == 1 ? encoded : Byte(255 * Math.Pow(encoded / 255.0, gamma));
        }
        private static Color GammaColor(Color encoded, double gamma)
        { return Color.FromArgb(GammaByte(encoded.R, gamma), GammaByte(encoded.G, gamma), GammaByte(encoded.B, gamma)); }
        private static Color BeforeGamma(Color source, double brightness, double saturation)
        {
            double light = source.R * .2126 + source.G * .7152 + source.B * .0722;
            return Color.FromArgb(Byte((light + (source.R - light) * saturation) * brightness),
                Byte((light + (source.G - light) * saturation) * brightness),
                Byte((light + (source.B - light) * saturation) * brightness));
        }
        private static void NeutralCompatibility()
        {
            List<byte> historicalComparison = new List<byte>();
            CaptureFrame[] frames = { Solid(Color.Black), Solid(Color.White), Solid(Color.Red), Solid(Color.FromArgb(1, 7, 20)),
                Solid(Color.FromArgb(200, 80, 20)), Pattern(0), Pattern(1) };
            foreach (CaptureFrame frame in frames)
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (double brightness in new double[] { .37, 1 })
            foreach (double saturation in new double[] { .5, 1, 1.8 }) {
                Color[] previous = ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones, ignoreDark);
                Color[] neutral = Sample(frame, zones, ignoreDark, brightness, saturation, 1);
                foreach (Color color in neutral) {
                    historicalComparison.Add(color.R); historicalComparison.Add(color.G);
                    historicalComparison.Add(color.B); historicalComparison.Add(color.A);
                }
                EqualColors(neutral, previous, "neutral legacy sampling compatibility");
                EqualBytes(GoveeLan.BuildFrame(neutral, false), GoveeLan.BuildFrame(previous, false), "neutral wire compatibility");
                if (!ignoreDark) {
                    EqualColors(ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones), neutral, "six-argument overload compatibility");
                    if (zones == 10) EqualColors(ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation), neutral, "five-argument overload compatibility");
                }
            }
            // Golden output was independently compiled from committed Capture.cs
            // at 02cca894979f43b06a807408524b6c76d4e4703d before gamma existed,
            // using the same generated inputs and ordered RGBA output bytes.
            string digest = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(historicalComparison.ToArray()))
                .Replace("-", "").ToLowerInvariant();
            Check(historicalComparison.Count == 9408, "historical neutral comparison input/output count changed");
            Check(digest == "ffd72ff5afffef007e41ced49aa899883e4c1e76e7d3c5ba28b472f374a1da1a",
                "neutral output differs from independently compiled pre-gamma production output");
        }
        private static void EncodedByteOrder()
        {
            Color[] sources = { Color.FromArgb(191, 67, 13), Color.FromArgb(80, 5, 0), Color.FromArgb(40, 110, 210), Color.FromArgb(129, 129, 129) };
            bool discriminatesOrder = false;
            foreach (Color source in sources)
            foreach (double brightness in new double[] { .37, .8, 1 })
            foreach (double saturation in new double[] { .5, 1.4, 1.8 })
            foreach (double gamma in new double[] { 1.2, 1.6, 2.2 }) {
                Color expected = GammaColor(BeforeGamma(source, brightness, saturation), gamma);
                Color incorrect = BeforeGamma(GammaColor(source, gamma), brightness, saturation);
                if (expected.ToArgb() != incorrect.ToArgb()) discriminatesOrder = true;
                foreach (Color actual in Sample(Solid(source), 18, false, brightness, saturation, gamma))
                    EqualColor(actual, expected, "gamma must follow rounded brightness/saturation bytes");
            }
            Check(discriminatesOrder, "chosen color cases do not distinguish gamma before versus after existing output adjustment");
        }
        private static void RedExamples()
        {
            foreach (Color source in new Color[] { Color.FromArgb(200, 80, 20), Color.FromArgb(80, 5, 0) }) {
                Color corrected = Sample(Solid(source), 18, true, 1, 1, 1.6)[0];
                EqualColor(corrected, GammaColor(source, 1.6), "independent warm/deep red arithmetic");
                Check(corrected.R < source.R && corrected.G <= source.G && corrected.B <= source.B, "red calibration failed to lower nonmaximum encoded channels");
                Check(corrected.R > corrected.G && corrected.G >= corrected.B, "red example reversed channel dominance");
            }
            EqualColor(Sample(Solid(Color.Red), 18, true, 1, 1, 2.2)[0], Color.Red, "pure red endpoint");
            Color partialRed = Color.FromArgb(80, 0, 0);
            EqualColor(Sample(Solid(partialRed), 18, false, 1, 1, 1.6)[0], GammaColor(partialRed, 1.6), "partial pure red");
        }
        private static void EndpointsAndMonotonicity()
        {
            foreach (double gamma in new double[] { 1, 1.2, 1.6, 2.2 }) {
                EqualColor(Sample(Solid(Color.Black), 18, false, 1, 1, gamma)[0], Color.Black, "black endpoint");
                EqualColor(Sample(Solid(Color.White), 18, true, 1, 1, gamma)[0], Color.White, "white endpoint");
                EqualColor(Sample(Solid(Color.White), 18, false, 0, 1, gamma)[0], Color.Black, "zero brightness endpoint");
                int previousR = -1;
                for (int channel = 0; channel <= 255; channel++) {
                    Color source = Color.FromArgb(channel, 255 - channel, channel * 73 % 256);
                    Color actual = Sample(Solid(source), 18, false, 1, 1, gamma)[0];
                    EqualColor(actual, GammaColor(source, gamma), "full encoded-byte ramp");
                    Check(actual.R <= source.R && actual.G <= source.G && actual.B <= source.B, "gamma above one increased a channel");
                    Check(actual.R >= previousR, "red encoded ramp lost monotonicity"); previousR = actual.R;
                }
            }
        }
        private static void GammaBounds()
        {
            CaptureFrame frame = Pattern(1);
            Color[] neutral = Sample(frame, 18, true, .8, 1.4, 1);
            foreach (double gamma in new double[] { -1, 0, .99, Double.NaN, Double.NegativeInfinity, Double.PositiveInfinity })
                EqualColors(Sample(frame, 18, true, .8, 1.4, gamma), neutral, "invalid or below-neutral gamma fallback");
            EqualColors(Sample(frame, 18, true, .8, 1.4, 9), Sample(frame, 18, true, .8, 1.4, 2.2), "gamma upper bound");
        }
        private static void SettingsCompatibility()
        {
            Check(new AppSettings().ColorGammaPercent == 100, "new settings are not gamma neutral");
            for (int percentage = 100; percentage <= 220; percentage++) {
                AppSettings settings = new AppSettings { ColorGammaPercent = percentage }; settings.Validate();
                Check(settings.ColorGammaPercent == percentage && settings.Copy().ColorGammaPercent == percentage,
                    "valid integer gamma calibration changed at " + percentage);
            }
            AppSettings old = new JavaScriptSerializer().Deserialize<AppSettings>("{\"Brightness\":77,\"Saturation\":140,\"ZoneCount\":18,\"Rotation\":15}");
            old.Validate(); Check(old.ColorGammaPercent == 100 && old.Brightness == 77 && old.Saturation == 140 && old.Rotation == 15,
                "missing gamma field changed legacy calibrated settings");
            foreach (int invalid in new int[] { Int32.MinValue, -1, 0, 99 }) {
                AppSettings settings = new AppSettings { ColorGammaPercent = invalid }; settings.Validate();
                Check(settings.ColorGammaPercent == 100, "gamma lower setting bound");
            }
            foreach (int invalid in new int[] { 221, Int32.MaxValue }) {
                AppSettings settings = new AppSettings { ColorGammaPercent = invalid }; settings.Validate();
                Check(settings.ColorGammaPercent == 220, "gamma upper setting bound");
            }
            AppSettings calibrated = new AppSettings { ColorGammaPercent = 171, Brightness = 80, ZoneCount = 18, Rotation = 15 };
            calibrated.Validate(); AppSettings copy = calibrated.Copy();
            Check(copy.ColorGammaPercent == 171 && copy.Brightness == 80 && copy.Rotation == 15, "settings copy lost calibration");
            copy.ColorGammaPercent = 190; Check(calibrated.ColorGammaPercent == 171, "settings copy shares mutable gamma state");
        }
        private static void IsolatedSettingsRoundTrip()
        {
            Directory.CreateDirectory(isolatedSettingsDirectory);
            string canonical = Path.GetFullPath(isolatedSettingsDirectory);
            Check(!canonical.StartsWith(Path.GetFullPath(Storage.DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "isolated serializer fixture unexpectedly points into owner settings directory");
            string oldPath = Path.Combine(isolatedSettingsDirectory, "old-settings.json");
            File.WriteAllText(oldPath, "{\"Brightness\":77,\"ZoneCount\":18,\"Rotation\":15,\"AutoSync\":true}");
            string warning; AppSettings old = Storage.LoadFrom(oldPath, out warning);
            Check(warning == "" && old.ColorGammaPercent == 100 && old.Brightness == 77 && old.AutoSync, "actual settings loader changed old JSON gamma default");
            AppSettings selected = old.Copy(); selected.ColorGammaPercent = 171; selected.Saturation = 145; selected.SmoothMs = 75;
            string newPath = Path.Combine(isolatedSettingsDirectory, "serialized-settings.json");
            // Same serializer contract as Storage.Save; intentionally no call to
            // that hardwired owner-settings mutation API in this regression.
            string serialized = new JavaScriptSerializer().Serialize(selected);
            File.WriteAllText(newPath, serialized);
            Check(serialized.Contains("\"ColorGammaPercent\":171"), "serialization omitted gamma calibration");
            AppSettings loaded = Storage.LoadFrom(newPath, out warning);
            Check(warning == "" && loaded.ColorGammaPercent == 171 && loaded.Saturation == 145 && loaded.SmoothMs == 75 && loaded.Rotation == 15,
                "isolated serialize/load round-trip lost calibrated settings");
        }
        private static void ConcurrentGammaSamples()
        {
            CaptureFrame frame = Solid(Color.FromArgb(191, 67, 13));
            System.Threading.Thread[] workers = new System.Threading.Thread[6];
            Exception failure = null;
            for (int i = 0; i < workers.Length; i++) {
                int workerIndex = i;
                workers[i] = new System.Threading.Thread(delegate() {
                    try {
                        for (int iteration = 0; iteration < 200; iteration++) {
                            double gamma = (100 + (workerIndex * 37 + iteration * 13) % 121) / 100.0;
                            Color expected = GammaColor(Color.FromArgb(191, 67, 13), gamma);
                            foreach (Color actual in Sample(frame, 18, false, 1, 1, gamma))
                                EqualColor(actual, expected, "concurrent calibration " + gamma);
                        }
                    } catch (Exception ex) { System.Threading.Interlocked.CompareExchange(ref failure, ex, null); }
                });
                workers[i].IsBackground = true; workers[i].Start();
            }
            foreach (System.Threading.Thread worker in workers) Check(worker.Join(10000), "concurrent gamma sample worker did not finish");
            if (failure != null) throw new InvalidOperationException("concurrent gamma profile changed", failure);
        }
        private static void EngineOrderAndMapping()
        {
            int orderWitnesses = 0;
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool reverse in new bool[] { false, true }) {
                AppSettings config = new AppSettings { DeviceId = "GAMMA-TEST", Monitor = "FAKE-DISPLAY", AutoSync = true, AreaMode = "Full",
                    ZoneCount = zones, Rotation = zones == 18 ? 15 : 3, Reverse = reverse, EdgePercent = 20, Brightness = 80,
                    Saturation = 140, SmoothMs = 150, ColorGammaPercent = 160, Fps = 24, IgnoreBlackPixels = true };
                CaptureFrame first = Pattern(0), second = Pattern(1);
                Color[] rawFirst = ColorProcessor.Sample(first, Full(first), .2, .8, 1.4, zones, true);
                Color[] rawSecond = ColorProcessor.Sample(second, Full(second), .2, .8, 1.4, zones, true);
                Color[] correctedFirst = Corrected(rawFirst, 1.6), correctedSecond = Corrected(rawSecond, 1.6);
                using (Fixture fixture = new Fixture(config, first)) {
                    fixture.Step(0);
                    EqualColors(fixture.Engine.State.Colors, correctedFirst, "engine initial corrected logical colors");
                    EqualColors(fixture.Transport.Last, Mapped(correctedFirst, config), "engine initial physical mapping");
                    fixture.Capture.Present(second); fixture.Step(.15);
                    Color[] expected = Blend(correctedFirst, correctedSecond, 1 - Math.Exp(-1));
                    Color[] wrongOrder = Corrected(Blend(rawFirst, rawSecond, 1 - Math.Exp(-1)), 1.6);
                    for (int i = 0; i < zones; i++) if (expected[i].ToArgb() != wrongOrder[i].ToArgb()) orderWitnesses++;
                    EqualColors(fixture.Engine.State.Colors, expected, "engine gamma before temporal smoothing");
                    EqualColors(fixture.Transport.Last, Mapped(expected, config), "engine smoothed physical mapping");
                    AppSettings changed = config.Copy(); changed.ColorGammaPercent = 190; changed.SmoothMs = 0;
                    fixture.Engine.Update(changed); fixture.Step(.15);
                    Color[] expectedChange = Corrected(rawSecond, 1.9);
                    Check(!fixture.Capture.HasNewFrame, "setting-update fixture accidentally supplied fresh pixels");
                    EqualColors(fixture.Engine.State.Colors, expectedChange, "cached image reapplied changed gamma setting");
                    EqualColors(fixture.Transport.Last, Mapped(expectedChange, changed), "changed gamma physical mapping");
                    Check(fixture.Engine.State.CaptureErrors == 0 && fixture.Engine.State.FreshFrames == 2,
                        "output calibration introduced capture errors or false fresh images");
                }
            }
            Check(orderWitnesses > 0, "engine cases did not distinguish gamma before versus after smoothing");
        }
        private static Color[] Corrected(Color[] source, double gamma)
        {
            Color[] colors = new Color[source.Length];
            for (int i = 0; i < source.Length; i++) colors[i] = GammaColor(source[i], gamma);
            return colors;
        }
        private static Color[] Blend(Color[] from, Color[] to, double alpha)
        {
            Color[] colors = new Color[from.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.FromArgb(Byte(from[i].R + (to[i].R - from[i].R) * alpha),
                Byte(from[i].G + (to[i].G - from[i].G) * alpha), Byte(from[i].B + (to[i].B - from[i].B) * alpha));
            return colors;
        }
        private static Color[] Mapped(Color[] logical, AppSettings config)
        {
            // Independently map logical -> wire (the inverse direction to the
            // production wire -> logical indexing function).
            Color[] wire = new Color[logical.Length];
            for (int logicalIndex = 0; logicalIndex < logical.Length; logicalIndex++) {
                int wireIndex = config.Reverse ? config.Rotation - logicalIndex : logicalIndex - config.Rotation;
                wireIndex = (wireIndex % logical.Length + logical.Length) % logical.Length;
                wire[wireIndex] = logical[logicalIndex];
            }
            return wire;
        }
        private sealed class Clock
        {
            public double Seconds = 100;
            public DateTime Utc { get { return new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } }
        }
        private sealed class Pixels : IEngineCapture, IEngineCaptureMetadata
        {
            private CaptureFrame frame;
            private bool fresh;
            public Pixels(CaptureFrame initial) { Present(initial); }
            public void Present(CaptureFrame next) { frame = next; fresh = true; }
            public bool HasNewFrame { get; private set; }
            public bool HasFreshDesktopFrame { get { return HasNewFrame; } }
            public bool IsReprocessedFrame { get { return false; } }
            public bool ProtectedContentMasked { get { return false; } }
            public string Description { get { return "Synthetic encoded SDR pixels"; } }
            public string LastError { get { return ""; } }
            public CaptureFrame Capture(Rectangle bounds) { HasNewFrame = fresh; fresh = false; return frame; }
            public void Dispose() { }
        }
        private sealed class Transport : IEngineTransport
        {
            private readonly Clock clock;
            private DeviceInfo target;
            public readonly List<Color[]> Colors = new List<Color[]>();
            public Color[] Last { get { return Colors.Count == 0 ? null : Colors[Colors.Count - 1]; } }
            public Transport(Clock value) { clock = value; }
            public event Action<DeviceInfo> DeviceSeen;
            public event Action<string> Error { add { } remove { } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, 1); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public int Generation { get { return 1; } }
            public void Start() { }
            public void Discover() { Publish(); }
            public void Poll() { Publish(); }
            public void SetTarget(DeviceInfo selected) { target = selected == null ? null : selected.Copy(); }
            private void Publish()
            {
                DeviceInfo reply = new DeviceInfo { Id = "GAMMA-TEST", Sku = "H6609", Ip = "192.0.2.44", LastSeenUtc = clock.Utc, IdentityGeneration = 1 };
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            private static void Authorize(Func<bool> authorize)
            { if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic authorization canceled"); }
            public void Enable(EndpointToken endpoint, Func<bool> authorize) { Authorize(authorize); }
            public void Disable(EndpointToken endpoint) { }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize)
            { Authorize(authorize); Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "gamma packet is empty"); Colors.Add((Color[])colors.Clone()); }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly Pixels Capture;
            public readonly Transport Transport;
            public readonly GlowEngine Engine;
            public Fixture(AppSettings config, CaptureFrame initial)
            {
                Capture = new Pixels(initial); Transport = new Transport(Clock);
                Engine = new GlowEngine(config, new EngineServices { Transport = Transport, CreateCapture = delegate { return Capture; },
                    UtcNow = delegate { return Clock.Utc; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return new Rectangle(0, 0, 3440, 1440); },
                    CompetitorRunning = delegate { return false; }, Log = delegate(string ignored) { }, ListenSystemEvents = false });
            }
            public void Step(double seconds) { Clock.Seconds += seconds; Engine.Step(); }
            public void Dispose() { Engine.Dispose(); }
        }
    }
}
