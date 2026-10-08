using System;
using System.Drawing;
namespace EdgeGlow
{
    public static class LegacyColorProcessor
    {
        // Clockwise viewed from the front: top L->R x4, right middle,
        // bottom R->L x4, left middle. Side zones read the middle 60%.
        public static Color[] Sample(CaptureFrame frame, Rectangle active, double edgeDepth, double brightness, double saturation)
        {
            return Sample(frame, active, edgeDepth, brightness, saturation, 10);
        }

        public static Color[] Sample(CaptureFrame frame, Rectangle active, double edgeDepth, double brightness, double saturation, int zoneCount)
        {
            if (zoneCount != 10 && zoneCount != 18) throw new ArgumentException("Expected 10 or 18 zones.", "zoneCount");
            if (frame == null) throw new ArgumentNullException("frame");
            active = Rectangle.Intersect(active, new Rectangle(0, 0, frame.Width, frame.Height));
            if (active.Width < (zoneCount == 18 ? 6 : 4) || active.Height < 4) throw new ArgumentException("The active picture is too small.");
            edgeDepth = Clamp(edgeDepth, 0.01, 0.35);
            brightness = Clamp(brightness, 0, 1);
            saturation = Clamp(saturation, 0, 2);
            int depthX = Math.Max(1, (int)Math.Round(active.Width * edgeDepth));
            int depthY = Math.Max(1, (int)Math.Round(active.Height * edgeDepth));
            // Preserve the established ten-zone geometry exactly. The eighteen-zone
            // layout divides the full side bands into thirds. Corners may overlap
            // the top/bottom bands, matching the rectangular averaging model.
            if (zoneCount == 18) {
                Color[] expanded = new Color[18];
                for (int part = 0; part < 6; part++) {
                    int left = active.Left + active.Width * part / 6;
                    int right = active.Left + active.Width * (part + 1) / 6;
                    expanded[part] = Average(frame, new Rectangle(left, active.Top, right - left, depthY), brightness, saturation);
                    expanded[14 - part] = Average(frame, new Rectangle(left, active.Bottom - depthY, right - left, depthY), brightness, saturation);
                }
                for (int part = 0; part < 3; part++) {
                    int top = active.Top + active.Height * part / 3;
                    int bottom = active.Top + active.Height * (part + 1) / 3;
                    expanded[6 + part] = Average(frame, new Rectangle(active.Right - depthX, top, depthX, bottom - top), brightness, saturation);
                    expanded[17 - part] = Average(frame, new Rectangle(active.Left, top, depthX, bottom - top), brightness, saturation);
                }
                return expanded;
            }
            Color[] colors = new Color[10];
            for (int part = 0; part < 4; part++)
            {
                int left = active.Left + active.Width * part / 4;
                int right = active.Left + active.Width * (part + 1) / 4;
                colors[part] = Average(frame, new Rectangle(left, active.Top, right - left, depthY), brightness, saturation);
                colors[8 - part] = Average(frame, new Rectangle(left, active.Bottom - depthY, right - left, depthY), brightness, saturation);
            }
            int sideTop = active.Top + active.Height / 5;
            int sideBottom = active.Bottom - active.Height / 5;
            colors[4] = Average(frame, new Rectangle(active.Right - depthX, sideTop, depthX, sideBottom - sideTop), brightness, saturation);
            colors[9] = Average(frame, new Rectangle(active.Left, sideTop, depthX, sideBottom - sideTop), brightness, saturation);
            return colors;
        }

        private static Color Average(CaptureFrame frame, Rectangle region, double brightness, double saturation)
        {
            double red = 0, green = 0, blue = 0;
            long count = 0;
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                {
                    int i = (y * frame.Width + x) * 4;
                    byte b = frame.Bgra[i], g = frame.Bgra[i + 1], r = frame.Bgra[i + 2];
                    red += r; green += g; blue += b; count++;
                }
            if (count <= 0 || brightness <= 0) return Color.Black;
            // Defined default: arithmetic mean of encoded 8-bit RGB samples.
            // Brightness and saturation are applied after the uniform mean.
            red /= count; green /= count; blue /= count;
            double light = red * 0.2126 + green * 0.7152 + blue * 0.0722;
            return Color.FromArgb(Channel((light + (red - light) * saturation) * brightness),
                Channel((light + (green - light) * saturation) * brightness),
                Channel((light + (blue - light) * saturation) * brightness));
        }

        private static int Channel(double value) { return (int)Math.Round(Clamp(value, 0, 255)); }
        private static double Clamp(double value, double low, double high)
        {
            if (Double.IsNaN(value)) return low;
            return Math.Min(high, Math.Max(low, value));
        }
    }
}
