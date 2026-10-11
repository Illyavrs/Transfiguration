using Microsoft.Win32.SafeHandles;

namespace Illyaverse.Transfiguration;

// Linux implementation. The path is walked once from "/" with O_NOFOLLOW|O_DIRECTORY and the
// descriptors are kept open; every later open/rename/unlink is relative to the verified directory
// descriptor, so a path component swapped after verification cannot redirect an operation.
// Files are opened with O_NOFOLLOW|O_CLOEXEC, must be regular (fstat/statx on the open
// descriptor), are read with a size bound, and are replaced by fsync + same-directory rename(2).
internal static partial class AuthConfigFiles
{
    private const uint privateMode = 0x180; // 0600

    private sealed class LinuxDirectories : IDisposable
    {
        internal readonly List<SafeFileHandle> Values = new();
        public void Dispose() { foreach (var value in Values) value.Dispose(); }
    }

    private sealed class LinuxLease(FileStream? stream, byte[] bytes, uint mode) : IDisposable
    {
        internal readonly byte[] Bytes = bytes;
        internal readonly uint Mode = mode;
        internal bool Exists => stream is not null;
        public void Dispose() => stream?.Dispose();
    }

    private static LinuxDirectories lockDirectoriesLinux(string path)
    {
        string canonical;
        try { canonical = ClientAssemblyPath.CanonicalPosix(path); }
        catch (TransfigurationStartupException) { fail("config_storage_unverified"); return null!; }
        var directories = new LinuxDirectories();
        try { directories.Values.AddRange(LinuxNative.OpenDirectoryChain(canonical)); }
        catch (IOException) { directories.Dispose(); fail("config_storage_unverified"); }
        return directories;
    }

    private static void requireLeafName(string filename)
    {
        if (filename.Length == 0 || filename is "." or ".." || filename.Contains('/') || filename.Contains('\0')) fail("config_file_unverified");
    }

    private static LinuxLease readLeaseLinux(SafeFileHandle directory, string filename, int limit)
    {
        requireLeafName(filename);
        var handle = LinuxNative.OpenRegularForRead(directory, filename);
        if (handle.IsInvalid)
        {
            var error = LinuxNative.LastError;
            handle.Dispose();
            if (error == LinuxNative.ENOENT) return new LinuxLease(null, [], privateMode);
            fail("config_file_unverified"); // includes ELOOP: a symlink is never followed
        }
        FileStream? stream = null;
        try
        {
            // Directories, FIFOs, devices and sockets are refused after open, on the descriptor itself.
            if (!LinuxNative.TryStat(handle, out var kind) || !kind.IsRegular) fail("config_file_unverified");
            if (kind.Size > (ulong)limit) fail("config_file_too_large");
            stream = new FileStream(handle, FileAccess.Read, 1, false);
            var bytes = new byte[checked((int)kind.Size)];
            stream.ReadExactly(bytes);
            return new LinuxLease(stream, bytes, kind.Permissions);
        }
        catch { stream?.Dispose(); handle.Dispose(); throw; }
    }

    private static byte[] readLinux(string root, string filename, int limit)
    {
        using var directories = lockDirectoriesLinux(root);
        using var lease = readLeaseLinux(directories.Values[^1], filename, limit);
        return lease.Bytes;
    }

    private static void writeLinux(string root, string filename, byte[] bytes, byte[] expected)
    {
        using var directories = lockDirectoriesLinux(root);
        var directory = directories.Values[^1];
        var limit = limitFor(filename);
        bool existed;
        uint mode;
        using (var destination = readLeaseLinux(directory, filename, limit))
        {
            if (!destination.Bytes.AsSpan().SequenceEqual(expected)) fail("config_file_changed");
            if (destination.Bytes.AsSpan().SequenceEqual(bytes)) return;
            existed = destination.Exists;
            // The private account store is always 0600; the shared game.ini keeps the mode the game gave it.
            mode = filename == Filename || !existed ? privateMode : destination.Mode;
        }
        var temporary = ".illyaverse-auth-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var handle = LinuxNative.CreateExclusive(directory, temporary, privateMode);
        if (handle.IsInvalid) fail("config_file_unverified");
        var committed = false;
        FileStream? stream = null;
        try
        {
            if (!LinuxNative.Chmod(handle, mode)) fail("config_save_unverified"); // not subject to the umask
            stream = new FileStream(handle, FileAccess.Write, 1, false);
            stream.Write(bytes);
            stream.Flush(true); // fsync before the rename publishes the bytes
            // One lazer session owns the shared preferences. This is optimistic conflict
            // detection, not compare-and-swap: an external replacement after this check is unsupported.
            using (var latest = readLeaseLinux(directory, filename, limit))
                if (latest.Exists != existed || !latest.Bytes.AsSpan().SequenceEqual(expected)) fail("config_file_changed");
            if (!LinuxNative.Rename(directory, temporary, filename)) fail("config_save_unverified");
            committed = true;
            _ = LinuxNative.Sync(directory); // best effort: persist the directory entry
        }
        finally
        {
            if (!committed) LinuxNative.Unlink(directory, temporary);
            stream?.Dispose();
        }
    }
}
