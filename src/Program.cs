using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Tap-Up")]
[assembly: System.Reflection.AssemblyDescription("Compact F13 tapper for Bongo Cat")]
[assembly: System.Reflection.AssemblyVersion("1.4.6.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace TapUp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += GamePatch.ResolveDependency;
            bool created;
            using (var instance = new Mutex(true, @"Local\TapUp.Clicker.v1", out created))
            {
                if (!created)
                {
                    IntPtr existing = FindWindow(null, "Tap-Up");
                    if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
                    return;
                }
                try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var engine = new TapEngine(new NativeInput()))
                using (var form = new MainForm(engine)) Application.Run(form);
                instance.ReleaseMutex();
            }
        }

        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    }

    internal sealed class MainForm : Form
    {
        private readonly TapEngine engine;
        private readonly TapButton toggle;
        private readonly CaptionButton minimize;
        private readonly CaptionButton close;
        private readonly Label counter;
        private readonly Label chestStatus;
        private readonly Label chestCounter;
        private readonly InputSafeCheckBox autoChests;
        private readonly InputSafeButton configureChests;
        private ChestSettings chestSettings;
        private readonly IChestCollector collector;
        private readonly Action<ChestSettings> saveSettings;
        private readonly Func<string, bool> installed;
        private bool gameInstalled;
        private volatile bool collectionSuspended;
        private string chestError;
        private bool configuring;
        private bool layingOut;
        private int contentHeight = 238;
        private int lastLayoutHeight;
        private float lastLayoutScale;
        private readonly Action refreshAction;
        private int refreshQueued;
        private volatile bool displayUpdates = true;
        private volatile bool closing;
        private readonly Font captionFont = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font counterFont = new Font("Segoe UI", 9);
        private readonly Icon appIcon;
        private float dpiScale = 1;
        private bool hotkey;
        private string notice;
        private static readonly Color Background = Color.FromArgb(19, 21, 26);
        private static readonly Color Muted = Color.FromArgb(141, 149, 165);
        private static readonly Color Accent = Color.FromArgb(165, 240, 197);
        internal static readonly Color Header = Color.FromArgb(28, 36, 35);

        internal MainForm(TapEngine engine) : this(engine, new ChestConnection(), null, null, null) { }

        internal MainForm(TapEngine engine, IChestCollector collector, ChestSettings settings,
            Action<ChestSettings> saveSettings, Func<string, bool> installed)
        {
            this.engine = engine;
            this.collector = collector;
            this.saveSettings = saveSettings ?? delegate(ChestSettings value) { value.Save(ChestSettings.FilePath); };
            this.installed = installed ?? GamePatch.Installed;
            Text = "Tap-Up";
            Size = new Size(328, 238);
            BackColor = Background;
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            appIcon = MakeIcon();
            Icon = appIcon;
            StartPosition = FormStartPosition.CenterScreen;
            // All geometry has one explicit DPI scale; never mix designer autoscaling
            // with custom-painted coordinates or controls created after autoscaling.
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            toggle = new TapButton { Text = "Tap-Up", TabIndex = 0 };
            toggle.Click += delegate { Toggle(); };
            minimize = new CaptionButton(false) { AccessibleName = "Свернуть", TabStop = false };
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            close = new CaptionButton(true) { AccessibleName = "Закрыть", TabStop = false };
            close.Click += delegate { Close(); };
            counter = new Label { AutoSize = false, Font = counterFont, ForeColor = Muted,
                Text = "Кликов совершено: 0", TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Счётчик кликов" };
            try { chestSettings = settings ?? ChestSettings.Load(ChestSettings.FilePath); }
            catch (Exception ex) { chestSettings = new ChestSettings(); chestError = "Не удалось прочитать настройки: " + ex.Message; }
            collector.Changed += ChestChanged;
            autoChests = new InputSafeCheckBox { Text = "Автосбор сундуков", Checked = chestSettings.Enabled,
                Font = counterFont, ForeColor = Accent, TabIndex = 1, AccessibleName = "Автосбор сундуков" };
            autoChests.CheckedChanged += delegate {
                RefreshInstallation();
                if (autoChests.Checked && !gameInstalled)
                {
                    ConfigureChests();
                    if (!gameInstalled) { autoChests.Checked = false; return; }
                }
                collectionSuspended = false;
                chestSettings.Enabled = autoChests.Checked;
                SaveChestSettings();
                ApplyAutoCollection();
                RefreshStatus();
            };
            configureChests = new InputSafeButton { Text = "Подключить…", Font = counterFont, TabIndex = 2,
                AccessibleName = "Подключить BongoCat", FlatStyle = FlatStyle.Flat, ForeColor = Muted };
            configureChests.Click += delegate { ConfigureChests(); };
            chestStatus = new StatusNotice { AutoSize = false, Font = counterFont, ForeColor = Muted,
                AccessibleName = "Состояние автосбора", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            chestCounter = new Label { AutoSize = false, Font = counterFont, ForeColor = Muted,
                AccessibleName = "Счётчик сундуков", Text = "Скинов собрано: 0\nЭмоций собрано: 0", TextAlign = ContentAlignment.MiddleLeft };
            Controls.AddRange(new Control[] { toggle, minimize, close, counter, autoChests, configureChests, chestCounter, chestStatus });
            refreshAction = delegate {
                Interlocked.Exchange(ref refreshQueued, 0);
                if (!IsDisposed && !Disposing) RefreshStatus();
            };
            engine.PulseCompleted += PulseCompleted;
            engine.Stopped += EngineStopped;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            RefreshInstallation();
            RefreshStatus();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshInstallation();
            ApplyAutoCollection();
            RefreshStatus();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (configureChests == null || configuring) return;
            bool before = gameInstalled;
            RefreshInstallation();
            if (before != gameInstalled) ApplyAutoCollection();
            RefreshStatus();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpi();
            int rounded = 2;
            try { DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int)); }
            catch (DllNotFoundException) { }
            hotkey = RegisterHotKey(Handle, 1, 0x4000, (uint)Keys.F8);
            if (!hotkey) notice = "F8 занята другим приложением";
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkey) UnregisterHotKey(Handle, 1);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312 && message.WParam.ToInt32() == 1)
            {
                if (!configuring) Toggle();
                else if (engine.Running) { engine.Stop(); RefreshStatus(); }
                return;
            }
            base.WndProc(ref message);
            if (message.Msg == 0x84 && message.Result.ToInt32() == 1)
            {
                long position = message.LParam.ToInt64();
                var point = PointToClient(new Point(unchecked((short)(position & 0xffff)), unchecked((short)((position >> 16) & 0xffff))));
                if (point.Y >= 0 && point.Y < 44 * dpiScale && point.X >= 0 && point.X < 248 * dpiScale)
                    message.Result = new IntPtr(2); // Native caption dragging and system menu.
            }
            if (message.Msg == 0x2e0) ApplyDpi();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style |= 0x00080000 | 0x00020000; // System menu and minimize; no resize/maximize.
                return parameters;
            }
        }

        private void ApplyDpi()
        {
            try { dpiScale = GetDpiForWindow(Handle) / 96f; }
            catch (EntryPointNotFoundException) { using (var graphics = CreateGraphics()) dpiScale = graphics.DpiX / 96f; }
            if (dpiScale <= 0) dpiScale = 1;
            LayoutContent();
        }

        private void LayoutContent()
        {
            if (layingOut || WindowState == FormWindowState.Minimized) return;
            layingOut = true;
            try
            {
                // Borderless window: set outer size directly. ClientSize accounting
                // can reserve an invisible caption when the system-menu style is set.
                bool hasStatistics = autoChests != null && autoChests.Checked;
                bool hasNotice = chestStatus != null && chestStatus.Text.Length != 0;
                contentHeight = hasStatistics || hasNotice ? 238 : 198;
                Size desired = new Size((int)Math.Round(328 * dpiScale), (int)Math.Round(contentHeight * dpiScale));
                if (chestCounter != null && lastLayoutHeight == contentHeight && lastLayoutScale == dpiScale && Size == desired) return;
                if (Size != desired) Size = desired;
                if (toggle != null) toggle.Bounds = ScaleBounds(16, 56, 296, 62);
                if (minimize != null) minimize.Bounds = ScaleBounds(248, 0, 40, 44);
                if (close != null) close.Bounds = ScaleBounds(288, 0, 40, 44);
                if (counter != null) counter.Bounds = ScaleBounds(19, 128, 293, 24);
                if (autoChests != null) autoChests.Bounds = ScaleBounds(19, 160, 174, 28);
                if (configureChests != null) configureChests.Bounds = ScaleBounds(200, 158, 112, 30);
                if (chestCounter != null) chestCounter.Bounds = ScaleBounds(19, 192, 174, 38);
                if (chestStatus != null)
                {
                    chestStatus.Bounds = ScaleBounds(200, 192, 112, 38);
                    chestStatus.Padding = new Padding((int)Math.Round(5 * dpiScale), 0, (int)Math.Round(3 * dpiScale), 0);
                }
                if (chestCounter != null) { lastLayoutHeight = contentHeight; lastLayoutScale = dpiScale; }
                Invalidate();
            }
            finally { layingOut = false; }
        }

        private Rectangle ScaleBounds(int x, int y, int width, int height)
        {
            return new Rectangle((int)Math.Round(x * dpiScale), (int)Math.Round(y * dpiScale),
                (int)Math.Round(width * dpiScale), (int)Math.Round(height * dpiScale));
        }

        private void Toggle()
        {
            notice = hotkey ? null : "F8 занята другим приложением";
            if (engine.Running) engine.Stop(); else engine.Start();
            RefreshStatus();
        }

        private void StopAll() { collector.Stop(); engine.Stop(); }

        private void ChestChanged() { QueueRefresh(); }

        private void RefreshInstallation()
        {
            try { gameInstalled = installed(chestSettings.GameDirectory); }
            catch (Exception ex) { gameInstalled = false; chestError = ex.Message; }
            if (configureChests == null) return;
            configureChests.Text = gameInstalled ? "✓ Подключено" : "Подключить…";
            configureChests.ForeColor = gameInstalled ? Accent : Muted;
        }

        private void ApplyAutoCollection()
        {
            if (autoChests.Checked && gameInstalled && !configuring && !collectionSuspended)
            {
                if (!collector.Running) collector.Start();
            }
            else collector.Stop();
        }

        private void SaveChestSettings()
        {
            try { saveSettings(chestSettings); chestError = null; }
            catch (Exception ex) { chestError = "Не удалось сохранить настройки: " + ex.Message; }
        }

        private void ConfigureChests()
        {
            if (configuring) return;
            configuring = true;
            try
            {
                collector.Stop();
                RefreshStatus();
                using (var dialog = new ChestSetupForm(chestSettings, appIcon))
                {
                    dialog.PathConnected += delegate(string path) {
                        chestSettings.GameDirectory = path;
                        SaveChestSettings();
                        RefreshStatus();
                    };
                    dialog.ShowDialog(this);
                }
            }
            finally
            {
                configuring = false;
                RefreshInstallation();
                ApplyAutoCollection();
                RefreshStatus();
            }
        }

        private void RefreshStatus()
        {
            bool active = engine.Running;
            toggle.Active = active;
            string title = active ? "Stop-Up" : "Tap-Up";
            if (toggle.Text != title) toggle.Text = title;
            if (toggle.AccessibleName != title) toggle.AccessibleName = title;
            string warning = engine.Error ?? notice;
            string countText = "Кликов совершено: " + engine.Sent.ToString("N0");
            if (warning != null) countText += engine.Error != null ? " · ошибка ввода" : " · F8 занята";
            if (counter.Text != countText) counter.Text = countText;
            Color color = warning == null ? Muted : Color.FromArgb(255, 169, 154);
            if (counter.ForeColor != color) counter.ForeColor = color;
            if (counter.AccessibleDescription != warning) counter.AccessibleDescription = warning;
            if (chestStatus != null)
            {
                chestCounter.Visible = autoChests.Checked;
                string chestText = "Скинов собрано: " + collector.SkinsCollected.ToString("N0") +
                    "\nЭмоций собрано: " + collector.EmotesCollected.ToString("N0");
                if (chestCounter.Text != chestText) chestCounter.Text = chestText;
                string status = chestError ?? (autoChests.Checked ?
                    collectionSuspended ? "Автосбор приостановлен" : !gameInstalled ? "Подключите BongoCat" : collector.Error : null) ?? "";
                string noticeText = status.Length == 0 ? "" : chestError != null ? "Ошибка\nнастроек" :
                    collectionSuspended ? "Сбор\nприостановлен" : !gameInstalled ? "Игра не\nподключена" :
                    status.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0 ? "Ошибка\nSteam" : "Нет связи\nс BongoCat";
                if (chestStatus.Text != noticeText) chestStatus.Text = noticeText;
                chestStatus.Visible = status.Length != 0;
                if (chestStatus.AccessibleDescription != status) chestStatus.AccessibleDescription = status;
                chestStatus.ForeColor = Color.FromArgb(255, 169, 154);
                LayoutContent();
            }
        }

        private void PulseCompleted(long count)
        {
            if (displayUpdates) QueueRefresh();
        }

        private void EngineStopped() { QueueRefresh(); }

        private void QueueRefresh()
        {
            // Post once per completed pulse, never poll. Coalesce only while the
            // UI is busy so a blocked window cannot accumulate stale callbacks.
            // Do not wait for the UI here: Stop() joins the worker.
            if (closing || !IsHandleCreated || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
            try { BeginInvoke(refreshAction); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref refreshQueued, 0); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            displayUpdates = WindowState != FormWindowState.Minimized;
            if (counter != null && !layingOut) RefreshStatus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.ScaleTransform(dpiScale, dpiScale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Header)) g.FillRectangle(brush, 0, 0, 328, 44);
            using (var pen = new Pen(Accent, 2))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 25, 30, 25, 15);
                g.DrawLines(pen, new[] { new Point(19, 21), new Point(25, 15), new Point(31, 21) });
            }
            Draw(g, "Tap-Up", captionFont, Color.FromArgb(235, 240, 239), new Rectangle(45, 0, 180, 44));
            using (var pen = new Pen(Color.FromArgb(49, 57, 57))) g.DrawRectangle(pen, 0, 0, 327, contentHeight - 1);
        }

        private static void Draw(Graphics g, string text, Font font, Color color, Rectangle rectangle,
            StringAlignment alignment = StringAlignment.Near)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(text, font, brush, rectangle, format);
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) { collectionSuspended = true; StopAll(); }
            else if (e.Mode == PowerModes.Resume) ResumeCollection();
        }

        private void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff ||
                e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
            { collectionSuspended = true; StopAll(); }
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect ||
                e.Reason == SessionSwitchReason.RemoteConnect) ResumeCollection();
        }

        private void ResumeCollection()
        {
            if (closing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate {
                if (closing || IsDisposed) return;
                collectionSuspended = false; ApplyAutoCollection(); RefreshStatus();
            })); }
            catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            StopAll();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                SystemEvents.PowerModeChanged -= PowerChanged;
                SystemEvents.SessionSwitch -= SessionChanged;
                engine.PulseCompleted -= PulseCompleted;
                engine.Stopped -= EngineStopped;
                collector.Changed -= ChestChanged;
                collector.Dispose();
                captionFont.Dispose();
                counterFont.Dispose();
                appIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);

        private static Icon MakeIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(Accent, 3))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                pen.StartCap = pen.EndCap = LineCap.Round;
                graphics.DrawLine(pen, 16, 26, 16, 6);
                graphics.DrawLines(pen, new[] { new Point(8, 14), new Point(16, 6), new Point(24, 14) });
                IntPtr handle = bitmap.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }

    internal sealed class StatusNotice : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(ForeColor)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class InputSafeCheckBox : CheckBox
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F13) { e.Handled = true; e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F13) { e.Handled = true; e.SuppressKeyPress = true; return; }
            base.OnKeyUp(e);
        }
    }

    internal class InputSafeButton : Button
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F13) { e.Handled = true; e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            // ButtonBase resets its mouse-pressed flags on ANY key-up. An F13
            // release during a real mouse click used to cancel Stop-Up's Click.
            if (e.KeyCode == Keys.F13) { e.Handled = true; e.SuppressKeyPress = true; return; }
            base.OnKeyUp(e);
        }
    }

    internal sealed class CaptionButton : InputSafeButton
    {
        private readonly bool isClose;
        private bool hover;
        internal CaptionButton(bool close)
        {
            isClose = close;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(hover ? (isClose ? Color.FromArgb(190, 48, 57) : Color.FromArgb(48, 57, 56)) : MainForm.Header);
            float scale = e.Graphics.DpiX / 96f;
            float x = Width / 2f, y = Height / 2f, half = 4 * scale;
            using (var pen = new Pen(Color.FromArgb(220, 226, 225), scale))
            {
                if (isClose)
                {
                    e.Graphics.DrawLine(pen, x - half, y - half, x + half, y + half);
                    e.Graphics.DrawLine(pen, x - half, y + half, x + half, y - half);
                }
                else e.Graphics.DrawLine(pen, x - half, y, x + half, y);
            }
        }
    }

    internal sealed class TapButton : InputSafeButton
    {
        private bool active, hover, pressed;
        internal bool Active { get { return active; } set { if (active != value) { active = value; Invalidate(); } } }

        internal TapButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 16, FontStyle.Bold);
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Tap-Up";
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96f;
            RectangleF r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = Rounded(r, 13 * scale))
            {
                Color background = active ? Color.FromArgb(40, 47, 46) :
                    pressed ? Color.FromArgb(130, 202, 163) : hover ? Color.FromArgb(186, 250, 213) : Color.FromArgb(165, 240, 197);
                using (var brush = new SolidBrush(background)) g.FillPath(brush, path);
                if (active || (Focused && ShowFocusCues))
                    using (var pen = new Pen(active ? Color.FromArgb(90, 125, 107) : Color.FromArgb(225, 255, 239), scale)) g.DrawPath(pen, path);
            }
            Color ink = active ? Color.FromArgb(190, 239, 211) : Color.FromArgb(21, 44, 31);
            Size textSize = TextRenderer.MeasureText(Text, Font);
            float left = (Width - textSize.Width - 27 * scale) / 2;
            float center = Height / 2f;
            using (var brush = new SolidBrush(ink))
            {
                if (active) g.FillRectangle(brush, left, center - 5 * scale, 10 * scale, 10 * scale);
                else g.FillPolygon(brush, new[] { new PointF(left, center - 6 * scale), new PointF(left, center + 6 * scale), new PointF(left + 10 * scale, center) });
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(left + 23 * scale), 0,
                textSize.Width, Height), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
