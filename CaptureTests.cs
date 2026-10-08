using System;
using System.Drawing;

namespace EdgeGlow
{
    // Synthetic frames only: these tests never capture or save the desktop.
    public static class CaptureTests
    {
        public static string Run()
        {
            int passed = 0;
            FullWidescreen(); passed++;
            PillarboxAndGameTransition(); passed++;
            Letterbox(); passed++;
            DarkSceneProtection(); passed++;
            AsymmetricBorders(); passed++;
            OverlayProtection(); passed++;
            MinimumRetainedArea(); passed++;
            ManualModes(); passed++;
            ColorMapping(); passed++;
            CropSamplingAndColorControls(); passed++;
            NarrowEdgesIgnorePictureCenter(); passed++;
            ArithmeticEncodedMean(); passed++;
            passed += CaptureRecoveryTests.Run();
            return passed + " capture/color tests passed (synthetic frames; no screen captured).";
        }

        private static void FullWidescreen()
        {
            CaptureFrame f = Frame(344, 144, Color.CornflowerBlue);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            for (int i = 0; i < 30; i++) Equal(new Rectangle(0, 0, 344, 144), detector.Update(f, "Auto"), "Full widescreen");
        }

        private static void PillarboxAndGameTransition()
        {
            CaptureFrame movie = Frame(344, 144, Color.Black);
            Rectangle picture = new Rectangle(44, 0, 256, 144);
            Fill(movie, picture, Color.Goldenrod);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            Rectangle full = new Rectangle(0, 0, 344, 144);
            for (int i = 0; i < 11; i++) Equal(full, detector.Update(movie, "Auto"), "Crop stability delay");
            Equal(picture, detector.Update(movie, "Auto"), "Center 16:9 crop");
            CaptureFrame game = Frame(344, 144, Color.CornflowerBlue);
            Equal(picture, detector.Update(game, "Auto"), "Expansion delay 1");
            Equal(picture, detector.Update(game, "Auto"), "Expansion delay 2");
            Equal(full, detector.Update(game, "Auto"), "21:9 game expansion");
        }

        private static void Letterbox()
        {
            CaptureFrame f = Frame(256, 144, Color.Black);
            Rectangle picture = new Rectangle(0, 18, 256, 108);
            Fill(f, picture, Color.Salmon);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            Rectangle actual = Rectangle.Empty;
            for (int i = 0; i < 12; i++) actual = detector.Update(f, "Auto");
            Equal(picture, actual, "Letterbox crop");
        }

        private static void DarkSceneProtection()
        {
            ActiveAreaDetector detector = new ActiveAreaDetector();
            CaptureFrame dark = Frame(344, 144, Color.Black);
            Fill(dark, new Rectangle(44, 0, 256, 144), Color.FromArgb(10, 10, 10));
            Rectangle full = new Rectangle(0, 0, 344, 144);
            for (int i = 0; i < 30; i++) Equal(full, detector.Update(dark, "Auto"), "Dark scene cannot invent crop");
            CaptureFrame movie = Frame(344, 144, Color.Black);
            Rectangle picture = new Rectangle(44, 0, 256, 144);
            Fill(movie, picture, Color.LimeGreen);
            for (int i = 0; i < 12; i++) detector.Update(movie, "Auto");
            CaptureFrame black = Frame(344, 144, Color.Black);
            for (int i = 0; i < 30; i++) Equal(picture, detector.Update(black, "Auto"), "Black frame preserves framing");
        }

        private static void AsymmetricBorders()
        {
            CaptureFrame f = Frame(344, 144, Color.Black);
            Fill(f, new Rectangle(44, 0, 276, 144), Color.Tomato);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            for (int i = 0; i < 20; i++) Equal(new Rectangle(0, 0, 344, 144), detector.Update(f, "Auto"), "Asymmetric dark region is not a bar pair");
        }

        private static void OverlayProtection()
        {
            CaptureFrame f = Frame(344, 144, Color.Black);
            Fill(f, new Rectangle(44, 0, 256, 144), Color.Tomato);
            // A sidebar overlay makes the framing ambiguous. Be conservative.
            Fill(f, new Rectangle(0, 30, 12, 30), Color.White);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            for (int i = 0; i < 20; i++) Equal(new Rectangle(0, 0, 344, 144), detector.Update(f, "Auto"), "Overlay prevents unsafe crop");
        }

        private static void MinimumRetainedArea()
        {
            CaptureFrame f = Frame(256, 144, Color.Black);
            Fill(f, new Rectangle(64, 36, 128, 72), Color.White);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            for (int i = 0; i < 20; i++) Equal(new Rectangle(0, 0, 256, 144), detector.Update(f, "Auto"), "Tiny content does not trigger extreme crop");
        }

        private static void ManualModes()
        {
            CaptureFrame f = Frame(344, 144, Color.Black);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            Equal(new Rectangle(44, 0, 256, 144), detector.Update(f, "Cinema16x9"), "Manual centered 16:9");
            Equal(new Rectangle(0, 0, 344, 144), detector.Update(f, "Full"), "Manual full display");
            CaptureFrame resized = Frame(256, 144, Color.Black);
            Equal(new Rectangle(0, 0, 256, 144), detector.Update(resized, "Auto"), "Display resize resets crop");
        }

