using System;
using System.Threading;

namespace TapUp
{
    internal sealed class TapEngine : IDisposable
    {
        internal const int HoldMilliseconds = 60;
        internal const int GapMilliseconds = 60;
        private readonly IInput input;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly object lifecycle = new object();
        private Thread worker;
        private volatile bool running, paused;
        private volatile string error;
        private long sent;
        private bool disposed;
        internal event Action Stopped;
        internal event Action<long> PulseCompleted;

        internal TapEngine(IInput input) { this.input = input; }
        internal bool Running { get { return running; } }
        internal bool Paused { get { return paused; } }
        internal string Error { get { return error; } }
        internal long Sent { get { return Interlocked.Read(ref sent); } }

        internal void Start()
        {
            lock (lifecycle)
            {
                if (disposed) throw new ObjectDisposedException("TapEngine");
                if (worker != null && worker.IsAlive) return;
                stop.Reset();
                error = null;
                paused = false;
                running = true;
                worker = new Thread(Run) { IsBackground = true, Name = "Tap-Up input" };
                try { worker.Start(); }
                catch { running = false; worker = null; throw; }
            }
        }

        internal void Stop()
        {
            lock (lifecycle)
            {
                if (disposed) return;
                stop.Set();
                // Wait until the finally block has released our key. No overlapping loops.
                if (worker != null) worker.Join();
                worker = null;
                running = false;
                paused = false;
            }
        }

        private void Run()
        {
            bool mayBeDown = false;
            try
            {
                while (!stop.WaitOne(0))
                {
                    paused = input.ShouldPause();
                    if (paused) { stop.WaitOne(20); continue; }
                    // Mark before sending: an exception can leave the delivery uncertain.
                    mayBeDown = true;
                    input.Send(false);
                    stop.WaitOne(HoldMilliseconds);
                    input.Send(true);
                    mayBeDown = false;
                    long count = Interlocked.Increment(ref sent);
                    var completed = PulseCompleted;
                    if (completed != null) completed(count);
                    if (stop.WaitOne(GapMilliseconds)) break;
                }
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                if (mayBeDown)
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try { input.Send(true); mayBeDown = false; break; }
                        catch (Exception ex) { error = ex.Message; }
                    }
                    if (mayBeDown) error = "Не удалось отпустить F13. Нажмите и отпустите F13 либо завершите сеанс Windows. " + error;
                }
                paused = false;
                running = false;
                var stopped = Stopped;
                if (stopped != null) stopped();
            }
        }

        public void Dispose()
        {
            lock (lifecycle)
            {
                if (disposed) return;
                Stop();
                disposed = true;
                stop.Dispose();
            }
        }
    }
}
