using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace TapUp
{
    internal static class WindowsStartup
    {
        internal const string Argument = "--windows-startup";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Tap-Up";
        private const int WaitTimeoutMs = 60000;

        internal static string CommandFor(string executablePath)
        {
            return "\"" + executablePath + "\" " + Argument;
        }

        internal static bool IsEnabled()
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                return run != null && String.Equals(run.GetValue(ValueName) as string,
                    CommandFor(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                string command = CommandFor(Application.ExecutablePath);
                if (command.Length > 260) throw new InvalidOperationException("Путь к Tap-Up.exe слишком длинный для автозапуска Windows.");
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                    run.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (run != null) run.DeleteValue(ValueName, false);
            }
        }

        internal static bool WaitForLogonApps(string instanceName)
        {
            if (InstanceExists(instanceName)) return false;
            Thread thread = Thread.CurrentThread;
            ThreadPriority originalPriority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                bool waitForBridge = AutocollectionConfigured();
                int sessionId;
                using (Process current = Process.GetCurrentProcess()) sessionId = current.SessionId;
                return WaitForReady(() => AppsReady(sessionId, waitForBridge), () => InstanceExists(instanceName), WaitTimeoutMs);
            }
            finally { thread.Priority = originalPriority; }
        }

        internal static bool WaitForReady(Func<bool> ready, Func<bool> instanceExists, int timeoutMs)
        {
            var timer = Stopwatch.StartNew();
            while (!instanceExists())
            {
                int remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0 || ready()) return true;
                Thread.Sleep(Math.Min(1000, remaining));
            }
            return false;
        }

        private static bool InstanceExists(string name)
        {
            Mutex instance;
            if (!Mutex.TryOpenExisting(name, out instance)) return false;
            instance.Dispose();
            return true;
        }

        private static bool AutocollectionConfigured()
        {
            try
            {
                var settings = ChestSettings.Load(ChestSettings.FilePath);
                return settings.Enabled && settings.Configured;
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
            catch (XmlException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static bool AppsReady(int sessionId, bool waitForBridge)
        {
            int gameId = ChestConnection.FindGameId(sessionId);
            if (gameId == 0) return false;
            Process[] steam = Process.GetProcessesByName("steam");
            try
            {
                foreach (Process process in steam)
                    if (ChestConnection.InSession(process, sessionId))
                        return !waitForBridge || WaitNamedPipe(@"\\.\pipe\" + ChestConnection.PipeName(gameId), 1);
                return false;
            }
            finally { foreach (Process process in steam) process.Dispose(); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool WaitNamedPipe(string name, uint timeout);
    }
}
