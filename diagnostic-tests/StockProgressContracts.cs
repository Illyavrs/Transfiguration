using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Illyaverse.Transfiguration;

internal static class StockProgressContracts
{
    internal static int Run(string installation)
    {
        var root = ClientAssemblyPath.Canonical(installation);
        Assembly? resolve(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name is null || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var file = Path.Combine(root, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        }
        AssemblyLoadContext.Default.Resolving += resolve;
        try
        {
            var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "osu.Framework.dll"));
            var desktop = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "osu!.dll"));
            requireIdentity(framework, "2026.1005.0.0", "93B62F61C72539ED6FDB3C8EBA18D276A74DE8E1C094280B2531F5A76CAEA528");
            requireIdentity(desktop, "2026.1005.1.0", "18AE9BB6BB441E9A654AC72C859F527041BDE52E5DCE4109F8D9074584FA57A4");
            ClientStartupProgress.VerifyContracts(desktop, framework);
            var hooks = new List<(MethodBase Method, string Hook, bool Postfix)>();
            ClientStartupProgress.Install((method, hook, _, postfix) => hooks.Add((method, hook, postfix)));
            if (hooks.Count != 9 || hooks.Select(hook => hook.Method).Distinct().Count() != 9
                || hooks.Count(hook => hook.Postfix) != 6) throw new InvalidOperationException();
            foreach (var hook in hooks)
            {
                var callback = typeof(ClientStartupProgress).GetMethod(hook.Hook, BindingFlags.NonPublic | BindingFlags.Static)!;
                if (callback.ReturnType != typeof(void) || callback.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                    throw new InvalidOperationException();
                Console.WriteLine("PASS observational stock contract " + hook.Method.DeclaringType!.FullName + "." + hook.Method.Name
                    + (hook.Postfix ? " successful-return postfix" : " entry prefix"));
            }
            Console.WriteLine("PASS 9 exact hash/version-pinned startup progress contracts; metadata only, no original Main/GameHost/window/renderer/GPU/Realm/player data/network executed");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("FAIL stock_startup_progress_contract");
            return 1;
        }
        finally { AssemblyLoadContext.Default.Resolving -= resolve; }
    }

    private static void requireIdentity(Assembly assembly, string version, string sha256)
    {
        if (assembly.GetName().Version?.ToString() != version) throw new InvalidOperationException();
        using var stream = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(stream)) != sha256) throw new InvalidOperationException();
    }
}
