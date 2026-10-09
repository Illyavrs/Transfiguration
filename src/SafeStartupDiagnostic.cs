using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Illyaverse.Transfiguration;

internal enum StartupStage { Bootstrap, Assemblies, Contracts, NetworkGuards, ClientGuards, RelaxGuards, Ready }

// The code is selected by our own pre-Main checks, never by an exception message.
internal sealed class TransfigurationStartupException(string code) : InvalidOperationException("transfiguration_" + code)
{
    internal string Code { get; } = code;
}

internal static class SafeStartupDiagnostic
{
    private static StartupStage stage;

    internal static void Begin() => stage = StartupStage.Bootstrap;
    internal static void Checkpoint(StartupStage value) => stage = value;
    internal static TransfigurationStartupException Rejection(string code) => new(code);

    internal static void Report(Exception exception)
    {
        // Reporting is best effort and can never turn a failed guard into readiness.
        try
        {
            if (!tryDestination(out var session, out var control, out var destination)) return;
            using var directories = lockAncestors(control);
            // CreateNew rejects both pre-existing ordinary files and dangling links.
            if (entryExists(destination)) return;
            var current = exception;
            for (var depth = 0; depth < 4 && current is TargetInvocationException { InnerException: { } inner }; depth++) current = inner;
            var currentStage = stage;
            var stageName = name(currentStage);
            var code = current is TransfigurationStartupException rejection && allowed(currentStage, rejection.Code)
                ? rejection.Code : defaultCode(currentStage);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = 1,
                session,
                stage = stageName,
                code,
                exception = classify(current),
#if ILLYAVERSE_FIXTURE
                fixture = true,
#else
                fixture = false,
#endif
            });
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        catch { }
    }

    private static bool tryDestination(out string session, out string control, out string destination)
    {
        session = Environment.GetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION") ?? string.Empty;
        control = string.Empty;
        destination = string.Empty;
        if (!OperatingSystem.IsWindows() || session.Length != 64
            || !session.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        var supplied = Environment.GetEnvironmentVariable("ILLYAVERSE_LAZER_CONTROL_ROOT");
        if (supplied is null || !Path.IsPathFullyQualified(supplied)) return false;
        control = localPath(supplied);
        if (!Directory.Exists(control)) return false;
#if ILLYAVERSE_FIXTURE
        // The fixture can write only to the checked-in bridge's owned proof-output.
        var fixtureRoot = localPath(Path.Combine(Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!, "../../..", "proof-output"));
        var parent = Path.GetDirectoryName(control);
        var filename = Path.GetFileName(control);
        if (!string.Equals(parent, fixtureRoot, StringComparison.OrdinalIgnoreCase)
            || !filename.StartsWith("data-transfiguration-proof", StringComparison.Ordinal)
            || !filename.EndsWith("-control", StringComparison.Ordinal)) return false;
#else
        var expected = localPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illyaverse", "Kaleidoscope"));
        if (!string.Equals(control, expected, StringComparison.OrdinalIgnoreCase)) return false;
#endif
        destination = Path.Combine(control, $"session-failure-{session}.json");
        return true;
    }

    private static string localPath(string value)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (path.Length < 7 || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\') throw new ArgumentException();
            path = path[4..];
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException();
        return path;
    }

    private static bool entryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private sealed class DirectoryLocks : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = new();
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }

    private static DirectoryLocks lockAncestors(string path)
    {
        var paths = new Stack<string>();
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent) paths.Push(current.FullName);
        var result = new DirectoryLocks();
        try
        {
            foreach (var current in paths)
            {
                // No FILE_SHARE_DELETE: each verified ancestor stays in place until
                // the sidecar is closed. OPEN_REPARSE_POINT inspects the entry itself.
                var handle = CreateFile(current, 0x80, 0x1 | 0x2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
                result.Handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info)
                    || (info.Attributes & (uint)FileAttributes.Directory) == 0
                    || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException();
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static string name(StartupStage value) => value switch
    {
        StartupStage.Assemblies => "assemblies", StartupStage.Contracts => "contracts",
        StartupStage.NetworkGuards => "network_guards", StartupStage.ClientGuards => "client_guards",
        StartupStage.RelaxGuards => "relax_guards", StartupStage.Ready => "ready", _ => "bootstrap"
    };

    private static string defaultCode(StartupStage value) => value switch
    {
        StartupStage.Assemblies => "assembly_load_failed", StartupStage.Contracts => "contract_validation_failed",
        StartupStage.NetworkGuards => "network_guard_install_failed", StartupStage.ClientGuards => "client_guard_install_failed",
        StartupStage.RelaxGuards => "relax_guard_install_failed", StartupStage.Ready => "ready_publish_failed", _ => "bootstrap_failed"
    };

    private static bool allowed(StartupStage value, string code) => value switch
    {
        StartupStage.Bootstrap => code is "session_missing" or "install_root_missing" or "data_root_missing" or "control_root_missing"
            or "ready_file_missing" or "session_invalid" or "path_not_absolute" or "path_not_local" or "directory_missing"
            or "fixture_data_path_invalid" or "fixture_control_path_invalid" or "control_path_invalid" or "control_data_overlap"
            or "data_reparse_point" or "ready_path_invalid" or "ready_file_exists",
        StartupStage.Assemblies => code is "client_assembly_missing" or "client_assembly_location_mismatch",
        StartupStage.Contracts => code is "client_version_unverified" or "client_assembly_unverified" or "runtime_version_unverified"
            or "ruleset_api_incompatible" or "endpoint_contract_incompatible" or "endpoint_factory_incompatible",
        StartupStage.Ready => code is "patch_not_installed",
        _ => false
    };

    private static string classify(Exception value) => value switch
    {
        TransfigurationStartupException => "TransfigurationStartupException",
        MissingMethodException => "MissingMethodException", MissingFieldException => "MissingFieldException",
        TypeLoadException => "TypeLoadException", FileNotFoundException => "FileNotFoundException",
        FileLoadException => "FileLoadException", BadImageFormatException => "BadImageFormatException",
        IOException => "IOException", UnauthorizedAccessException => "UnauthorizedAccessException",
        NotSupportedException => "NotSupportedException", ArgumentException => "ArgumentException",
        InvalidOperationException => "InvalidOperationException",
        _ when value.GetType().FullName == "HarmonyLib.HarmonyException" => "HarmonyException",
        _ => "Exception"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
