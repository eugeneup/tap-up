using System;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using TapUp;

internal static class ChestProbe
{
    private static int Main(string[] args)
    {
        int mask = args.Length > 0 ? Int32.Parse(args[0]) : 0;
        int seconds = args.Length > 1 ? Int32.Parse(args[1]) : 45;
        int seen = 0, successes = 0;
        var baseline = new[] { -1, -1 };
        IntPtr claimForeground = IntPtr.Zero;
        POINT claimCursor = new POINT();
        using (var connection = new ChestConnection())
        {
            connection.Message += delegate(string line) {
                Console.WriteLine(line);
                if (line.StartsWith("STATE\t", StringComparison.Ordinal))
                {
                    ChestState state = ChestState.Parse(line);
                    AddBit(ref seen, 1 << state.Index);
                    if (baseline[state.Index] < 0) baseline[state.Index] = state.Collected;
                    if (state.Pending)
                    {
                        claimForeground = GetForegroundWindow(); GetCursorPos(out claimCursor);
                        Console.WriteLine("CLAIM_FOREGROUND " + ForegroundProcess(claimForeground));
                    }
                    if (state.Collected > baseline[state.Index])
                    {
                        POINT now; GetCursorPos(out now);
                        Console.WriteLine("SUCCESS_UI foreground=" + ForegroundProcess(GetForegroundWindow()) + " unchanged=" +
                            (GetForegroundWindow() == claimForeground) + " cursor_unchanged=" + (now.X == claimCursor.X && now.Y == claimCursor.Y));
                        AddBit(ref successes, 1 << state.Index);
                    }
                }
            };
            connection.Start(mask);
            var clock = Stopwatch.StartNew();
            while (connection.Running && clock.Elapsed.TotalSeconds < seconds)
            {
                if (mask == 0 ? Volatile.Read(ref seen) == 3 : (Volatile.Read(ref successes) & mask) == mask) break;
                Thread.Sleep(20);
            }
            bool passed = mask == 0 ? seen == 3 : (successes & mask) == mask;
            Console.WriteLine("FINAL " + connection.Status.Replace('\n', '|'));
            connection.Stop();
            return passed ? 0 : 1;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { internal int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    private static string ForegroundProcess(IntPtr window)
    {
        uint id; GetWindowThreadProcessId(window, out id);
        try { using (var process = Process.GetProcessById((int)id)) return process.ProcessName + " pid=" + id + " hwnd=" + window; }
        catch (ArgumentException) { return "none"; }
    }
    private static void AddBit(ref int target, int bit)
    {
        int prior;
        do { prior = Volatile.Read(ref target); } while (Interlocked.CompareExchange(ref target, prior | bit, prior) != prior);
    }
}
