using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace TapUp
{
    internal interface IChestCollector : IDisposable
    {
        event Action Changed;
        bool Running { get; }
        string Status { get; }
        string Error { get; }
        long Collected { get; }
        long SkinsCollected { get; }
        long EmotesCollected { get; }
        void Start();
        void Stop();
    }

    // A session belongs to this Tap-Up process. Existing game totals establish
    // a baseline, and reconnecting to the same game must not count them twice.
    internal sealed class ChestSessionCounter
    {
        private readonly Dictionary<int, int[]> observations = new Dictionary<int, int[]>();
        private long collected, skins, emotes;
        internal long Collected { get { return Interlocked.Read(ref collected); } }
        internal long SkinsCollected { get { return Interlocked.Read(ref skins); } }
        internal long EmotesCollected { get { return Interlocked.Read(ref emotes); } }
        internal void Observe(int game, ChestState state)
        {
            if (state.Index < 0 || state.Index > 1 || state.Collected < 0) throw new InvalidDataException("Invalid chest total");
            int[] previous;
            if (!observations.TryGetValue(game, out previous))
            {
                previous = new[] { -1, -1 };
                observations.Add(game, previous);
            }
            int before = previous[state.Index];
            if (before >= 0 && state.Collected > before)
            {
                int delta = state.Collected - before;
                if (state.Index == 0) Interlocked.Add(ref skins, delta); else Interlocked.Add(ref emotes, delta);
                Interlocked.Add(ref collected, delta);
            }
            previous[state.Index] = state.Collected;
        }
    }

    internal sealed class ChestState
    {
        internal int Index, Remaining, Collected, Attempts, Item;
        internal bool Ready, Affordable, Pending;
        internal string Error;
        internal static ChestState Parse(string line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 10 || parts[0] != "STATE") throw new InvalidDataException("Invalid chest state");
            return new ChestState { Index = Int32.Parse(parts[1]), Remaining = Int32.Parse(parts[2]), Ready = parts[3] == "1",
                Affordable = parts[4] == "1", Pending = parts[5] == "1", Collected = Int32.Parse(parts[6]),
                Attempts = Int32.Parse(parts[7]), Item = Int32.Parse(parts[8]), Error = parts[9] };
        }
    }

    internal sealed class ChestConnection : IChestCollector
    {
        internal const string SteamExchangeError = "Не удалось собрать сундук: ошибка Steam.";
        private readonly object gate = new object();
        private readonly object lifecycle = new object();
        private readonly ManualResetEvent stopped = new ManualResetEvent(false);
        private NamedPipeClientStream pipe;
        private StreamWriter writer;
        private Thread worker;
        private volatile bool stopping, running;
        private bool disposed;
        private volatile string status = "Автосбор выключен";
        private volatile string error;
        private readonly string[] gameErrors = new string[2];
        private readonly ChestSessionCounter sessionCounter = new ChestSessionCounter();
        private readonly string[] descriptions = { "Обычный: ожидание игры", "Эмоции: ожидание игры" };
        private readonly Func<int> gameId;
        public event Action Changed;
        internal event Action<string> Message;
        public string Status { get { return status; } }
        public string Error { get { return error; } }
        public long Collected { get { return sessionCounter.Collected; } }
        public long SkinsCollected { get { return sessionCounter.SkinsCollected; } }
        public long EmotesCollected { get { return sessionCounter.EmotesCollected; } }
        public bool Running { get { return running; } }
        internal ChestConnection() : this(() => FindGameId()) { }
        internal ChestConnection(Func<int> gameId) { this.gameId = gameId; }
        internal static int FindGameId(int? sessionId = null)
        {
            Process[] games = Process.GetProcessesByName("BongoCat");
            try
            {
                int id = 0;
                foreach (Process game in games)
                {
                    if (sessionId.HasValue && !InSession(game, sessionId.Value)) continue;
                    if (id != 0) return 0;
                    id = game.Id;
                }
                return id;
            }
            finally { foreach (Process game in games) game.Dispose(); }
        }

        internal static bool InSession(Process process, int sessionId)
        {
            try { return process.SessionId == sessionId; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        internal static string PipeName(int gameId) { return "TapUp.BongoCat." + gameId; }

        public void Start() { Start(3); }
        internal void Start(int mask)
        {
            if (mask < 0 || mask > 3) throw new ArgumentOutOfRangeException("mask");
            lock (lifecycle)
            {
                if (disposed) throw new ObjectDisposedException("ChestConnection");
                if (worker != null && worker.IsAlive) return;
                stopped.Reset();
                stopping = false;
                error = null;
                gameErrors[0] = gameErrors[1] = null;
                running = true;
                SetStatus("Подключение к событиям BongoCat…");
                worker = new Thread(delegate() { Run(mask); }) { IsBackground = true, Name = "Tap-Up chest connection" };
                try { worker.Start(); }
                catch { running = false; worker = null; throw; }
            }
        }

        private void Run(int mask)
        {
            try
            {
                int id = gameId();
                if (id == 0) throw new InvalidOperationException("Запустите один экземпляр BongoCat.");
                var connection = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous);
                lock (gate) pipe = connection;
                connection.Connect(2000);
                using (var reader = new StreamReader(connection))
                using (var output = new StreamWriter(connection) { AutoFlush = true })
                {
                    lock (gate) writer = output;
                    if (stopping) return;
                    string line;
                    bool hello = false;
                    while (!stopping && (line = reader.ReadLine()) != null)
                    {
                        if (line == "HELLO\t1.4.0")
                        {
                            hello = true;
                            Send(mask == 0 ? "STATUS" : "START\t" + mask);
                        }
                        else if (!hello) throw new InvalidDataException("Несовместимая версия подключения BongoCat.");
                        else if (line == "STOPPED") stopped.Set();
                        else if (line.StartsWith("STATE\t", StringComparison.Ordinal)) Update(id, ChestState.Parse(line));
                        else if (line.StartsWith("ERROR\t", StringComparison.Ordinal)) throw new InvalidOperationException(line.Substring(6));
                        var received = Message;
                        if (received != null) received(line);
                    }
                    if (!stopping) throw new IOException("Связь с BongoCat прервана. Выключите и включите автосбор после запуска игры.");
                }
            }
            catch (TimeoutException) { if (!stopping) SetError("Проверьте подключение и перезапустите BongoCat."); }
            catch (Exception ex) { if (!stopping) SetError(ex.Message); }
            finally
            {
                lock (gate)
                {
                    writer = null;
                    if (pipe != null) pipe.Dispose();
                    pipe = null;
                }
                running = false;
                var changed = Changed;
                if (changed != null) changed();
            }
        }

        private void Update(int game, ChestState state)
        {
            if (state.Index < 0 || state.Index > 1) throw new InvalidDataException("Unknown chest");
            sessionCounter.Observe(game, state);
            gameErrors[state.Index] = state.Error.Length == 0 ? null : SteamExchangeError;
            error = gameErrors[0] ?? gameErrors[1];
            string detail = state.Pending ? "сбор…" : state.Error.Length != 0 ? "ошибка Steam" :
                state.Ready ? (state.Affordable ? "готов" : "нужно 1000 нажатий") :
                state.Remaining > 0 ? "в " + DateTime.Now.AddSeconds(state.Remaining).ToString("HH:mm") : "ожидание жетона";
            descriptions[state.Index] = (state.Index == 0 ? "Обычный: " : "Эмоции: ") + detail + " · собрано " + state.Collected;
            SetStatus(String.Join("\n", descriptions));
        }

        internal void Send(string command)
        {
            lock (gate) { if (writer != null) writer.WriteLine(command); }
        }

        public void Stop()
        {
            lock (lifecycle)
            {
                if (disposed) return;
                // Keep the receiver alive until STOPPED has been acknowledged.
                bool sent = false;
                lock (gate)
                {
                    if (writer != null)
                        try { writer.WriteLine("STOP"); sent = true; } catch (IOException) { } catch (ObjectDisposedException) { }
                }
                if (sent) stopped.WaitOne(1000);
                stopping = true;
                lock (gate) { if (pipe != null) pipe.Dispose(); }
                if (worker != null) worker.Join();
                worker = null;
                running = false;
                error = null;
                SetStatus("Автосбор выключен");
            }
        }

        private void SetStatus(string text)
        {
            status = text;
            var changed = Changed;
            if (changed != null) changed();
        }

        private void SetError(string message) { error = message; SetStatus("Автосбор: " + message); }

        public void Dispose()
        {
            lock (lifecycle)
            {
                if (disposed) return;
                Stop(); disposed = true; stopped.Dispose();
            }
        }
    }
}
