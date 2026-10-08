using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using EdgeGlow;

internal static class DarkBackgroundTests
{
    private static int checks;
    private static int sink;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static void Equal(Color actual, Color expected, string name) { Check(actual.ToArgb() == expected.ToArgb(), name + ": expected " + expected + " got " + actual); }
    private static CaptureFrame Frame(int width, int height, Color color)
    {
        byte[] data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = color.B; data[i + 1] = color.G; data[i + 2] = color.R; data[i + 3] = 255; }
        return new CaptureFrame(width, height, data);
    }
    private static void Pixel(CaptureFrame frame, int x, int y, Color color)
    { int i = (y * frame.Width + x) * 4; frame.Bgra[i] = color.B; frame.Bgra[i + 1] = color.G; frame.Bgra[i + 2] = color.R; }
    private static Color First(CaptureFrame frame, bool mode, double brightness, double saturation)
    { return ColorProcessor.Sample(frame, new Rectangle(0, 0, frame.Width, frame.Height), .1, brightness, saturation, 18, mode)[0]; }
    private static CaptureFrame Sparse(int red, int white)
    {
        // The first top region is exactly 100 x 10 = 1000 pixels.
        CaptureFrame frame = Frame(600, 100, Color.Black);
        for (int i = 0; i < red + white; i++) Pixel(frame, i % 100, i / 100, i < red ? Color.Red : Color.White);
        return frame;
    }
    private static void Throws(Action action, string name)
    { bool threw = false; try { action(); } catch (ArgumentException) { threw = true; } Check(threw, name); }
    private static void Synthetic()
    {
        Equal(First(Frame(600, 100, Color.Black), true, 1, 1), Color.Black, "all black");
        foreach (Color c in new Color[] { Color.Red, Color.Blue, Color.White, Color.FromArgb(80, 80, 80), Color.FromArgb(180, 60, 24), Color.FromArgb(24, 0, 0) })
            Equal(First(Frame(600, 100, c), true, 1, 1), c, "uniform color retained");
        Equal(First(Sparse(100, 0), true, .8, 1), Color.FromArgb(204, 0, 0), "ten percent red stays vivid");
        Equal(First(Sparse(100, 0), false, .8, 1), Color.FromArgb(20, 0, 0), "legacy red average");
        Equal(First(Sparse(50, 50), true, 1, 1), Color.FromArgb(255, 128, 128), "white retained and mixed normally");
        Equal(First(Sparse(1, 0), true, 1, 1), Color.FromArgb(5, 0, 0), "single red bounded");
        Equal(First(Sparse(0, 1), true, 1, 1), Color.FromArgb(5, 5, 5), "single white bounded");
        Equal(First(Sparse(10, 0), true, 1, 1), Color.FromArgb(51, 0, 0), "one percent fades by coverage");
        Equal(First(Sparse(50, 0), true, 1, 1), Color.Red, "five percent has full support");
        Equal(First(Sparse(100, 0), true, 0, 1), Color.Black, "zero brightness");
        Equal(First(Sparse(100, 0), true, .8, 0), Color.FromArgb(43, 43, 43), "saturation applied after extraction");
        int previous = -1;
        for (int v = 0; v <= 30; v++)
        {
            Color actual = First(Frame(600, 100, Color.FromArgb(v, v, v)), true, 1, 1);
            double w = Math.Max(0, Math.Min(1, (v - 8) / 16.0)); w = w * w * (3 - 2 * w);
            int expected = (int)Math.Round(v * Math.Min(1, w / .05));
            Equal(actual, Color.FromArgb(expected, expected, expected), "near-black ramp " + v);
            Check(actual.R >= previous, "near-black monotonic " + v); previous = actual.R;
        }
        Check(First(Frame(600, 100, Color.FromArgb(9, 9, 9)), true, 1, 1).R > 0, "smooth support starts above eight");
        Check(First(Frame(600, 100, Color.FromArgb(9, 9, 9)), true, 1, 1).R < 9, "near-black coverage attenuates");
        // A dark background at/below eight has zero influence on supported white.
        CaptureFrame dark = Frame(600, 100, Color.FromArgb(8, 8, 8));
        for (int i = 0; i < 50; i++) Pixel(dark, i, 0, Color.White);
        Equal(First(dark, true, 1, 1), Color.White, "white on near-black retained");
        CaptureFrame tiny = Frame(6, 4, Color.FromArgb(90, 40, 30));
        Check(ColorProcessor.Sample(tiny, new Rectangle(-5, -5, 11, 9), .15, 1, 1, 18, true).Length == 18, "intersected minimum crop valid");
        foreach (Color c in ColorProcessor.Sample(tiny, new Rectangle(0, 0, 6, 4), .15, 1, 1, 18, true)) Equal(c, Color.FromArgb(90, 40, 30), "minimum crop color");
        Throws(delegate { ColorProcessor.Sample(tiny, new Rectangle(1, 0, 5, 4), .15, 1, 1, 18, true); }, "too narrow rejected");
        Throws(delegate { ColorProcessor.Sample(tiny, new Rectangle(0, 0, 6, 3), .15, 1, 1, 18, true); }, "too short rejected");
        Throws(delegate { ColorProcessor.Sample(tiny, new Rectangle(0, 0, 6, 4), .15, 1, 1, 11, true); }, "invalid zones rejected");
        Throws(delegate { ColorProcessor.Sample(null, new Rectangle(0, 0, 6, 4), .15, 1, 1, 18, true); }, "null frame rejected");
        Random random = new Random(731);
        for (int k = 0; k < 32; k++)
        {
            int width = 61 + k, height = 43 + k;
            byte[] pixels = new byte[width * height * 4]; random.NextBytes(pixels);
            CaptureFrame f = new CaptureFrame(width, height, pixels);
            Rectangle crop = new Rectangle(2, 1, width - 5, height - 3);
            double brightness = (k % 7) / 6.0, saturation = (k % 5) / 2.0;
            foreach (int count in new int[] { 10, 18 })
            {
                Color[] expected = LegacyColorProcessor.Sample(f, crop, .15, brightness, saturation, count);
                Color[] actual = ColorProcessor.Sample(f, crop, .15, brightness, saturation, count, false);
                Color[] oldOverload = ColorProcessor.Sample(f, crop, .15, brightness, saturation, count);
                for (int z = 0; z < count; z++) { Equal(actual[z], expected[z], "legacy exact output"); Equal(oldOverload[z], expected[z], "legacy overload output"); }
                if (count == 10) for (int z = 0; z < count; z++) Equal(ColorProcessor.Sample(f, crop, .15, brightness, saturation)[z], expected[z], "default ten-zone overload");
            }
        }
    }
    private static void Bench(CaptureFrame frame, string label, Func<Color[]> sample)
    {
        for (int i = 0; i < 300; i++) sink ^= sample()[2].R;
        double[] times = new double[5];
        for (int trial = 0; trial < times.Length; trial++)
        {
            Stopwatch timer = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) sink ^= sample()[2].R;
            timer.Stop(); times[trial] = timer.Elapsed.TotalMilliseconds / 1000;
        }
        Array.Sort(times);
        Console.WriteLine("BENCH " + label + " ms/sample median=" + times[2].ToString("F4") + " min=" + times[0].ToString("F4") + " max=" + times[4].ToString("F4") + " (5x1000, after300 warmup)");
    }
    public static int Main(string[] args)
    {
        try
        {
            Synthetic();
            Console.WriteLine("PASS synthetic/legacy: " + checks + " assertions");
            if (args.Length == 1)
            {
                CaptureFrame frame = new CaptureFrame(256, 107, File.ReadAllBytes(args[0]));
                Rectangle active = new Rectangle(0, 0, 256, 107);
                Color[] before = ColorProcessor.Sample(frame, active, .15, .8, 1, 18, false);
                Color[] after = ColorProcessor.Sample(frame, active, .15, .8, 1, 18, true);
                Console.WriteLine("OFFLINE saved 256x107 frame, full crop,18zones,15% depth,80% brightness,100% saturation");
                for (int i = 0; i < 18; i++) Console.WriteLine("zone=" + (i + 1) + " false=" + before[i].R + "," + before[i].G + "," + before[i].B + " true=" + after[i].R + "," + after[i].G + "," + after[i].B);
                Bench(frame, "original arithmetic", delegate { return LegacyColorProcessor.Sample(frame, active, .15, .8, 1, 18); });
                Bench(frame, "current false", delegate { return ColorProcessor.Sample(frame, active, .15, .8, 1, 18, false); });
                Bench(frame, "current true", delegate { return ColorProcessor.Sample(frame, active, .15, .8, 1, 18, true); });
            }
            Console.WriteLine("PASS no native capture, network, UI, or settings invoked; sink=" + sink);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
