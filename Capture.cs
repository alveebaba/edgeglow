using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace EdgeGlow
{
    public sealed class CaptureFrame
    {
        public int Width;
        public int Height;
        public byte[] Bgra;

        public CaptureFrame(int width, int height, byte[] bgra)
        {
            if (width <= 0 || height <= 0 || bgra == null || (long)bgra.Length < (long)width * height * 4)
                throw new ArgumentException("A frame must contain width * height BGRA pixels.");
            Width = width;
            Height = height;
            Bgra = bgra;
        }
    }

    internal sealed class CaptureReadResult
    {
        // 1 = fresh desktop image, 2 = conversion-only re-render, 0 = idle.
        public int Result, Flags, HResult, TextureFormat, OutputColorSpace, ConversionKind;
        public long CaptureGeneration, ImageGeneration, ColorMetadataGeneration;
        public bool CurrentGenerationValid;
        public double SdrWhiteMultiplier = 1;
    }

    internal interface IScreenCaptureBackend : IDisposable
    {
        CaptureReadResult Read(byte[] destination);
    }

    // GPU desktop duplication downsamples before the only CPU readback.
    // Old pixels may be returned for holding lights, but are usable for normal
    // processing only while HasCurrentGenerationFrame is true and LastError empty.
    public sealed class ScreenSampler : IDisposable
    {
        private readonly Func<Rectangle, int, int, IScreenCaptureBackend> _factory;
        private readonly Func<long> _monotonicMs;
        private IScreenCaptureBackend _native;
        private CaptureFrame _frame;
        private Rectangle _bounds;
        private bool _hasFrame, _disposed;
        private long _generationOffset, _lastFreshMs;
        public bool HasNewFrame { get; private set; }
        public bool HasFreshDesktopFrame { get; private set; }
        public bool IsReprocessedFrame { get; private set; }
        public bool HasCurrentGenerationFrame { get; private set; }
        public bool IsHdr { get; private set; }
        public bool HdrToneMapped { get; private set; }
        public bool ProtectedContentMasked { get; private set; }
        public string LastError { get; private set; }
        public int LastHResult { get; private set; }
        public long CaptureGeneration { get; private set; }
        public long ImageGeneration { get; private set; }
        public long ColorMetadataGeneration { get; private set; }
        public int NativeTextureFormat { get; private set; }
        public int NativeOutputColorSpace { get; private set; }
        public int ConversionKind { get; private set; }
        public double SdrWhiteLevelNits { get; private set; }
        public DateTime LastUsableFrameUtc { get; private set; }
        public long FrameAgeMilliseconds { get { return _hasFrame ? Math.Max(0, _monotonicMs() - _lastFreshMs) : -1; } }
        public string ConversionMode
        {
            get
            {
                if (ConversionKind == 1) return "Linear scRGB to approximate SDR";
                if (ConversionKind == 2) return "HDR10 PQ / BT.2020 to approximate SDR";
                if (ConversionKind == 3) return "Encoded RGB under HDR output (source transfer not verified)";
                if (ConversionKind == 4) return "Unsupported source color convention";
                return "Encoded SDR RGB";
            }
        }

        public ScreenSampler() : this(delegate(Rectangle bounds, int width, int height) { return new NativeBackend(bounds, width, height); },
            delegate { return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency); }) { }

        internal ScreenSampler(Func<Rectangle, int, int, IScreenCaptureBackend> factory, Func<long> monotonicMs)
        {
            if (factory == null || monotonicMs == null) throw new ArgumentNullException();
            _factory = factory; _monotonicMs = monotonicMs; LastError = "";
        }

        public CaptureFrame Capture(Rectangle screenBounds)
        {
            if (_disposed) throw new ObjectDisposedException("ScreenSampler");
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0) throw new ArgumentException("The screen bounds must have positive dimensions.");
            HasNewFrame = HasFreshDesktopFrame = IsReprocessedFrame = false;
            int width = Math.Min(256, screenBounds.Width);
            int height = Math.Max(1, (int)Math.Round((double)screenBounds.Height * width / screenBounds.Width));
            if (height > 256) { height = 256; width = Math.Max(1, (int)Math.Round((double)screenBounds.Width * height / screenBounds.Height)); }
            if (_native == null || _bounds != screenBounds)
            {
                if (_native != null) _native.Dispose();
                _native = null; _hasFrame = HasCurrentGenerationFrame = false;
                _generationOffset = CaptureGeneration;
                _frame = new CaptureFrame(width, height, new byte[width * height * 4]);
                _native = _factory(screenBounds, width, height);
                _bounds = screenBounds;
            }
            CaptureReadResult read;
            try { read = _native.Read(_frame.Bgra); }
            catch (Exception ex)
            {
                HasCurrentGenerationFrame = false;
                LastError = "GPU capture failed; holding the last colors. " + ex.Message;
                LastHResult = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
                throw;
            }
            if (read == null) throw new InvalidOperationException("The capture backend returned no result.");
            long nextGeneration = _generationOffset + read.CaptureGeneration;
            long nextImageGeneration = read.ImageGeneration > 0 ? _generationOffset + read.ImageGeneration : 0;
            bool continuingValidImage = HasCurrentGenerationFrame && nextGeneration == CaptureGeneration && nextImageGeneration == ImageGeneration;
            CaptureGeneration = nextGeneration;
            ColorMetadataGeneration = read.ColorMetadataGeneration;
            NativeTextureFormat = read.TextureFormat; NativeOutputColorSpace = read.OutputColorSpace;
            ConversionKind = read.ConversionKind; SdrWhiteLevelNits = read.SdrWhiteMultiplier * 80;
            IsHdr = (read.Flags & 1) != 0; HdrToneMapped = (read.Flags & 2) != 0;
            bool usable = read.CurrentGenerationValid && nextImageGeneration == nextGeneration && nextGeneration > 0;
            bool accepted = usable && (read.Result == 1 || (read.Result == 2 && continuingValidImage && String.IsNullOrEmpty(LastError)));
            if (accepted)
            {
                _hasFrame = HasCurrentGenerationFrame = HasNewFrame = true;
                HasFreshDesktopFrame = read.Result == 1; IsReprocessedFrame = read.Result == 2;
                ImageGeneration = nextImageGeneration;
                ProtectedContentMasked = false; LastError = ""; LastHResult = 0;
                if (HasFreshDesktopFrame) { _lastFreshMs = _monotonicMs(); LastUsableFrameUtc = DateTime.UtcNow; }
            }
            else if (read.Result < 0 || (read.Flags & 4) != 0)
            {
                HasCurrentGenerationFrame = false;
                LastHResult = read.HResult;
                if (read.Result == -2 || (read.Flags & 4) != 0)
                {
                    ProtectedContentMasked = true;
                    LastError = "Windows has excluded protected video from capture; holding the last colors.";
                }
                else LastError = "GPU capture is reconnecting (0x" + unchecked((uint)read.HResult).ToString("X8") + "); holding the last colors.";
            }
            else
            {
                HasCurrentGenerationFrame = continuingValidImage && usable;
                // Idle or cursor-only success is never evidence that an error,
                // protected image or obsolete generation has recovered.
                if (_hasFrame && !HasCurrentGenerationFrame && String.IsNullOrEmpty(LastError))
                    LastError = "Waiting for a usable image from the current capture session; holding the last colors.";
            }
            return _hasFrame ? _frame : null;
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_native != null) _native.Dispose();
            _native = null; _frame = null;
        }

        public static void VerifyNativeShaders()
        {
            int result = EG_ShaderSelfTest();
            if (result < 0) throw new InvalidOperationException("GPU shader compilation failed (0x" + unchecked((uint)result).ToString("X8") + ").");
        }
        public static int VerifyNativePipeline()
        {
            int result = EG_RenderSelfTest();
            if (result < 0) throw new InvalidOperationException("Synthetic GPU pipeline test failed (0x" + unchecked((uint)result).ToString("X8") + ").");
            return result;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NativeMetadata
        {
            public ulong CaptureGeneration, ImageGeneration, ColorMetadataGeneration;
            public int CurrentGenerationValid, TextureFormat, OutputColorSpace, ConversionKind;
            public float SdrWhiteMultiplier;
            public int Reserved;
        }
        private sealed class NativeBackend : IScreenCaptureBackend
        {
            private readonly CaptureHandle _handle;
            public NativeBackend(Rectangle bounds, int width, int height)
            {
                IntPtr pointer = EG_Create(bounds.X, bounds.Y, bounds.Width, bounds.Height, width, height);
                if (pointer == IntPtr.Zero) throw new InvalidOperationException("The GPU capture buffer could not be created.");
                _handle = new CaptureHandle(pointer);
            }
            public CaptureReadResult Read(byte[] destination)
            {
                int flags, error;
                int result = EG_Read(_handle, destination, destination.Length, out flags, out error);
                NativeMetadata metadata;
                EG_Metadata(_handle, out metadata);
                return new CaptureReadResult { Result = result, Flags = flags, HResult = error,
                    CaptureGeneration = (long)metadata.CaptureGeneration, ImageGeneration = (long)metadata.ImageGeneration,
                    ColorMetadataGeneration = (long)metadata.ColorMetadataGeneration, CurrentGenerationValid = metadata.CurrentGenerationValid != 0,
                    TextureFormat = metadata.TextureFormat, OutputColorSpace = metadata.OutputColorSpace,
                    ConversionKind = metadata.ConversionKind, SdrWhiteMultiplier = metadata.SdrWhiteMultiplier };
            }
            public void Dispose() { _handle.Dispose(); }
        }
        private sealed class CaptureHandle : SafeHandle
        {
            public CaptureHandle(IntPtr pointer) : base(IntPtr.Zero, true) { SetHandle(pointer); }
            public override bool IsInvalid { get { return handle == IntPtr.Zero; } }
            protected override bool ReleaseHandle() { EG_Destroy(handle); return true; }
        }
        [DllImport("EdgeGlowCapture.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr EG_Create(int x, int y, int width, int height, int smallWidth, int smallHeight);
        [DllImport("EdgeGlowCapture.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int EG_Read(CaptureHandle handle, [In, Out] byte[] bgra, int bytes, out int flags, out int error);
        [DllImport("EdgeGlowCapture.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void EG_Metadata(CaptureHandle handle, out NativeMetadata metadata);
        [DllImport("EdgeGlowCapture.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void EG_Destroy(IntPtr handle);
        [DllImport("EdgeGlowCapture.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int EG_ShaderSelfTest();
        [DllImport("EdgeGlowCapture.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int EG_RenderSelfTest();
    }

    // The detector waits for 12 matching bright frames before cropping, but
    // expands after 3. Black/dark scenes preserve the last known framing.
    public sealed class ActiveAreaDetector
    {
        private Rectangle _active;
        private Rectangle _pending;
        private int _pendingFrames;
        private int _width;
        private int _height;
        private string _lastMode;

        public Rectangle Update(CaptureFrame frame, string mode)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            mode = String.IsNullOrEmpty(mode) ? "Auto" : mode;
            Rectangle full = new Rectangle(0, 0, frame.Width, frame.Height);
            if (_width != frame.Width || _height != frame.Height || mode != _lastMode)
            {
                _width = frame.Width;
                _height = frame.Height;
                _lastMode = mode;
                _active = full;
                _pending = full;
                _pendingFrames = 0;
            }
            if (String.Equals(mode, "Full", StringComparison.OrdinalIgnoreCase)) return _active = full;
            if (String.Equals(mode, "Cinema16x9", StringComparison.OrdinalIgnoreCase))
            {
                int width = frame.Width;
                int height = frame.Height;
                if ((double)width / height > 16.0 / 9.0) width = (int)Math.Round(height * 16.0 / 9.0);
                else height = (int)Math.Round(width * 9.0 / 16.0);
                return _active = new Rectangle((frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
            }

            Rectangle candidate;
            if (!TryFindArea(frame, out candidate))
            {
                _pendingFrames = 0;
                return _active;
            }
            if (Near(candidate, _active, 2))
            {
                _pendingFrames = 0;
                return _active;
            }
            if (Near(candidate, _pending, 2)) _pendingFrames++;
            else { _pending = candidate; _pendingFrames = 1; }

            bool expanding = candidate.Left < _active.Left - 2 || candidate.Top < _active.Top - 2
                || candidate.Right > _active.Right + 2 || candidate.Bottom > _active.Bottom + 2;
            if (_pendingFrames >= (expanding ? 3 : 12))
            {
                // A framing change in two axes first expands safely; a new
                // narrower boundary must independently pass the crop delay.
                _active = expanding ? Rectangle.Union(_active, candidate) : candidate;
                _pendingFrames = 0;
            }
            return _active;
        }

        public void ClearPendingEvidence() { _pendingFrames = 0; _pending = _active; }

        private static bool Near(Rectangle a, Rectangle b, int tolerance)
        {
            return Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance
                && Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;
        }

        private static bool TryFindArea(CaptureFrame frame, out Rectangle result)
        {
            result = new Rectangle(0, 0, frame.Width, frame.Height);
            int left = CountBorder(frame, true, false);
            int right = CountBorder(frame, true, true);
            int top = CountBorder(frame, false, false);
            int bottom = CountBorder(frame, false, true);
            // An exhausted scan has not found a picture boundary. Dark content
            // around a bright subject must not manufacture or remove a crop.
            if (left < 0 || right < 0 || top < 0 || bottom < 0) return false;
            int horizontal = SymmetricInset(left, right);
            int vertical = SymmetricInset(top, bottom);
            Rectangle candidate = new Rectangle(horizontal, vertical,
                frame.Width - horizontal * 2, frame.Height - vertical * 2);
            if ((long)candidate.Width * candidate.Height < (long)frame.Width * frame.Height / 2)
                candidate = result;

            // Evaluate the interior, not the black bars. A dark scene is
            // ambiguous; do not let it manufacture or remove a crop.
            int insetX = Math.Max(1, candidate.Width / 8);
            int insetY = Math.Max(1, candidate.Height / 8);
            int count = 0;
            int bright = 0;
            double sum = 0;
            for (int y = candidate.Top + insetY; y < candidate.Bottom - insetY; y += 2)
                for (int x = candidate.Left + insetX; x < candidate.Right - insetX; x += 2)
                {
                    int i = (y * frame.Width + x) * 4;
                    double light = Luminance(frame.Bgra, i);
                    sum += light;
                    if (light >= 24) bright++;
                    count++;
                }
            if (count == 0 || sum / count < 14 || bright < count * 0.15) return false;
            result = candidate;
            return true;
        }

        private static int SymmetricInset(int first, int second)
        {
            int smaller = Math.Min(first, second);
            if (smaller < 3) return 0;
            int allowedDifference = Math.Max(2, smaller / 10);
            return Math.Abs(first - second) <= allowedDifference ? smaller : 0;
        }

        private static int CountBorder(CaptureFrame frame, bool columns, bool reverse)
        {
            int dimension = columns ? frame.Width : frame.Height;
            int length = columns ? frame.Height : frame.Width;
            int maximum = Math.Max(0, dimension / 4);
            for (int inset = 0; inset < maximum; inset++)
            {
                int coordinate = reverse ? dimension - inset - 1 : inset;
                double sum = 0;
                int bright = 0;
                // Frame dimensions are already tiny. Inspect every pixel so
                // an overlay cannot hide between a sparse sampling grid.
                for (int along = 0; along < length; along++)
                {
                    int x = columns ? coordinate : along;
                    int y = columns ? along : coordinate;
                    int i = (y * frame.Width + x) * 4;
                    sum += Luminance(frame.Bgra, i);
                    if (Math.Max(frame.Bgra[i], Math.Max(frame.Bgra[i + 1], frame.Bgra[i + 2])) > 18) bright++;
                }
                if (sum / length > 7 || bright > length / 50) return inset;
            }
            return -1; // Scan cap reached without observing non-black content.
        }

        private static double Luminance(byte[] pixels, int i)
        {
            return pixels[i + 2] * 0.2126 + pixels[i + 1] * 0.7152 + pixels[i] * 0.0722;
        }
    }

    public static class ColorProcessor
    {
        // Clockwise viewed from the front: top L->R x4, right middle,
        // bottom R->L x4, left middle. Side zones read the middle 60%.
        public static Color[] Sample(CaptureFrame frame, Rectangle active, double edgeDepth, double brightness, double saturation)
        {
            return Sample(frame, active, edgeDepth, brightness, saturation, 10);
        }

        public static Color[] Sample(CaptureFrame frame, Rectangle active, double edgeDepth, double brightness, double saturation, int zoneCount)
        {
            return Sample(frame, active, edgeDepth, brightness, saturation, zoneCount, false);
        }

        public static Color[] Sample(CaptureFrame frame, Rectangle active, double edgeDepth, double brightness, double saturation, int zoneCount, bool ignoreBlackPixels)
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
                    expanded[part] = Average(frame, new Rectangle(left, active.Top, right - left, depthY), brightness, saturation, ignoreBlackPixels);
                    expanded[14 - part] = Average(frame, new Rectangle(left, active.Bottom - depthY, right - left, depthY), brightness, saturation, ignoreBlackPixels);
                }
                for (int part = 0; part < 3; part++) {
                    int top = active.Top + active.Height * part / 3;
                    int bottom = active.Top + active.Height * (part + 1) / 3;
                    expanded[6 + part] = Average(frame, new Rectangle(active.Right - depthX, top, depthX, bottom - top), brightness, saturation, ignoreBlackPixels);
                    expanded[17 - part] = Average(frame, new Rectangle(active.Left, top, depthX, bottom - top), brightness, saturation, ignoreBlackPixels);
                }
                return expanded;
            }
            Color[] colors = new Color[10];
            for (int part = 0; part < 4; part++)
            {
                int left = active.Left + active.Width * part / 4;
                int right = active.Left + active.Width * (part + 1) / 4;
                colors[part] = Average(frame, new Rectangle(left, active.Top, right - left, depthY), brightness, saturation, ignoreBlackPixels);
                colors[8 - part] = Average(frame, new Rectangle(left, active.Bottom - depthY, right - left, depthY), brightness, saturation, ignoreBlackPixels);
            }
            int sideTop = active.Top + active.Height / 5;
            int sideBottom = active.Bottom - active.Height / 5;
            colors[4] = Average(frame, new Rectangle(active.Right - depthX, sideTop, depthX, sideBottom - sideTop), brightness, saturation, ignoreBlackPixels);
            colors[9] = Average(frame, new Rectangle(active.Left, sideTop, depthX, sideBottom - sideTop), brightness, saturation, ignoreBlackPixels);
            return colors;
        }

        private static Color Average(CaptureFrame frame, Rectangle region, double brightness, double saturation, bool ignoreBlackPixels = false)
        {
            double red = 0, green = 0, blue = 0, support = 0;
            long count = 0;
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                {
                    int i = (y * frame.Width + x) * 4;
                    byte b = frame.Bgra[i], g = frame.Bgra[i + 1], r = frame.Bgra[i + 2];
                    if (ignoreBlackPixels)
                    {
                        // Give white and colors the same support; only near-black
                        // intensity is suppressed, with a smooth transition.
                        double weight = Clamp((Math.Max(r, Math.Max(g, b)) - 8) / 16.0, 0, 1);
                        weight = weight * weight * (3 - 2 * weight);
                        red += r * weight; green += g * weight; blue += b * weight;
                        support += weight;
                    }
                    else
                    {
                        red += r; green += g; blue += b;
                    }
                    count++;
                }
            if (count <= 0 || brightness <= 0) return Color.Black;
            if (ignoreBlackPixels)
            {
                if (support <= 0) return Color.Black;
                red /= support; green /= support; blue /= support;
                // Below five percent support, fade proportionally so one bright
                // pixel cannot light the entire region at full intensity.
                double coverageFade = Math.Min(1, support / (count * 0.05));
                red *= coverageFade; green *= coverageFade; blue *= coverageFade;
            }
            else
            {
                // Preserve the default arithmetic mean of encoded RGB exactly.
                red /= count; green /= count; blue /= count;
            }
            // Brightness and saturation always follow extraction.
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
