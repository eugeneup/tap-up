using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: AssemblyVersion("1.4.0.0")]

namespace TapUp.Game
{
    // Called only from the game's existing lifecycle / inventory events.
    // No screenshots, simulated input, frame polling, or altered exchange rules.
    public static class BongoBridge
    {
        public const string Version = "1.4.0";
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private sealed class Chest
        {
            internal object Shop;
            internal bool Pending;
            internal int Attempts, Collected, Item = -1;
            internal string Error = "", LastState;
        }
        private static readonly Chest[] chests = { new Chest(), new Chest() };
        private static readonly object outputLock = new object();
        private static readonly object controlLock = new object();
        private static readonly string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tap-Up", "bongo-bridge.log");
        private static SynchronizationContext main;
        private static StreamWriter output;
        private static volatile int mask;
        private static int initialized, queued, session;
        private static int revision;
        private static PropertyInfo background;
        private static bool previousBackground;
        private static bool ownsBackground;
        private static bool logEnabled;

        public static void Register(object shop)
        {
            Guard(delegate {
                int index = Index(shop);
                logEnabled = shop.GetType().FullName == "BongoCat.Shop";
                chests[index].Shop = shop;
                main = SynchronizationContext.Current;
                if (main == null) throw new InvalidOperationException("Unity synchronization context is unavailable");
                if (Interlocked.Exchange(ref initialized, 1) == 0)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type application = assembly.GetType("UnityEngine.Application");
                        if (application != null) { background = application.GetProperty("runInBackground"); break; }
                    }
                    new Thread(Serve) { IsBackground = true, Name = "Tap-Up game events" }.Start();
                    Log("bridge " + Version + " loaded");
                }
                Log("registered " + index.ToString());
                Publish(index, true);
                QueueClaim();
            });
        }

        public static void Ready(object shop)
        {
            if (mask == 0 && output == null) return;
            Guard(delegate {
                int index = Index(shop);
                if (chests[index].Shop == null) chests[index].Shop = shop;
                Publish(index, false);
                if (mask != 0) QueueClaim();
            });
        }

        public static void Affordable(object item)
        {
            // This is an existing Pets stats event. A single fast gate while
            // disabled; never initiate collection inside that event's stack.
            if (mask == 0) return;
            Guard(delegate {
                object shop = Field(item, "_shop");
                if ((bool)Field(shop, "ChestIsReady")) QueueClaim();
            });
        }

        public static void Completed(object item, int itemId)
        {
            Guard(delegate {
                object shop = Field(item, "_shop");
                int index = Index(shop);
                Chest chest = chests[index];
                bool ours = chest.Pending;
                chest.Pending = false;
                chest.Item = itemId;
                chest.Error = itemId < 0 ? "Steam exchange failed" : "";
                if (ours && itemId >= 0) chest.Collected++;
                Log(String.Format("result chest={0} ours={1} item={2} remaining={3} collected={4}", index, ours, itemId, Field(shop, "StockRefreshTimeLeft"), chest.Collected));
                Publish(index, true);
                QueueClaim();
            });
        }

        private static int Index(object shop) { return (bool)Field(shop, "_isEmoteShop") ? 1 : 0; }
        private static object Field(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Members);
            if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
            return field.GetValue(target);
        }
        private static bool Property(object target, string name)
        {
            return (bool)target.GetType().GetProperty(name, Members).GetValue(target, null);
        }

        private static void QueueClaim()
        {
            if (mask == 0 || main == null || Interlocked.Exchange(ref queued, 1) != 0) return;
            main.Post(delegate(object unused) {
                Interlocked.Exchange(ref queued, 0);
                Guard(Claim);
            }, null);
        }

        private static void Claim()
        {
            if (mask == 0 || chests[0].Pending || chests[1].Pending) return;
            for (int index = 0; index < 2; index++)
            {
                Chest chest = chests[index];
                object shop = chest.Shop;
                if ((mask & (1 << index)) == 0 || shop == null) continue;
                if (!(bool)Field(shop, "ChestIsReady") || !Property(shop, "CanGetChest") || (bool)Field(shop, "_openingChest")) continue;
                object item = Field(shop, "_shopItem");
                if ((bool)Field(item, "_waitingForServer")) continue;
                // Buy() keeps the original price, token requirements and Steam
                // exchange path, independently of activeSelf / visible popup.
                lock (controlLock)
                {
                    if (mask == 0) return;
                    chest.Pending = true;
                    chest.Attempts++;
                    chest.Error = "";
                    try
                    {
                        Log(String.Format("request chest={0} remaining={1}", index, Field(shop, "StockRefreshTimeLeft")));
                        item.GetType().GetMethod("Buy", Members).Invoke(item, null);
                        if (!(bool)Field(item, "_waitingForServer")) chest.Pending = false;
                    }
                    catch { chest.Pending = false; throw; }
                }
                Publish(index, true);
                if (chest.Pending) return; // Serialize the two Steam exchanges.
            }
        }

        private static void Publish(int index, bool force)
        {
            Chest chest = chests[index];
            object shop = chest.Shop;
            if (shop == null) return;
            int remaining = (int)Field(shop, "StockRefreshTimeLeft");
            bool ready = (bool)Field(shop, "ChestIsReady");
            bool afford = false;
            try { afford = Property(shop, "CanAffordChest"); } catch { }
            // Ready() can repeat in the game when popups are hidden. Deduplicate
            // events; a ready chest is not a reason to poll or log each frame.
            string state = String.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}", index, remaining, ready ? 1 : 0, afford ? 1 : 0,
                chest.Pending ? 1 : 0, chest.Collected, chest.Attempts, chest.Item, chest.Error);
            if (!force && chest.LastState == state) return;
            chest.LastState = state;
            Write("STATE\t" + state);
        }

        private static void SetMode(int requested, int owner, int expectedRevision)
        {
            lock (controlLock)
            {
                if (owner != Volatile.Read(ref session) || expectedRevision != Volatile.Read(ref revision)) return;
                if (requested != 0 && background != null)
                {
                    if (!ownsBackground) { previousBackground = (bool)background.GetValue(null, null); ownsBackground = true; }
                    background.SetValue(null, true, null);
                }
                mask = requested;
                if (requested == 0 && background != null && ownsBackground)
                {
                    background.SetValue(null, previousBackground, null);
                    ownsBackground = false;
                }
            }
            Log("mode=" + requested.ToString());
            Write(requested == 0 ? "STOPPED" : "RUNNING");
            Publish(0, true); Publish(1, true);
            QueueClaim();
        }

        private static void Serve()
        {
            string name = "TapUp.BongoCat." + GetCurrentProcessId().ToString();
            while (true)
            {
                int owner = 0;
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        pipe.WaitForConnection();
                        owner = Interlocked.Increment(ref session);
                        using (var reader = new StreamReader(pipe, Encoding.UTF8))
                        using (var writer = new StreamWriter(pipe) { AutoFlush = true })
                        {
                            lock (outputLock) { output = writer; writer.WriteLine("HELLO\t" + Version); }
                            main.Post(delegate(object unused) { Guard(delegate { Publish(0, true); Publish(1, true); }); }, null);
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                int requested;
                                if (line == "STOP")
                                {
                                    int stoppedRevision;
                                    lock (controlLock)
                                    {
                                        mask = 0; // Wait for a Buy already entering, then acknowledge.
                                        stoppedRevision = Interlocked.Increment(ref revision);
                                    }
                                    Write("STOPPED");
                                    main.Post(delegate(object unused) { Guard(delegate { SetMode(0, owner, stoppedRevision); }); }, null);
                                }
                                else if (line.StartsWith("START\t", StringComparison.Ordinal) && Int32.TryParse(line.Substring(6), out requested) && requested >= 1 && requested <= 3)
                                {
                                    int selection = requested;
                                    int startRevision = Interlocked.Increment(ref revision);
                                    main.Post(delegate(object unused) { Guard(delegate { SetMode(selection, owner, startRevision); }); }, null);
                                }
                                else if (line == "STATUS")
                                    main.Post(delegate(object unused) { Guard(delegate { Publish(0, true); Publish(1, true); }); }, null);
                            }
                        }
                    }
                }
                catch (IOException) { }
                catch (Exception ex) { Log("pipe " + ex.GetType().Name + ": " + ex.Message); Thread.Sleep(1000); }
                finally
                {
                    int disconnectedRevision;
                    lock (controlLock) { mask = 0; disconnectedRevision = Interlocked.Increment(ref revision); }
                    lock (outputLock) output = null;
                    int closedOwner = owner;
                    if (main != null) main.Post(delegate(object unused) { Guard(delegate { SetMode(0, closedOwner, disconnectedRevision); }); }, null);
                }
            }
        }

        private static void Write(string line)
        {
            lock (outputLock)
            {
                if (output == null) return;
                try { output.WriteLine(line); }
                catch (IOException) { mask = 0; }
                catch (ObjectDisposedException) { mask = 0; }
            }
        }

        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                mask = 0;
                while (ex.InnerException != null) ex = ex.InnerException;
                string error = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                Log("error " + error);
                Write("ERROR\t" + error);
            }
        }

        private static void Log(string text)
        {
            if (!logEnabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                if (!File.Exists(logPath) || File.ReadAllBytes(logPath).Length < 512 * 1024)
                    using (var writer = new StreamWriter(logPath, true)) writer.WriteLine(DateTime.UtcNow.ToString("o") + " pid=" + GetCurrentProcessId().ToString() + " " + text);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    }
}
