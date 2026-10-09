using System.Text.Json;
using Illyaverse.Transfiguration;

internal static class Program
{
    private static int checks;
    private static readonly string[] environment = ["ILLYAVERSE_LAZER_SESSION", "ILLYAVERSE_LAZER_CONTROL_ROOT", "ILLYAVERSE_LAZER_READY_FILE"];
    private static readonly string session = new('a', 64);

    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("diagnostic_fixture_failed_" + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    public static int Main(string[] args)
    {
        if (args is ["--stock-contracts", var installation]) return StockProgressContracts.Run(installation);
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Windows directory-handle proof requires Windows"); return 2; }
        LaunchState.Active = false;
        var previous = environment.ToDictionary(key => key, Environment.GetEnvironmentVariable);
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
            require(ClientAssemblyPath.Matches(@"\\?\" + assembly, assembly), "hostfxr extended drive prefix matches the identical assembly path");
            require(ClientAssemblyPath.Matches(assembly, @"\\?\" + assembly), "expected extended drive prefix matches the identical assembly path");
            require(ClientAssemblyPath.Matches(assembly.ToUpperInvariant(), assembly), "Windows case equivalence stays accepted");
            require(!ClientAssemblyPath.Matches(Path.Combine(outside, "osu!.dll"), assembly), "different assembly directory stays rejected");
            require(!ClientAssemblyPath.Matches(@"\\?\" + Path.Combine(outside, "osu!.dll"), assembly), "different extended assembly directory stays rejected");
            foreach (var foreign in new[] { "relative.dll", @"\\fixture.invalid\never-connected\osu!.dll", @"\\?\UNC\fixture.invalid\never-connected\osu!.dll", @"\\.\C:\fixture\osu!.dll" })
            {
                try { _ = ClientAssemblyPath.Matches(foreign, assembly); }
                catch (TransfigurationStartupException) { require(true, "remote relative or device assembly path stays rejected"); continue; }
                throw new InvalidOperationException("diagnostic_fixture_foreign_assembly_path_accepted");
            }
            configure(control, session);
            foreach (var value in new string?[] { null, "", "a", new('a', 65), new('g', 64), new('A', 64), "../" + new string('a', 61) })
            {
                Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION", value);
                SafeStartupDiagnostic.Report(new InvalidOperationException("synthetic-secret"));
                require(!Directory.EnumerateFileSystemEntries(control).Any(), "invalid nonce emits no file");
            }
            Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION", session);
            foreach (var value in new string?[] { null, "relative-control", @"\\fixture.invalid\never-connected", outside, Path.Combine(proofRoot, "data-transfiguration-proof-nonexistent-control") })
            {
                Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_CONTROL_ROOT", value);
                SafeStartupDiagnostic.Report(new InvalidOperationException("synthetic-secret"));
                require(!Directory.EnumerateFileSystemEntries(control).Any() && Directory.EnumerateFileSystemEntries(outside).Count() == 1,
                    "invalid control emits no file");
            }
            configure(control, session);
            File.WriteAllText(failure, "foreign-owned-sidecar");
            var foreignTime = File.GetLastWriteTimeUtc(failure);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(File.ReadAllText(failure) == "foreign-owned-sidecar" && File.GetLastWriteTimeUtc(failure) == foreignTime,
                "preexisting foreign sidecar remains byte and timestamp identical");
            File.Delete(failure);
            Directory.CreateDirectory(failure);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(Directory.Exists(failure) && !Directory.EnumerateFileSystemEntries(failure).Any(), "preexisting sidecar directory remains untouched");
            Directory.Delete(failure);

            // The harness owns both the links and every target; no real user path is used.
            Directory.CreateSymbolicLink(link, outside);
            configure(link, session);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(Directory.EnumerateFileSystemEntries(outside).Count() == 1, "control reparse cannot redirect a sidecar");
            Directory.Delete(link);
            Directory.CreateSymbolicLink(ancestorLink, outside);
            var nested = Path.Combine(ancestorLink, "data-transfiguration-proof-nested-control");
            Directory.CreateDirectory(nested);
            configure(nested, session);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(!Directory.EnumerateFileSystemEntries(nested).Any(), "foreign/reparse ancestor cannot redirect a sidecar");
            Directory.Delete(nested);
            Directory.Delete(ancestorLink);
            configure(control, session);
            File.CreateSymbolicLink(failure, marker);
            SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
            require(File.ReadAllText(marker) == "synthetic-foreign-token-never-serialized" && File.GetLastWriteTimeUtc(marker) == markerTime,
                "preexisting sidecar link cannot overwrite a foreign target");
            File.Delete(failure);

            // Move only this invocation's two owned directories, then replace the
            // exact expected fixture parent with a link to exercise ancestor checks.
            var entries = Directory.EnumerateFileSystemEntries(proofRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
            require(entries.SetEquals([control, outside]), "ancestor fixture contains only this invocation's owned directories");
            var movedProofRoot = proofRoot + "-owned-" + nonce;
            require(Path.GetDirectoryName(movedProofRoot) == Path.GetDirectoryName(proofRoot) && !Directory.Exists(movedProofRoot),
                "ancestor fixture move stays under the owned diagnostic project");
            Directory.Move(proofRoot, movedProofRoot);
            try
            {
                Directory.CreateSymbolicLink(proofRoot, movedProofRoot);
                configure(control, session);
                SafeStartupDiagnostic.Report(new Exception("synthetic-secret"));
                require(!File.Exists(failure) && Directory.EnumerateFileSystemEntries(outside).Count() == 1,
                    "exact expected control under a reparse ancestor emits no file");
            }
            finally
            {
                if (Directory.Exists(proofRoot))
                {
                    if ((File.GetAttributes(proofRoot) & FileAttributes.ReparsePoint) == 0)
                        throw new InvalidOperationException("diagnostic_fixture_parent_link_changed");
                    Directory.Delete(proofRoot);
                }
                Directory.Move(movedProofRoot, proofRoot);
            }

            foreach (var stage in Enum.GetValues<StartupStage>())
            {
                var exception = new InvalidOperationException("synthetic-account-path-token-secret", new Exception("nested-secret"));
                exception.Data["token"] = "synthetic-data-secret";
                Guard.Stage = stage;
                Guard.Failure = exception;
                rejectSameException(exception);
                var text = File.ReadAllText(failure);
                using var parsed = JsonDocument.Parse(text);
                var value = parsed.RootElement;
                require(value.EnumerateObject().Count() == 6 && value.GetProperty("protocol").GetInt32() == 1
                    && value.GetProperty("session").GetString() == session && value.GetProperty("fixture").GetBoolean(), "exact six-field failure schema");
                require(value.GetProperty("stage").GetString() == stageName(stage)
                    && value.GetProperty("code").GetString() == code(stage)
                    && value.GetProperty("exception").GetString() == "InvalidOperationException", "failure stage and static exception classification");
                require(!text.Contains("secret", StringComparison.Ordinal) && !text.Contains(control, StringComparison.Ordinal)
                    && !text.Contains("StackTrace", StringComparison.Ordinal) && text.Length < 1024, "diagnostic has no exception message path token data or stack");
                require(!File.Exists(Environment.GetEnvironmentVariable("ILLYAVERSE_LAZER_READY_FILE")), "untrusted ready-file environment never redirects the diagnostic");
                File.Delete(failure);
            }
            Guard.Stage = StartupStage.Bootstrap;
            Guard.Failure = SafeStartupDiagnostic.Rejection("ready_path_invalid");
            rejectSameException(Guard.Failure);
            using (var parsed = JsonDocument.Parse(File.ReadAllText(failure)))
                require(parsed.RootElement.GetProperty("code").GetString() == "ready_path_invalid"
                    && parsed.RootElement.GetProperty("exception").GetString() == "TransfigurationStartupException", "own rejection has an allowlisted static code");
            File.Delete(failure);
            Guard.Failure = SafeStartupDiagnostic.Rejection("synthetic-unallowlisted-secret");
            rejectSameException(Guard.Failure);
            using (var parsed = JsonDocument.Parse(File.ReadAllText(failure)))
                require(parsed.RootElement.GetProperty("code").GetString() == "bootstrap_failed", "unknown own code falls back to static stage code");
            File.Delete(failure);
            StartupProgressProof.Run(control, outside, marker, session, require);
            configure(outside, session);
            Guard.Failure = new InvalidOperationException("synthetic-secret");
            rejectSameException(Guard.Failure);
            require(Directory.EnumerateFileSystemEntries(outside).Count() == 1, "reporting failure preserves rejection and writes nothing outside owned control");
            Console.WriteLine($"PASS {checks} isolated managed startup diagnostic/progress checks; no client assemblies, GUI, Realm, real player data or network used");
            return 0;
        }
        catch (Exception exception)
        {
            // Keep even fixture failures bounded; never print exception text or paths.
            Console.Error.WriteLine("FAIL startup_diagnostic_fixture " + exception.GetType().Name);
            return 1;
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            // Delete links non-recursively before deleting only this invocation's owned directories.
            foreach (var path in new[] { link, ancestorLink }) if (Directory.Exists(path)) Directory.Delete(path);
            foreach (var path in new[] { control, outside })
            {
                var full = Path.GetFullPath(path);
                if (Path.GetDirectoryName(full) != proofRoot) throw new InvalidOperationException("diagnostic_fixture_cleanup_path_invalid");
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
        }
    }

    private static void configure(string control, string value)
    {
        Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_CONTROL_ROOT", control);
        Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION", value);
        Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_READY_FILE", Path.Combine(Path.GetDirectoryName(control)!, "diagnostic-unused", "foreign-ready.json"));
    }

    private static void rejectSameException(Exception expected)
    {
        try { StartupHook.Initialize(); }
        catch (Exception actual) when (ReferenceEquals(actual, expected)) { require(true, "mandatory startup hook rethrows the original failure"); return; }
        throw new InvalidOperationException("diagnostic_fixture_startup_did_not_reject");
    }

    private static string stageName(StartupStage stage) => stage switch
    {
        StartupStage.Assemblies => "assemblies", StartupStage.Contracts => "contracts", StartupStage.NetworkGuards => "network_guards",
        StartupStage.ClientGuards => "client_guards", StartupStage.RelaxGuards => "relax_guards", StartupStage.Ready => "ready", _ => "bootstrap"
    };
    private static string code(StartupStage stage) => stage switch
    {
        StartupStage.Assemblies => "assembly_load_failed", StartupStage.Contracts => "contract_validation_failed", StartupStage.NetworkGuards => "network_guard_install_failed",
        StartupStage.ClientGuards => "client_guard_install_failed", StartupStage.RelaxGuards => "relax_guard_install_failed", StartupStage.Ready => "ready_publish_failed", _ => "bootstrap_failed"
    };
}

// This source-linked fixture never loads Harmony or any client type. It exercises
// the actual StartupHook catch/rethrow and the actual bounded diagnostic writer.
namespace Illyaverse.Transfiguration
{
    internal static class Guard
    {
        internal static StartupStage Stage;
        internal static Exception Failure = new InvalidOperationException();
        internal static void Initialize() { SafeStartupDiagnostic.Checkpoint(Stage); throw Failure; }
    }
}
