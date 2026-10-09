using System.Reflection;
using System.Text.Json;

namespace Illyaverse.Transfiguration;

internal enum ClientStartupCheckpoint
{
    OriginalMainEntered, GameHostCreated, GameHostRunEntered, RendererInitializing,
    WindowCreated, RendererInitialized, FirstSuccessfulDraw
}

// Observation only: every prefix is void and every postfix runs after the
// original returns. No renderer/window method or application entry is replaced.
internal static class ClientStartupProgress
{
    private const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly object gate = new();
    private static int observed;
    private static object? host;
    private static Type hostType = null!;
    private static MethodInfo windowGetter = null!;
    private static MethodInfo main = null!, createHost = null!, run = null!, chooseRenderer = null!, setupRenderer = null!;
    private sealed record WindowContract(Type ActualType, Type BaseType, MethodInfo Create, MethodInfo Draw, MethodInfo Exists);
    private static readonly List<WindowContract> windows = new();

    internal static void VerifyContracts(Assembly desktop, Assembly framework)
    {
        var program = type(desktop, "osu.Desktop.Program");
        var factory = type(framework, "osu.Framework.Host");
        var gameHost = type(framework, "osu.Framework.Platform.GameHost");
        var desktopHost = type(framework, "osu.Framework.Platform.DesktopGameHost");
        hostType = type(framework, "osu.Framework.Platform.Windows.WindowsGameHost");
        var renderer = type(framework, "osu.Framework.Graphics.Rendering.IRenderer");
        var surface = type(framework, "osu.Framework.Platform.GraphicsSurfaceType");
        main = method(program, "Main", typeof(void), true, typeof(string[]));
        if (desktop.EntryPoint != main) reject();
        createHost = method(factory, "GetSuitableDesktopHost", desktopHost, true, typeof(string), type(framework, "osu.Framework.HostOptions"));
        run = method(gameHost, "Run", typeof(void), false, type(framework, "osu.Framework.Game"));
        chooseRenderer = method(gameHost, "ChooseAndSetupRenderer", typeof(void), false);
        setupRenderer = method(gameHost, "SetupRendererAndWindow", typeof(void), false, renderer, surface);
        windowGetter = method(gameHost, "get_Window", type(framework, "osu.Framework.Platform.IWindow"), false);
        if (!desktopHost.IsAssignableFrom(hostType) || !chooseRenderer.IsVirtual || chooseRenderer.IsAbstract
            || hostType.GetMethod("ChooseAndSetupRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType != gameHost)
            reject();
        windows.Clear();
        foreach (var backend in new[] { "SDL3", "SDL2" })
        {
            var baseType = type(framework, $"osu.Framework.Platform.{backend}.{backend}Window");
            var actualType = type(framework, $"osu.Framework.Platform.Windows.{backend}WindowsWindow");
            var create = method(baseType, "Create", typeof(void), false);
            var draw = method(baseType, "OnDraw", typeof(void), false);
            var exists = method(baseType, "get_Exists", typeof(bool), false);
            var firstDraw = baseType.GetField("firstDraw", declared);
            var actualCreate = method(actualType, "Create", typeof(void), false);
            if (!baseType.IsAssignableFrom(actualType) || !create.IsVirtual || actualCreate.GetBaseDefinition() != create
                || firstDraw?.FieldType != typeof(bool) || !firstDraw.IsPrivate || firstDraw.IsStatic) reject();
            windows.Add(new(actualType, baseType, create, draw, exists));
        }
    }

    internal static void Install(Action<MethodBase, string, Type?, bool> patch)
    {
        patch(main, nameof(MainPrefix), typeof(ClientStartupProgress), false);
        patch(createHost, nameof(HostPostfix), typeof(ClientStartupProgress), true);
        patch(run, nameof(RunPrefix), typeof(ClientStartupProgress), false);
        patch(chooseRenderer, nameof(RendererPrefix), typeof(ClientStartupProgress), false);
        patch(setupRenderer, nameof(RendererPostfix), typeof(ClientStartupProgress), true);
        foreach (var window in windows)
        {
            patch(window.Create, nameof(WindowPostfix), typeof(ClientStartupProgress), true);
            patch(window.Draw, nameof(DrawPostfix), typeof(ClientStartupProgress), true);
        }
    }

    private static Type type(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;
    private static MethodInfo method(Type declaringType, string name, Type result, bool isStatic, params Type[] arguments)
    {
        var value = declaringType.GetMethod(name, declared, null, arguments, null);
        if (value is null || value.DeclaringType != declaringType || value.ReturnType != result
            || value.IsStatic != isStatic || value.IsAbstract || value.ContainsGenericParameters || value.GetMethodBody() is null) reject();
        return value!;
    }
    private static void reject() => throw SafeStartupDiagnostic.Rejection("startup_progress_contract_incompatible");

    private static void MainPrefix() => Emit(ClientStartupCheckpoint.OriginalMainEntered);
    private static void HostPostfix(object __result)
    {
        if (!LaunchState.Active || __result is null || __result.GetType() != hostType) return;
        lock (gate) host ??= __result;
        if (ReferenceEquals(host, __result)) Emit(ClientStartupCheckpoint.GameHostCreated);
    }
    private static void RunPrefix(object __instance)
    {
        if (ReferenceEquals(host, __instance)) Emit(ClientStartupCheckpoint.GameHostRunEntered);
    }
    private static void RendererPrefix(object __instance)
    {
        if (ReferenceEquals(host, __instance)) Emit(ClientStartupCheckpoint.RendererInitializing);
    }
    private static void RendererPostfix(object __instance)
    {
        if (!ReferenceEquals(host, __instance)) return;
        try
        {
            if (windowGetter.Invoke(host, null) is { } value && isHostedWindow(value)) Emit(ClientStartupCheckpoint.RendererInitialized);
        }
        catch { }
    }
    private static bool isHostedWindow(object value)
    {
        if (host is null) return false;
        var contract = windows.FirstOrDefault(window => value.GetType() == window.ActualType);
        if (contract is null) return false;
        try { return ReferenceEquals(windowGetter.Invoke(host, null), value) && contract.Exists.Invoke(value, null) is true; }
        catch { return false; }
    }
    private static void WindowPostfix(object __instance)
    {
        if (isHostedWindow(__instance)) Emit(ClientStartupCheckpoint.WindowCreated);
    }
    private static void DrawPostfix(object __instance)
    {
        if ((Volatile.Read(ref observed) & (1 << (int)ClientStartupCheckpoint.FirstSuccessfulDraw)) != 0) return;
        if (isHostedWindow(__instance)) Emit(ClientStartupCheckpoint.FirstSuccessfulDraw);
    }

    internal static void Emit(ClientStartupCheckpoint checkpoint)
    {
        if (!LaunchState.Active || !Enum.IsDefined(checkpoint)) return;
        if ((Volatile.Read(ref observed) & (1 << (int)checkpoint)) != 0) return;
        lock (gate)
        {
            var bit = 1 << (int)checkpoint;
            if ((observed & bit) != 0) return;
            observed |= bit;
            var stage = Name(checkpoint);
            SafeStartupDiagnostic.Publish("progress-" + stage, session => JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = 1,
                session,
                stage,
                endpoint = NetworkPolicy.Website,
#if ILLYAVERSE_FIXTURE
                fixture = true,
#else
                fixture = false,
#endif
            }));
        }
    }

    internal static string Name(ClientStartupCheckpoint checkpoint) => checkpoint switch
    {
        ClientStartupCheckpoint.OriginalMainEntered => "original_main_entered",
        ClientStartupCheckpoint.GameHostCreated => "game_host_created",
        ClientStartupCheckpoint.GameHostRunEntered => "game_host_run_entered",
        ClientStartupCheckpoint.RendererInitializing => "renderer_initializing",
        ClientStartupCheckpoint.WindowCreated => "window_created",
        ClientStartupCheckpoint.RendererInitialized => "renderer_initialized",
        ClientStartupCheckpoint.FirstSuccessfulDraw => "first_successful_draw",
        _ => throw new ArgumentOutOfRangeException(nameof(checkpoint))
    };

#if ILLYAVERSE_FIXTURE
    internal static void ResetFixture() { lock (gate) { observed = 0; host = null; } }
#endif
}
