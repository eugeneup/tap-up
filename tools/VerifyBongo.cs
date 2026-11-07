// Runs the installed game's keyboard polling code in THIS process, never in the game.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using TapUp;

internal static class VerifyBongo
{
    private static int Main(string[] args) { return Run(args); }
    internal static int Run(string[] args)
    {
        try
        {
            string path = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
                string dependency = Path.Combine(Path.GetDirectoryName(path), new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
            };
            var assembly = Assembly.LoadFrom(path);
            var type = assembly.GetType("GlobalKeyHook.WinKeyHook", true);
            var hook = Activator.CreateInstance(type);
            var init = type.GetMethod("Init");
            var process = type.GetMethod("ProcessInput");
            Console.WriteLine("Game assembly SHA256: " + Hash(path));
            // Isolate F13 in the test process so actual user typing does not pollute results.
            foreach (int mode in new[] { 16, 50, 0 })
            {
                init.Invoke(hook, null);
                type.GetField("BUTTONS", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SetValue(null, new int[] { NativeInput.Key });
                type.GetField("WasPressed", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SetValue(null, new bool[1]);
                int counted = 0;
                long first = -1;
                int period = mode == 0 ? 50 : mode;
                using (var engine = new TapEngine(new NativeInput()))
                {
                    engine.Start();
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 4000)
                    {
                        int taps = (int)process.Invoke(hook, new object[] { false });
                        counted += taps;
                        if (taps > 0 && first < 0) first = clock.ElapsedMilliseconds;
                        if (mode == 0 && counted > 0) period = 16;
                        Thread.Sleep(period);
                    }
                    engine.Stop();
                    // Confirm that the game's release branch sees the key up.
                    Thread.Sleep(60); process.Invoke(hook, new object[] { false });
                    Console.WriteLine("Polling " + (mode == 0 ? "idle 50 -> active 16" : mode.ToString()) + " ms: sent=" + engine.Sent + ", game handler counted=" + counted + ", first=" + first + " ms");
                    if (engine.Error != null) throw new Exception(engine.Error);
                    if (counted < (mode == 50 ? 5 : 20)) throw new Exception("Insufficient counts in original game handler.");
                    if ((NativeInput.GetAsyncKeyState(NativeInput.Key) & 0x8000) != 0) throw new Exception("F13 remained held.");
                }
                Thread.Sleep(120);
            }
            Console.WriteLine("PASS installed Bongo Cat keyboard handler recognizes Tap-Up 60/60 ms input.");
            Console.WriteLine("This verifies the input handler, not the in-game score or long-running item rewards.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static string Hash(string path)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }
}
