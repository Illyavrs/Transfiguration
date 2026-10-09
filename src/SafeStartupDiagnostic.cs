using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Illyaverse.Transfiguration;

internal enum StartupStage { Bootstrap, Assemblies, Contracts, NetworkGuards, ClientGuards, RelaxGuards, Ready }
#if ILLYAVERSE_FIXTURE
internal enum StartupPublishPhase { Locked, Created, Written, BeforeRename }
#endif

// The code is selected by our own pre-Main checks, never by an exception message.
internal sealed class TransfigurationStartupException(string code) : InvalidOperationException("transfiguration_" + code)
{
    internal string Code { get; } = code;
}

internal static class SafeStartupDiagnostic
{
    private static StartupStage stage;
#if ILLYAVERSE_FIXTURE
    internal static Action<StartupPublishPhase>? FixturePublishObserver { get; set; }
#endif

    internal static void Begin() => stage = StartupStage.Bootstrap;
    internal static void Checkpoint(StartupStage value) => stage = value;
    internal static TransfigurationStartupException Rejection(string code) => new(code);

    internal static void Report(Exception exception)
    {
        // Reporting is best effort and can never turn a failed guard into readiness.
        try
        {
            var current = exception;
            for (var depth = 0; depth < 4 && current is TargetInvocationException { InnerException: { } inner }; depth++) current = inner;
            var currentStage = stage;
            var stageName = name(currentStage);
            var code = current is TransfigurationStartupException rejection && allowed(currentStage, rejection.Code)
                ? rejection.Code : defaultCode(currentStage);
            Publish("failure", session => JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = 1,
                session,
                stage = stageName,
                code,
                exception = classify(current),
                endpoint = NetworkPolicy.DiagnosticEndpoint,
#if ILLYAVERSE_FIXTURE
                fixture = true,
#else
                fixture = false,
#endif
            }));
        }
        catch { }
    }

    // Immutable records appear only after every byte is flushed. The final entry
    // is never replaced, even when a foreign file happens to have a valid schema.
    internal static bool Publish(string suffix, Func<string, byte[]> payloadFactory)
    {
        try
        {
            if (!tryDestination(suffix, out var session, out var control, out var destination)) return false;
            using var directories = lockAncestors(control);
            if (entryExists(destination)) return false;
            var payload = payloadFactory(session);
            if (payload.Length is 0 or > 2048) return false;
#if ILLYAVERSE_FIXTURE
            FixturePublishObserver?.Invoke(StartupPublishPhase.Locked);
#endif
            directories.Validate();
            var temporary = Path.GetFileNameWithoutExtension(destination) + "-" + Guid.NewGuid().ToString("N") + ".tmp";
            // Resolve both entries against the verified directory object, never
            // by following its absolute pathname again after ancestor validation.
            using var handle = relativeFile(directories.Handles[^1], temporary, 0x40000000 | 0x10000 | 0x100000, 0, 2, 0x40 | 0x20);
            var published = false;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(handle, FileAccess.Write);
#if ILLYAVERSE_FIXTURE
                FixturePublishObserver?.Invoke(StartupPublishPhase.Created);
#endif
                directories.Validate();
                stream.Write(payload);
#if ILLYAVERSE_FIXTURE
                FixturePublishObserver?.Invoke(StartupPublishPhase.Written);
#endif
                stream.Flush(flushToDisk: true);
#if ILLYAVERSE_FIXTURE
                FixturePublishObserver?.Invoke(StartupPublishPhase.BeforeRename);
#endif
                directories.Validate();
                published = renameCreateOnly(handle, directories.Handles[^1], Path.GetFileName(destination));
                return published;
            }
            catch { return false; }
            finally
            {
                if (!published) deleteByHandle(handle);
                stream?.Dispose();
            }
        }
        catch { return false; }
    }

    private static bool tryDestination(string suffix, out string session, out string control, out string destination)
    {
        session = Environment.GetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION") ?? string.Empty;
        control = string.Empty;
        destination = string.Empty;
        if (suffix is not ("failure" or "progress-original_main_entered" or "progress-game_host_created" or "progress-game_host_run_entered"
            or "progress-renderer_initializing" or "progress-renderer_initialized" or "progress-window_created" or "progress-first_successful_draw")) return false;
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
        destination = Path.Combine(control, suffix == "failure" ? $"session-failure-{session}.json"
            : $"session-progress-{session}-{suffix["progress-".Length..]}.json");
        return true;
    }

    private static bool renameCreateOnly(SafeFileHandle handle, SafeFileHandle directory, string destination)
    {
        var filename = System.Text.Encoding.Unicode.GetBytes(destination);
        var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Filename)).ToInt32();
        var length = Marshal.SizeOf<RenameInformation>() + filename.Length;
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            for (var index = 0; index < length; index++) Marshal.WriteByte(memory, index, 0);
            Marshal.WriteIntPtr(memory, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.RootDirectory)).ToInt32(), directory.DangerousGetHandle());
            Marshal.WriteInt32(memory, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FilenameLength)).ToInt32(), filename.Length);
            Marshal.Copy(filename, 0, memory + offset, filename.Length);
            Marshal.WriteInt16(memory, offset + filename.Length, 0);
            // Native FileRenameInformation=10, ReplaceIfExists=false, name relative to the same
            // still-open verified control directory; terminating NUL excluded from length.
            return NtSetInformationFile(handle, out _, memory, (uint)length, 10) >= 0;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void deleteByHandle(SafeFileHandle handle)
    {
        var memory = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(memory, 1); _ = SetFileInformationByHandle(handle, 4, memory, 1); }
        finally { Marshal.FreeHGlobal(memory); }
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
        internal void Validate()
        {
            foreach (var handle in Handles)
                if (!GetFileInformationByHandle(handle, out var info)
                    || (info.Attributes & (uint)FileAttributes.Directory) == 0
                    || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException();
        }
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
                var handle = result.Handles.Count == 0
                    ? CreateFile(@"\\?\" + current, 0x80, 0x1, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero)
                    : relativeFile(result.Handles[^1], Path.GetFileName(current), 0x80 | 0x100000, 0x1, 1, 0x1 | 0x20 | 0x200000);
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

    private static SafeFileHandle relativeFile(SafeFileHandle directory, string filename, uint access, uint sharing, uint disposition, uint options)
    {
        var text = Marshal.StringToHGlobalUni(filename);
        var name = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
        try
        {
            Marshal.StructureToPtr(new UnicodeName { Length = checked((ushort)(filename.Length * 2)), MaximumLength = checked((ushort)((filename.Length + 1) * 2)), Buffer = text }, name, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = directory.DangerousGetHandle(), ObjectName = name, Attributes = 0x40
            };
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80, sharing, disposition, options, IntPtr.Zero, 0);
            if (status >= 0 && !handle.IsInvalid) return handle;
            handle.Dispose();
            throw new IOException();
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(text); }
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
            or "server_endpoint_missing" or "server_endpoint_invalid"
            or "ready_file_missing" or "session_invalid" or "path_not_absolute" or "path_not_local" or "directory_missing"
            or "fixture_data_path_invalid" or "fixture_control_path_invalid" or "control_path_invalid" or "control_data_overlap"
            or "data_reparse_point" or "ready_path_invalid" or "ready_file_exists",
        StartupStage.Assemblies => code is "client_assembly_missing" or "client_assembly_location_mismatch",
        StartupStage.Contracts => code is "client_version_unverified" or "client_assembly_unverified" or "runtime_version_unverified"
            or "ruleset_api_incompatible" or "endpoint_contract_incompatible" or "endpoint_factory_incompatible" or "startup_progress_contract_incompatible",
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        internal byte ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FilenameLength;
        internal ushort Filename;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeName { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory, ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint informationClass, IntPtr information, uint length);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status,
        IntPtr allocation, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr extendedAttributes, uint extendedLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr information, uint length, uint informationClass);
}
