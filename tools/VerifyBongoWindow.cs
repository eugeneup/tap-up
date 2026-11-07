// Interactive-desktop host for the same compatibility probe used by build.ps1.
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal static class VerifyBongoWindow
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        var form = new Form { Text = "Tap-Up input check", ClientSize = new Size(850, 320), StartPosition = FormStartPosition.CenterScreen };
        var output = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10), ScrollBars = ScrollBars.Both,
            Text = "Checking 60/60 ms F13 against the installed game's original input handler..." };
        form.Controls.Add(output);
        form.Shown += delegate {
            new Thread(delegate() {
                var log = new StringWriter();
                Console.SetOut(log); Console.SetError(log);
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Steam\steamapps\common\BongoCat\BongoCat_Data\Managed\Assembly-CSharp.dll");
                int result = VerifyBongo.Run(new[] { path });
                string report = log.ToString();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bongo-verification.txt"), report);
                form.BeginInvoke(new Action(delegate { output.Text = report; form.Text = "Tap-Up input check - " + (result == 0 ? "PASS" : "FAIL"); }));
            }) { IsBackground = true }.Start();
        };
        Application.Run(form);
    }
}
