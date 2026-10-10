using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Detached proof: only metadata from previously hash-bound stock DLL references
// and compiled bridge helpers. No native UI, APIAccess or storage construction.
public static class ProductionLogoutProof
{
    private static int checks;
    private const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static int Run(string productionPath, string nativeDirectory, string boundReceipt)
    {
        if (Environment.Version != new Version(10, 0, 12)) throw new InvalidOperationException("Owned .NET 10.0.12 required");
        var nativeRoot = Path.GetFullPath(nativeDirectory);
        using var receipt = JsonDocument.Parse(File.ReadAllText(boundReceipt));
        var bound = receipt.RootElement.GetProperty("frozen_inputs").EnumerateArray()
            .Select(entry => (Path: Path.GetFullPath(entry.GetProperty("path").GetString()!), Hash: entry.GetProperty("sha256").GetString()!))
            .Where(entry => Path.GetDirectoryName(entry.Path)!.Equals(nativeRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(entry.Path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => Path.GetFileNameWithoutExtension(entry.Path), StringComparer.OrdinalIgnoreCase);
        require(bound.ContainsKey("osu.Game") && bound.ContainsKey("osu.Framework") && bound.ContainsKey("Realm"), "prior receipt binds the required stock assembly references");
        checkBound();
        var bridge = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(productionPath));
        using (var stream = bridge.GetManifestResourceStream("Illyaverse.Transfiguration.0Harmony.dll")
            ?? throw new InvalidOperationException("Embedded Harmony missing"))
            AssemblyLoadContext.Default.LoadFromStream(stream);
        Assembly? resolver(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name is null || !bound.TryGetValue(name.Name, out var entry)) return null;
            if (hash(entry.Path) != entry.Hash) throw new InvalidOperationException("Requested stock DLL changed");
            return context.LoadFromAssemblyPath(entry.Path);
        }
        AssemblyLoadContext.Default.Resolving += resolver;
        try
        {
            var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(bound["osu.Game"].Path);
            var logout = bridge.GetType("Illyaverse.Transfiguration.SessionLogout", true)!;
            method(logout, "VerifyContracts").Invoke(null, [game]);
            var targets = (IDictionary)logout.GetField("targets", statics)!.GetValue(null)!;
            require(targets.Count == 4, "only four pinned UI sign-out methods are selected");
            var harmony = AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == "0Harmony");
            var processor = harmony.GetType("HarmonyLib.PatchProcessor", true)!;
            var getInstructions = processor.GetMethods(statics).Single(candidate => candidate.Name == "GetOriginalInstructions"
                && candidate.GetParameters().Length == 2 && candidate.GetParameters()[0].ParameterType == typeof(MethodBase)
                && candidate.GetParameters()[1].ParameterType == typeof(ILGenerator));
            object instructions(MethodBase target) => getInstructions.Invoke(null, [target, null])!;
            var instructionType = harmony.GetType("HarmonyLib.CodeInstruction", true)!;
            var opcode = instructionType.GetField("opcode")!;
            var operand = instructionType.GetField("operand")!;
            var labels = instructionType.GetField("labels")!;
            var blocks = instructionType.GetField("blocks")!;
            var direct = 0;
            var delegates = 0;
            foreach (DictionaryEntry entry in targets)
            {
                var target = (MethodBase)entry.Key;
                require(target.DeclaringType!.FullName!.StartsWith("osu.Game.Overlays.Login.", StringComparison.Ordinal)
                    && target.Name.Contains('<'), "selected site is a compiler-generated native login UI action");
                var original = instructions(target);
                var before = ((IEnumerable)original).Cast<object>().ToArray();
                var after = ((IEnumerable)method(logout, "Rewrite").Invoke(null, [original, target])!).Cast<object>().ToArray();
                var removedDelegateIndex = Array.FindIndex(before, instruction => (OpCode)opcode.GetValue(instruction)! == OpCodes.Ldvirtftn
                    && operand.GetValue(instruction) is MethodInfo delegated && delegated.Name == "Logout");
                if (removedDelegateIndex > 0)
                {
                    var pattern = instructions(target);
                    var removed = ((IList)pattern)[removedDelegateIndex]!;
                    var removedLabels = (IList)labels.GetValue(removed)!;
                    removedLabels.Add(new DynamicMethod("SyntheticLabel", typeof(void), []).GetILGenerator().DefineLabel());
                    require(method(logout, "delegatePattern").Invoke(null, [pattern, removedDelegateIndex - 1]) is false,
                        "delegate rewriting rejects labels on an instruction it would remove");
                    removedLabels.Clear();
                    var removedBlocks = (IList)blocks.GetValue(removed)!;
                    var blockType = harmony.GetType("HarmonyLib.ExceptionBlock", true)!;
                    var blockKind = harmony.GetType("HarmonyLib.ExceptionBlockType", true)!;
                    removedBlocks.Add(Activator.CreateInstance(blockType, [Enum.Parse(blockKind, "BeginExceptionBlock"), null])!);
                    require(method(logout, "delegatePattern").Invoke(null, [pattern, removedDelegateIndex - 1]) is false,
                        "delegate rewriting rejects exception boundaries on an instruction it would remove");
                }
                var cursor = 0;
                for (var index = 0; index < before.Length; index++)
                {
                    var originalOpcode = (OpCode)opcode.GetValue(before[index])!;
                    var originalOperand = operand.GetValue(before[index]);
                    var isLogout = originalOperand is MethodInfo called && called.Name == "Logout"
                        && called.DeclaringType?.FullName == "osu.Game.Online.API.IAPIProvider";
                    if (originalOpcode == OpCodes.Dup && index + 2 < before.Length
                        && (OpCode)opcode.GetValue(before[index + 1])! == OpCodes.Ldvirtftn
                        && operand.GetValue(before[index + 1]) is MethodInfo delegated && delegated.Name == "Logout")
                    {
                        require((OpCode)opcode.GetValue(after[cursor])! == OpCodes.Call
                            && operand.GetValue(after[cursor]) is MethodInfo helper && helper.DeclaringType == logout && helper.Name == "MakeExplicitLogoutAction",
                            "native sign-out delegate keeps the receiver and produces a guarded Action");
                        preserve(before[index], after[cursor]);
                        index += 2; delegates++;
                    }
                    else if (originalOpcode == OpCodes.Callvirt && isLogout)
                    {
                        require((OpCode)opcode.GetValue(after[cursor])! == OpCodes.Call
                            && operand.GetValue(after[cursor]) is MethodInfo helper && helper.DeclaringType == logout && helper.Name == "ExplicitLogout",
                            "only an explicit native UI Logout call is replaced");
                        preserve(before[index], after[cursor]); direct++;
                    }
                    else if ((OpCode)opcode.GetValue(before[index])! != (OpCode)opcode.GetValue(after[cursor])!
                        || !Equals(operand.GetValue(before[index]), operand.GetValue(after[cursor]))) throw new InvalidOperationException("Unrelated UI instruction changed");
                    cursor++;
                }
                require(cursor == after.Length, "rewritten UI body has exactly the planned instruction count");
                require(true, "all unrelated UI instructions retain their original opcode and operand");
                void preserve(object original, object replacement)
                {
                    require(((IEnumerable)labels.GetValue(original)!).Cast<object>().SequenceEqual(((IEnumerable)labels.GetValue(replacement)!).Cast<object>())
                        && ((IEnumerable)blocks.GetValue(original)!).Cast<object>().SequenceEqual(((IEnumerable)blocks.GetValue(replacement)!).Cast<object>()),
                        "replacement preserves entry labels and exception blocks");
                }
            }
            require(direct == 3 && delegates == 1, "exactly three direct calls and one method-group action are rewritten");
            var api = game.GetType("osu.Game.Online.API.APIAccess", true)!;
            var connector = game.GetType("osu.Game.Online.HubClientConnector", true)!;
            require(!targets.Contains(api.GetMethod("Logout")!) && !targets.Contains(api.GetMethod("Dispose")!), "automatic API logout and ordinary disposal are not targets");
            require(targets.Keys.Cast<MethodBase>().All(target => target.DeclaringType != connector), "hub disconnect cannot acquire explicit sign-out behavior");
            var providerType = game.GetType("osu.Game.Online.API.IAPIProvider", true)!;
            var proxy = createProvider(providerType);
            method(logout, "ExplicitLogout").Invoke(null, [proxy]);
            require(((NoCredentialProvider)proxy).Logouts == 1 && ((NoCredentialProvider)proxy).OtherCalls == 0,
                "a non-native provider logs out exactly once without inspecting any access getter or endpoint");
            checkBound();
            require(((IList)bridge.GetType("Illyaverse.Transfiguration.Guard", true)!.GetField("patched", statics)!.GetValue(null)!).Count == 0
                && bridge.GetCustomAttributesData().All(attribute => attribute.AttributeType.FullName != "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute"),
                "proof does not install full guards or enter startup bootstrap");
            Console.WriteLine($"PASS {checks} compiled production explicit logout contract checks; metadata and synthetic provider only, no game constructors, config, auth, Realm database, shell or network");
            return 0;
        }
        finally { AssemblyLoadContext.Default.Resolving -= resolver; }

