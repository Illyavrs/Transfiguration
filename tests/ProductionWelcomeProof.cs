using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Compiled-bridge metadata and ordinary callback state only. This mode does not
// install Harmony patches or construct a Game, APIAccess, notification or storage.
internal static class ProductionWelcomeProof
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
            "prior immutable receipt binds all required stock references");
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
        try
        {
            var welcome = bridge.GetType("Illyaverse.Transfiguration.SessionWelcome", true)!;
            var guard = bridge.GetType("Illyaverse.Transfiguration.Guard", true)!;
            var logout = bridge.GetType("Illyaverse.Transfiguration.SessionLogout", true)!;
            method(welcome, "VerifyContracts").Invoke(null, null);
            require(true, "compiled welcome validates pinned API bindables, scheduler and stock notification contracts");
            var game = AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == "osu.Game");
            var api = game.GetType("osu.Game.Online.API.APIAccess", true)!;
            var constructor = (ConstructorInfo)welcome.GetField("apiConstructor", statics)!.GetValue(null)!;
            require(constructor.DeclaringType == api && constructor.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(["osu.Game.OsuGameBase", "osu.Game.Configuration.OsuConfigManager", "osu.Game.Online.EndpointConfiguration", "System.String"]),
                "welcome reuses the existing four-argument API constructor target");
            var callback = method(welcome, "ApiConstructorPostfix");
            require(callback.ReturnType == typeof(void) && callback.GetParameters().Length == 1
                && callback.GetParameters()[0].Name == "__instance" && callback.GetParameters()[0].ParameterType == api,
                "constructor observer is a void postfix without by-reference return mutation");

            var harmony = AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == "0Harmony");
            var getInstructions = harmony.GetType("HarmonyLib.PatchProcessor", true)!.GetMethods(statics)
                .Single(candidate => candidate.Name == "GetOriginalInstructions" && candidate.GetParameters().Length == 2
                    && candidate.GetParameters()[0].ParameterType == typeof(MethodBase) && candidate.GetParameters()[1].ParameterType == typeof(ILGenerator));
            var instructionType = harmony.GetType("HarmonyLib.CodeInstruction", true)!;
            var operand = instructionType.GetField("operand")!;
            MethodBase[] called(MethodBase target) => ((IEnumerable)getInstructions.Invoke(null, [target, null])!).Cast<object>()
                .Select(instruction => operand.GetValue(instruction)).OfType<MethodBase>().ToArray();
            require(called(method(guard, "verifyContracts")).Count(calledMethod => Equals(calledMethod, method(welcome, "VerifyContracts"))) == 1,
                "full bootstrap verifies welcome metadata before installing client guards");
            require(called(method(guard, "installClientGuards")).Count(calledMethod => Equals(calledMethod, method(welcome, "Install"))) == 1,
                "client guards register the welcome observer exactly once");
            require(!called(method(welcome, "Install")).Any(calledMethod => calledMethod.Name == "Add"),
                "welcome registration does not append another entry to native patch target accounting");
            require(called(method(logout, "ExplicitLogout")).Count(calledMethod => Equals(calledMethod, method(welcome, "ExplicitLogout"))) == 1,
                "explicit UI sign-out resets the welcome epoch once");
            require(!called(api.GetMethod("Logout")!).Any(calledMethod => calledMethod.DeclaringType == welcome),
                "ordinary API logout retains stock behavior without resetting welcome state");

            var stateType = bridge.GetType("Illyaverse.Transfiguration.SessionWelcomeState", true)!;
            var queued = new Queue<Action>();
            var delayed = new Queue<Action>();
            var online = false;
            var ready = true;
            var posts = 0;
            var state = stateType.GetConstructors(instances).Single().Invoke([
                new Func<bool>(() => online), new Func<bool>(() => ready),
                new Action<Action>(queued.Enqueue), new Action<Action>(delayed.Enqueue), new Action(() => posts++)]);
            void observe() => stateType.GetMethod("Observe", instances)!.Invoke(state, null);
            void signOut() => stateType.GetMethod("ExplicitLogout", instances)!.Invoke(state, null);
            void flush() { while (queued.TryDequeue(out var action)) action(); }
            observe();
            require(queued.Count == 0 && posts == 0, "compiled state emits nothing for offline login");
            online = true;
            observe(); observe();
            require(queued.Count == 1 && posts == 0, "compiled state queues one verified online callback");
            flush();
            require(posts == 1, "compiled state posts one notification");
            online = false; observe(); online = true; observe(); flush();
            require(posts == 1, "compiled state suppresses refresh and reconnect welcomes");
            signOut(); ready = false; observe(); flush();
            require(posts == 1 && delayed.Count == 1, "compiled state defers a new login until the stock overlay is ready");
            signOut(); ready = true; delayed.Dequeue()();
            require(posts == 1 && delayed.Count == 0, "compiled state discards a callback from an older explicit logout epoch");
            observe(); flush();
            require(posts == 2, "compiled state welcomes the next verified login after explicit logout");
            require(((IList)guard.GetField("patched", statics)!.GetValue(null)!).Count == 0,
                "detached proof installs no native guards or Harmony patches");
            checkBound();
            Console.WriteLine($"PASS {checks} compiled production welcome checks; pinned metadata and synthetic callbacks only, no game constructors, notification UI, auth, player data, Realm, shell or network");
            return 0;
        }
        finally { AssemblyLoadContext.Default.Resolving -= resolve; }

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
        if (!value) throw new InvalidOperationException("Welcome production proof failed");
        checks++;
        Console.WriteLine("PASS " + name);
    }
}
