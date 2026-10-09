using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.IO;
using osu.Game.Online;
using osu.Game.Online.API;

internal static class AccountIsolationProof
{
    private const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    private static int pausedApiThreads;
    private static readonly AutoResetEvent apiWorkerStopped = new(false);

    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL account isolation " + name);
        checks++;
        Console.WriteLine("PASS account isolation " + name);
    }

    private static void rejected(Action action, string reason, string name)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message == "transfiguration_" + reason) { require(true, name); return; }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException cause && cause.Message == "transfiguration_" + reason) { require(true, name); return; }
        throw new InvalidOperationException("FAIL account isolation expected rejection " + name);
    }

    // Test-only interception: the constructor's worker exits at its entry point,
    // before auth or network work. No production bridge uses this patch.
    private static bool pauseApiWorker()
    {
        Interlocked.Increment(ref pausedApiThreads);
        apiWorkerStopped.Set();
        return false;
    }

    internal static int Run(Assembly bridge, string root, object fixedEndpoints)
    {
        if (!Path.GetFileName(root).StartsWith("data-transfiguration-proof", StringComparison.Ordinal))
            throw new InvalidOperationException("Proof-owned storage is required");
        var officialPath = Path.Combine(root, "game.ini");
        var privatePath = Path.Combine(root, "auth.illyaverse.json");
        var realmPath = Path.Combine(root, "client.realm");
        var resourcePath = Path.Combine(root, "files", "proof-library-resource");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        File.WriteAllText(realmPath, "synthetic-realm-marker-not-a-real-database", Encoding.UTF8);
        File.WriteAllText(resourcePath, "synthetic-existing-beatmap-resource", Encoding.UTF8);
        var realmHash = SHA256.HashData(File.ReadAllBytes(realmPath));
        var resourceHash = SHA256.HashData(File.ReadAllBytes(resourcePath));
        var realmCreated = File.GetCreationTimeUtc(realmPath);
        var resourceCreated = File.GetCreationTimeUtc(resourcePath);
        var storage = new WrappedStorage(new WrappedStorage(new NativeStorage(root)));
        var isolation = bridge.GetType("Illyaverse.Transfiguration.AccountConfigIsolation", true)!;
        var filename = typeof(IniConfigManager<OsuSetting>).GetProperty("Filename", instance)!.GetMethod!;
        var storageField = typeof(IniConfigManager<OsuSetting>).GetField("storage", instance)!;
        var precondition = isolation.GetMethod("ApiConstructorPrefix", BindingFlags.Static | BindingFlags.NonPublic)!;

        var harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "0Harmony");
        var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true)!;
        var harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", true)!;
        var testHarmony = Activator.CreateInstance(harmonyType, "illyaverse.proof.account-constructor")!;
        var testPrefix = Activator.CreateInstance(harmonyMethodType, typeof(AccountIsolationProof).GetMethod(nameof(pauseApiWorker), BindingFlags.Static | BindingFlags.NonPublic)!)!;
        var apiRun = typeof(APIAccess).GetMethod("run", instance)!;
        harmonyType.GetMethod("Patch")!.Invoke(testHarmony, [apiRun, testPrefix, null, null, null]);
        try
        {
            var game = (OsuGameBase)RuntimeHelpers.GetUninitializedObject(typeof(OsuGameBase));
            // Share the existing proof's mods bindable; the bridge refuses multiple game contexts.
            var relax = bridge.GetType("Illyaverse.Transfiguration.RelaxContext", true)!;
            var sharedMods = relax.GetField("mods", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)
                ?? new osu.Framework.Bindables.Bindable<IReadOnlyList<osu.Game.Rulesets.Mods.Mod>>(Array.Empty<osu.Game.Rulesets.Mods.Mod>());
            typeof(OsuGameBase).GetField("SelectedMods", instance)!.SetValue(game, sharedMods);
            foreach (var expired in new[] { false, true })
            {
                if (File.Exists(privatePath)) File.Delete(privatePath);
                File.WriteAllText(privatePath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    protocol = 1,
                    profiles = new Dictionary<string, object>
                    {
                        ["https://fixture-other.illya.test"] = new
                        {
                            username = "proof-other-deployment", token = "other-access|9999999999|other-refresh", saveUsername = true, savePassword = true,
                            wasSupporter = true, userOnlineStatus = "DoNotDisturb", pmFriendsOnly = true
                        }
                    }
                }), new UTF8Encoding(false));
                var officialToken = new OAuthToken
                {
                    AccessToken = "proof-official-access-never-sent",
                    AccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expired ? -3600 : 3600).ToUnixTimeSeconds(),
                    RefreshToken = "proof-official-refresh-never-sent"
                };
                var officialAccount = $"Username = proof-official-user\r\n1 = {officialToken}\r\nSavePassword = True\r\nSaveUsername = True\r\nWasSupporter = True\r\nUserOnlineStatus = DoNotDisturb\r\nPMFriendsOnly = True\r\n";
                File.WriteAllText(officialPath, officialAccount + "AudioOffset = 123\nMouseDisableButtons = True\nShowStoryboard = False\nDimLevel = 0.33\nUIScale = 1.25\n", new UTF8Encoding(false));
                using var config = new OsuConfigManager(storage);
                require(config.Get<string>(OsuSetting.Username) == string.Empty && config.Get<string>(OsuSetting.Token) == string.Empty,
                    (expired ? "expired" : "valid") + " official username/access/refresh are absent before API construction");
                require(config.Get<string>(OsuSetting.Username) != "proof-other-deployment", "another private origin's account never enters the selected native config");
                require(config.Get<double>(OsuSetting.AudioOffset) == 123, "original game.ini audio preference loads through the stock parser");
                require(config.Get<bool>(OsuSetting.MouseDisableButtons) && !config.Get<bool>(OsuSetting.ShowStoryboard)
                    && Math.Abs(config.Get<double>(OsuSetting.DimLevel) - 0.33) < 0.000001 && Math.Abs(config.Get<float>(OsuSetting.UIScale) - 1.25f) < 0.000001,
                    "original input, storyboard, dim and UI scale preferences load together");
                require(!config.Get<bool>(OsuSetting.WasSupporter) && !config.Get<bool>(OsuSetting.PMFriendsOnly)
                    && status(config) == "Online", "official account caches are absent before API construction");
                require(filename.Invoke(config, null) as string == "game.ini", "actual native filename getter stays on the original shared preferences");
                require(ReferenceEquals(storageField.GetValue(config), storage)
                    && storage.GetFullPath("client.realm") == realmPath && storage.GetFullPath("files/proof-library-resource") == resourcePath,
                    "native config retains stock nested WrappedStorage forwarding to the existing effective library and resource paths");
                precondition.Invoke(null, [config, fixedEndpoints]);

                var unsafeConfig = (OsuConfigManager)RuntimeHelpers.GetUninitializedObject(typeof(OsuConfigManager));
                storageField.SetValue(unsafeConfig, storage);
                rejected(() => precondition.Invoke(null, [unsafeConfig, fixedEndpoints]), "config_not_isolated",
                    "untracked config is rejected before API token reads or worker startup");
                var workersBeforeRejection = pausedApiThreads;
                rejected(() => _ = new APIAccess(game, unsafeConfig, (EndpointConfiguration)fixedEndpoints, "proof-version"),
                    "config_not_isolated", "actual patched API constructor rejects an untracked shared-storage config");
                require(pausedApiThreads == workersBeforeRejection, "unsafe API constructor never reaches worker startup");
                var officialEndpoints = new EndpointConfiguration();
                rejected(() => precondition.Invoke(null, [config, officialEndpoints]), "api_config_not_private",
                    "official endpoint configuration is rejected before API construction");

                using var api = new APIAccess(game, config, (EndpointConfiguration)fixedEndpoints, "proof-version");
                require(apiWorkerStopped.WaitOne(TimeSpan.FromSeconds(2)), "test-only worker entry interception runs before persistence exercise");
                var authentication = (OAuth)typeof(APIAccess).GetField("authentication", instance)!.GetValue(api)!;
                require(api.ProvidedUsername == string.Empty && authentication.Token.Value is null && pausedApiThreads > 0,
                    "real API constructor receives no official credentials before test-only worker interception");
                var privateToken = new OAuthToken
                {
                    AccessToken = "proof-private-access",
                    AccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(3600).ToUnixTimeSeconds(),
                    RefreshToken = "proof-private-refresh"
                };
                config.SetValue(OsuSetting.SavePassword, true);
                config.SetValue(OsuSetting.SaveUsername, true);
                config.SetValue(OsuSetting.Username, "proof-private-user");
                config.SetValue(OsuSetting.AudioOffset, 45.0);
                config.SetValue(OsuSetting.MouseDisableButtons, false);
                config.SetValue(OsuSetting.ShowStoryboard, true);
                config.SetValue(OsuSetting.DimLevel, 0.51);
                config.SetValue(OsuSetting.UIScale, 1.15f);
                config.SetValue(OsuSetting.WasSupporter, true);
                config.SetValue(OsuSetting.PMFriendsOnly, true);
                setStatus(config, "DoNotDisturb");
                authentication.Token.Value = privateToken;
                require(config.Get<string>(OsuSetting.Token) == privateToken.ToString() && config.Save(),
                    "native remember-token handler and serializer save private credentials and shared preferences");
                using (var reloaded = new OsuConfigManager(storage))
                {
                    require(reloaded.Get<string>(OsuSetting.Username) == "proof-private-user"
                        && reloaded.Get<string>(OsuSetting.Token) == privateToken.ToString()
                        && reloaded.Get<bool>(OsuSetting.SavePassword) && reloaded.Get<bool>(OsuSetting.SaveUsername),
                        "private credentials and remember flags reload from only the private file");
                    require(reloaded.Get<double>(OsuSetting.AudioOffset) == 45 && reloaded.Get<bool>(OsuSetting.WasSupporter)
                        && reloaded.Get<bool>(OsuSetting.PMFriendsOnly) && status(reloaded) == "DoNotDisturb",
                        "shared edited preference and private account caches reload independently");
                    require(!reloaded.Get<bool>(OsuSetting.MouseDisableButtons) && reloaded.Get<bool>(OsuSetting.ShowStoryboard)
                        && Math.Abs(reloaded.Get<double>(OsuSetting.DimLevel) - 0.51) < 0.000001 && Math.Abs(reloaded.Get<float>(OsuSetting.UIScale) - 1.15f) < 0.000001,
                        "edited input, storyboard, dim and UI scale save and reload from shared original file");
                }
                require(File.ReadAllText(officialPath).EndsWith(officialAccount, StringComparison.Ordinal)
                    && File.ReadAllText(officialPath).Contains("AudioOffset = 45", StringComparison.Ordinal)
                    && !File.ReadAllText(officialPath).Contains("proof-private", StringComparison.Ordinal),
                    "original game.ini saves edited preferences and preserves every official account line byte for byte");
                require(File.ReadAllText(privatePath).Contains("proof-private-user", StringComparison.Ordinal)
                    && File.ReadAllText(privatePath).Contains("proof-other-deployment", StringComparison.Ordinal)
                    && !File.ReadAllText(privatePath).Contains("proof-official", StringComparison.Ordinal)
                    && !File.ReadAllText(privatePath).Contains("AudioOffset", StringComparison.Ordinal),
                    "small private account store never contains official credentials or gameplay preferences");

                config.SetValue(OsuSetting.SavePassword, false);
                authentication.Token.Value = new OAuthToken { AccessToken = "proof-private-second", AccessTokenExpiry = privateToken.AccessTokenExpiry, RefreshToken = "proof-private-second-refresh" };
                require(config.Get<string>(OsuSetting.Token) == string.Empty, "native remember-token-off handler clears persisted private token");
                config.SetValue(OsuSetting.SavePassword, true);
                authentication.Token.Value = privateToken;
                api.Logout();
                require(authentication.Token.Value is null && string.IsNullOrEmpty(config.Get<string>(OsuSetting.Token)) && config.Save(),
                    "native logout clears and saves only the private token");
                using (var storedLogout = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(privatePath)))
                    require(storedLogout.RootElement.GetProperty("profiles").GetProperty(((EndpointConfiguration)fixedEndpoints).WebsiteUrl)
                            .GetProperty("token").GetString() == string.Empty,
                        "native null-or-empty logout bindable persists as strict empty private token string");
                config.SetValue(OsuSetting.SaveUsername, false);
                config.SetValue(OsuSetting.Username, string.Empty);
                require(config.Save(), "private username and remember flags save independently");
                using (var loggedOut = new OsuConfigManager(storage))
                    require(loggedOut.Get<string>(OsuSetting.Token) == string.Empty
                        && loggedOut.Get<string>(OsuSetting.Username) == string.Empty
                        && !loggedOut.Get<bool>(OsuSetting.SaveUsername), "private logged-out state survives a real config reload");
                require(File.ReadAllText(officialPath).EndsWith(officialAccount, StringComparison.Ordinal)
                    && !File.ReadAllText(officialPath).Contains("proof-private", StringComparison.Ordinal),
                    "official account lines stay byte-identical through private remember changes and logout");
            }

            var unrelated = RuntimeHelpers.GetUninitializedObject(typeof(IniConfigManager<OsuSetting>));
            require(filename.Invoke(unrelated, null) as string == "game.ini", "same closed generic non-Osu manager retains stock filename");
            File.WriteAllText(Path.Combine(root, "unrelated-proof.ini"), "Marker = original-generic-value\n", new UTF8Encoding(false));
            using (var generic = new UnrelatedConfig(storage))
            {
                require(generic.Get<string>(FixtureSetting.Marker) == "original-generic-value", "unrelated generic config uses original native load stream");
                generic.SetValue(FixtureSetting.Marker, "generic-edited-value");
                require(generic.Save() && File.ReadAllText(Path.Combine(root, "unrelated-proof.ini")).Contains("generic-edited-value", StringComparison.Ordinal),
                    "unrelated generic config uses original native serializer and safe-write stream");
            }
            require(SHA256.HashData(File.ReadAllBytes(realmPath)).SequenceEqual(realmHash)
                && SHA256.HashData(File.ReadAllBytes(resourcePath)).SequenceEqual(resourceHash)
                && File.GetCreationTimeUtc(realmPath) == realmCreated && File.GetCreationTimeUtc(resourcePath) == resourceCreated,
                "existing Realm/resource marker files are not copied, replaced or modified");
            require(pausedApiThreads == 2, "only the two fixture APIAccess workers were intercepted");
            rejected(() => _ = new OsuConfigManager(new NativeStorage(root + "-foreign")), "config_storage_unverified",
                "actual config constructor rejects another storage before loading files");
            File.Delete(privatePath);
            Directory.CreateDirectory(privatePath);
            try
            {
                rejected(() => _ = new OsuConfigManager(storage), "config_file_unverified",
                    "directory at private filename is rejected before native config load");
            }
            finally { Directory.Delete(privatePath); }

            foreach (var malformed in new[]
            {
                "", "{}", "{\"protocol\":1,\"protocol\":1,\"profiles\":{}}",
                "{\"protocol\":1,\"profiles\":{},\"extra\":true}", "not json"
            })
            {
                File.WriteAllText(privatePath, malformed, new UTF8Encoding(false));
                rejected(() => _ = new OsuConfigManager(storage), "config_auth_invalid", "malformed private account store fails before any API construction");
            }
            File.Delete(privatePath);
            File.WriteAllText(officialPath, "Token = opaque-official\n1 = conflicting-official\nAudioOffset = 123\n", new UTF8Encoding(false));
            rejected(() => _ = new OsuConfigManager(storage), "config_auth_ambiguous", "duplicate symbolic and numeric auth aliases fail closed");
            File.WriteAllText(officialPath, "AudioOffset = 99\n", new UTF8Encoding(false));
            using (var recovered = new OsuConfigManager(storage))
                require(recovered.Get<double>(OsuSetting.AudioOffset) == 99 && recovered.Get<string>(OsuSetting.Token) == string.Empty,
                    "failed load restores scoped hooks before a subsequent valid config load");
        }
        finally
        {
            harmonyType.GetMethod("Unpatch", [typeof(MethodBase), typeof(MethodInfo)])!
                .Invoke(testHarmony, [apiRun, typeof(AccountIsolationProof).GetMethod(nameof(pauseApiWorker), BindingFlags.Static | BindingFlags.NonPublic)!]);
        }
        Console.WriteLine($"PASS {checks} real native shared-preference/auth-only config checks; API workers stopped at entry by test-only hook; no GameHost/Realm/GUI/real credentials/network");
        return checks;
    }

    private static osu.Framework.Bindables.IBindable statusBindable(OsuConfigManager config)
    {
        var field = typeof(ConfigManager<OsuSetting>).GetField("ConfigStore", instance)!;
        return ((Dictionary<OsuSetting, osu.Framework.Bindables.IBindable>)field.GetValue(config)!)[OsuSetting.UserOnlineStatus];
    }

    private static string status(OsuConfigManager config) => statusBindable(config).ToString(null, System.Globalization.CultureInfo.InvariantCulture);
    private static void setStatus(OsuConfigManager config, string value) => ((osu.Framework.Bindables.IParseable)statusBindable(config)).Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private enum FixtureSetting { Marker }
    private sealed class UnrelatedConfig(Storage storage) : IniConfigManager<FixtureSetting>(storage)
    {
        protected override string Filename => "unrelated-proof.ini";
        protected override void InitialiseDefaults() => SetDefault(FixtureSetting.Marker, string.Empty);
    }
}