        void checkBound()
        {
            foreach (var entry in bound.Values)
                if (hash(entry.Path) != entry.Hash) throw new InvalidOperationException("Pinned stock reference changed");
        }
    }

    private static NoCredentialProvider createProvider(Type provider)
    {
        // The pinned interface has an internal scheduler member. As in the
        // existing RelaxProof, access metadata belongs only to this in-memory
        // synthetic test assembly, never to the production bridge.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Illyaverse.Proof.LogoutProvider"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute)
            .GetConstructor([typeof(string)])!, ["osu.Game"]));
        var type = assembly.DefineDynamicModule("proof").DefineType("LogoutProvider", TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(NoCredentialProvider), [provider]);
        foreach (var contract in provider.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var parameters = contract.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            var implementation = type.DefineMethod(contract.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final
                | MethodAttributes.NewSlot | MethodAttributes.HideBySig, contract.ReturnType, parameters);
            var il = implementation.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, contract.Name);
            il.Emit(OpCodes.Call, typeof(NoCredentialProvider).GetMethod(nameof(NoCredentialProvider.Handle))!);
            if (contract.ReturnType == typeof(void)) il.Emit(OpCodes.Pop);
            else if (contract.ReturnType.IsValueType) il.Emit(OpCodes.Unbox_Any, contract.ReturnType);
            else il.Emit(OpCodes.Castclass, contract.ReturnType);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(implementation, contract);
        }
        return (NoCredentialProvider)Activator.CreateInstance(type.CreateType()!)!;
    }

    public class NoCredentialProvider
    {
        public int Logouts;
        public int OtherCalls;
        public object? Handle(string name)
        {
            if (name == "Logout") { Logouts++; return null; }
            OtherCalls++;
            throw new InvalidOperationException("A passive sign-out helper must not invoke API getters");
        }
    }

    private static string hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static MethodInfo method(Type type, string name) => type.GetMethod(name, statics) ?? throw new MissingMethodException(type.FullName, name);
    private static void require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
}
