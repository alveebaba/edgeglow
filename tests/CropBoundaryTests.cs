using System;
using System.Drawing;

namespace EdgeGlow
{
    // Generated pixels only. No desktop/native capture, app, network or settings.
    public static class CropBoundaryTests
    {
        private static int assertions;
        private static int failures;
        private static readonly Rectangle Full = new Rectangle(0, 0, 256, 107);
        private static readonly Rectangle Movie = new Rectangle(33, 0, 190, 107);

        public static int Main()
        {
            Run("Unobserved vertical cap cannot invent a crop", VerticalCap);
            Run("Unobserved cap preserves an accepted movie crop", PreserveAcceptedCrop);
            Run("Unobserved horizontal cap cannot invent a crop", HorizontalCap);
            Run("Ambiguous observation clears pending shrink evidence", ClearsPendingEvidence);
            Run("Static full to movie to full to movie keeps exact delays", NormalTransitions);
            Run("Observed letterbox boundary just below scan cap works", ObservedVerticalBoundary);
            Run("Observed pillarbox boundary just below scan cap works", ObservedHorizontalBoundary);
            Run("Black and colored near-black frames preserve accepted area", DarkFrames);
            Run("Original capture and recovery controls unchanged", delegate { Console.WriteLine(CaptureTests.Run()); });
            Console.WriteLine("Crop boundary: " + (9 - failures) + " passed, " + failures + " failed; " + assertions + " focused assertions.");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }

        private static void VerticalCap()
        {
            // Bright middle content passes the old interior guard. Both border
            // scans exhaust their26-row limit before this subject begins at34.
            CaptureFrame subject = Frame(new Rectangle(0, 34, 256, 39), Color.Red);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            for (int i = 0; i < 30; i++) Equal(Full, detector.Update(subject, "Auto"), "center subject tick " + i);
        }

        private static void PreserveAcceptedCrop()
        {
            ActiveAreaDetector detector = AcceptedMovie();
            CaptureFrame subject = Frame(new Rectangle(33, 34, 190, 39), Color.Red);
            for (int i = 0; i < 30; i++) Equal(Movie, detector.Update(subject, "Auto"), "accepted movie with center subject tick " + i);
        }

        private static void HorizontalCap()
        {
            CaptureFrame subject = Frame(new Rectangle(84, 0, 88, 107), Color.Lime);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            for (int i = 0; i < 30; i++) Equal(Full, detector.Update(subject, "Auto"), "narrow central subject tick " + i);
        }

        private static void ClearsPendingEvidence()
        {
            ActiveAreaDetector detector = new ActiveAreaDetector();
            CaptureFrame movie = Frame(Movie, Color.CornflowerBlue);
            for (int i = 0; i < 11; i++) Equal(Full, detector.Update(movie, "Auto"), "first pending shrink " + i);
            Equal(Full, detector.Update(Frame(new Rectangle(0, 34, 256, 39), Color.Red), "Auto"), "ambiguous interruption");
            for (int i = 0; i < 11; i++) Equal(Full, detector.Update(movie, "Auto"), "new pending shrink " + i);
            Equal(Movie, detector.Update(movie, "Auto"), "fresh twelfth observation");
        }

        private static void NormalTransitions()
        {
            ActiveAreaDetector detector = new ActiveAreaDetector();
            CaptureFrame full = Frame(Full, Color.CornflowerBlue), movie = Frame(Movie, Color.Goldenrod);
            Equal(Full, detector.Update(full, "Auto"), "initial full");
            for (int i = 0; i < 11; i++) Equal(Full, detector.Update(movie, "Auto"), "first movie delay " + i);
            Equal(Movie, detector.Update(movie, "Auto"), "first movie accepted");
            for (int i = 0; i < 2; i++) Equal(Movie, detector.Update(full, "Auto"), "full expansion delay " + i);
            Equal(Full, detector.Update(full, "Auto"), "full expansion accepted");
            for (int i = 0; i < 11; i++) Equal(Full, detector.Update(movie, "Auto"), "second movie delay " + i);
            Equal(Movie, detector.Update(movie, "Auto"), "second movie accepted");
        }

        private static void ObservedVerticalBoundary()
        {
            Rectangle picture = new Rectangle(0, 25, 256, 57);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            CaptureFrame frame = Frame(picture, Color.OrangeRed);
            for (int i = 0; i < 11; i++) Equal(Full, detector.Update(frame, "Auto"), "observed vertical delay " + i);
            Equal(picture, detector.Update(frame, "Auto"), "row25 boundary is observed before cap26");
        }

        private static void ObservedHorizontalBoundary()
        {
            Rectangle picture = new Rectangle(63, 0, 130, 107);
            ActiveAreaDetector detector = new ActiveAreaDetector();
            CaptureFrame frame = Frame(picture, Color.Cyan);
            for (int i = 0; i < 11; i++) Equal(Full, detector.Update(frame, "Auto"), "observed horizontal delay " + i);
            Equal(picture, detector.Update(frame, "Auto"), "column63 boundary is observed before cap64");
        }

        private static void DarkFrames()
        {
            ActiveAreaDetector detector = AcceptedMovie();
            CaptureFrame black = Frame(Full, Color.Black), blue = Frame(Full, Color.FromArgb(0, 0, 18));
            for (int i = 0; i < 20; i++) {
                Equal(Movie, detector.Update(black, "Auto"), "black frame " + i);
                Equal(Movie, detector.Update(blue, "Auto"), "near-black blue frame " + i);
            }
        }

        private static ActiveAreaDetector AcceptedMovie()
        {
            ActiveAreaDetector detector = new ActiveAreaDetector();
            CaptureFrame movie = Frame(Movie, Color.Goldenrod);
            for (int i = 0; i < 12; i++) detector.Update(movie, "Auto");
            Equal(Movie, detector.Update(movie, "Auto"), "setup movie accepted");
            return detector;
        }

        private static CaptureFrame Frame(Rectangle picture, Color color)
        {
            byte[] pixels = new byte[256 * 107 * 4];
            for (int y = picture.Top; y < picture.Bottom; y++) for (int x = picture.Left; x < picture.Right; x++) {
                int i = (y * 256 + x) * 4;
                pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
            }
            return new CaptureFrame(256, 107, pixels);
        }

        private static void Equal(Rectangle expected, Rectangle actual, string message)
        {
            assertions++;
            if (expected != actual) throw new Exception(message + ": expected " + expected + ", actual " + actual);
        }
    }
}
