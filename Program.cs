using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace EdgeGlow
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (Array.IndexOf(args, "--self-test") >= 0) { RunTests(); return; }
            if (Array.IndexOf(args, "--diagnose") >= 0) { Diagnostics.Run(); return; }
            if (Array.IndexOf(args, "--capture-test") >= 0) { Diagnostics.Run(false); return; }
            if (Array.IndexOf(args, "--ui-preview") >= 0)
            {
                using (MainWindow form = new MainWindow(false, null, true))
                {
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                    form.Show(); Application.DoEvents();
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-preview.png")); }
                }
                return;
            }
            bool trayStart = Array.IndexOf(args, "--tray") >= 0;
            try
            {
                bool created;
                using (Mutex singleton = new Mutex(true, "Local\\EdgeGlow.single", out created))
                using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EdgeGlow.show"))
                {
                    if (!created) { if (!trayStart) show.Set(); return; }
                    try { Application.Run(new MainWindow(trayStart, show, false)); }
                    finally { singleton.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { Storage.Log(ex.ToString()); if (!trayStart) MessageBox.Show(ex.Message, "EdgeGlow could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private static void RunTests()
        {
            string report;
            try { ScreenSampler.VerifyNativeShaders(); int gpuTests = ScreenSampler.VerifyNativePipeline(); report = "PASS\n" + ProtocolTests.Run() + "\n" + CaptureTests.Run() + "\n" + gpuTests + " synthetic GPU tests passed (no desktop capture)."; }
            catch (Exception ex) { report = "FAIL\n" + ex.ToString(); Environment.ExitCode = 1; }
            Directory.CreateDirectory(Storage.DirectoryPath);
            File.WriteAllText(Path.Combine(Storage.DirectoryPath, "self-test.txt"), report);
        }
    }

    internal sealed class MainWindow : Form
    {
        private readonly Color background = Color.FromArgb(18, 22, 30);
        private readonly Color foreground = Color.FromArgb(226, 232, 242);
        private readonly Color muted = Color.FromArgb(155, 168, 185);
        private readonly Color accent = Color.FromArgb(91, 215, 187);
        private readonly GlowEngine engine;
        private ShutdownCoordinator shutdown;
        private readonly Stopwatch stoppingClock = new Stopwatch();
        private Button forceExit;
        private readonly EventWaitHandle showEvent;
        private AppSettings settings;
        private NotifyIcon tray;
        private System.Windows.Forms.Timer timer;
        private bool exiting, startsHidden, preview;
        private Label status, detail, metrics, device;
        private ComboBox monitor, area;
        private NumericUpDown brightness, saturation, smoothing, fps, edge, rotate, zone;
        private CheckBox reverse, ignoreBlack, startup, autoSync;
        private Button toggle;
        private StripPreview visual;
        protected override bool ShowWithoutActivation { get { return preview; } }
        private long lastFrames, lastDatagrams;
        private DateTime lastTick = DateTime.UtcNow;
        private string rememberedIp;
        private DateTime nextHealth = DateTime.MinValue;

        public MainWindow(bool hidden, EventWaitHandle show, bool previewOnly)
        {
            startsHidden = hidden; showEvent = show; preview = previewOnly;
            settings = Storage.Load(); rememberedIp = settings.DeviceIp;
            Text = "EdgeGlow | Your screen, beyond the edges";
            BackColor = background; ForeColor = foreground; Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Font; ClientSize = new Size(880, 620); MinimumSize = new Size(870, 650);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = CreateIcon();
            BuildUi();
            if (!preview)
            {
                engine = new GlowEngine(settings); shutdown = new ShutdownCoordinator(engine); engine.Start();
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("Open EdgeGlow", null, delegate { Reveal(); });
                menu.Items.Add("Pause / resume", null, delegate { engine.Paused = !engine.Paused; });
                menu.Items.Add("Rediscover strip", null, delegate { engine.Discover(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit EdgeGlow", null, delegate { exiting = true; Close(); });
                tray = new NotifyIcon { Icon = Icon, Text = "EdgeGlow - screen lighting", Visible = true, ContextMenuStrip = menu };
                tray.DoubleClick += delegate { Reveal(); };
                timer = new System.Windows.Forms.Timer { Interval = 500 };
                timer.Tick += Tick; timer.Start();
            }
            else
            {
                status.Text = "Screen sync is running"; detail.Text = "Preview only - " + settings.ZoneCount + " zones";
                Color[] previewColors = new Color[settings.ZoneCount];
                Color[] palette = new Color[] { Color.Teal, Color.CadetBlue, Color.CornflowerBlue, Color.Orchid, Color.MediumPurple, Color.OrangeRed, Color.Coral, Color.Goldenrod, Color.Teal, Color.SeaGreen };
                for (int i = 0; i < previewColors.Length; i++) previewColors[i] = palette[i % palette.Length];
                visual.State = new EngineState { FrameWidth = 256, FrameHeight = 107, ActiveArea = new Rectangle(33, 0, 190, 107), Colors = previewColors };
            }
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && !preview && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                if (!preview && shutdown != null && !shutdown.Completed) {
                    e.Cancel = true; exiting = true;
                    if (!shutdown.Requested) { stoppingClock.Start(); shutdown.Request(); }
                    Reveal(); status.Text = "Stopping EdgeGlow...";
                    detail.Text = "Waiting for lighting cleanup. This window will close when it finishes.";
                }
            };
        }
        private Label LabelText(string text, float size, Color color)
        { return new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, ForeColor = color, Font = new Font("Segoe UI", size), TextAlign = ContentAlignment.MiddleLeft }; }
        private Button ButtonText(string text, EventHandler action)
        {
            Button b = new Button { Text = text, AutoSize = true, Height = 36, Padding = new Padding(12, 3, 12, 3), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(35, 44, 58), ForeColor = foreground, Margin = new Padding(0, 0, 10, 0) };
            b.FlatAppearance.BorderColor = Color.FromArgb(64, 81, 102); b.Click += action; return b;
        }
        private NumericUpDown Number(int min, int max, int value, int step)
        { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Increment = step, Width = 100, BackColor = Color.FromArgb(29, 36, 48), ForeColor = foreground, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 8, 0, 0) }; }
        private ComboBox Choice()
        {
            ComboBox choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 25, Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 36, 48), ForeColor = foreground, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 5, 15, 5) };
            choice.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                using (Brush brush = new SolidBrush(choice.BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, choice.Items[e.Index].ToString(), choice.Font, e.Bounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            };
            return choice;
        }
        private void Field(TableLayoutPanel grid, string caption, Control control, int col, int row)
        { grid.Controls.Add(LabelText(caption, 10, muted), col, row); grid.Controls.Add(control, col + 1, row); }
        private void BuildUi()
        {
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(25, 15, 25, 15), ColumnCount = 1, RowCount = 12 };
            foreach (int h in new int[] { 44, 28, 29, 22, 167, 27, 84, 0, 46, 46, 42, 1 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            layout.RowStyles[11] = new RowStyle(SizeType.Percent, 100); Controls.Add(layout);
            layout.Controls.Add(LabelText("EdgeGlow", 25, foreground), 0, 0);
            layout.Controls.Add(LabelText("Your screen, beyond the edges.", 11, muted), 0, 1);
            status = LabelText("Connecting to your G1...", 12, accent); layout.Controls.Add(status, 0, 2);
            detail = LabelText(Storage.LoadWarning, 9, muted); layout.Controls.Add(detail, 0, 3);
            visual = new StripPreview { ZoneCount = settings.ZoneCount, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 8) }; layout.Controls.Add(visual, 0, 4);
            visual.TestRequested += delegate(int selectedZone) { if (engine != null) engine.TestZone(selectedZone); };
            visual.CancelRequested += delegate { if (engine != null) engine.CancelTest(); };
            metrics = LabelText("Automatic picture area  |  Local processing  |  No account or cloud", 9, muted); layout.Controls.Add(metrics, 0, 5);

            TableLayoutPanel choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            choices.Controls.Add(LabelText("Display", 10, muted), 0, 0); choices.Controls.Add(LabelText("Picture area", 10, muted), 1, 0);
            monitor = Choice(); int selected = 0, i = 0;
            foreach (Screen s in Screen.AllScreens) { monitor.Items.Add(new MonitorChoice(s)); if (s.DeviceName == settings.Monitor || (settings.Monitor == "" && s.Primary)) selected = i; i++; }
            monitor.SelectedIndex = selected;
            area = Choice(); area.Items.AddRange(new object[] { "Auto: follow the visible picture", "Full screen: 21:9 / desktop", "Center 16:9: ignore side bars" });
            area.SelectedIndex = settings.AreaMode == "Full" ? 1 : settings.AreaMode == "Cinema16x9" ? 2 : 0;
            choices.Controls.Add(monitor, 0, 1); choices.Controls.Add(area, 1, 1); layout.Controls.Add(choices, 0, 6);

            TableLayoutPanel tuning = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 3 };
            for (int c = 0; c < 3; c++) { tuning.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19)); tuning.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14.33f)); }
            for (int r = 0; r < 3; r++) tuning.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            brightness = Number(1, 100, settings.Brightness, 5); saturation = Number(50, 180, settings.Saturation, 5); smoothing = Number(0, 1000, settings.SmoothMs, 25);
            fps = Number(10, 40, settings.Fps, 2); edge = Number(5, 35, settings.EdgePercent, 1); rotate = Number(0, settings.ZoneCount - 1, settings.Rotation, 1);
            Field(tuning, "Brightness %", brightness, 0, 0); Field(tuning, "Color strength %", saturation, 2, 0); Field(tuning, "Smoothness ms", smoothing, 4, 0);
            Field(tuning, "Updates / second", fps, 0, 1); Field(tuning, "Edge depth %", edge, 2, 1); Field(tuning, "Rotate zones", rotate, 4, 1);
            reverse = new CheckBox { Text = "Reverse strip", Checked = settings.Reverse, Dock = DockStyle.Fill };
            ignoreBlack = new CheckBox { Text = "Ignore dark background", Checked = settings.IgnoreBlackPixels, Dock = DockStyle.Fill };
            tuning.Controls.Add(reverse, 0, 2); tuning.SetColumnSpan(reverse, 2); tuning.Controls.Add(ignoreBlack, 2, 2); tuning.SetColumnSpan(ignoreBlack, 2);
            layout.Controls.Add(tuning, 0, 7); tuning.Visible = false;

            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            startup = new CheckBox { Text = "Start with Windows", Checked = settings.StartWithWindows, AutoSize = true, Margin = new Padding(0, 3, 25, 0) };
            autoSync = new CheckBox { Text = "Start syncing when opened", Checked = settings.AutoSync, AutoSize = true };
            options.Controls.Add(startup); options.Controls.Add(autoSync); layout.Controls.Add(options, 0, 8);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            toggle = ButtonText("Pause sync", delegate { if (engine != null) engine.Paused = !engine.Paused; });
            Button apply = ButtonText("Apply settings", Apply); apply.Visible = false;
            Button test = ButtonText("Test zone", delegate { if (engine != null) engine.TestZone((int)zone.Value - 1); }); test.Visible = false;
            zone = Number(1, settings.ZoneCount, 1, 1); zone.Margin = new Padding(0, 5, 0, 0); zone.Width = 52; zone.Visible = false;
            test.AutoSize = false; test.Dock = DockStyle.Fill; test.Margin = new Padding(0, 0, 6, 0);
            tuning.Controls.Add(test, 4, 2); tuning.Controls.Add(zone, 5, 2);
            Button tuningButton = ButtonText("Tune lighting", delegate { });
            tuningButton.Click += delegate
            {
                bool expanded = !tuning.Visible; tuning.Visible = expanded; apply.Visible = expanded; test.Visible = expanded; zone.Visible = expanded;
                layout.RowStyles[7].Height = expanded ? 117 : 0;
                Height += expanded ? 117 : -117;
                tuningButton.Text = expanded ? "Hide tuning" : "Tune lighting";
            };
            actions.Controls.Add(toggle); actions.Controls.Add(tuningButton); actions.Controls.Add(apply);
            actions.Controls.Add(ButtonText("Find strip", delegate { if (engine != null) engine.Discover(); }));
            actions.Controls.Add(ButtonText("Exit EdgeGlow", delegate { exiting = true; Close(); }));
            forceExit = ButtonText("Exit now", delegate { if (shutdown != null && shutdown.Pending) shutdown.ForceExit(); });
            forceExit.Visible = false; actions.Controls.Add(forceExit); actions.Controls.SetChildIndex(forceExit, 0);
            layout.Controls.Add(actions, 0, 9);
            device = LabelText("H6609 G1  |  " + settings.ZoneCount + " screen zones  |  Close to tray; Exit stops EdgeGlow.", 9, muted); layout.Controls.Add(device, 0, 10);
            monitor.SelectedIndexChanged += Apply; area.SelectedIndexChanged += Apply; startup.CheckedChanged += Apply; autoSync.CheckedChanged += Apply; ignoreBlack.CheckedChanged += Apply;
            layout.Controls.Add(LabelText("Auto follows the picture edges, ignoring black bars. Tuning is optional.\nProtected video may block capture; borderless games usually capture best.", 9, muted), 0, 11);
        }
        private void Apply(object sender, EventArgs e)
        {
            AppSettings updated = settings.Copy();
            updated.Monitor = ((MonitorChoice)monitor.SelectedItem).DeviceName;
            updated.AreaMode = area.SelectedIndex == 1 ? "Full" : area.SelectedIndex == 2 ? "Cinema16x9" : "Auto";
            updated.Brightness = (int)brightness.Value; updated.Saturation = (int)saturation.Value; updated.SmoothMs = (int)smoothing.Value;
            updated.Fps = (int)fps.Value; updated.EdgePercent = (int)edge.Value; updated.Rotation = (int)rotate.Value;
            updated.Reverse = reverse.Checked; updated.IgnoreBlackPixels = ignoreBlack.Checked; updated.StartWithWindows = startup.Checked; updated.AutoSync = autoSync.Checked;
            try { Storage.SetStartup(updated.StartWithWindows); Storage.Save(updated); settings = updated; if (engine != null) engine.Update(updated); }
            catch (Exception ex) { MessageBox.Show(this, "Settings could not be saved: " + ex.Message, "EdgeGlow", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void Tick(object sender, EventArgs args)
        {
            if (showEvent != null && showEvent.WaitOne(0)) Reveal();
            if (shutdown != null && shutdown.Requested) {
                if (shutdown.Completed) { Close(); return; }
                status.Text = "Stopping EdgeGlow...";
                detail.Text = "Lighting cleanup is still pending. Exit now may leave the lights unchanged.";
                toggle.Enabled = false; forceExit.Visible = stoppingClock.ElapsedMilliseconds >= 4000;
                tray.Text = "EdgeGlow - stopping";
                return;
            }
            EngineState s = engine.State;
            if (!String.IsNullOrEmpty(s.Ip) && s.Ip != rememberedIp && s.LastReply > DateTime.UtcNow.AddSeconds(-20))
            {
                rememberedIp = s.Ip; settings.DeviceIp = s.Ip;
                if (!String.IsNullOrEmpty(s.DeviceId)) settings.DeviceId = s.DeviceId;
                try { Storage.Save(settings); } catch (Exception ex) { Storage.Log(ex.Message); }
            }
            status.Text = s.Message; detail.Text = String.IsNullOrEmpty(Storage.LoadWarning) ? s.Detail : Storage.LoadWarning;
            toggle.Text = engine.Paused ? "Resume sync" : "Pause sync";
            tray.Text = "EdgeGlow - " + (s.Sending ? "syncing" : "paused / reconnecting");
            if (Visible)
            {
                visual.State = s; visual.Invalidate();
                double elapsed = (DateTime.UtcNow - lastTick).TotalSeconds;
                double actualFps = (s.FreshFrames - lastFrames) / Math.Max(.1, elapsed);
                double sendRate = (s.Datagrams - lastDatagrams) / Math.Max(.1, elapsed);
                metrics.Text = s.Sending ? String.Format("Picture {0:F2}:1  |  {1:F0} new frames/s  |  {2:F0} sends/s  |  {3:F1} ms work", s.ActiveArea.Width / (double)Math.Max(1, s.ActiveArea.Height), actualFps, sendRate, s.WorkMs) : "Automatic picture area  |  Local processing  |  No account or cloud";
                device.Text = "H6609 G1  |  " + settings.ZoneCount + " screen zones  |  Close to tray; Exit stops EdgeGlow.";
            }
            if (DateTime.UtcNow >= nextHealth)
            {
                nextHealth = DateTime.UtcNow.AddSeconds(10);
                try
                {
                    Directory.CreateDirectory(Storage.DirectoryPath);
                    string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { updatedUtc = DateTime.UtcNow.ToString("o"), status = s.Message, detail = s.Detail, sending = s.Sending, zoneCount = settings.ZoneCount, edgeDepthPercent = settings.EdgePercent, ignoreDarkBackground = settings.IgnoreBlackPixels, deviceIp = s.Ip, lastReplyUtc = s.LastReply.ToString("o"), freshFrames = s.FreshFrames, loopIterations = s.LoopIterations, captureAttempts = s.CaptureAttempts, idleResults = s.IdleResults, captureErrors = s.CaptureErrors, missingFrames = s.MissingFrames, protectedResults = s.ProtectedResults, reprocessedFrames = s.ReprocessedFrames, datagrams = s.Datagrams, allBlackColorDatagrams = s.AllBlackColorDatagrams, nearBlackColorDatagrams = s.NearBlackColorDatagrams, nearBlackMaximumChannel = 3, lastColorPeak = s.LastColorPeak, lastColorWasTest = s.LastColorWasTest, lastColorSendUtc = s.LastColorSendUtc.ToString("o"), lastNearBlackSendUtc = s.LastNearBlackSendUtc.ToString("o"), lastAllBlackSendUtc = s.LastAllBlackSendUtc.ToString("o"), lastNearBlackWasTest = s.LastNearBlackWasTest, lastAllBlackWasTest = s.LastAllBlackWasTest, maximumColorSendGapUtc = s.MaximumColorSendGapUtc.ToString("o"), maximumColorSendGapWasTest = s.MaximumColorSendGapWasTest, lastColorSendGapSeconds = s.LastColorSendGapSeconds, maximumColorSendGapSeconds = s.MaximumColorSendGapSeconds, activationAttemptsTotal = s.ActivationAttemptsTotal, lastActivationAttemptUtc = s.LastActivationAttemptUtc.ToString("o"), lastActivationSucceededUtc = s.LastActivationSucceededUtc.ToString("o"), lastActivationReason = s.LastActivationReason, processedPowerOffObservations = s.PowerOffReports, lastPowerOffReportUtc = s.LastPowerOffReportUtc.ToString("o"), outputDiagnosticEvents = s.OutputDiagnosticEvents, outputDiagnosticEventLimit = 64, outputDiagnosticsScope = "Successful local color sends only; no physical acknowledgement. Current-stream gaps exclude new activation and test/ambient crossings. Off observations can recount cached fresh status after resume/reset. RAM event history is bounded; legitimate dark content is included.", outputPolicy = s.OutputPolicy, lastUsableImageUtc = s.LastUsableImageUtc.ToString("o"), imageAgeSeconds = s.ImageAgeSeconds, sessionGeneration = s.SessionGeneration, releasePending = s.ReleasePending, settingsWarning = Storage.LoadWarning, workMs = s.WorkMs, frameWidth = s.FrameWidth, frameHeight = s.FrameHeight, activeX = s.ActiveArea.X, activeY = s.ActiveArea.Y, activeWidth = s.ActiveArea.Width, activeHeight = s.ActiveArea.Height, workingSetBytes = Environment.WorkingSet });
                    File.WriteAllText(Path.Combine(Storage.DirectoryPath, "status.json"), json);
                }
                catch { }
            }
            lastTick = DateTime.UtcNow; lastFrames = s.FreshFrames; lastDatagrams = s.Datagrams;
        }
        protected override void SetVisibleCore(bool value)
        {
            if (startsHidden) {
                // Application.Run needs the form handle, but login must never show it.
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }
        private void Reveal() { startsHidden = false; Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (timer != null) timer.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (engine != null) engine.Dispose();
                if (Icon != null) Icon.Dispose();
            }
            base.Dispose(disposing);
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        private static Icon CreateIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (Pen glow = new Pen(Color.FromArgb(91, 215, 187), 4)) g.DrawRectangle(glow, 3, 6, 26, 19);
                using (Brush fill = new SolidBrush(Color.FromArgb(25, 35, 55))) g.FillRectangle(fill, 6, 9, 20, 13);
                IntPtr handle = bmp.GetHicon(); Icon icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon;
            }
        }
        private sealed class MonitorChoice
        {
            public readonly string DeviceName; private readonly string text;
            public MonitorChoice(Screen screen) { DeviceName = screen.DeviceName; text = (screen.Primary ? "Main display" : screen.DeviceName.Replace(@"\\.\", "")) + " - " + screen.Bounds.Width + " x " + screen.Bounds.Height; }
            public override string ToString() { return text; }
        }
    }

    internal sealed class StripPreview : Control
    {
        public EngineState State = new EngineState();
        public int ZoneCount = 10;
        public event Action<int> TestRequested;
        public event Action CancelRequested;
        public StripPreview() { DoubleBuffered = true; }
        private Rectangle DisplayRectangleForPreview { get { return new Rectangle(48, 26, Math.Max(100, Width - 96), Math.Max(50, Height - 52)); } }
        internal Rectangle ZoneRectangle(int index)
        {
            Rectangle display = DisplayRectangleForPreview;
            int horizontal = ZoneCount == 18 ? 6 : 4, vertical = ZoneCount == 18 ? 3 : 1;
            if (index < horizontal || (index >= horizontal + vertical && index < horizontal * 2 + vertical)) {
                bool top = index < horizontal;
                int part = top ? index : horizontal * 2 + vertical - 1 - index;
                int x0 = display.Left + display.Width * part / horizontal;
                int x1 = display.Left + display.Width * (part + 1) / horizontal;
                return new Rectangle(x0 + 2, top ? 3 : display.Bottom + 8, x1 - x0 - 4, 16);
            }
            bool right = index < horizontal + vertical;
            int row = right ? index - horizontal : ZoneCount - 1 - index;
            int y0 = display.Top + display.Height * row / vertical;
            int y1 = display.Top + display.Height * (row + 1) / vertical;
            return new Rectangle(right ? display.Right + 9 : display.Left - 31, y0 + 1, 22, Math.Max(1, y1 - y0 - 2));
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < ZoneCount; i++) {
                if (!ZoneRectangle(i).Contains(e.Location)) continue;
                Action<int> handler = TestRequested;
                if (handler != null) handler(i);
                return;
            }
            if (DisplayRectangleForPreview.Contains(e.Location)) {
                Action handler = CancelRequested;
                if (handler != null) handler();
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle display = DisplayRectangleForPreview;
            using (Brush b = new SolidBrush(Color.FromArgb(25, 32, 43))) g.FillRectangle(b, display);
            using (Pen p = new Pen(Color.FromArgb(68, 81, 101))) g.DrawRectangle(p, display);
            if (State.FrameWidth > 0 && State.FrameHeight > 0)
            {
                Rectangle r = State.ActiveArea;
                Rectangle active = new Rectangle(display.X + r.X * display.Width / State.FrameWidth, display.Y + r.Y * display.Height / State.FrameHeight, r.Width * display.Width / State.FrameWidth, r.Height * display.Height / State.FrameHeight);
                using (Brush b = new SolidBrush(Color.FromArgb(37, 52, 66))) g.FillRectangle(b, active);
            }
            using (Font font = new Font("Segoe UI", 10))
            using (Brush text = new SolidBrush(Color.FromArgb(170, 191, 208)))
            using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Click a numbered zone to test (8 seconds)\nClick the center to end the test", font, text, display, center);
            for (int i = 0; i < ZoneCount; i++)
            {
                Rectangle segment = ZoneRectangle(i);
                Color color = State.Colors != null && State.Colors.Length == ZoneCount ? State.Colors[i] : Color.FromArgb(50, 65, 85);
                if (color.A == 0) color = Color.FromArgb(45, 57, 75);
                using (Brush brush = new SolidBrush(color)) g.FillRectangle(brush, segment);
                using (Font f = new Font("Segoe UI", 8))
                using (Brush b = new SolidBrush(color.GetBrightness() > .55 ? Color.Black : Color.White))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) g.DrawString((i + 1).ToString(), f, b, segment, sf);
            }
        }
    }
}
