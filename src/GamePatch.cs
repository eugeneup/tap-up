using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TapUp
{
    internal static class GamePatch
    {
        // Eight verified Windows depot manifests share these four originals.
        // New hashes must pass the compatibility audit before being added.
        private static readonly string[] OriginalHashes = {
            "D6209ECCE915E15AADF4DE11191EEF5C6D3C6AAAFBD82A67C773B72070AE54CB",
            "D22026DF9CF2915A58582111F1D7F1525423113A201DF6F69D0A7CA36F9BDC1A",
            "729AA44186B1EF9C209E72DF1D91466C8541892CB2C7936C6113614EBC28C30D",
            "9D9973D8DDDBFF31648496225CDF00082D4389820CC834F74736BB39E08C1F58"
        };
        internal const string BridgeFile = "TapUp.BongoBridge.dll";
        internal const string BridgeResource = "TapUp.GameBridge";
        internal static string Managed(string game) { return Path.Combine(game, "BongoCat_Data", "Managed"); }
        private static string GameAssembly(string game) { return Path.Combine(Managed(game), "Assembly-CSharp.dll"); }
        private static string BackupDirectory(string game) { return Path.Combine(Managed(game), ".tap-up"); }

        internal static byte[] Resource(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidOperationException("Отсутствует встроенный ресурс " + name);
                using (var data = new MemoryStream()) { stream.CopyTo(data); return data.ToArray(); }
            }
        }

        internal static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name == "Mono.Cecil") return Assembly.Load(Resource("TapUp.Mono.Cecil"));
            return null;
        }

        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        internal static string HashFile(string path) { return Hash(File.ReadAllBytes(path)); }
        private static bool SupportedOriginal(string hash) { return Array.IndexOf(OriginalHashes, hash) >= 0; }

        internal static bool Installed(string game)
        {
            if (!VerifiedInstallation(game)) return false;
            string[] hashes = File.ReadAllLines(Path.Combine(BackupDirectory(game), "patch.txt"));
            return hashes[2] == Hash(Resource(BridgeResource));
        }

        private static bool VerifiedInstallation(string game)
        {
            if (String.IsNullOrWhiteSpace(game)) return false;
            string manifest = Path.Combine(BackupDirectory(game), "patch.txt");
            string bridge = Path.Combine(Managed(game), BridgeFile);
            string original = Path.Combine(BackupDirectory(game), "Assembly-CSharp.original.dll");
            if (!File.Exists(manifest) || !File.Exists(bridge) || !File.Exists(original) || !File.Exists(GameAssembly(game))) return false;
            string[] hashes = File.ReadAllLines(manifest);
            return hashes.Length == 3 && SupportedOriginal(hashes[0]) &&
                HashFile(GameAssembly(game)) == hashes[1] && HashFile(bridge) == hashes[2] && HashFile(original) == hashes[0];
        }

        internal static string FindGame()
        {
            foreach (Process process in Process.GetProcessesByName("BongoCat"))
                using (process)
                    try { return Path.GetDirectoryName(process.MainModule.FileName); }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "BongoCat");
            return File.Exists(Path.Combine(defaultPath, "BongoCat.exe")) ? defaultPath : "";
        }

        private static void RequireClosed(string game)
        {
            Process[] processes = Process.GetProcessesByName("BongoCat");
            bool running = false;
            string expected = Path.GetFullPath(Path.Combine(game, "BongoCat.exe"));
            foreach (Process process in processes)
                using (process)
                {
                    try { if (String.Equals(process.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase)) running = true; }
                    catch (System.ComponentModel.Win32Exception) { running = true; }
                    catch (InvalidOperationException) { }
                }
            if (running) throw new InvalidOperationException("Закройте BongoCat перед установкой или удалением подключения.");
        }

        internal static void Install(string game)
        {
            RequireClosed(game);
            if (!File.Exists(Path.Combine(game, "BongoCat.exe"))) throw new InvalidOperationException("Выберите папку с BongoCat.exe.");
            if (Installed(game)) return;
            if (VerifiedInstallation(game)) Uninstall(game);
            string original = GameAssembly(game);
            byte[] originalBytes = File.ReadAllBytes(original);
            string originalHash = Hash(originalBytes);
            if (!SupportedOriginal(originalHash)) throw new InvalidOperationException("Версия DLL отличается от проверенной. Файл игры оставлен без изменений.");
            byte[] bridge = Resource(BridgeResource);
            byte[] patched = CreatePatched(originalBytes, bridge);
            string directory = BackupDirectory(game);
            string bridgePath = Path.Combine(Managed(game), BridgeFile);
            if (File.Exists(bridgePath) && HashFile(bridgePath) != Hash(bridge))
                throw new InvalidOperationException("В папке игры уже есть другой мост Tap-Up. Сначала удалите прежнее подключение.");
            Directory.CreateDirectory(directory);
            string backup = PrepareBackup(directory, originalBytes, originalHash);
            File.WriteAllBytes(bridgePath, bridge);
            string staged = Path.Combine(directory, "Assembly-CSharp.staged.dll");
            File.WriteAllBytes(staged, patched);
            try
            {
                ReplaceFile(staged, original);
                File.WriteAllLines(Path.Combine(directory, "patch.txt"), new[] { originalHash, Hash(patched), Hash(bridge) });
            }
            catch
            {
                // Restore our verified original if the install did not commit.
                if (HashFile(original) == Hash(patched)) File.Copy(backup, original, true);
                throw;
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }

        private static string PrepareBackup(string directory, byte[] original, string originalHash)
        {
            string backup = Path.Combine(directory, "Assembly-CSharp.original.dll");
            if (!File.Exists(backup)) { File.WriteAllBytes(backup, original); return backup; }
            byte[] previous = File.ReadAllBytes(backup);
            string previousHash = Hash(previous);
            if (previousHash == originalHash) return backup;
            if (!SupportedOriginal(previousHash)) throw new InvalidOperationException("Резервная копия не прошла проверку. Установка остановлена.");
            // Steam may replace a patched game with another verified original.
            // Keep the previous backup, then atomically prepare the current one.
            string archive = Path.Combine(directory, "Assembly-CSharp." + previousHash + ".original.dll");
            if (File.Exists(archive))
            {
                if (HashFile(archive) != previousHash) throw new InvalidOperationException("Резервная копия не прошла проверку. Установка остановлена.");
            }
            else File.WriteAllBytes(archive, previous);
            string staged = Path.Combine(directory, "Assembly-CSharp.backup.staged.dll");
            File.WriteAllBytes(staged, original);
            try { ReplaceFile(staged, backup); }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            return backup;
        }

        internal static void Uninstall(string game)
        {
            RequireClosed(game);
            if (!VerifiedInstallation(game)) throw new InvalidOperationException("Подключение изменено или игра обновилась. Откат старой DLL не выполняется.");
            string original = GameAssembly(game);
            string directory = BackupDirectory(game);
            string staged = Path.Combine(directory, "Assembly-CSharp.restore.dll");
            File.Copy(Path.Combine(directory, "Assembly-CSharp.original.dll"), staged, true);
            ReplaceFile(staged, original);
            File.Delete(Path.Combine(Managed(game), BridgeFile));
            File.Delete(Path.Combine(directory, "patch.txt"));
            // Retain the verified backup for the user.
        }

        private static void ReplaceFile(string source, string destination)
        {
            // Indexers and scanners can briefly retain a valid DLL after it is
            // written. Keep the replacement atomic and retry only intact-file
            // sharing failures; never fall back to deleting the original.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(source, destination, null); return; }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xffff;
                    if (attempt >= 4 || (code != 32 && code != 33 && code != 1175)) throw;
                    Thread.Sleep(40);
                }
            }
        }

        internal static byte[] CreatePatched(byte[] original, byte[] bridge)
        {
            if (!SupportedOriginal(Hash(original))) throw new InvalidOperationException("Unsupported BongoCat assembly");
            using (var source = AssemblyDefinition.ReadAssembly(new MemoryStream(original)))
            using (var helper = AssemblyDefinition.ReadAssembly(new MemoryStream(bridge)))
            {
                ModuleDefinition module = source.MainModule;
                if (module.AssemblyReferences.Any(r => r.Name == "TapUp.BongoBridge")) throw new InvalidOperationException("Bridge already referenced");
                TypeDefinition shop = module.GetType("BongoCat.Shop");
                TypeDefinition item = module.GetType("BongoCat.ShopItem");
                TypeDefinition hooks = helper.MainModule.GetType("TapUp.Game.BongoBridge");
                if (shop == null || item == null || hooks == null) throw new InvalidOperationException("Expected game types are missing");
                RequireField(shop, "ChestIsReady", "System.Boolean");
                RequireField(shop, "StockRefreshTimeLeft", "System.Int32");
                RequireField(shop, "_openingChest", "System.Boolean");
                RequireField(item, "_waitingForServer", "System.Boolean");
                MethodDefinition start = Method(shop, "Start", 0);
                MethodDefinition ready = Method(shop, "OnChestReady", 0);
                MethodDefinition affordable = Method(item, "CheckAffordable", 0);
                MethodDefinition callback = Method(item, "Callback", 2);
                if (callback.Parameters[0].ParameterType.FullName != "System.Int32" || callback.Parameters[1].ParameterType.FullName != "System.Boolean")
                    throw new InvalidOperationException("Callback signature mismatch");
                InsertEntry(start, module.ImportReference(Method(hooks, "Register", 1)));
                InsertReturns(ready, module.ImportReference(Method(hooks, "Ready", 1)), false);
                InsertReturns(affordable, module.ImportReference(Method(hooks, "Affordable", 1)), false);
                InsertReturns(callback, module.ImportReference(Method(hooks, "Completed", 2)), true);
                using (var output = new MemoryStream()) { source.Write(output); return output.ToArray(); }
            }
        }

        private static void RequireField(TypeDefinition type, string name, string fieldType)
        {
            if (!type.Fields.Any(f => f.Name == name && f.FieldType.FullName == fieldType)) throw new InvalidOperationException("Field mismatch: " + name);
        }
        private static MethodDefinition Method(TypeDefinition type, string name, int parameters)
        {
            var candidates = type.Methods.Where(m => m.Name == name && m.Parameters.Count == parameters && m.HasBody).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("Method mismatch: " + name);
            return candidates[0];
        }
        private static void ExpandBranches(MethodDefinition method)
        {
            var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Name);
            foreach (Instruction instruction in method.Body.Instructions)
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                    instruction.OpCode = codes[instruction.OpCode.Name.Substring(0, instruction.OpCode.Name.Length - 2)];
        }
        private static void InsertEntry(MethodDefinition method, MethodReference hook)
        {
            ExpandBranches(method);
            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        }
        private static void InsertReturns(MethodDefinition method, MethodReference hook, bool argument)
        {
            ExpandBranches(method);
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                // Keep branch targets pointing at the start of the hook.
                ret.OpCode = OpCodes.Ldarg_0;
                Instruction last = ret;
                if (argument) { Instruction load = il.Create(OpCodes.Ldarg_1); il.InsertAfter(last, load); last = load; }
                Instruction call = il.Create(OpCodes.Call, hook); il.InsertAfter(last, call);
                il.InsertAfter(call, il.Create(OpCodes.Ret));
            }
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 2);
        }
    }
}