        private static void ColorMapping()
        {
            CaptureFrame f = Frame(200, 100, Color.Black);
            Color[] expected = new Color[] {
                Color.Red, Color.Lime, Color.Blue, Color.White,
                Color.Magenta, Color.Cyan, Color.Yellow, Color.Orange,
                Color.Purple, Color.Teal
            };
            for (int p = 0; p < 4; p++)
            {
                Fill(f, new Rectangle(p * 50, 0, 50, 10), expected[p]);
                Fill(f, new Rectangle(p * 50, 90, 50, 10), expected[8 - p]);
            }
            Fill(f, new Rectangle(180, 20, 20, 60), expected[4]);
            Fill(f, new Rectangle(0, 20, 20, 60), expected[9]);
            Color[] actual = ColorProcessor.Sample(f, new Rectangle(0, 0, 200, 100), 0.1, 1, 1);
            for (int p = 0; p < 10; p++) EqualColor(expected[p], actual[p], "Clockwise mapping zone " + p);
        }

        private static void CropSamplingAndColorControls()
        {
            CaptureFrame f = Frame(344, 144, Color.Black);
            Rectangle active = new Rectangle(44, 0, 256, 144);
            Fill(f, active, Color.Red);
            Color[] colors = ColorProcessor.Sample(f, active, 0.08, 0.5, 1);
            foreach (Color color in colors) EqualColor(Color.FromArgb(128, 0, 0), color, "Sample inside crop with brightness");
            colors = ColorProcessor.Sample(f, active, 0.08, 1, 0);
            foreach (Color color in colors) EqualColor(Color.FromArgb(54, 54, 54), color, "Zero saturation");
            CaptureFrame black = Frame(200, 100, Color.Black);
            colors = ColorProcessor.Sample(black, new Rectangle(0, 0, 200, 100), 0.1, 1, 2);
            foreach (Color color in colors) EqualColor(Color.Black, color, "Black stays off");
        }

        private static void NarrowEdgesIgnorePictureCenter()
        {
            // A 16:9 image sits inside black pillarboxes. Its thin visible
            // perimeter is red while the large central picture is green.
            CaptureFrame frame = Frame(344, 144, Color.Black);
            Rectangle active = new Rectangle(44, 0, 256, 144);
            Fill(frame, active, Color.Lime);
            Fill(frame, new Rectangle(active.Left, active.Top, active.Width, 12), Color.Red);
            Fill(frame, new Rectangle(active.Left, active.Bottom - 12, active.Width, 12), Color.Red);
            Fill(frame, new Rectangle(active.Left, active.Top, 20, active.Height), Color.Red);
            Fill(frame, new Rectangle(active.Right - 20, active.Top, 20, active.Height), Color.Red);

            Color[] narrow = ColorProcessor.Sample(frame, active, 0.08, 1, 1);
            for (int zone = 0; zone < narrow.Length; zone++)
                EqualColor(Color.Red, narrow[zone], "8% visible edge ignores green center and black bars, zone " + zone);

            // The negative control establishes that wider sampling really
            // would pull green from the center in this same synthetic scene.
            Color[] broad = ColorProcessor.Sample(frame, active, 0.18, 1, 1);
            for (int zone = 0; zone < broad.Length; zone++)
                if (broad[zone].G < 25)
                    throw new InvalidOperationException("Wide-band control should include the green center, zone " + zone);
        }

        private static void ArithmeticEncodedMean()
        {
            CaptureFrame checker = Frame(200, 100, Color.Black);
            for (int phase = 0; phase < 2; phase++)
            {
                for (int y = 0; y < 100; y++) for (int x = 0; x < 200; x++)
                { int i = (y * 200 + x) * 4; byte c = (byte)(((x + y + phase) % 2) * 255); checker.Bgra[i] = checker.Bgra[i + 1] = checker.Bgra[i + 2] = c; }
                foreach (Color color in ColorProcessor.Sample(checker, new Rectangle(0, 0, 200, 100), 0.1, 1, 1))
                    EqualColor(Color.FromArgb(128, 128, 128), color, "Equal black/white arithmetic encoded mean, checker phase " + phase);
            }
            for (int x = 0; x < 25; x++)
            {
                CaptureFrame line = Frame(200, 100, Color.Black);
                Fill(line, new Rectangle(x, 0, 1, 10), Color.White);
                Color color = ColorProcessor.Sample(line, new Rectangle(0, 0, 200, 100), 0.1, 1, 1)[0];
                EqualColor(Color.FromArgb(5, 5, 5), color, "One-pixel moving line keeps its equal-area mean");
            }
            CaptureFrame dark = Frame(200, 100, Color.FromArgb(2, 2, 2));
            foreach (Color color in ColorProcessor.Sample(dark, new Rectangle(0, 0, 200, 100), 0.1, 1, 1))
                EqualColor(Color.FromArgb(2, 2, 2), color, "No hidden dark cutoff in arithmetic mean");
        }

        private static CaptureFrame Frame(int width, int height, Color color)
        {
            CaptureFrame f = new CaptureFrame(width, height, new byte[width * height * 4]);
            Fill(f, new Rectangle(0, 0, width, height), color);
            return f;
        }

        private static void Fill(CaptureFrame frame, Rectangle region, Color color)
        {
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                {
                    int i = (y * frame.Width + x) * 4;
                    frame.Bgra[i] = color.B;
                    frame.Bgra[i + 1] = color.G;
                    frame.Bgra[i + 2] = color.R;
                    frame.Bgra[i + 3] = 255;
                }
        }

        private static void Equal(Rectangle expected, Rectangle actual, string context)
        {
            if (expected != actual) throw new InvalidOperationException(context + ": expected " + expected + ", got " + actual);
        }

        private static void EqualColor(Color expected, Color actual, string context)
        {
            if (expected.ToArgb() != actual.ToArgb())
                throw new InvalidOperationException(context + ": expected " + expected + ", got " + actual);
        }
    }
}
