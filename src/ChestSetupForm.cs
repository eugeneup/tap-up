using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TapUp
{
    internal sealed class ChestSetupForm : Form
    {
        internal ChestSettings Settings { get; private set; }
        internal event Action<string> PathConnected;
        internal event Action WindowPreferencesChanged;
        private static readonly Size DialogSize = new Size(440, 349);
        private readonly Dictionary<Control, Rectangle> layout = new Dictionary<Control, Rectangle>();
        private readonly Font dialogFont = new Font("Segoe UI", 9);
        private readonly Font sectionFont;
        private readonly TextBox gamePath;
        private readonly Label status, error;
        private readonly InputSafeButton connection, close;
        private readonly ToolTip help = new ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 15000, ShowAlways = true };
        private readonly Func<string, bool> installed;
        private readonly Action<string, bool> change;

        internal ChestSetupForm(ChestSettings settings, Icon icon) : this(settings, icon, GamePatch.Installed,
            delegate(string path, bool add) { if (add) GamePatch.Install(path); else GamePatch.Uninstall(path); }) { }

        internal ChestSetupForm(ChestSettings settings, Icon icon, Func<string, bool> installed, Action<string, bool> change,
            Func<bool> readStartup = null, Action<bool> changeStartup = null)
        {
            Settings = settings.Clone();
            this.installed = installed;
            this.change = change;
            Text = "Подключение BongoCat";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = DialogSize;
            BackColor = Color.FromArgb(19, 21, 26);
            ForeColor = Color.White;
            Font = dialogFont;
            sectionFont = new Font(dialogFont, FontStyle.Bold);

            var systemTitle = new Label { Text = "Системные", Font = sectionFont, Bounds = new Rectangle(16, 12, 408, 24),
                TextAlign = ContentAlignment.MiddleLeft };
            var minimizeToTray = new OutlineCheckBox { Text = "Сворачивать в область уведомлений",
                Checked = Settings.MinimizeToTray, Bounds = new Rectangle(20, 36, 404, 28) };
            var minimizeAtLogon = new OutlineCheckBox { Text = "Запускать свёрнутым при старте Windows",
                Checked = Settings.MinimizeAtLogon, Enabled = false, Bounds = new Rectangle(20, 100, 404, 28) };
            EventHandler saveWindowPreferences = delegate {
                Settings.MinimizeToTray = minimizeToTray.Checked;
                Settings.MinimizeAtLogon = minimizeAtLogon.Checked;
                var changed = WindowPreferencesChanged;
                if (changed != null) changed();
            };
            minimizeToTray.CheckedChanged += saveWindowPreferences;
            minimizeAtLogon.CheckedChanged += saveWindowPreferences;
            var startWithWindows = new OutlineCheckBox { Text = "Автозапуск при старте Windows", AutoCheck = false,
                Bounds = new Rectangle(20, 68, 404, 28) };
            startWithWindows.CheckedChanged += delegate { minimizeAtLogon.Enabled = startWithWindows.Checked; };
            var saveStartup = changeStartup ?? WindowsStartup.SetEnabled;
            startWithWindows.Click += delegate {
                bool enable = !startWithWindows.Checked;
                try
                {
                    saveStartup(enable);
                    startWithWindows.Checked = enable;
                    SetError("");
                }
                catch (Exception ex) { SetError("Не удалось изменить автозапуск Windows.\n" + ex.Message); }
            };
            var chestTitle = new Label { Text = "Автосбор сундуков", Font = sectionFont, Bounds = new Rectangle(16, 140, 408, 24),
                TextAlign = ContentAlignment.MiddleLeft };
            var title = new Label { Text = "Путь до BongoCat", Bounds = new Rectangle(16, 169, 126, 26),
                TextAlign = ContentAlignment.MiddleLeft };
            var info = new PathInfoButton { Bounds = new Rectangle(146, 171, 20, 22),
                AccessibleName = "Как найти папку BongoCat", ForeColor = Color.FromArgb(165, 174, 188) };
            const string instructions = "В Steam откройте свойства Bongo Cat → «Установленные файлы» → «Обзор…».\nВыберите папку, в которой находится BongoCat.exe.";
            help.SetToolTip(info, instructions);
            info.AccessibleDescription = instructions;
            info.Click += delegate { help.Show(instructions, info, 0, info.Height + 4, 15000); };
            gamePath = new TextBox { Text = settings.GameDirectory, Bounds = new Rectangle(16, 207, 310, 26),
                AccessibleName = "Папка BongoCat", BackColor = Color.FromArgb(28, 36, 35), ForeColor = Color.White };
            var browse = new InputSafeButton { Text = "Папка…", Bounds = new Rectangle(336, 203, 88, 30) };
            browse.Click += delegate {
                using (var picker = new FolderBrowserDialog { Description = "Папка, содержащая BongoCat.exe", SelectedPath = gamePath.Text })
                    if (picker.ShowDialog(this) == DialogResult.OK) gamePath.Text = picker.SelectedPath;
            };
            connection = new InputSafeButton { Bounds = new Rectangle(16, 251, 194, 34),
                AccessibleName = "Управление подключением" };
            connection.Click += delegate { ToggleConnection(); };
            status = new Label { Bounds = new Rectangle(230, 251, 194, 34), TextAlign = ContentAlignment.MiddleRight,
                AccessibleName = "Статус подключения" };
            error = new Label { Bounds = new Rectangle(16, 294, 408, 44), Visible = false,
                AccessibleName = "Ошибка настроек", ForeColor = Color.FromArgb(255, 169, 154), AutoEllipsis = true };
            close = new InputSafeButton { Text = "Закрыть", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(332, 301, 92, 32) };
            Controls.AddRange(new Control[] { systemTitle, minimizeToTray, startWithWindows, minimizeAtLogon, chestTitle, title, info,
                gamePath, browse, connection, status, error, close });
            AcceptButton = close; CancelButton = close;
            gamePath.TextChanged += delegate { RefreshInstallation(); };
            foreach (Control control in Controls) layout.Add(control, control.Bounds);
            RefreshInstallation();
            try { startWithWindows.Checked = (readStartup ?? WindowsStartup.IsEnabled)(); }
            catch (Exception ex)
            {
                startWithWindows.Enabled = false;
                SetError("Не удалось прочитать настройку автозапуска Windows.\n" + ex.Message);
            }
        }

        private void ToggleConnection()
        {
            try
            {
                bool add = !installed(gamePath.Text);
                change(gamePath.Text, add);
                if (add)
                {
                    Settings.GameDirectory = Path.GetFullPath(gamePath.Text);
                    var connectedPath = PathConnected;
                    if (connectedPath != null) connectedPath(Settings.GameDirectory);
                }
                RefreshInstallation();
            }
            catch (Exception ex) { SetError(ex.Message); }
        }

        private void RefreshInstallation()
        {
            try
            {
                bool connected = installed(gamePath.Text);
                bool valid = File.Exists(Path.Combine(gamePath.Text, "BongoCat.exe"));
                connection.Text = connected ? "Отключить" : "Подключить";
                status.Text = connected ? "Подключение установлено" : "Не подключено";
                status.ForeColor = connected ? MainForm.Accent : MainForm.Muted;
                status.AccessibleDescription = status.Text;
                connection.Enabled = valid;
                SetError("");
            }
            catch (Exception ex) { SetError(ex.Message); }
        }

        private void SetError(string text)
        {
            error.Text = text;
            error.AccessibleDescription = text;
            error.Visible = text.Length != 0;
            if (IsHandleCreated) ApplyDpi();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyDpi(); }
        protected override void WndProc(ref Message message) { base.WndProc(ref message); if (message.Msg == 0x2e0) ApplyDpi(); }
        private void ApplyDpi()
        {
            float scale;
            try { scale = GetDpiForWindow(Handle) / 96f; }
            catch (EntryPointNotFoundException) { using (var graphics = CreateGraphics()) scale = graphics.DpiX / 96f; }
            if (scale <= 0) scale = 1;
            int extra = error != null && error.Text.Length != 0 ? 60 : 0;
            ClientSize = new Size((int)Math.Round(DialogSize.Width * scale), (int)Math.Round((DialogSize.Height + extra) * scale));
            foreach (var entry in layout)
            {
                Rectangle r = entry.Value;
                if (entry.Key == close) r.Y += extra;
                entry.Key.Bounds = new Rectangle((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale),
                    (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale));
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { help.Dispose(); sectionFont.Dispose(); dialogFont.Dispose(); }
            base.Dispose(disposing);
        }
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    }

    internal sealed class OutlineCheckBox : InputSafeCheckBox
    {
        internal OutlineCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96f;
            float size = 12 * scale;
            float top = (Height - size) / 2;
            float corner = 4 * scale;
            var mark = new RectangleF(scale, top, size, size);
            Color markColor = Enabled ? MainForm.Accent : MainForm.Muted;
            using (var outline = new GraphicsPath())
            using (var pen = new Pen(markColor, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                outline.AddArc(mark.Left, mark.Top, corner, corner, 180, 90);
                outline.AddArc(mark.Right - corner, mark.Top, corner, corner, 270, 90);
                outline.AddArc(mark.Right - corner, mark.Bottom - corner, corner, corner, 0, 90);
                outline.AddArc(mark.Left, mark.Bottom - corner, corner, corner, 90, 90);
                outline.CloseFigure();
                e.Graphics.DrawPath(pen, outline);
                if (Checked)
                    e.Graphics.DrawLines(pen, new[] { new PointF(4 * scale, top + 6 * scale),
                        new PointF(6 * scale, top + 8 * scale), new PointF(10 * scale, top + 4 * scale) });
            }
            int labelLeft = (int)Math.Round(18 * scale);
            Color labelColor = Enabled && !Checked ? ForeColor : markColor;
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(labelLeft, 0, Width - labelLeft, Height),
                labelColor, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, labelColor, BackColor);
        }
    }

    internal sealed class PathInfoButton : InputSafeButton
    {
        internal PathInfoButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Text = "i";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96f;
            float size = 14 * scale;
            var circle = new RectangleF((Width - size) / 2, (Height - size) / 2, size, size);
            using (var pen = new Pen(ForeColor, scale)) e.Graphics.DrawEllipse(pen, circle);
            TextRenderer.DrawText(e.Graphics, "i", Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }
    }
}
