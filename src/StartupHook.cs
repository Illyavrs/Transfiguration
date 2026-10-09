using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// This entry point is invoked by the runtime before the client's Main method.
internal static class StartupHook
{
    public static void Initialize()
    {
        Illyaverse.Transfiguration.SafeStartupDiagnostic.Begin();
        try
        {
            AssemblyLoadContext.Default.Resolving += resolveEmbeddedHarmony;
            initializeGuard();
        }
        catch (Exception exception)
        {
            // This bounded sidecar is the only diagnostic forwarded to the launcher.
            // Preserve the startup-hook rejection: client Main must never follow it.
            Illyaverse.Transfiguration.SafeStartupDiagnostic.Report(exception);
            throw;
        }
    }

    private static Assembly? resolveEmbeddedHarmony(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name != "0Harmony") return null;
        using var resource = typeof(StartupHook).Assembly.GetManifestResourceStream("Illyaverse.Transfiguration.0Harmony.dll")
            ?? throw new InvalidOperationException("transfiguration_dependency_missing");
        return context.LoadFromStream(resource);
    }

    // Avoid resolving Harmony before the embedded resolver has been registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void initializeGuard() => Illyaverse.Transfiguration.Guard.Initialize();
}
