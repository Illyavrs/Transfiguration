using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;

namespace Illyaverse.Transfiguration;

internal static class Guard
{
    private const string owner = "community.illyaverse.transfiguration";
    private static readonly object gate = new();
    private static string installRoot = string.Empty;
    private static string dataRoot = string.Empty;
    private static string controlRoot = string.Empty;
    private static readonly List<MethodBase> patched = new();
    private static Assembly game = null!;
    private static Assembly framework = null!;
    private static Assembly desktop = null!;
    private static Harmony harmony = null!;

    internal static void Initialize()
    {
        lock (gate)
        {
            if (LaunchState.Active) return;
            NetworkPolicy.Initialize();
            var session = required("ILLYAVERSE_LAZER_SESSION", "session_missing");
            if (session.Length != 64 || !session.All(Uri.IsHexDigit)) fail("session_invalid");
            installRoot = absoluteDirectory(required("ILLYAVERSE_LAZER_INSTALL_ROOT", "install_root_missing"));
            dataRoot = absoluteDirectory(required("ILLYAVERSE_LAZER_DATA_ROOT", "data_root_missing"));
            controlRoot = absoluteDirectory(required("ILLYAVERSE_LAZER_CONTROL_ROOT", "control_root_missing"));
            validateDataRoot();
            var readyFile = canonicalPath(required("ILLYAVERSE_LAZER_READY_FILE", "ready_file_missing"));
            if (!string.Equals(readyFile, Path.Combine(controlRoot, $"session-ready-{session}.json"), ClientAssemblyPath.Comparison)) fail("ready_path_invalid");
            if (File.Exists(readyFile)) fail("ready_file_exists");
            rejectReparse(dataRoot);
            rejectReparse(controlRoot);
            Environment.SetEnvironmentVariable("OSU_DISABLE_ERROR_REPORTING", "1");
            Environment.SetEnvironmentVariable("OSU_EXTERNAL_UPDATE_PROVIDER", "Illyaverse");
            // No inherited hook may apply unexpectedly to child processes. The active runtime property is unchanged.
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
            SafeStartupDiagnostic.Checkpoint(StartupStage.Assemblies);
            AssemblyLoadContext.Default.Resolving += resolveClientAssembly;
            framework = loadClient("osu.Framework.dll");
            game = loadClient("osu.Game.dll");
            desktop = loadClient("osu!.dll");
            harmony = new Harmony(owner);
            SafeStartupDiagnostic.Checkpoint(StartupStage.Contracts);
            verifyContracts();
            SafeStartupDiagnostic.Checkpoint(StartupStage.NetworkGuards);
            installNetworkGuards();
            SafeStartupDiagnostic.Checkpoint(StartupStage.ClientGuards);
            installClientGuards();
            SafeStartupDiagnostic.Checkpoint(StartupStage.RelaxGuards);
            installRelaxGuards();
            SafeStartupDiagnostic.Checkpoint(StartupStage.Ready);
            foreach (var method in patched)
                if (Harmony.GetPatchInfo(method)?.Owners.Contains(owner) != true) fail("patch_not_installed");
            LaunchState.Active = true;
            using var readyStream = new FileStream(readyFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(readyStream, new
            {
                protocol = 1,
                session,
                status = "ready",
                endpoint = NetworkPolicy.Website,
#if ILLYAVERSE_FIXTURE
                fixture = true,
#else
                fixture = false,
#endif
            });
            readyStream.Flush(flushToDisk: true);
        }
    }

    private static string required(string key, string code) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw SafeStartupDiagnostic.Rejection(code);

    private static string absoluteDirectory(string value)
    {
        if (!Path.IsPathFullyQualified(value)) fail("path_not_absolute");
        var full = Path.TrimEndingDirectorySeparator(canonicalPath(value));
        if (!Directory.Exists(full)) fail("directory_missing");
        return full;
    }

    private static string canonicalPath(string value) => ClientAssemblyPath.Canonical(value);

    private static void validateDataRoot()
    {
#if ILLYAVERSE_FIXTURE
        if (!Path.GetFileName(dataRoot).StartsWith("data-transfiguration-proof", StringComparison.Ordinal)) fail("fixture_data_path_invalid");
        if (!Path.GetFileName(controlRoot).StartsWith("data-transfiguration-proof", StringComparison.Ordinal)) fail("fixture_control_path_invalid");
#else
        var expected = Path.Combine(ClientAssemblyPath.LocalDataHome(), "Illyaverse", "Kaleidoscope");
        if (!string.Equals(controlRoot, expected, ClientAssemblyPath.Comparison)) fail("control_path_invalid");
#endif
        // Portable lazer legitimately stores its library beside its executable. Only the
        // launcher control directory is independent; the resolved library is reused.
        if (string.Equals(dataRoot, controlRoot, ClientAssemblyPath.Comparison)
            || dataRoot.StartsWith(controlRoot + Path.DirectorySeparatorChar, ClientAssemblyPath.Comparison)
            || controlRoot.StartsWith(dataRoot + Path.DirectorySeparatorChar, ClientAssemblyPath.Comparison)) fail("control_data_overlap");
    }

    private static void rejectReparse(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) fail("data_reparse_point");
    }

