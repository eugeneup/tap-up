using System;
using System.IO;
using TapUp;

internal static class GamePatchCli
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += GamePatch.ResolveDependency;
        try
        {
            if (args.Length < 2) throw new ArgumentException("build|install|remove|check GAME_DIRECTORY [OUTPUT_DLL]");
            string game = Path.GetFullPath(args[1]);
            switch (args[0])
            {
                case "build":
                    byte[] original = File.ReadAllBytes(Path.Combine(GamePatch.Managed(game), "Assembly-CSharp.dll"));
                    byte[] patched = GamePatch.CreatePatched(original, GamePatch.Resource(GamePatch.BridgeResource));
                    File.WriteAllBytes(args[2], patched);
                    Console.WriteLine("Original: " + GamePatch.Hash(original));
                    Console.WriteLine("Patched:  " + GamePatch.Hash(patched));
                    break;
                case "install": GamePatch.Install(game); Console.WriteLine("Installed: " + GamePatch.Installed(game)); break;
                case "remove": GamePatch.Uninstall(game); Console.WriteLine("Original restored: " + GamePatch.HashFile(Path.Combine(GamePatch.Managed(game), "Assembly-CSharp.dll"))); break;
                case "check": Console.WriteLine("Installed: " + GamePatch.Installed(game)); break;
                default: throw new ArgumentException("Unknown command");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
