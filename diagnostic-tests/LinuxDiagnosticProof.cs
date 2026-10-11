using System.Text.Json;
using Illyaverse.Transfiguration;

// Linux twin of the Windows directory-handle proof: synthetic directories only, no client assembly,
// GUI, Realm, real player data or network. Runs only on Linux (it uses the libc descriptor guards).
internal static class LinuxDiagnosticProof
{
    private static int checks;
    private static readonly string session = new('a', 64);
    private static readonly string[] environment = ["ILLYAVERSE_LAZER_SESSION", "ILLYAVERSE_LAZER_CONTROL_ROOT", "ILLYAVERSE_LAZER_SERVER_ENDPOINT"];

    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("diagnostic_fixture_failed_" + name);
        checks++;
        Console.WriteLine("PASS linux " + name);
    }

    private static void configure(string control, string? nonce)
    {
        Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_CONTROL_ROOT", control);
        Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION", nonce);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static int Run()
    {
        var previous = environment.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_SERVER_ENDPOINT", "https://illya.test");
        LaunchState.Active = false;
        var proofRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..", "proof-output"));
        var nonce = Guid.NewGuid().ToString("N");
        var control = Path.Combine(proofRoot, "data-transfiguration-proof-diagnostic-" + nonce + "-control");
        var outside = Path.Combine(proofRoot, "diagnostic-outside-" + nonce);
        var link = Path.Combine(proofRoot, "data-transfiguration-proof-link-" + nonce + "-control");
        var ancestorLink = Path.Combine(proofRoot, "diagnostic-ancestor-" + nonce);
        var failure = Path.Combine(control, "session-failure-" + session + ".json");
        Directory.CreateDirectory(control);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "foreign-marker.txt");
        File.WriteAllText(marker, "synthetic-foreign-token-never-serialized");
        var markerTime = File.GetLastWriteTimeUtc(marker);
        try
        {
            var assembly = Path.Combine(control, "osu!.dll");
            require(ClientAssemblyPath.Matches(assembly, control + "/../" + Path.GetFileName(control) + "/osu!.dll"), "dot-dot assembly path normalizes to the identical path");
            require(!ClientAssemblyPath.Matches(assembly.ToUpperInvariant(), assembly), "POSIX paths are case-sensitive");
            require(!ClientAssemblyPath.Matches(Path.Combine(outside, "osu!.dll"), assembly), "different assembly directory is rejected");
            foreach (var foreign in new[] { "relative.dll", "osu!.dll", "" })
            {
                try { _ = ClientAssemblyPath.Matches(foreign, assembly); }
                catch (TransfigurationStartupException) { require(true, "relative assembly path is rejected"); continue; }
                throw new InvalidOperationException("diagnostic_fixture_foreign_assembly_path_accepted");
            }
            configure(control, session);
            foreach (var value in new string?[] { null, "", "a", new('a', 65), new('g', 64), new('A', 64) })
            {
                configure(control, value);
                SafeStartupDiagnostic.Report(new InvalidOperationException("synthetic-secret"));
                require(!Directory.EnumerateFileSystemEntries(control).Any(), "invalid nonce emits no file");
            }
            foreach (var value in new string?[] { null, "relative-control", outside, Path.Combine(proofRoot, "data-transfiguration-proof-nonexistent-control") })
            {
                configure(value!, session);
                SafeStartupDiagnostic.Report(new InvalidOperationException("synthetic-secret"));
                require(!Directory.EnumerateFileSystemEntries(control).Any() && Directory.EnumerateFileSystemEntries(outside).Count() == 1, "invalid control emits no file");
            }
            configure(control, session);
            File.WriteAllText(failure, "foreign-owned-sidecar");
            var foreignTime = File.GetLastWriteTimeUtc(failure);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(File.ReadAllText(failure) == "foreign-owned-sidecar" && File.GetLastWriteTimeUtc(failure) == foreignTime, "preexisting foreign sidecar stays identical");
            File.Delete(failure);
            Directory.CreateDirectory(failure);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(Directory.Exists(failure) && !Directory.EnumerateFileSystemEntries(failure).Any(), "preexisting sidecar directory stays untouched");
            Directory.Delete(failure);

            Directory.CreateSymbolicLink(link, outside);
            configure(link, session);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(Directory.EnumerateFileSystemEntries(outside).Count() == 1, "control symlink cannot redirect a sidecar");
            Directory.Delete(link);
            Directory.CreateSymbolicLink(ancestorLink, outside);
            var nested = Path.Combine(ancestorLink, "data-transfiguration-proof-nested-control");
            Directory.CreateDirectory(nested);
            configure(nested, session);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(!Directory.EnumerateFileSystemEntries(nested).Any(), "symlink ancestor cannot redirect a sidecar");
            Directory.Delete(nested);
            Directory.Delete(ancestorLink);
            configure(control, session);
            File.CreateSymbolicLink(failure, marker);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(File.ReadAllText(marker) == "synthetic-foreign-token-never-serialized" && File.GetLastWriteTimeUtc(marker) == markerTime, "sidecar symlink cannot overwrite its target");
            File.Delete(failure);

            configure(control, session);
            SafeStartupDiagnostic.Report(new InvalidOperationException("synthetic-account-path-token-secret"));
            var text = File.ReadAllText(failure);
            using (var parsed = JsonDocument.Parse(text))
                require(parsed.RootElement.EnumerateObject().Count() == 7 && parsed.RootElement.GetProperty("session").GetString() == session
                    && parsed.RootElement.GetProperty("exception").GetString() == "InvalidOperationException"
                    && !text.Contains("secret", StringComparison.Ordinal) && !text.Contains(control, StringComparison.Ordinal), "failure sidecar has the bounded seven-field schema and no secret or path");
            var mode = File.GetUnixFileMode(failure);
            require((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0
                && (mode & UnixFileMode.UserRead) != 0, "sidecar is private to the owner");
            require(!Directory.EnumerateFileSystemEntries(control).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)), "no temporary file remains after publication");
            var bytes = File.ReadAllBytes(failure);
            SafeStartupDiagnostic.Report(new Exception("another"));
            require(File.ReadAllBytes(failure).SequenceEqual(bytes), "a published sidecar is never replaced");
            File.Delete(failure);
            Console.WriteLine($"PASS {checks} isolated Linux startup diagnostic checks; no client assemblies, GUI, Realm, real player data or network used");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL linux_startup_diagnostic_fixture " + exception.GetType().Name + " " + exception.Message);
            return 1;
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            foreach (var path in new[] { link, ancestorLink }) if (Directory.Exists(path)) Directory.Delete(path);
            foreach (var path in new[] { control, outside })
            {
                var full = Path.GetFullPath(path);
                if (Path.GetDirectoryName(full) != proofRoot) throw new InvalidOperationException("diagnostic_fixture_cleanup_path_invalid");
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
        }
    }
}
