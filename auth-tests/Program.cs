using System.Text;
using System.Text.Json;
using Illyaverse.Transfiguration;

internal static class Program
{
    private static int checks;
    private static readonly UTF8Encoding utf8 = new(false, true);
    private const string endpointA = "https://illya.test";
    private const string endpointB = "https://another.illya.test:8443";
    private static readonly Dictionary<string, string> numeric = new(StringComparer.Ordinal)
    {
        ["1"] = "Token", ["34"] = "Username", ["36"] = "SavePassword", ["37"] = "SaveUsername",
        ["96"] = "UserOnlineStatus", ["115"] = "WasSupporter", ["120"] = "PMFriendsOnly"
    };
    private static string? key(string value) => AuthConfigFiles.Keys.Contains(value, StringComparer.Ordinal)
        ? value : numeric.GetValueOrDefault(value);
    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL auth-only " + name);
        checks++;
        Console.WriteLine("PASS auth-only " + name);
    }
    private static void rejected(Action action, string reason, string name)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message == "transfiguration_" + reason) { require(true, name); return; }
        throw new InvalidOperationException("FAIL auth-only expected rejection " + name);
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new InvalidOperationException("Owned proof root required");
            var root = Path.GetFullPath(args[0]);
            if (!Path.GetFileName(root).StartsWith("data-transfiguration-proof-auth-", StringComparison.Ordinal) || Directory.Exists(root))
                throw new InvalidOperationException("Fresh owned auth fixture directory required");
            Directory.CreateDirectory(root);
            purePartitionProof();
            accountProfilesProof(root);
            fileBoundaryProof(root);
            portableProof();
            if (OperatingSystem.IsLinux()) linuxFileProof(root);
            Console.WriteLine($"PASS {checks} original source-linked auth-only data/atomic-file checks; synthetic files only, no client assemblies/config/Realm/player data/network");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void purePartitionProof()
    {
        var originalAuth = "34  =  official-user\r\n1 = official-access|9999999999|official-refresh\r\nSaveUsername = False\r\nSavePassword = False\r\n115 = True\r\n96 = DoNotDisturb\r\n120 = True";
        var original = utf8.GetPreamble().Concat(utf8.GetBytes("# retained native comment\r\nAudioOffset = 123\r\n" + originalAuth)).ToArray();
        // A UTF-8 BOM is deliberately present, independently of StreamWriter defaults.
        original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(original).ToArray();
        var partition = AuthConfigFiles.Partition(original, key);
        require(partition.Bom && partition.AccountLines.Count == 7, "seven symbolic/numeric account keys are partitioned with original BOM");
        var load = utf8.GetString(AuthConfigFiles.ForLoad(partition, AuthConfigFiles.Account.Empty));
        require(!load.Contains("official", StringComparison.Ordinal) && load.Contains("AudioOffset = 123", StringComparison.Ordinal),
            "sanitized load retains shared preferences without any official account values");
        require(load.Contains("Username = \n", StringComparison.Ordinal) && load.Contains("WasSupporter = False", StringComparison.Ordinal),
            "new private profile supplies logged-out credentials and independent account cache defaults");
        var privateSerialized = utf8.GetBytes("AudioOffset = 45\r\nUsername = private-user\r\nToken = private-access|123|private-refresh\r\nSaveUsername = True\r\nSavePassword = True\r\nWasSupporter = False\r\nUserOnlineStatus = Online\r\nPMFriendsOnly = False\r\n");
        var saved = AuthConfigFiles.ForSave(privateSerialized, partition, key);
        require(saved.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
            && saved.AsSpan(saved.Length - utf8.GetByteCount(originalAuth)).SequenceEqual(utf8.GetBytes(originalAuth)),
            "shared save preserves official account byte slices, line endings, no-newline EOF and BOM");
        require(utf8.GetString(saved).Contains("AudioOffset = 45", StringComparison.Ordinal)
            && !utf8.GetString(saved).Contains("private", StringComparison.Ordinal), "private credentials never enter shared preference output");
        require(AuthConfigFiles.Partition(utf8.GetBytes("#Token = commented\nusername = native-ignored\nAudioOffset = 11\n"), key).AccountLines.Count == 0,
            "commented and native case-sensitive ignored keys keep their original semantics");
        rejected(() => AuthConfigFiles.Partition(utf8.GetBytes("Token = first\n1 = second\n"), key), "config_auth_ambiguous", "symbolic/numeric duplicate account aliases reject");
        rejected(() => AuthConfigFiles.Partition(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Token = official")).ToArray(), key),
            "config_encoding_unverified", "UTF-16 file cannot bypass account filtering through native encoding detection");
        rejected(() => AuthConfigFiles.Partition([0xff], key), "config_encoding_unverified", "invalid UTF-8 shared file rejects");
        rejected(() => AuthConfigFiles.Partition(new byte[AuthConfigFiles.MaximumSharedBytes + 1], key), "config_file_too_large", "shared preference file is bounded");
    }

    private static void accountProfilesProof(string root)
    {
        var path = Path.Combine(root, AuthConfigFiles.Filename);
        require(AuthConfigFiles.ReadAccount(root, endpointA) == AuthConfigFiles.Account.Empty, "missing private store starts logged out");
        var accountA = new AuthConfigFiles.Account("private-a", "access-a|9999999999|refresh-a", true, true, true, "DoNotDisturb", true);
        var bytesA = AuthConfigFiles.Encode(root, endpointA, accountA);
        AuthConfigFiles.Write(root, AuthConfigFiles.Filename, bytesA, []);
        require(AuthConfigFiles.ReadAccount(root, endpointA) == accountA, "selected exact-origin credentials and three account caches reload");
        require(AuthConfigFiles.ReadAccount(root, endpointB) == AuthConfigFiles.Account.Empty, "changing canonical origin/port never inherits another private account");
        var accountB = new AuthConfigFiles.Account("private-b", "access-b|9999999999|refresh-b", true, true, false, "Online", false);
        var bytesB = AuthConfigFiles.Encode(root, endpointB, accountB);
        AuthConfigFiles.Write(root, AuthConfigFiles.Filename, bytesB, bytesA);
        require(AuthConfigFiles.ReadAccount(root, endpointA) == accountA && AuthConfigFiles.ReadAccount(root, endpointB) == accountB,
            "saving another selected origin preserves both private profiles independently");
        var clearedA = accountA with { Username = string.Empty, Token = string.Empty, SaveUsername = false, SavePassword = false, WasSupporter = false, UserOnlineStatus = "Online" };
        var clearedBytes = AuthConfigFiles.Encode(root, endpointA, clearedA);
        AuthConfigFiles.Write(root, AuthConfigFiles.Filename, clearedBytes, bytesB);
        require(AuthConfigFiles.ReadAccount(root, endpointA) == clearedA && AuthConfigFiles.ReadAccount(root, endpointB) == accountB,
            "logout and remember flags affect only selected origin");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        require(json.RootElement.EnumerateObject().Count() == 2
            && json.RootElement.GetProperty("profiles").EnumerateObject().All(p => p.Value.EnumerateObject().Count() == 7),
            "small store schema has bounded metadata and exactly seven account fields per profile");

        foreach (var token in new[] { "one", "access|not-integer|refresh", "access|123|refresh|extra", "access\n|123|refresh", "access|123|" })
            rejected(() => AuthConfigFiles.Validate(accountA with { Token = token }), "config_auth_invalid", "malformed private token grammar rejects before native parse");
        rejected(() => AuthConfigFiles.Validate(accountA with { SavePassword = false }), "config_auth_invalid", "disabled remember-token store cannot retain a token");
        rejected(() => AuthConfigFiles.Validate(accountA with { SaveUsername = false }), "config_auth_invalid", "disabled remember-username store cannot retain username");
        rejected(() => AuthConfigFiles.Validate(accountA with { Username = string.Empty, Token = string.Empty, SaveUsername = false }),
            "config_auth_invalid", "impossible native remember-password-without-username flags reject");
        foreach (var malformed in new[] { "", "{}", "not-json", "{\"protocol\":1,\"protocol\":1,\"profiles\":{}}", "{\"protocol\":1,\"profiles\":{},\"extra\":1}" })
        {
            File.WriteAllText(path, malformed, utf8);
            rejected(() => AuthConfigFiles.ReadAccount(root, endpointA), "config_auth_invalid", "malformed or ambiguous private JSON rejects");
        }
        File.WriteAllBytes(path, clearedBytes);
        rejected(() => AuthConfigFiles.Encode(root, "https://illya.test/path", accountA), "config_auth_invalid", "private origin with a path cannot bind credentials");
        rejected(() => AuthConfigFiles.Encode(root, "https://illya.test:443", accountA), "config_auth_invalid", "noncanonical origin default port cannot create an ambiguous profile");
    }

    // Platform-neutral logic that runs identically on Windows and Linux.
    private static void portableProof()
    {
        require(ClientAssemblyPath.CanonicalPosix("/a/b/../c//d/./") == "/a/c/d", "POSIX canonical path resolves dot segments and duplicate separators");
        require(ClientAssemblyPath.CanonicalPosix("/..") == "/" && ClientAssemblyPath.CanonicalPosix("/") == "/", "POSIX canonical path never climbs above root");
        require(ClientAssemblyPath.CanonicalPosix("/a\\b") == "/a\\b", "backslash is an ordinary POSIX file-name character");
        foreach (var bad in new[] { "relative/path", "", "./x", "/a\0b" })
        {
            try { _ = ClientAssemblyPath.CanonicalPosix(bad); }
            catch (TransfigurationStartupException) { require(true, "non-absolute or NUL POSIX path rejects"); continue; }
            throw new InvalidOperationException("FAIL auth-only POSIX path accepted");
        }
        var windows = ClientProfile.Windows;
        var linux = ClientProfile.Linux;
        require(new[] { linux.GameSha256, linux.FrameworkSha256, linux.EntrySha256, linux.HttpSha256, linux.WebSocketsSha256 }
            .Intersect(new[] { windows.GameSha256, windows.FrameworkSha256, windows.EntrySha256, windows.HttpSha256, windows.WebSocketsSha256 }).Count() == 0,
            "Windows and Linux stock hashes are distinct pins");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        foreach (var (file, profile) in new[] { ("compatibility.json", windows), ("compatibility.linux-x64.json", linux) })
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, file)));
            var assemblies = manifest.RootElement.GetProperty("assemblies");
            string hash(string name) => assemblies.GetProperty(name).GetProperty("sha256").GetString()!;
            require(manifest.RootElement.GetProperty("platform").GetString() == profile.Platform
                && string.Equals(hash("osu.Game.dll"), profile.GameSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(hash("osu.Framework.dll"), profile.FrameworkSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(hash("osu!.dll"), profile.EntrySha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(hash("System.Net.Http.dll"), profile.HttpSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(hash("System.Net.WebSockets.Client.dll"), profile.WebSocketsSha256, StringComparison.OrdinalIgnoreCase)
                && assemblies.GetProperty("osu.Game.dll").GetProperty("version").GetString() == ClientProfile.GameVersion
                && assemblies.GetProperty("osu.Framework.dll").GetProperty("version").GetString() == ClientProfile.FrameworkVersion
                && assemblies.GetProperty("osu!.dll").GetProperty("version").GetString() == ClientProfile.EntryVersion,
                "compiled " + profile.Platform + " pins equal " + file);
        }
    }

    // Linux-only: runs the descriptor-based implementation against a real kernel.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void linuxFileProof(string root)
    {
        var area = Path.Combine(root, "linux");
        Directory.CreateDirectory(area);
        var auth = Path.Combine(area, AuthConfigFiles.Filename);
        var bytes = utf8.GetBytes("{\"protocol\":1,\"profiles\":{}}");
        AuthConfigFiles.Write(area, AuthConfigFiles.Filename, bytes, []);
        require(File.ReadAllBytes(auth).SequenceEqual(bytes) && File.GetUnixFileMode(auth) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
            "private account store is created with mode 0600");
        var ini = Path.Combine(area, "game.ini");
        File.WriteAllBytes(ini, utf8.GetBytes("AudioOffset = 1\n"));
        File.SetUnixFileMode(ini, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        AuthConfigFiles.Write(area, "game.ini", utf8.GetBytes("AudioOffset = 2\n"), utf8.GetBytes("AudioOffset = 1\n"));
        require(File.ReadAllText(ini) == "AudioOffset = 2\n" && File.GetUnixFileMode(ini) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
            "shared game.ini replacement keeps the mode the game gave it");
        require(!Directory.GetFiles(area, ".illyaverse-auth-*.tmp").Any(), "no temporary file remains after atomic replacement");
        rejected(() => AuthConfigFiles.Write(area, "game.ini", utf8.GetBytes("AudioOffset = 3\n"), utf8.GetBytes("stale\n")), "config_file_changed", "stale expected bytes reject on Linux");
        require(!Directory.GetFiles(area, ".illyaverse-auth-*.tmp").Any(), "rejected save leaves no temporary file");
        rejected(() => AuthConfigFiles.Read(area, "../escape"), "config_file_unverified", "leaf name cannot traverse");
        rejected(() => AuthConfigFiles.Read(area, "sub/dir"), "config_file_unverified", "leaf name cannot contain a separator");

        var fifo = Path.Combine(area, "fifo-area");
        Directory.CreateDirectory(fifo);
        using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", [Path.Combine(fifo, AuthConfigFiles.Filename)]) { UseShellExecute = false })!)
        {
            mkfifo.WaitForExit();
            require(mkfifo.ExitCode == 0, "FIFO fixture created");
        }
        rejected(() => AuthConfigFiles.ReadAccount(fifo, endpointA), "config_file_unverified", "FIFO at the private store name is refused without blocking");
        var real = Path.Combine(area, "real");
        Directory.CreateDirectory(real);
        File.WriteAllBytes(Path.Combine(real, "game.ini"), utf8.GetBytes("x = 1\n"));
        var alias = Path.Combine(area, "alias");
        Directory.CreateSymbolicLink(alias, real);
        rejected(() => AuthConfigFiles.Read(alias, "game.ini"), "config_storage_unverified", "symlinked directory ancestor is refused");
        rejected(() => AuthConfigFiles.Write(alias, "game.ini", [1], utf8.GetBytes("x = 1\n")), "config_storage_unverified", "save through a symlinked directory is refused");
        File.CreateSymbolicLink(Path.Combine(real, AuthConfigFiles.Filename), ini);
        rejected(() => AuthConfigFiles.ReadAccount(real, endpointA), "config_file_unverified", "private account symlink is never followed on Linux");
        require(File.ReadAllText(ini) == "AudioOffset = 2\n", "link target stays untouched");
    }

    private static void fileBoundaryProof(string root)
    {
        var official = Path.Combine(root, "game.ini");
        var expected = utf8.GetBytes("AudioOffset = 123\nToken = opaque-official\n");
        File.WriteAllBytes(official, expected);
        var writeTime = File.GetLastWriteTimeUtc(official);
        AuthConfigFiles.Write(root, "game.ini", expected, expected);
        require(File.GetLastWriteTimeUtc(official) == writeTime, "unchanged preference commit leaves original file timestamp unchanged");
        File.WriteAllText(official, "AudioOffset = 999\n", utf8);
        rejected(() => AuthConfigFiles.Write(root, "game.ini", utf8.GetBytes("AudioOffset = 45\n"), expected), "config_file_changed", "another writer's changed file is preserved");
        var foreignTemp = Path.Combine(root, ".illyaverse-auth-foreign.tmp");
        File.WriteAllText(foreignTemp, "foreign-marker", utf8);
        var actual = File.ReadAllBytes(official);
        AuthConfigFiles.Write(root, "game.ini", expected, actual);
        require(File.ReadAllBytes(official).SequenceEqual(expected) && File.ReadAllText(foreignTemp) == "foreign-marker"
            && Directory.GetFiles(root, ".illyaverse-auth-*.tmp").Length == 1, "atomic replacement cleans only its own handle and preserves foreign temporary file");
        var auth = Path.Combine(root, AuthConfigFiles.Filename);
        File.Delete(auth);
        Directory.CreateDirectory(auth);
        rejected(() => AuthConfigFiles.ReadAccount(root, endpointA), "config_file_unverified", "directory at private account filename rejects");
        Directory.Delete(auth);
        File.WriteAllBytes(auth, new byte[64 * 1024 + 1]);
        rejected(() => AuthConfigFiles.ReadAccount(root, endpointA), "config_file_too_large", "private profile store read is bounded before allocation");
        File.Delete(auth);

        var link = Path.Combine(root, AuthConfigFiles.Filename);
        try
        {
            File.CreateSymbolicLink(link, official);
            rejected(() => AuthConfigFiles.ReadAccount(root, endpointA), "config_file_unverified", "private account file reparse entry is never followed");
            rejected(() => AuthConfigFiles.Write(root, AuthConfigFiles.Filename, [123], []), "config_file_unverified", "private account reparse entry cannot be overwritten by save");
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP file symlink fixture: current Windows user lacks symlink privilege; no security policy changed"); }
        finally { if (File.Exists(link)) File.Delete(link); }
    }

}
