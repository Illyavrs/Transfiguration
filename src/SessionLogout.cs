using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Game.Online.API;

namespace Illyaverse.Transfiguration;

// Only the pinned UI sign-out actions call these helpers. API failure, refresh,
// remote disconnect and normal client disposal still use untouched stock Logout.
internal static class SessionLogout
{
    private const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<MethodBase, (int Direct, int Delegate)> targets = new();
    private static FieldInfo authentication = null!, endpoint = null!, token = null!, accessToken = null!;
    private static PropertyInfo tokenValue = null!;
    private static MethodInfo nativeLogout = null!;

    internal static void VerifyContracts(Assembly game)
    {
        nativeLogout = typeof(IAPIProvider).GetMethod("Logout")!;
        var actualLogout = typeof(APIAccess).GetMethod("Logout")!;
        authentication = typeof(APIAccess).GetField("authentication", declared)!;
        endpoint = typeof(OAuth).GetField("endpoint", declared)!;
        token = typeof(OAuth).GetField("Token", declared)!;
        tokenValue = typeof(Bindable<OAuthToken>).GetProperty("Value")!;
        accessToken = typeof(OAuthToken).GetField("AccessToken", declared)!;
        var logoutMapping = typeof(APIAccess).GetInterfaceMap(typeof(IAPIProvider));
        var logoutIndex = Array.IndexOf(logoutMapping.InterfaceMethods, nativeLogout);
        if (nativeLogout is null || nativeLogout.IsStatic || nativeLogout.ReturnType != typeof(void) || nativeLogout.GetParameters().Length != 0
            || actualLogout is null || actualLogout.IsStatic || actualLogout.DeclaringType != typeof(APIAccess) || actualLogout.ReturnType != typeof(void) || actualLogout.GetParameters().Length != 0
            || logoutIndex < 0 || !Equals(logoutMapping.TargetMethods[logoutIndex], actualLogout)
            || authentication?.FieldType != typeof(OAuth) || !authentication.IsPrivate || !authentication.IsInitOnly
            || endpoint?.FieldType != typeof(string) || !endpoint.IsPrivate || !endpoint.IsInitOnly
            || token?.FieldType != typeof(Bindable<OAuthToken>) || !token.IsPublic || !token.IsInitOnly
            || tokenValue?.PropertyType != typeof(OAuthToken) || tokenValue.GetMethod?.IsPublic != true
            || accessToken?.FieldType != typeof(string) || !accessToken.IsPublic || accessToken.IsStatic)
            reject();

        targets.Clear();
        foreach (var (name, directExpected, delegateExpected) in new[]
        {
            ("osu.Game.Overlays.Login.LoginPanel", 1, 1),
            ("osu.Game.Overlays.Login.SecondFactorAuthForm", 2, 0),
        })
        {
            var root = game.GetType(name, throwOnError: true)!;
            var direct = 0;
            var delegates = 0;
            foreach (var method in methods(root))
            {
                if (method.GetMethodBody() is null) continue;
                var instructions = PatchProcessor.GetOriginalInstructions(method);
                var methodDirect = instructions.Count(instruction => instruction.opcode == OpCodes.Callvirt && Equals(instruction.operand, nativeLogout));
                var methodDelegates = instructions.Count(instruction => instruction.opcode == OpCodes.Ldvirtftn && Equals(instruction.operand, nativeLogout));
                if (instructions.Any(instruction => Equals(instruction.operand, nativeLogout)
                    && instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Ldvirtftn)) reject();
                if (methodDirect + methodDelegates == 0) continue;
                if (method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != typeof(void)
                    || !method.Name.Contains('<')) reject();
                for (var index = 0; index < instructions.Count; index++)
                    if (instructions[index].opcode == OpCodes.Ldvirtftn && Equals(instructions[index].operand, nativeLogout)
                        && !delegatePattern(instructions, index - 1)) reject();
                targets.Add(method, (methodDirect, methodDelegates));
                direct += methodDirect;
                delegates += methodDelegates;
            }
            if (direct != directExpected || delegates != delegateExpected) reject();
        }
        if (targets.Count != 4) reject();
    }

    internal static void Install(Harmony harmony, Action<MethodBase> registered)
    {
        foreach (var target in targets.Keys)
        {
            harmony.Patch(target, transpiler: new HarmonyMethod(local(nameof(Rewrite))) { priority = Priority.First });
            registered(target);
        }
    }

    private static IEnumerable<MethodInfo> methods(Type root)
    {
        foreach (var method in root.GetMethods(declared)) yield return method;
        foreach (var nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var method in methods(nested)) yield return method;
    }

    private static bool delegatePattern(IReadOnlyList<CodeInstruction> instructions, int index) => index >= 0 && index + 2 < instructions.Count
        && instructions[index].opcode == OpCodes.Dup
        && instructions[index + 1].opcode == OpCodes.Ldvirtftn && Equals(instructions[index + 1].operand, nativeLogout)
        && instructions[index + 2].opcode == OpCodes.Newobj && instructions[index + 2].operand is ConstructorInfo constructor
        && constructor.DeclaringType == typeof(Action) && constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(object), typeof(IntPtr)])
        && instructions[index + 1].labels.Count == 0 && instructions[index + 1].blocks.Count == 0
        && instructions[index + 2].labels.Count == 0 && instructions[index + 2].blocks.Count == 0;

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        if (!targets.TryGetValue(__originalMethod, out var expected)) reject();
        var instructions = original.ToList();
        var result = new List<CodeInstruction>();
        var direct = 0;
        var delegates = 0;
        for (var index = 0; index < instructions.Count; index++)
        {
            if (delegatePattern(instructions, index))
            {
                var replacement = new CodeInstruction(instructions[index]) { opcode = OpCodes.Call, operand = local(nameof(MakeExplicitLogoutAction)) };
                result.Add(replacement);
                index += 2;
                delegates++;
            }
            else if (instructions[index].opcode == OpCodes.Callvirt && Equals(instructions[index].operand, nativeLogout))
            {
                result.Add(new CodeInstruction(instructions[index]) { opcode = OpCodes.Call, operand = local(nameof(ExplicitLogout)) });
                direct++;
            }
            else result.Add(instructions[index]);
        }
        if ((direct, delegates) != expected) reject();
        return result;
    }

    private static Action MakeExplicitLogoutAction(IAPIProvider provider) => () => ExplicitLogout(provider);

    private static void ExplicitLogout(IAPIProvider provider)
    {
        try
        {
            if (provider.GetType() == typeof(APIAccess) && provider.Endpoints.APIUrl == NetworkPolicy.Api
                && provider.Endpoints.WebsiteUrl == NetworkPolicy.Website
                && provider.Endpoints.APIClientID == NetworkPolicy.ClientId && provider.Endpoints.APIClientSecret == NetworkPolicy.ClientSecret
                && authentication.GetValue(provider) is OAuth oauth && endpoint.GetValue(oauth) as string == NetworkPolicy.Api)
            {
                // Snapshot the current passive value once. AccessToken's API
                // getter could refresh; TokenString would expose refresh data.
                var current = tokenValue.GetValue(token.GetValue(oauth));
                var captured = current is null ? null : accessToken.GetValue(current) as string;
                if (captured is not null) PrivateSessionRevocation.Queue(NetworkPolicy.Api, captured);
            }
        }
        catch (Exception)
        {
            // Native local logout must still run if the optional capture fails.
        }
        provider.Logout();
    }

    private static MethodInfo local(string name) => typeof(SessionLogout).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void reject() => throw SafeStartupDiagnostic.Rejection("logout_contract_incompatible");
}
