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
        // Older Tap-Up releases removed patch.txt on disconnect and left an
        // original without a local integrity record. Recognize those backups.
        private static readonly string[] LegacyBackupHashes = {
            // Steam build 26784007, Windows depot manifest 538652396572221049.
            "30BADB31A61225BB2379A525A338A71B0AAB1815E13DEBB421BA5C554BE84598",
            // Steam build 26754239, Windows depot manifest 2186243361634716529.
            "825BDAD753912D2AAF70918E46C0326AD00E279D1A6B4BD5B145F6EB84231F24",
            // Steam build 26538670, Windows depot manifest 1792075920147193060.
            "8F75DF923B6F36F45633F27823AD8D9C63DFA5D42759635D9918857361AF31A2",
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
        private static bool LegacyBackup(string hash) { return Array.IndexOf(LegacyBackupHashes, hash) >= 0; }
        private static string OriginalBackup(string directory) { return Path.Combine(directory, "Assembly-CSharp.original.dll"); }
        private static string PatchRecord(string directory) { return Path.Combine(directory, "patch.txt"); }

        internal static bool Installed(string game)
        {
            string[] hashes = VerifiedInstallation(game);
            return hashes != null && hashes[2] == Hash(Resource(BridgeResource));
        }

        private static string[] VerifiedInstallation(string game)
        {
            if (String.IsNullOrWhiteSpace(game)) return null;
            string directory = BackupDirectory(game);
            string manifest = PatchRecord(directory);
            string bridge = Path.Combine(Managed(game), BridgeFile);
            string original = OriginalBackup(directory);
            if (!File.Exists(manifest) || !File.Exists(bridge) || !File.Exists(original) || !File.Exists(GameAssembly(game))) return null;
            string[] hashes = File.ReadAllLines(manifest);
            return hashes.Length == 3 &&
                HashFile(GameAssembly(game)) == hashes[1] && HashFile(bridge) == hashes[2] && HashFile(original) == hashes[0] ? hashes : null;
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
            string[] installation = VerifiedInstallation(game);
            byte[] bridge = Resource(BridgeResource);
            string bridgeHash = Hash(bridge);
            if (installation != null && installation[2] == bridgeHash) return;
            string original = GameAssembly(game);
            string directory = BackupDirectory(game);
            byte[] originalBytes = File.ReadAllBytes(installation != null ? OriginalBackup(directory) : original);
            string originalHash = Hash(originalBytes);
            if (installation != null && originalHash != installation[0]) throw InstallationChanged();
            byte[] patched = CreatePatched(originalBytes, bridge);
            string patchedHash = Hash(patched);
            if (installation != null) Uninstall(game);
            string bridgePath = Path.Combine(Managed(game), BridgeFile);
            if (File.Exists(bridgePath) && HashFile(bridgePath) != bridgeHash)
                throw new InvalidOperationException("В папке игры уже есть другой мост Tap-Up. Сначала удалите прежнее подключение.");
            RequireFileHash(original, originalHash);
            Directory.CreateDirectory(directory);
            string backup = PrepareBackup(directory, originalBytes, originalHash);
            File.WriteAllBytes(bridgePath, bridge);
            string staged = Path.Combine(directory, "Assembly-CSharp.staged.dll");
            File.WriteAllBytes(staged, patched);
            try
            {
                RequireFileHash(original, originalHash);
                ReplaceFile(staged, original);
                File.WriteAllLines(PatchRecord(directory), new[] { originalHash, patchedHash, bridgeHash });
            }
            catch
            {
                // Restore our verified original if the install did not commit.
                if (File.Exists(original) && HashFile(original) == patchedHash && HashFile(backup) == originalHash)
                {
                    File.WriteAllBytes(staged, originalBytes);
                    ReplaceFile(staged, original);
                }
                throw;
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }

        private static string PrepareBackup(string directory, byte[] original, string originalHash)
        {
            string backup = OriginalBackup(directory);
            if (!File.Exists(backup)) { File.WriteAllBytes(backup, original); return backup; }
            byte[] previous = File.ReadAllBytes(backup);
            string previousHash = Hash(previous);
            if (previousHash == originalHash) return backup;
            string record = PatchRecord(directory);
            string[] hashes = File.Exists(record) ? File.ReadAllLines(record) : null;
            string archive = BackupArchive(directory, previousHash);
            bool recordedOriginal = hashes == null ? LegacyBackup(previousHash) : hashes.Length == 3 && hashes[0] == previousHash;
            if (!recordedOriginal && !File.Exists(archive))
                throw new InvalidOperationException("Резервная копия не прошла проверку. Установка остановлена.");
            VerifyOrSaveArchive(archive, previous, previousHash);
            string staged = Path.Combine(directory, "Assembly-CSharp.backup.staged.dll");
            File.WriteAllBytes(staged, original);
            try { ReplaceFile(staged, backup); }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            return backup;
        }

        private static string BackupArchive(string directory, string hash)
        {
            return Path.Combine(directory, "Assembly-CSharp." + hash + ".original.dll");
        }

        private static void VerifyOrSaveArchive(string archive, byte[] original, string hash)
        {
            if (!File.Exists(archive)) { File.WriteAllBytes(archive, original); return; }
            if (HashFile(archive) != hash)
                throw new InvalidOperationException("Резервная копия не прошла проверку.");
        }

        internal static void Uninstall(string game)
        {
            RequireClosed(game);
            string[] installation = VerifiedInstallation(game);
            if (installation == null) throw InstallationChanged();
            string original = GameAssembly(game);
            string directory = BackupDirectory(game);
            string staged = Path.Combine(directory, "Assembly-CSharp.restore.dll");
            byte[] backup = File.ReadAllBytes(OriginalBackup(directory));
            string hash = Hash(backup);
            if (hash != installation[0]) throw InstallationChanged();
            if (!LegacyBackup(hash)) VerifyOrSaveArchive(BackupArchive(directory, hash), backup, hash);
            File.WriteAllBytes(staged, backup);
            try
            {
                RequireFileHash(original, installation[1]);
                ReplaceFile(staged, original);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            File.Delete(Path.Combine(Managed(game), BridgeFile));
            File.Delete(PatchRecord(directory));
            // Retain the verified backup for the user.
        }

        private static void RequireFileHash(string path, string expected)
        {
            if (!File.Exists(path) || HashFile(path) != expected) throw InstallationChanged();
        }

        private static InvalidOperationException InstallationChanged()
        {
            return new InvalidOperationException("Подключение изменено или игра обновилась. Замена DLL отменена.");
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
            using (var source = ReadGameAssembly(original))
            using (var helper = AssemblyDefinition.ReadAssembly(new MemoryStream(bridge)))
            {
                ModuleDefinition module = source.MainModule;
                if (module.AssemblyReferences.Any(r => r.Name == "TapUp.BongoBridge"))
                    throw new InvalidOperationException("В DLL BongoCat уже есть ссылка на мост Tap-Up.");
                ChestGameContract.Verify(module);
                TypeDefinition shop = module.GetType("BongoCat.Shop");
                TypeDefinition item = module.GetType("BongoCat.ShopItem");
                TypeDefinition hooks = helper.MainModule.GetType("TapUp.Game.BongoBridge");
                if (shop == null || item == null || hooks == null) throw new InvalidOperationException("Expected game types are missing");
                MethodDefinition start = Method(shop, "Start", 0);
                MethodDefinition ready = Method(shop, "OnChestReady", 0);
                MethodDefinition affordable = Method(item, "CheckAffordable", 0);
                MethodDefinition callback = Method(item, "Callback", 2);
                InsertEntry(start, module.ImportReference(Method(hooks, "Register", 1)));
                InsertReturns(ready, module.ImportReference(Method(hooks, "Ready", 1)), false);
                InsertReturns(affordable, module.ImportReference(Method(hooks, "Affordable", 1)), false);
                InsertReturns(callback, module.ImportReference(Method(hooks, "Completed", 2)), true);
                using (var output = new MemoryStream()) { source.Write(output); return output.ToArray(); }
            }
        }

        private static AssemblyDefinition ReadGameAssembly(byte[] original)
        {
            try { return AssemblyDefinition.ReadAssembly(new MemoryStream(original)); }
            catch (BadImageFormatException ex) { throw new InvalidOperationException("Не удалось прочитать DLL BongoCat.", ex); }
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
