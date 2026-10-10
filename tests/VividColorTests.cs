using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;

namespace EdgeGlow
{
    // Desired color behavior, legacy output compatibility and engine ordering.
    // Generated pixels and in-memory transport do not establish physical color.
    public static class VividColorTests
    {
        private static int passed, failed, assertions;
        public static int Main(string[] args)
        {
            Run("Disabled vivid preference preserves pre-feature output and overloads", DisabledCompatibility);
            Run("Red mixed with white or pale pixels lowers secondary components", RedMixtures);
            Run("Vivid preference cannot raise the existing encoded output peak", PeakBound);
            Run("Uniform white gray pastels and colors retain their output", UniformContent);
            Run("Grayscale mixtures remain grayscale and retain their level", GrayscaleContent);
            Run("Original black coverage and sparse-support fading remain effective", SparseBlackCoverage);
            Run("All-black areas never invent colored light", AllBlackContent);
            Run("Concurrent vivid and gamma profiles remain independent", ConcurrentProfiles);
            Run("Old settings remain opt-in and true preference copies and serializes", SettingsCompatibility);
            Run("Engine applies vivid preference before gamma smoothing and mapping", EngineOrdering);
            Console.WriteLine("Vivid color regression: " + passed + " passed, " + failed + " failed, " + assertions + " assertions.");
            Console.WriteLine("Evidence qualification: production managed color/settings/engine code with generated pixels, fake time and in-memory transport. Serializer fixtures are isolated beside the test executable. No owner settings, native capture, GUI, LAN, startup or physical color acceptance operation.");
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
        private static int Peak(Color color) { return Math.Max(color.R, Math.Max(color.G, color.B)); }
        private static int Byte(double value) { return (int)Math.Round(Math.Max(0, Math.Min(255, value))); }
        private static Color Gamma(Color color, double gamma)
        {
            return Color.FromArgb(Byte(255 * Math.Pow(color.R / 255.0, gamma)), Byte(255 * Math.Pow(color.G / 255.0, gamma)),
                Byte(255 * Math.Pow(color.B / 255.0, gamma)));
        }
        private static CaptureFrame Solid(Color color)
        {
            byte[] pixels = new byte[24 * 12 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
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
        private static CaptureFrame Mixed(Color first, Color second)
        {
            byte[] pixels = new byte[60 * 30 * 4];
            for (int y = 0; y < 30; y++) for (int x = 0; x < 60; x++) {
                Color color = (x + y) % 2 == 0 ? first : second;
                int i = (y * 60 + x) * 4; pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
            }
            return new CaptureFrame(60, 30, pixels);
        }
        private static Rectangle Full(CaptureFrame frame) { return new Rectangle(0, 0, frame.Width, frame.Height); }
        private static Color[] Sample(CaptureFrame frame, int zones, bool ignoreDark, double brightness, double saturation, double gamma, bool favor)
        { return ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones, ignoreDark, gamma, favor); }
        private static void DisabledCompatibility()
        {
            CaptureFrame[] frames = { Solid(Color.Black), Solid(Color.White), Solid(Color.Red), Solid(Color.FromArgb(1, 7, 20)),
                Solid(Color.FromArgb(200, 80, 20)), Pattern(0), Pattern(1) };
            List<byte> baselineBytes = new List<byte>();
            foreach (CaptureFrame frame in frames)
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (double brightness in new double[] { .37, 1 })
            foreach (double saturation in new double[] { .5, 1, 1.8 })
            foreach (double gamma in new double[] { 1, 1.6, 2.2 }) {
                Color[] old = ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones, ignoreDark, gamma);
                Color[] disabled = Sample(frame, zones, ignoreDark, brightness, saturation, gamma, false);
                EqualColors(disabled, old, "disabled vivid preference eight-argument compatibility");
                foreach (Color color in disabled) { baselineBytes.Add(color.R); baselineBytes.Add(color.G); baselineBytes.Add(color.B); baselineBytes.Add(color.A); }
                byte[] oldPacket = GoveeLan.BuildFrame(old, false), packet = GoveeLan.BuildFrame(disabled, false);
                Check(packet.Length == oldPacket.Length, "disabled wire length");
                for (int i = 0; i < packet.Length; i++) Check(packet[i] == oldPacket[i], "disabled wire byte changed");
                if (gamma == 1) {
                    EqualColors(disabled, ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones, ignoreDark), "legacy seven-argument compatibility");
                    if (!ignoreDark) {
                        EqualColors(disabled, ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation, zones), "legacy six-argument compatibility");
                        if (zones == 10) EqualColors(disabled, ColorProcessor.Sample(frame, Full(frame), .2, brightness, saturation), "legacy five-argument compatibility");
                    }
                }
            }
            // Independently compiled from the gamma-only production Capture.cs
            // SHA a83e7f329f1d212af69982aee3f860cfbe66f104935eda32b9da1fcabf813a51.
            string digest = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(baselineBytes.ToArray())).Replace("-", "").ToLowerInvariant();
            Check(baselineBytes.Count == 28224, "disabled baseline comparison count changed");
            Check(digest == "00c57408627272bf79bcaee6eee47cf0d64c73411ce9ea651719c31b8ab71e54", "disabled preference changed pre-feature encoded output");
        }
        private static void RedMixtures()
        {
            Color red = Color.FromArgb(200, 10, 0);
            foreach (Color pale in new Color[] { Color.FromArgb(200, 200, 200), Color.FromArgb(200, 130, 130) })
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (double saturation in new double[] { 1, 1.4 }) {
                CaptureFrame mixture = Mixed(red, pale);
                Color[] normal = Sample(mixture, zones, ignoreDark, .8, saturation, 1, false);
                Color[] favored = Sample(mixture, zones, ignoreDark, .8, saturation, 1, true);
                for (int i = 0; i < zones; i++) {
                    Check(favored[i].G < normal[i].G && favored[i].B < normal[i].B, "red/white or red/pale averaging retained diluted secondary components");
                    Check(favored[i].R > favored[i].G && favored[i].R > favored[i].B, "red mixture lost dominant red");
                    Check(Peak(favored[i]) <= Peak(normal[i]), "red preference amplified the encoded output peak");
                }
            }
        }
        private static void PeakBound()
        {
            foreach (CaptureFrame frame in new CaptureFrame[] { Pattern(0), Pattern(1), Mixed(Color.Red, Color.White), Mixed(Color.Blue, Color.LightGray) })
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (double brightness in new double[] { .4, .8, 1 })
            foreach (double saturation in new double[] { .5, 1.4, 1.8 })
            foreach (double gamma in new double[] { 1, 1.6, 2.2 }) {
                Color[] normal = Sample(frame, zones, ignoreDark, brightness, saturation, gamma, false);
                Color[] favored = Sample(frame, zones, ignoreDark, brightness, saturation, gamma, true);
                for (int i = 0; i < zones; i++) Check(Peak(favored[i]) <= Peak(normal[i]), "vivid profile increased peak after strength/brightness/gamma");
            }
        }
        private static void UniformContent()
        {
            foreach (Color color in new Color[] { Color.White, Color.FromArgb(8, 8, 8), Color.FromArgb(80, 80, 80), Color.FromArgb(200, 180, 180),
                Color.FromArgb(180, 200, 190), Color.FromArgb(200, 10, 0), Color.Red, Color.Blue })
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (double gamma in new double[] { 1, 1.6, 2.2 }) {
                CaptureFrame frame = Solid(color);
                EqualColors(Sample(frame, zones, ignoreDark, .8, 1.4, gamma, true), Sample(frame, zones, ignoreDark, .8, 1.4, gamma, false),
                    "uniform content must retain normal output");
            }
        }
        private static void GrayscaleContent()
        {
            foreach (CaptureFrame frame in new CaptureFrame[] { Mixed(Color.White, Color.Gray), Mixed(Color.FromArgb(20, 20, 20), Color.FromArgb(80, 80, 80)) })
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true }) {
                Color[] normal = Sample(frame, zones, ignoreDark, .8, 1.4, 1, false), favored = Sample(frame, zones, ignoreDark, .8, 1.4, 1, true);
                for (int i = 0; i < zones; i++) {
                    Check(favored[i].R == favored[i].G && favored[i].G == favored[i].B, "grayscale mixture invented a hue");
                    Check(Math.Abs(favored[i].R - normal[i].R) <= 1, "grayscale mixture changed light level");
                }
            }
        }
        private static void SparseBlackCoverage()
        {
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (int brightPixels in new int[] { 1, 2, 3, 8 }) {
                byte[] pixels = new byte[60 * 30 * 4];
                for (int i = 0; i < pixels.Length; i += 4) pixels[i + 3] = 255;
                for (int x = 0; x < brightPixels; x++) pixels[x * 4 + 2] = 200;
                CaptureFrame frame = new CaptureFrame(60, 30, pixels);
                Color[] normal = Sample(frame, zones, ignoreDark, 1, 1, 1, false), favored = Sample(frame, zones, ignoreDark, 1, 1, 1, true);
                EqualColors(favored, normal, "pure colored support among black must retain original coverage");
                int regionPixels = (60 / (zones == 18 ? 6 : 4)) * 6;
                double fraction = (double)brightPixels / regionPixels;
                int expected = Byte(200 * (ignoreDark ? Math.Min(1, fraction / .05) : fraction));
                EqualColor(favored[0], Color.FromArgb(expected, 0, 0), "independent top-zone black coverage reference");
                if (fraction < .05) Check(favored[0].R < 200, "a sparse colored pixel lit an entire zone at full intensity");
            }
        }
        private static void AllBlackContent()
        {
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool ignoreDark in new bool[] { false, true })
            foreach (double gamma in new double[] { 1, 1.6, 2.2 })
            foreach (Color color in Sample(Solid(Color.Black), zones, ignoreDark, 1, 1.8, gamma, true)) EqualColor(color, Color.Black, "all-black vivid output");
        }
        private static void ConcurrentProfiles()
        {
            CaptureFrame frame = Pattern(1);
            Color[][] references = new Color[8][];
            for (int i = 0; i < 8; i++) references[i] = Sample(frame, i < 4 ? 10 : 18, (i & 2) != 0, .8, 1.4, (i & 1) != 0 ? 1.6 : 1, (i & 1) != 0);
            System.Threading.Thread[] workers = new System.Threading.Thread[6]; Exception failure = null;
            for (int worker = 0; worker < workers.Length; worker++) {
                int index = worker;
                workers[worker] = new System.Threading.Thread(delegate() {
                    try {
                        for (int iteration = 0; iteration < 100; iteration++) {
                            int i = (index + iteration * 3) % 8;
                            EqualColors(Sample(frame, i < 4 ? 10 : 18, (i & 2) != 0, .8, 1.4, (i & 1) != 0 ? 1.6 : 1, (i & 1) != 0), references[i], "concurrent vivid/gamma profile");
                        }
                    } catch (Exception ex) { System.Threading.Interlocked.CompareExchange(ref failure, ex, null); }
                });
                workers[worker].IsBackground = true; workers[worker].Start();
            }
            foreach (System.Threading.Thread worker in workers) Check(worker.Join(10000), "concurrent vivid worker did not finish");
            if (failure != null) throw new InvalidOperationException("concurrent vivid profile output changed", failure);
        }
        private static void SettingsCompatibility()
        {
            Check(!new AppSettings().FavorVividColors, "new settings enabled vivid preference by default");
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-vivid-settings"); Directory.CreateDirectory(directory);
            Check(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Storage.DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "settings fixture points into owner settings");
            string oldPath = Path.Combine(directory, "old-settings.json");
            File.WriteAllText(oldPath, "{\"Brightness\":80,\"Saturation\":140,\"ColorGammaPercent\":160,\"Rotation\":15,\"ZoneCount\":18}");
            string warning; AppSettings old = Storage.LoadFrom(oldPath, out warning);
            Check(warning == "" && !old.FavorVividColors && old.ColorGammaPercent == 160 && old.Rotation == 15, "old JSON enabled vivid preference or changed calibration");
            AppSettings selected = old.Copy(); selected.FavorVividColors = true; selected.Validate();
            AppSettings copy = selected.Copy(); Check(copy.FavorVividColors && copy.ColorGammaPercent == 160, "settings copy lost vivid preference");
            copy.FavorVividColors = false; Check(selected.FavorVividColors, "copy changed the original vivid preference");
            string serialized = new JavaScriptSerializer().Serialize(selected), path = Path.Combine(directory, "serialized-settings.json");
            File.WriteAllText(path, serialized); AppSettings loaded = Storage.LoadFrom(path, out warning);
            Check(serialized.Contains("\"FavorVividColors\":true") && warning == "" && loaded.FavorVividColors && loaded.ColorGammaPercent == 160 && loaded.Saturation == 140,
                "isolated serialize/load lost the opt-in preference or calibration");
        }
        private static void EngineOrdering()
        {
            int vividWitnesses = 0, orderingWitnesses = 0;
            foreach (int zones in new int[] { 10, 18 })
            foreach (bool reverse in new bool[] { false, true }) {
                AppSettings config = new AppSettings { DeviceId = "VIVID-TEST", Monitor = "FAKE-DISPLAY", AutoSync = true, AreaMode = "Full",
                    ZoneCount = zones, Rotation = zones == 18 ? 15 : 3, Reverse = reverse, EdgePercent = 20, Brightness = 80,
                    Saturation = 140, SmoothMs = 150, ColorGammaPercent = 160, FavorVividColors = true, Fps = 24, IgnoreBlackPixels = true };
                CaptureFrame first = Mixed(Color.FromArgb(200, 10, 0), Color.FromArgb(200, 200, 200)), second = Pattern(1);
                Color[] firstPreGamma = Sample(first, zones, true, .8, 1.4, 1, true), secondPreGamma = Sample(second, zones, true, .8, 1.4, 1, true);
                Color[] correctedFirst = Corrected(firstPreGamma, 1.6), correctedSecond = Corrected(secondPreGamma, 1.6);
                using (Fixture fixture = new Fixture(config, first)) {
                    fixture.Step(0); EqualColors(fixture.Engine.State.Colors, correctedFirst, "engine vivid then gamma logical colors");
                    EqualColors(fixture.Transport.Last, Mapped(correctedFirst, config), "initial vivid physical mapping");
                    fixture.Capture.Present(second); fixture.Step(.15);
                    Color[] expected = Blend(correctedFirst, correctedSecond, 1 - Math.Exp(-1)), wrongOrder = Corrected(Blend(firstPreGamma, secondPreGamma, 1 - Math.Exp(-1)), 1.6);
                    for (int i = 0; i < zones; i++) if (expected[i].ToArgb() != wrongOrder[i].ToArgb()) orderingWitnesses++;
                    EqualColors(fixture.Engine.State.Colors, expected, "vivid and gamma before smoothing");
                    EqualColors(fixture.Transport.Last, Mapped(expected, config), "smoothed vivid physical mapping");
                    AppSettings disabled = config.Copy(); disabled.FavorVividColors = false; disabled.SmoothMs = 0;
                    fixture.Engine.Update(disabled); fixture.Step(.15);
                    Color[] normalSecond = Sample(second, zones, true, .8, 1.4, 1.6, false);
                    for (int i = 0; i < zones; i++) if (normalSecond[i].ToArgb() != correctedSecond[i].ToArgb()) vividWitnesses++;
                    Check(!fixture.Capture.HasNewFrame, "preference-change fixture accidentally supplied fresh pixels");
                    EqualColors(fixture.Engine.State.Colors, normalSecond, "cached pixels after disabling vivid preference");
                    EqualColors(fixture.Transport.Last, Mapped(normalSecond, disabled), "disabled preference physical mapping");
                    AppSettings enabled = disabled.Copy(); enabled.FavorVividColors = true;
                    fixture.Engine.Update(enabled); fixture.Step(.15);
                    EqualColors(fixture.Engine.State.Colors, correctedSecond, "cached pixels after enabling vivid preference");
                    EqualColors(fixture.Transport.Last, Mapped(correctedSecond, enabled), "re-enabled vivid physical mapping");
                    Check(fixture.Engine.State.FreshFrames == 2 && fixture.Engine.State.CaptureErrors == 0, "preference changes invented capture freshness or errors");
                }
            }
            Check(vividWitnesses > 0 && orderingWitnesses > 0, "engine cases do not distinguish vivid selection or smoothing order");
        }
        private static Color[] Corrected(Color[] source, double gamma)
        { Color[] result = new Color[source.Length]; for (int i = 0; i < result.Length; i++) result[i] = Gamma(source[i], gamma); return result; }
        private static Color[] Blend(Color[] from, Color[] to, double alpha)
        {
            Color[] result = new Color[from.Length];
            for (int i = 0; i < result.Length; i++) result[i] = Color.FromArgb(Byte(from[i].R + (to[i].R - from[i].R) * alpha),
                Byte(from[i].G + (to[i].G - from[i].G) * alpha), Byte(from[i].B + (to[i].B - from[i].B) * alpha));
            return result;
        }
        private static Color[] Mapped(Color[] logical, AppSettings config)
        {
            Color[] wire = new Color[logical.Length];
            for (int logicalIndex = 0; logicalIndex < logical.Length; logicalIndex++) {
                int wireIndex = config.Reverse ? config.Rotation - logicalIndex : logicalIndex - config.Rotation;
                wireIndex = (wireIndex % logical.Length + logical.Length) % logical.Length; wire[wireIndex] = logical[logicalIndex];
            }
            return wire;
        }
        private sealed class Clock
        { public double Seconds = 100; public DateTime Utc { get { return new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds); } } }
        private sealed class Pixels : IEngineCapture, IEngineCaptureMetadata
        {
            private CaptureFrame frame; private bool fresh;
            public Pixels(CaptureFrame initial) { Present(initial); }
            public void Present(CaptureFrame next) { frame = next; fresh = true; }
            public bool HasNewFrame { get; private set; }
            public bool HasFreshDesktopFrame { get { return HasNewFrame; } }
            public bool IsReprocessedFrame { get { return false; } }
            public bool ProtectedContentMasked { get { return false; } }
            public string Description { get { return "Generated SDR pixels"; } }
            public string LastError { get { return ""; } }
            public CaptureFrame Capture(Rectangle bounds) { HasNewFrame = fresh; fresh = false; return frame; }
            public void Dispose() { }
        }
        private sealed class Transport : IEngineTransport
        {
            private readonly Clock clock; private DeviceInfo target;
            public readonly List<Color[]> Colors = new List<Color[]>();
            public Color[] Last { get { return Colors.Count == 0 ? null : Colors[Colors.Count - 1]; } }
            public Transport(Clock value) { clock = value; }
            public event Action<DeviceInfo> DeviceSeen; public event Action<string> Error { add { } remove { } }
            public DeviceInfo CurrentTarget { get { return target == null ? null : target.Copy(); } }
            public EndpointToken CurrentEndpoint { get { return target == null ? null : new EndpointToken(target, 1); } }
            public DateTime LastSeenUtc { get { return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
            public int Generation { get { return 1; } }
            public void Start() { } public void Discover() { Publish(); } public void Poll() { Publish(); }
            public void SetTarget(DeviceInfo selected) { target = selected == null ? null : selected.Copy(); }
            private void Publish()
            {
                DeviceInfo reply = new DeviceInfo { Id = "VIVID-TEST", Sku = "H6609", Ip = "192.0.2.44", LastSeenUtc = clock.Utc, IdentityGeneration = 1 };
                Action<DeviceInfo> seen = DeviceSeen; if (seen != null) seen(reply);
            }
            private static void Authorize(Func<bool> authorize) { if (authorize != null && !authorize()) throw new OperationCanceledException("Synthetic authorization canceled"); }
            public void Enable(EndpointToken endpoint, Func<bool> authorize) { Authorize(authorize); } public void Disable(EndpointToken endpoint) { }
            public void SendColors(EndpointToken endpoint, Color[] colors, bool flag, Func<bool> authorize)
            { Authorize(authorize); Check(GoveeLan.BuildFrame(colors, flag).Length > 0, "vivid packet is empty"); Colors.Add((Color[])colors.Clone()); }
            public void Dispose() { }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock = new Clock(); public readonly Pixels Capture; public readonly Transport Transport; public readonly GlowEngine Engine;
            public Fixture(AppSettings config, CaptureFrame initial)
            {
                Capture = new Pixels(initial); Transport = new Transport(Clock);
                Engine = new GlowEngine(config, new EngineServices { Transport = Transport, CreateCapture = delegate { return Capture; },
                    UtcNow = delegate { return Clock.Utc; }, MonotonicSeconds = delegate { return Clock.Seconds; },
                    SelectBounds = delegate(AppSettings ignored) { return new Rectangle(0, 0, 3440, 1440); }, CompetitorRunning = delegate { return false; },
                    Log = delegate(string ignored) { }, ListenSystemEvents = false });
            }
            public void Step(double seconds) { Clock.Seconds += seconds; Engine.Step(); } public void Dispose() { Engine.Dispose(); }
        }
    }
}