    private static Assembly loadClient(string filename)
    {
        var path = Path.Combine(installRoot, filename);
        if (!File.Exists(path)) fail("client_assembly_missing");
        var expectedName = Path.GetFileNameWithoutExtension(filename);
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == expectedName);
        if (loaded is not null)
        {
            if (!ClientAssemblyPath.Matches(loaded.Location, path)) fail("client_assembly_location_mismatch");
            return loaded;
        }
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private static Assembly? resolveClientAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = Path.Combine(installRoot, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static Type type(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;
    private static MethodInfo method(Type t, string name, params Type[] parameters) => t.GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
        binder: null, types: parameters, modifiers: null) ?? throw new MissingMethodException(t.FullName, name);
    private static MethodInfo local(Type t, string name) => t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(t.FullName, name);

    private static void patch(MethodBase target, string patchName, Type? patchType = null, bool postfix = false)
    {
        var injection = new HarmonyMethod(local(patchType ?? typeof(Guard), patchName)) { priority = Priority.First };
        harmony.Patch(target, prefix: postfix ? null : injection, postfix: postfix ? injection : null);
        patched.Add(target);
    }

    private static void patchFinalizer(MethodBase target, string patchName, Type patchType)
    {
        harmony.Patch(target, finalizer: new HarmonyMethod(local(patchType, patchName)) { priority = Priority.First });
        patched.Add(target);
    }

    private static void verifyContracts()
    {
        // This allowlist is updated only after the isolated contract and transport proofs pass.
        var profile = ClientProfile.Current;
        requireAssembly(game, ClientProfile.GameVersion, profile.GameSha256);
        requireAssembly(framework, ClientProfile.FrameworkVersion, profile.FrameworkSha256);
        requireAssembly(desktop, ClientProfile.EntryVersion, profile.EntrySha256);
        requireAssembly(typeof(SocketsHttpHandler).Assembly, ClientProfile.NetworkVersion, profile.HttpSha256);
        requireAssembly(typeof(ClientWebSocket).Assembly, ClientProfile.NetworkVersion, profile.WebSocketsSha256);
        if (Environment.Version != new Version(10, 0, 12)) fail("runtime_version_unverified");
        var apiVersion = type(game, "osu.Game.Rulesets.Ruleset").GetField("CURRENT_RULESET_API_VERSION", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
        if (!Equals(apiVersion, "2026.818.0")) fail("ruleset_api_incompatible");
        var endpoint = type(game, "osu.Game.Online.EndpointConfiguration");
        foreach (var property in new[] { "APIUrl", "WebsiteUrl", "APIClientID", "APIClientSecret", "SpectatorUrl", "MultiplayerUrl", "MetadataUrl", "BeatmapSubmissionServiceUrl", "LivenessProbeUrl" })
        {
            var value = endpoint.GetProperty(property);
            if (value?.PropertyType != typeof(string) || value.SetMethod?.IsPublic != true) fail("endpoint_contract_incompatible");
        }
        if (method(type(game, "osu.Game.OsuGameBase"), "CreateEndpoints").ReturnType != endpoint) fail("endpoint_factory_incompatible");
        _ = method(type(framework, "osu.Framework.Host"), "GetSuitableDesktopHost", typeof(string), type(framework, "osu.Framework.HostOptions"));
        _ = method(type(framework, "osu.Framework.Platform.DesktopGameHost"), "GetDefaultGameStorage");
        if (method(type(framework, "osu.Framework.Platform.DesktopGameHost"), "OpenUrlExternally", typeof(string)).ReturnType != typeof(void))
            fail("browser_contract_incompatible");
        _ = method(type(desktop, "osu.Desktop.OsuGameDesktop"), "GetStorageForStableInstall");
        _ = typeof(osu.Game.Rulesets.Transfiguration.BridgeRuleset).GetMethods();
        AccountConfigIsolation.VerifyContracts(dataRoot);
        SessionLogout.VerifyContracts(game);
        SessionWelcome.VerifyContracts();
        RelaxContext.VerifyContracts();
        ClientStartupProgress.VerifyContracts(desktop, framework);
    }

    private static void installRelaxGuards()
    {
        var api = type(game, "osu.Game.Online.API.APIAccess");
        patch(api.GetConstructor([type(game, "osu.Game.OsuGameBase"), type(game, "osu.Game.Configuration.OsuConfigManager"), type(game, "osu.Game.Online.EndpointConfiguration"), typeof(string)])!, "CapturePrefix", typeof(RelaxContext));
        var request = type(game, "osu.Game.Online.API.APIRequest");
        patch(request.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!, "RequestConstructorPrefix", typeof(RelaxContext));
        patch(request.GetProperty("Uri", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!, "UriPostfix", typeof(RelaxContext), postfix: true);
        var scores = type(game, "osu.Game.Online.API.Requests.GetScoresRequest");
        patch(scores.GetConstructors().Single(), "ScoresConstructorPrefix", typeof(RelaxContext));
        patch(method(scores, "Equals", scores), "ScoresEqualsPrefix", typeof(RelaxContext));
        foreach (var name in new[] { "GetScoresRequest", "GetUserScoresRequest", "GetUserRankingsRequest" })
            patch(method(type(game, "osu.Game.Online.API.Requests." + name), "CreateWebRequest"), "ReadWebRequestPostfix", typeof(RelaxContext), postfix: true);
        var statistics = type(game, "osu.Game.Online.LocalUserStatisticsProvider");
        patch(statistics.GetConstructor(Type.EmptyTypes)!, "StatisticsConstructorPostfix", typeof(RelaxContext), postfix: true);
        patch(method(statistics, "GetStatisticsFor", type(game, "osu.Game.Rulesets.RulesetInfo")), "StatisticsGetPrefix", typeof(RelaxContext));
        patch(method(statistics, "RefetchStatistics", type(game, "osu.Game.Rulesets.RulesetInfo"), typeof(Action<>).MakeGenericType(type(game, "osu.Game.Online.UserStatisticsUpdate"))), "StatisticsRefetchPrefix", typeof(RelaxContext));
    }

    private static void requireAssembly(Assembly assembly, string version, string sha256)
    {
        if (assembly.GetName().Version?.ToString() != version) fail("client_version_unverified");
        using var stream = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(stream)) != sha256) fail("client_assembly_unverified");
    }

    private static void installNetworkGuards()
    {
        patch(typeof(SocketsHttpHandler).GetConstructor(Type.EmptyTypes)!, "SocketsHandlerPostfix", typeof(NetworkPolicy), postfix: true);
        patch(typeof(HttpClientHandler).GetConstructor(Type.EmptyTypes)!, "HttpHandlerPostfix", typeof(NetworkPolicy), postfix: true);
        patch(method(typeof(SocketsHttpHandler), "Send", typeof(HttpRequestMessage), typeof(CancellationToken)), "HttpSendPrefix", typeof(NetworkPolicy));
        patch(method(typeof(SocketsHttpHandler), "SendAsync", typeof(HttpRequestMessage), typeof(CancellationToken)), "HttpSendPrefix", typeof(NetworkPolicy));
        patch(method(typeof(ClientWebSocket), "ConnectAsync", typeof(Uri), typeof(CancellationToken)), "SocketConnectPrefix", typeof(NetworkPolicy));
        patch(method(typeof(ClientWebSocket), "ConnectAsync", typeof(Uri), typeof(HttpMessageInvoker), typeof(CancellationToken)), "SocketConnectPrefix", typeof(NetworkPolicy));
        patch(method(type(game, "osu.Game.Online.TrustedDomainOnlineStore"), "GetLookupUrl", typeof(string)), "ResourcePrefix", typeof(NetworkPolicy));
    }

    private static void installClientGuards()
    {
        patch(method(type(game, "osu.Game.OsuGameBase"), "CreateEndpoints"), "EndpointsPrefix");
        patch(method(type(framework, "osu.Framework.Host"), "GetSuitableDesktopHost", typeof(string), type(framework, "osu.Framework.HostOptions")), "HostPrefix");
        patch(method(type(framework, "osu.Framework.Platform.DesktopGameHost"), "GetDefaultGameStorage"), "StoragePrefix");
        patch(method(type(desktop, "osu.Desktop.OsuGameDesktop"), "GetStorageForStableInstall"), "NoStablePrefix");
        patch(method(type(game, "osu.Game.Rulesets.RulesetStore"), "addRuleset", typeof(Assembly)), "RulesetAssemblyPrefix");
        patch(method(type(framework, "osu.Framework.Platform.DesktopGameHost"), "OpenUrlExternally", typeof(string)), "ExternalUrlPrefix");
        var storage = type(game, "osu.Game.IO.OsuStorage");
        patch(storage.GetProperty("CustomStoragePath")!.GetMethod!, "NoCustomPathPrefix");
        patch(method(storage, "TryChangeToCustomStorage", typeof(osu.Game.IO.OsuStorageError).MakeByRefType()), "NoCustomStoragePrefix");
        patch(method(storage, "ChangeDataPath", typeof(string)), "FixedDataPathPrefix");
        patch(method(type(game, "osu.Game.OsuGameBase"), "MigrateUserData", typeof(string)), "NoMigrationPrefix");
        // Install the stream interception before Harmony compiles native config
        // methods that could otherwise inline the small safe-write factory.
        patch(AccountConfigIsolation.ReadStream, "ReadStreamPrefix", typeof(AccountConfigIsolation));
        patch(AccountConfigIsolation.SafeWriteStream, "SafeWritePrefix", typeof(AccountConfigIsolation));
        patch(AccountConfigIsolation.ConfigConstructor, "ConfigConstructorPrefix", typeof(AccountConfigIsolation));
        patch(AccountConfigIsolation.ConfigConstructor, "ConfigConstructorPostfix", typeof(AccountConfigIsolation), postfix: true);
        patch(AccountConfigIsolation.PerformLoad, "LoadPrefix", typeof(AccountConfigIsolation));
        patchFinalizer(AccountConfigIsolation.PerformLoad, "ScopeFinalizer", typeof(AccountConfigIsolation));
        patch(AccountConfigIsolation.PerformSave, "SavePrefix", typeof(AccountConfigIsolation));
        patch(AccountConfigIsolation.PerformSave, "SavePostfix", typeof(AccountConfigIsolation), postfix: true);
        patchFinalizer(AccountConfigIsolation.PerformSave, "ScopeFinalizer", typeof(AccountConfigIsolation));
        patch(AccountConfigIsolation.ApiConstructor, "ApiConstructorPrefix", typeof(AccountConfigIsolation));
        SessionWelcome.Install(harmony, AccountConfigIsolation.ApiConstructor);
        ClientStartupProgress.Install(patch);
        SessionLogout.Install(harmony, patched.Add);
    }

    private static bool EndpointsPrefix(ref object __result)
    {
        var endpoint = Activator.CreateInstance(type(game, "osu.Game.Online.EndpointConfiguration"))!;
        set(endpoint, "APIUrl", NetworkPolicy.Api);
        set(endpoint, "WebsiteUrl", NetworkPolicy.Website);
        set(endpoint, "APIClientID", NetworkPolicy.ClientId);
        set(endpoint, "APIClientSecret", NetworkPolicy.ClientSecret);
        set(endpoint, "SpectatorUrl", NetworkPolicy.Hubs + "/spectator");
        set(endpoint, "MultiplayerUrl", NetworkPolicy.Hubs + "/multiplayer");
        set(endpoint, "MetadataUrl", NetworkPolicy.Hubs + "/metadata");
        set(endpoint, "BeatmapSubmissionServiceUrl", null);
        set(endpoint, "LivenessProbeUrl", null);
        __result = endpoint;
        return false;
    }

    private static void set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);

