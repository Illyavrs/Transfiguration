using System.Runtime.InteropServices;
using System.Text.Json;
using Illyaverse.Transfiguration;
using Microsoft.Win32.SafeHandles;

internal static class StartupProgressProof
{
    internal static void Run(string control, string outside, string marker, string session, Action<bool, string> require)
    {
        var previous = LaunchState.Active;
        var finals = Enum.GetValues<ClientStartupCheckpoint>().Select(stage => Path.Combine(control, $"session-progress-{session}-{ClientStartupProgress.Name(stage)}.json")).ToArray();
        var final = finals[0];
        var foreignTemporary = Path.Combine(control, $"session-progress-{session}-original_main_entered-{new string('b', 32)}.tmp");
        var link = Path.Combine(Path.GetDirectoryName(control)!, "data-transfiguration-proof-progress-link-" + Guid.NewGuid().ToString("N") + "-control");
        void configure(string root, string nonce)
        {
            ClientStartupProgress.ResetFixture();
            Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_CONTROL_ROOT", root);
            Environment.SetEnvironmentVariable("ILLYAVERSE_LAZER_SESSION", nonce);
        }
        void noRecords(string name) => require(!finals.Any(File.Exists) && !Directory.EnumerateFiles(control, "*.tmp").Any(), name);
        try
        {
            configure(control, session);
            LaunchState.Active = false;
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            noRecords("progress cannot precede mandatory guard activation");
            LaunchState.Active = true;
            ClientStartupProgress.Emit((ClientStartupCheckpoint)999);
            noRecords("unknown progress stage emits no record");
            configure(control, "invalid");
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            noRecords("invalid progress nonce creates no final or temporary");
            configure(outside, session);
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            require(Directory.EnumerateFileSystemEntries(outside).Count() == 1, "foreign progress control remains untouched");
            configure(control, session);
            var invoked = false;
            require(!SafeStartupDiagnostic.Publish("progress-../foreign", _ => { invoked = true; return []; }) && !invoked,
                "unallowlisted record suffix cannot invoke a writer");
            noRecords("invalid progress suffix creates no entry");
            File.WriteAllText(final, "foreign-final-secret");
            var foreignTime = File.GetLastWriteTimeUtc(final);
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            require(File.ReadAllText(final) == "foreign-final-secret" && File.GetLastWriteTimeUtc(final) == foreignTime,
                "progress preserves foreign final bytes and timestamps");
            File.Delete(final);
            configure(control, session);
            File.CreateSymbolicLink(final, marker);
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            require(File.ReadAllText(marker) == "synthetic-foreign-token-never-serialized", "progress final link cannot overwrite its target");
            File.Delete(final);
            Directory.CreateSymbolicLink(link, outside);
            configure(link, session);
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            require(Directory.EnumerateFileSystemEntries(outside).Count() == 1, "progress control reparse cannot redirect a write");
            Directory.Delete(link);
            configure(control, session);
            File.WriteAllText(foreignTemporary, "foreign-temporary-secret");
            var temporaryTime = File.GetLastWriteTimeUtc(foreignTemporary);
            foreach (var phase in Enum.GetValues<StartupPublishPhase>())
            {
                SafeStartupDiagnostic.FixturePublishObserver = value => { if (value == phase) throw new InvalidOperationException("synthetic-injected-secret"); };
                ClientStartupProgress.ResetFixture();
                ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
                require(!File.Exists(final) && Directory.EnumerateFileSystemEntries(control).Single() == foreignTemporary,
                    "managed publication failure removes only its own unpublished temporary");
            }
            SafeStartupDiagnostic.FixturePublishObserver = null;
            require(File.ReadAllText(foreignTemporary) == "foreign-temporary-secret" && File.GetLastWriteTimeUtc(foreignTemporary) == temporaryTime,
                "foreign temporary remains byte and timestamp identical");
            File.Delete(foreignTemporary);

            using (var writableDirectory = CreateFile(@"\\?\" + control, 0x40000000, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                require(!writableDirectory.IsInvalid, "owned fixture opens a synthetic writable directory handle");
                var redirected = false;
                SafeStartupDiagnostic.FixturePublishObserver = phase =>
                {
                    if (phase != StartupPublishPhase.Locked) return;
                    redirected = setJunction(writableDirectory, outside);
                    require(redirected, "owned fixture changes an empty pinned directory into a reparse point");
                };
                try
                {
                    configure(control, session);
                    ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
                    require(redirected && Directory.EnumerateFileSystemEntries(outside).Count() == 1,
                        "in-place control reparse mutation cannot create a foreign temporary or final");
                }
                finally
                {
                    SafeStartupDiagnostic.FixturePublishObserver = null;
                    if (redirected) require(clearJunction(writableDirectory), "owned in-place reparse fixture restores its original directory");
                }
                noRecords("in-place control mutation leaves the owned directory untouched");
            }
            configure(control, session);
            ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered);
            require(File.Exists(final), "complete record publishes relative to the verified control handle");
            File.Delete(final);
            SafeStartupDiagnostic.FixturePublishObserver = null;

            // A deterministic pause before rename proves that the final path is
            // absent throughout an unfinished write, then contains complete JSON.
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            SafeStartupDiagnostic.FixturePublishObserver = phase =>
            {
                if (phase != StartupPublishPhase.Written) return;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException();
            };
            configure(control, session);
            var publication = Task.Run(() => ClientStartupProgress.Emit(ClientStartupCheckpoint.OriginalMainEntered));
            try
            {
                require(entered.Wait(TimeSpan.FromSeconds(5)), "atomic fixture reaches an unfinished temporary write");
                for (var index = 0; index < 20; index++) require(!File.Exists(final), "unfinished temporary never exposes a partial final");
            }
            finally { release.Set(); publication.GetAwaiter().GetResult(); SafeStartupDiagnostic.FixturePublishObserver = null; }
            using (var record = JsonDocument.Parse(File.ReadAllText(final))) require(record.RootElement.GetProperty("stage").GetString() == "original_main_entered", "atomic rename exposes a complete final record");
            File.Delete(final);

            configure(control, session);
            foreach (var stage in Enum.GetValues<ClientStartupCheckpoint>().Reverse()) ClientStartupProgress.Emit(stage);
            require(finals.All(File.Exists) && Directory.EnumerateFileSystemEntries(control).Count() == 7,
                "shuffled progress observations create exactly seven immutable finals");
            foreach (var stage in Enum.GetValues<ClientStartupCheckpoint>())
            {
                var filename = Path.Combine(control, $"session-progress-{session}-{ClientStartupProgress.Name(stage)}.json");
                var text = File.ReadAllText(filename);
                using var parsed = JsonDocument.Parse(text);
                var record = parsed.RootElement;
                require(record.EnumerateObject().Count() == 5 && record.GetProperty("protocol").GetInt32() == 1
                    && record.GetProperty("session").GetString() == session && record.GetProperty("stage").GetString() == ClientStartupProgress.Name(stage)
                    && record.GetProperty("endpoint").GetString() == NetworkPolicy.Website && record.GetProperty("fixture").GetBoolean(),
                    "progress has exact fixed endpoint nonce stage fixture schema");
                require(!text.Contains("secret", StringComparison.Ordinal) && !text.Contains(control, StringComparison.Ordinal) && text.Length < 1024,
                    "progress contains no account path exception token or arbitrary payload");
                var time = File.GetLastWriteTimeUtc(filename);
                Parallel.For(0, 32, _ => ClientStartupProgress.Emit(stage));
                require(File.ReadAllText(filename) == text && File.GetLastWriteTimeUtc(filename) == time,
                    "duplicate concurrent milestones preserve the first immutable record");
            }
            require(!Directory.EnumerateFiles(control, "*.tmp").Any(), "successful progress publications leave no temporary files");
            foreach (var filename in finals) File.Delete(filename);
            configure(control, session);
        }
        finally
        {
            SafeStartupDiagnostic.FixturePublishObserver = null;
            ClientStartupProgress.ResetFixture();
            LaunchState.Active = previous;
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);

    private static bool setJunction(SafeFileHandle directory, string target)
    {
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = System.Text.Encoding.Unicode.GetBytes(target);
        var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)dataLength)).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        return DeviceIoControl(directory, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
    private static bool clearJunction(SafeFileHandle directory)
    {
        var buffer = new byte[8];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        return DeviceIoControl(directory, 0x000900AC, buffer, 8, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
