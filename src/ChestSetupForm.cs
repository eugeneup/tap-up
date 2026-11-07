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
        private readonly Dictionary<Control, Rectangle> layout = new Dictionary<Control, Rectangle>();
        private readonly Font dialogFont = new Font("Segoe UI", 9);
        private readonly TextBox gamePath;
        private readonly Label status, error;
        private readonly InputSafeButton connection, close;
        private readonly ToolTip help = new ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 15000, ShowAlways = true };
        private readonly Func<string, bool> installed;
        private readonly Action<string, bool> change;
        private bool connected;

        internal ChestSetupForm(ChestSettings settings, Icon icon) : this(settings, icon, GamePatch.Installed,
            delegate(string path, bool add) { if (add) GamePatch.Install(path); else GamePatch.Uninstall(path); }) { }

        internal ChestSetupForm(ChestSettings settings, Icon icon, Func<string, bool> installed, Action<string, bool> change)
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
            ClientSize = new Size(440, 194);
            BackColor = Color.FromArgb(19, 21, 26);
            ForeColor = Color.White;
            Font = dialogFont;

            var title = new Label { Text = "Путь до BongoCat", Bounds = new Rectangle(16, 14, 126, 26),
                TextAlign = ContentAlignment.MiddleLeft };
            var info = new PathInfoButton { Bounds = new Rectangle(146, 16, 20, 22),
                AccessibleName = "Как найти папку BongoCat", ForeColor = Color.FromArgb(165, 174, 188) };
            const string instructions = "В Steam откройте свойства Bongo Cat → «Установленные файлы» → «Обзор…».\nВыберите папку, в которой находится BongoCat.exe.";
            help.SetToolTip(info, instructions);
            info.AccessibleDescription = instructions;
            info.Click += delegate { help.Show(instructions, info, 0, info.Height + 4, 15000); };
            gamePath = new TextBox { Text = settings.GameDirectory, Bounds = new Rectangle(16, 52, 310, 26),
                AccessibleName = "Папка BongoCat", BackColor = Color.FromArgb(28, 36, 35), ForeColor = Color.White };
            var browse = new InputSafeButton { Text = "Папка…", Bounds = new Rectangle(336, 48, 88, 30) };
            browse.Click += delegate {
                using (var picker = new FolderBrowserDialog { Description = "Папка, содержащая BongoCat.exe", SelectedPath = gamePath.Text })
                    if (picker.ShowDialog(this) == DialogResult.OK) gamePath.Text = picker.SelectedPath;
            };
            status = new Label { Bounds = new Rectangle(16, 96, 204, 34), TextAlign = ContentAlignment.MiddleLeft,
                AccessibleName = "Статус подключения" };
            connection = new InputSafeButton { Bounds = new Rectangle(230, 96, 194, 34), AccessibleName = "Управление подключением" };
            connection.Click += delegate { ToggleConnection(); };
            error = new Label { Bounds = new Rectangle(16, 142, 408, 44), Visible = false,
                AccessibleName = "Ошибка подключения", ForeColor = Color.FromArgb(255, 169, 154), AutoEllipsis = true };
            close = new InputSafeButton { Text = "Закрыть", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(332, 146, 92, 32) };
            Controls.AddRange(new Control[] { title, info, gamePath, browse, status, connection, error, close });
            AcceptButton = close; CancelButton = close;
            gamePath.TextChanged += delegate { RefreshInstallation(); };
            foreach (Control control in Controls) layout.Add(control, control.Bounds);
            RefreshInstallation();
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
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void RefreshInstallation()
        {
            try
            {
                connected = installed(gamePath.Text);
                bool valid = File.Exists(Path.Combine(gamePath.Text, "BongoCat.exe"));
                status.Text = connected ? "✅ Подключение установлено" : "Подключение не установлено";
                status.ForeColor = connected ? Color.FromArgb(165, 240, 197) : Color.FromArgb(165, 174, 188);
                status.AccessibleDescription = status.Text;
                connection.Text = connected ? "Отключить" : "Подключить";
                connection.Enabled = valid;
                error.Text = "";
                error.Visible = false;
                if (IsHandleCreated) ApplyDpi();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void ShowError(string text)
        {
            error.Text = text;
            error.AccessibleDescription = text;
            error.Visible = true;
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
            ClientSize = new Size((int)Math.Round(440 * scale), (int)Math.Round((194 + extra) * scale));
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
            if (disposing) { help.Dispose(); dialogFont.Dispose(); }
            base.Dispose(disposing);
        }
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
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