    private static void HostPrefix(ref string gameName, ref object hostOptions)
    {
        hostOptions ??= Activator.CreateInstance(type(framework, "osu.Framework.HostOptions"))!;
        set(hostOptions, "IPCPipeName", "illyaverse-lazer");
        set(hostOptions, "FriendlyGameName", "Illyaverse • osu!lazer");
    }

    private static bool StoragePrefix(object __instance, ref object __result)
    {
        __result = method(__instance.GetType(), "GetStorage", typeof(string)).Invoke(__instance, [dataRoot])!;
        return false;
    }

    private static bool NoStablePrefix(ref object? __result) { __result = null; return false; }
    private static bool NoCustomPathPrefix(ref string? __result) { __result = null; return false; }
    private static bool NoCustomStoragePrefix(ref osu.Game.IO.OsuStorageError error, ref bool __result)
    {
        error = osu.Game.IO.OsuStorageError.NotAccessible;
        __result = false;
        return false;
    }
    // Skip the entire write. Clearing newPath would still save shared storage.ini.
    private static bool FixedDataPathPrefix() => false;
    private static bool NoMigrationPrefix(ref bool __result) { __result = false; return false; }
    private static bool RulesetAssemblyPrefix(Assembly assembly) => assembly != typeof(StartupHook).Assembly;
    private static bool ExternalUrlPrefix(ref string url)
    {
        if (!ExternalBrowserPolicy.TryResolve(url, out var resolved)) return false;
        url = resolved;
        return true;
    }
    private static void fail(string reason) => throw SafeStartupDiagnostic.Rejection(reason);
}
