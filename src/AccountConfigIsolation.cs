using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Online;
using osu.Game.Online.API;

namespace Illyaverse.Transfiguration;

internal static class AccountConfigIsolation
{
    internal const string Filename = AuthConfigFiles.Filename;
    private const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConditionalWeakTable<OsuConfigManager, ConfigState> configs = new();
    private static readonly object fileGate = new();
    [ThreadStatic] private static ConfigScope? current;
    private static string libraryRoot = string.Empty;
    private static FieldInfo storageField = null!;
    private static FieldInfo valuesField = null!;
    private static MethodInfo filenameGetter = null!;
    internal static ConstructorInfo ConfigConstructor { get; private set; } = null!;
    internal static ConstructorInfo ApiConstructor { get; private set; } = null!;
    internal static MethodInfo PerformLoad { get; private set; } = null!;
    internal static MethodInfo PerformSave { get; private set; } = null!;
    internal static MethodInfo ReadStream { get; private set; } = null!;
    internal static MethodInfo SafeWriteStream { get; private set; } = null!;
    private sealed class ConfigState { internal bool Loaded, ReadIntercepted; }
    private sealed class ConfigScope(OsuConfigManager config, Storage storage, bool saving) : IDisposable
    {
        internal readonly OsuConfigManager Config = config;
        internal readonly Storage Storage = storage;
        internal readonly bool Saving = saving;
        internal readonly ConfigScope? Previous = current;
        internal MemoryStream? Serialized;
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            current = Previous;
            Monitor.Exit(fileGate);
        }
    }

    internal static void VerifyContracts(string effectiveLibraryRoot)
    {
        libraryRoot = effectiveLibraryRoot;
        var closedIni = typeof(IniConfigManager<OsuSetting>);
        if (typeof(OsuConfigManager).BaseType != closedIni) fail("config_base_incompatible");
        filenameGetter = closedIni.GetProperty("Filename", instance)?.GetMethod!;
        storageField = closedIni.GetField("storage", instance)!;
        valuesField = typeof(ConfigManager<OsuSetting>).GetField("ConfigStore", instance)!;
        PerformLoad = closedIni.GetMethod("PerformLoad", instance)!;
        PerformSave = closedIni.GetMethod("PerformSave", instance)!;
        ReadStream = typeof(NativeStorage).GetMethod("GetStream", [typeof(string), typeof(FileAccess), typeof(FileMode)])!;
        SafeWriteStream = typeof(Storage).GetMethod("CreateFileSafely", [typeof(string)])!;
        ConfigConstructor = typeof(OsuConfigManager).GetConstructor([typeof(Storage)])!;
        ApiConstructor = typeof(APIAccess).GetConstructor([typeof(OsuGameBase), typeof(OsuConfigManager), typeof(EndpointConfiguration), typeof(string)])!;
        if (filenameGetter?.ReturnType != typeof(string) || !filenameGetter.IsVirtual || filenameGetter.DeclaringType != closedIni
            || storageField?.FieldType != typeof(Storage) || !storageField.IsInitOnly
            || valuesField?.FieldType != typeof(Dictionary<OsuSetting, IBindable>) || !valuesField.IsInitOnly
            || PerformLoad?.ReturnType != typeof(void) || PerformLoad.DeclaringType != closedIni || !PerformLoad.IsVirtual || PerformLoad.GetParameters().Length != 0
            || PerformSave?.ReturnType != typeof(bool) || PerformSave.DeclaringType != closedIni || !PerformSave.IsVirtual || PerformSave.GetParameters().Length != 0
            || ReadStream?.ReturnType != typeof(Stream) || ReadStream.DeclaringType != typeof(NativeStorage) || !ReadStream.IsVirtual
            || SafeWriteStream?.ReturnType != typeof(Stream) || SafeWriteStream.DeclaringType != typeof(Storage) || SafeWriteStream.IsVirtual
            || ConfigConstructor is null || ApiConstructor is null) fail("config_contract_incompatible");
        foreach (var pair in new[] { ("Token", 1), ("Username", 34), ("SavePassword", 36), ("SaveUsername", 37),
                     ("UserOnlineStatus", 96), ("WasSupporter", 115), ("PMFriendsOnly", 120) })
            if (!Enum.TryParse<OsuSetting>(pair.Item1, out var setting) || Convert.ToInt32(setting) != pair.Item2) fail("config_contract_incompatible");
    }

    private static void ConfigConstructorPrefix(OsuConfigManager __instance, Storage storage)
    {
        requireExactManager(__instance);
        requireStorage(storage);
        configs.Add(__instance, new ConfigState());
    }

    private static void ConfigConstructorPostfix(OsuConfigManager __instance)
    {
        requireStorage(storageField.GetValue(__instance) as Storage);
        if (!configs.TryGetValue(__instance, out var state) || !state.ReadIntercepted
            || filenameGetter.Invoke(__instance, null) as string != "game.ini") fail("config_not_isolated");
        state.Loaded = true;
    }

    private static void LoadPrefix(object __instance, ref ConfigScope? __state) => beginScope(__instance, false, ref __state);
    private static void SavePrefix(object __instance, ref ConfigScope? __state) => beginScope(__instance, true, ref __state);

    private static void beginScope(object configObject, bool saving, ref ConfigScope? state)
    {
        // Closed generic methods may share machine code with unrelated managers.
        if (configObject is not OsuConfigManager config) return;
        requireExactManager(config);
        var storage = storageField.GetValue(config) as Storage;
        requireStorage(storage);
        if (!configs.TryGetValue(config, out var tracked) || (saving && !tracked.Loaded)) fail("config_not_isolated");
        Monitor.Enter(fileGate);
        state = new ConfigScope(config, storage!, saving);
        current = state;
    }

    private static void ScopeFinalizer(ConfigScope? __state) => __state?.Dispose();

    private static bool ReadStreamPrefix(NativeStorage __instance, string __0, FileAccess __1, FileMode __2, ref Stream __result)
    {
        var scope = current;
        if (scope is null || scope.Saving) return true;
        if (!string.Equals(ClientAssemblyPath.Canonical(__instance.GetFullPath(__0)),
                ClientAssemblyPath.Canonical(Path.Combine(libraryRoot, "game.ini")), ClientAssemblyPath.Comparison)) return true;
        if (__1 != FileAccess.Read || __2 != FileMode.OpenOrCreate) fail("config_load_unverified");
        var account = AuthConfigFiles.ReadAccount(libraryRoot, NetworkPolicy.Website);
        var shared = AuthConfigFiles.Partition(AuthConfigFiles.Read(libraryRoot, "game.ini"), accountKey);
        __result = new MemoryStream(AuthConfigFiles.ForLoad(shared, account), writable: false);
        configs.GetValue(scope.Config, _ => throw new InvalidOperationException()).ReadIntercepted = true;
        return false;
    }

    private static bool SafeWritePrefix(Storage __instance, string __0, ref Stream __result)
    {
        var scope = current;
        if (scope is null || !scope.Saving) return true;
        if (!ReferenceEquals(__instance, scope.Storage) || __0 != "game.ini" || scope.Serialized is not null) fail("config_save_unverified");
        scope.Serialized = new MemoryStream();
        __result = scope.Serialized;
        return false;
    }

    private static void SavePostfix(ConfigScope? __state, ref bool __result)
    {
        if (__state is null || !__result) return;
        try
        {
            if (__state.Serialized is null) fail("config_save_unverified");
            var oldAuth = AuthConfigFiles.Read(libraryRoot, Filename, 64 * 1024);
            _ = AuthConfigFiles.ReadAccount(libraryRoot, NetworkPolicy.Website); // malformed stores are never silently replaced.
            var shared = AuthConfigFiles.Partition(AuthConfigFiles.Read(libraryRoot, "game.ini"), accountKey);
            var config = __state.Config;
            var rememberUsername = config.Get<bool>(OsuSetting.SaveUsername);
            var rememberToken = config.Get<bool>(OsuSetting.SavePassword);
            // Native logout handlers can assign null to string bindables; the
            // stock serializer represents those values as an empty INI value.
            var account = new AuthConfigFiles.Account(rememberUsername ? config.Get<string>(OsuSetting.Username) ?? string.Empty : string.Empty,
                rememberToken ? config.Get<string>(OsuSetting.Token) ?? string.Empty : string.Empty, rememberUsername, rememberToken,
                config.Get<bool>(OsuSetting.WasSupporter), ((Dictionary<OsuSetting, IBindable>)valuesField.GetValue(config)!)[OsuSetting.UserOnlineStatus].ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                config.Get<bool>(OsuSetting.PMFriendsOnly));
            var merged = AuthConfigFiles.ForSave(__state.Serialized!.ToArray(), shared, accountKey);
            AuthConfigFiles.Write(libraryRoot, Filename, AuthConfigFiles.Encode(libraryRoot, NetworkPolicy.Website, account), oldAuth);
            AuthConfigFiles.Write(libraryRoot, "game.ini", merged, shared.Bytes);
        }
        catch { __result = false; }
    }

    private static void ApiConstructorPrefix(OsuConfigManager config, EndpointConfiguration endpoints)
    {
        // Before APIAccess reads credentials or starts its background worker.
        requireExactManager(config);
        requireStorage(storageField.GetValue(config) as Storage);
        if (!configs.TryGetValue(config, out var state) || !state.Loaded || !state.ReadIntercepted
            || filenameGetter.Invoke(config, null) as string != "game.ini") fail("config_not_isolated");
        if (endpoints.APIUrl != NetworkPolicy.Api || endpoints.WebsiteUrl != NetworkPolicy.Website
            || endpoints.APIClientID != NetworkPolicy.ClientId || endpoints.APIClientSecret != NetworkPolicy.ClientSecret)
            fail("api_config_not_private");
    }

    private static string? accountKey(string key) => Enum.TryParse<OsuSetting>(key, out var setting)
        && AuthConfigFiles.Keys.Contains(setting.ToString(), StringComparer.Ordinal) ? setting.ToString() : null;

    private static void requireExactManager(OsuConfigManager config)
    {
        if (config is null || config.GetType() != typeof(OsuConfigManager)) fail("config_manager_unverified");
    }

    private static void requireStorage(Storage? storage)
    {
        if (storage is null || !string.Equals(ClientAssemblyPath.Canonical(storage.GetFullPath(".")),
                libraryRoot, ClientAssemblyPath.Comparison)) fail("config_storage_unverified");
    }

    [DoesNotReturn]
    private static void fail(string reason) => throw new InvalidOperationException("transfiguration_" + reason);
}
