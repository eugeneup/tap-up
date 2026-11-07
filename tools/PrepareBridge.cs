using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

internal static class PrepareBridge
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request) {
            if (new AssemblyName(request.Name).Name != "Mono.Cecil") return null;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cecil"))
            using (var copy = new MemoryStream()) { stream.CopyTo(copy); return Assembly.Load(copy.ToArray()); }
        };
        return Prepare(args);
    }
    private static int Prepare(string[] args)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory("."); resolver.RemoveSearchDirectory("bin");
        resolver.AddSearchDirectory(args[1]);
        using (var helper = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(args[0])), new ReaderParameters { AssemblyResolver = resolver }))
        {
            foreach (var attribute in helper.CustomAttributes.ToArray())
                if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilationRelaxationsAttribute" ||
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute") helper.CustomAttributes.Remove(attribute);
            // Re-read after removing compiler attributes, then validate every
            // remaining reference against the game's stripped Mono libraries.
            helper.MainModule.Mvid = Guid.Empty;
            using (var output = new MemoryStream())
            {
                helper.Write(output, new WriterParameters { Timestamp = 0, DeterministicMvid = true });
                File.WriteAllBytes(args[0], output.ToArray());
            }
        }
        int missing = 0;
        using (var helper = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver }))
            foreach (var member in helper.MainModule.GetMemberReferences())
                try { if (member.Resolve() == null) { Console.Error.WriteLine("Missing game API: " + member.FullName); missing++; } }
                catch (AssemblyResolutionException) { Console.Error.WriteLine("Missing game assembly: " + member.FullName); missing++; }
        Console.WriteLine("Bridge Mono API validation: " + (missing == 0 ? "PASS" : "FAIL"));
        return missing == 0 ? 0 : 1;
    }
}
