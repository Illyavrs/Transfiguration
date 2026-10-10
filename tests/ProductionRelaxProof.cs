using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Detached request-builder contracts against immutable stock references. Only
// the production RX guards are installed under a disposable Harmony owner.
// No bootstrap, native game/API constructor, data directory or request is run.
internal static class ProductionRelaxProof
{
    private const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags instances = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    internal static int Run(string productionPath, string nativeDirectory, string boundReceipt)
    {
        if (Environment.Version != new Version(10, 0, 12)) throw new InvalidOperationException("Verified .NET 10.0.12 required");
        if (!Path.IsPathFullyQualified(productionPath) || !Path.IsPathFullyQualified(nativeDirectory)
            || !Path.IsPathFullyQualified(boundReceipt)) throw new ArgumentException("Absolute bound proof paths required");
        var nativeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nativeDirectory));
        using var receipt = JsonDocument.Parse(File.ReadAllText(boundReceipt));
        var bound = receipt.RootElement.GetProperty("frozen_inputs").EnumerateArray()
            .Select(entry => (Path: Path.GetFullPath(entry.GetProperty("path").GetString()!), Hash: entry.GetProperty("sha256").GetString()!))
            .Where(entry => Path.GetDirectoryName(entry.Path)!.Equals(nativeRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(entry.Path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => Path.GetFileNameWithoutExtension(entry.Path), StringComparer.OrdinalIgnoreCase);
        require(bound.ContainsKey("osu.Game") && bound.ContainsKey("osu.Framework") && bound.ContainsKey("Realm"),
            "immutable receipt binds all required stock references");
        checkBound();
        var bridge = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(productionPath));
        using (var resource = bridge.GetManifestResourceStream("Illyaverse.Transfiguration.0Harmony.dll")
            ?? throw new InvalidOperationException("Embedded Harmony required"))
            AssemblyLoadContext.Default.LoadFromStream(resource);
        Assembly? resolve(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name is null || !bound.TryGetValue(name.Name, out var entry)) return null;
            if (hash(entry.Path) != entry.Hash) throw new InvalidOperationException("Bound stock reference changed");
            return context.LoadFromAssemblyPath(entry.Path);
        }
        AssemblyLoadContext.Default.Resolving += resolve;
        object? scopedHarmony = null;
        Type? harmonyType = null;
        MethodInfo? unpatchScoped = null;
        IList? registered = null;
        var owner = "illyaverse.proof.relax." + Guid.NewGuid().ToString("N");
        try
        {
            var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(bound["osu.Game"].Path);
            var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(bound["osu.Framework"].Path);
            require(game.GetName().Version == new Version(2026, 1005, 1, 0)
                && framework.GetName().Version == new Version(2026, 1005, 0, 0), "pinned tachyon game and framework versions match");
            var policy = bridge.GetType("Illyaverse.Transfiguration.NetworkPolicy", true)!;
            method(policy, "Initialize").Invoke(null, null);
            var endpoint = (string)policy.GetProperty("Website", statics)!.GetValue(null)!;
            require(new Uri(endpoint).Scheme == "https", "detached production proof captures one HTTPS origin from task environment");
            var relax = bridge.GetType("Illyaverse.Transfiguration.RelaxContext", true)!;
            method(relax, "VerifyContracts").Invoke(null, null);
            require(true, "production context validates exact pinned native builder metadata");
            var guard = bridge.GetType("Illyaverse.Transfiguration.Guard", true)!;
            registered = (IList)guard.GetField("patched", statics)!.GetValue(null)!;
            require(registered.Count == 0, "detached process starts without any bootstrap guards");
            harmonyType = AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == "0Harmony")
                .GetType("HarmonyLib.Harmony", true)!;
            unpatchScoped = harmonyType.GetMethod("UnpatchAll", instances, binder: null, types: [typeof(string)], modifiers: null)
                ?? throw new MissingMethodException("HarmonyLib.Harmony", "UnpatchAll(string)");
            scopedHarmony = Activator.CreateInstance(harmonyType, [owner])!;
            guard.GetField("game", statics)!.SetValue(null, game);
            guard.GetField("harmony", statics)!.SetValue(null, scopedHarmony);
            method(guard, "installRelaxGuards").Invoke(null, null);
            require(registered.Count == 11, "only eleven RX constructor, URI, builder and statistics guard registrations are installed");
            var getPatchInfo = harmonyType.GetMethod("GetPatchInfo", statics)!;
            foreach (MethodBase target in registered)
            {
                var patchInfo = getPatchInfo.Invoke(null, [target])!;
                var owners = (IEnumerable<string>)patchInfo.GetType().GetProperty("Owners")!.GetValue(patchInfo)!;
                require(owners.Contains(owner), "each selected target is patched by this temporary proof owner");
            }
            var builderNames = registered.Cast<MethodBase>().Where(target => target.Name == "CreateWebRequest")
                .Select(target => target.DeclaringType!.Name).Order().ToArray();
            require(builderNames.SequenceEqual(["GetScoresRequest", "GetUserRankingsRequest", "GetUserScoresRequest"]),
                "exactly the three parameterized read builders receive the new variant postfix");

            var request = framework.GetType("osu.Framework.IO.Network.WebRequest", true)!;
            var perform = request.GetMethod("internalPerform", instances)!;
            var machine = perform.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                ?? throw new InvalidOperationException("Pinned framework serializer metadata required");
            require(machine.GetMethod("MoveNext", instances)!.GetMethodBody() is not null,
                "pinned framework serializer body is available as metadata; request execution remains excluded");
            checks += RelaxProof.Run(bridge, endpoint, buildersOnly: true);
            require(registered.Count == 11, "inert builders do not install unrelated native guards");
            checkBound();
            unpatchScoped.Invoke(scopedHarmony, [owner]);
            scopedHarmony = null;
            foreach (MethodBase target in registered)
            {
                var patchInfo = getPatchInfo.Invoke(null, [target]);
                IEnumerable<string> owners = patchInfo is null ? [] : (IEnumerable<string>)patchInfo.GetType().GetProperty("Owners")!.GetValue(patchInfo)!;
                require(!owners.Contains(owner), "temporary owner is removed without unpatching another owner");
            }
            Console.WriteLine($"PASS {checks} compiled production RX request-builder checks; pinned metadata and inert builders only, no bootstrap/GameHost/API worker/Perform/Queue/config/player auth/Realm database/network");
            return 0;
        }
        finally
        {
            if (scopedHarmony is not null) unpatchScoped!.Invoke(scopedHarmony, [owner]);
            registered?.Clear();
            AssemblyLoadContext.Default.Resolving -= resolve;
        }

        void checkBound()
        {
            foreach (var entry in bound.Values)
                if (hash(entry.Path) != entry.Hash) throw new InvalidOperationException("Bound stock reference changed");
        }
    }

    private static MethodInfo method(Type type, string name) => type.GetMethod(name, statics)!;
    private static string hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("RX production proof failed");
        checks++;
        Console.WriteLine("PASS " + name);
    }
}
