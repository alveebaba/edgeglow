using System;
using System.Drawing;

namespace EdgeGlow
{
    public static class ProtocolTests
    {
        // Pure packet tests: no sockets, screen capture, or hardware changes.
        public static string Run()
        {
            AssertBytes(Convert.FromBase64String(GoveeLan.EnablePacket), new byte[] { 0xBB, 0, 1, 0xB1, 1, 0x0A }, "enable packet");
            AssertBytes(Convert.FromBase64String(GoveeLan.DisablePacket), new byte[] { 0xBB, 0, 1, 0xB1, 0, 0x0B }, "disable packet");
            AssertChecksum(Convert.FromBase64String(GoveeLan.EnablePacket), "enable checksum");
            AssertChecksum(Convert.FromBase64String(GoveeLan.DisablePacket), "disable checksum");

            Color[] colors = new Color[10];
            colors[0] = Color.FromArgb(17, 34, 51);
            colors[1] = Color.FromArgb(255, 128, 1);
            colors[9] = Color.FromArgb(4, 5, 6);
            byte[] frame = GoveeLan.BuildFrame(colors, false);
            Assert(frame.Length == 37, "10-zone frame has 37 bytes");
            Assert(frame[0] == 0xBB && frame[1] == 0 && frame[2] == 0x20 && frame[3] == 0xB0 && frame[4] == 0 && frame[5] == 10, "frame header/length/count");
            Assert(frame[6] == 17 && frame[7] == 34 && frame[8] == 51, "first RGB zone order");
            Assert(frame[9] == 255 && frame[10] == 128 && frame[11] == 1, "second RGB zone order");
            Assert(frame[33] == 4 && frame[34] == 5 && frame[35] == 6, "last RGB zone order");
            Assert(frame[36] == 0x58, "independently calculated multicolor checksum");
            AssertChecksum(frame, "color checksum");
            AssertBytes(Convert.FromBase64String(Convert.ToBase64String(frame)), frame, "base64 roundtrip");

            byte[] flagged = GoveeLan.BuildFrame(colors, true);
            Assert(flagged[4] == 1 && flagged[36] == 0x59, "alternate flag and checksum");
            for (int i = 0; i < frame.Length; i++)
                if (i != 4 && i != 36) Assert(flagged[i] == frame[i], "flag preserves RGB payload");

            Color[] black = new Color[10];
            byte[] blackFrame = GoveeLan.BuildFrame(black, false);
            Assert(blackFrame[36] == 0x21, "black packet checksum");
            Assert(Convert.ToBase64String(blackFrame) == "uwAgsAAKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIQ==", "known black packet base64");

            bool nullRejected = false;
            try { GoveeLan.BuildFrame(null, false); } catch (ArgumentNullException) { nullRejected = true; }
            Assert(nullRejected, "null colors rejected");
            foreach (int count in new int[] { 0, 1, 9, 11, 255 })
            {
                bool rejected = false;
                try { GoveeLan.BuildFrame(new Color[count], false); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "invalid zone count " + count + " rejected");
            }
            return "Protocol tests passed: enable/disable, ten-zone byte layout, checksums, flags, known base64, and invalid inputs. No device or screen access.";
        }

        private static void AssertChecksum(byte[] bytes, string name)
        {
            byte checksum = 0;
            foreach (byte value in bytes) checksum ^= value;
            Assert(checksum == 0, name);
        }

        private static void AssertBytes(byte[] actual, byte[] expected, string name)
        {
            Assert(actual.Length == expected.Length, name + " length");
            for (int i = 0; i < actual.Length; i++) Assert(actual[i] == expected[i], name + " byte " + i);
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Protocol test failed: " + name);
        }
    }
}
