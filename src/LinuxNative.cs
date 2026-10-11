using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Illyaverse.Transfiguration;

// Minimal libc surface for the Linux file guards. Every call is relative to an
// already-verified directory descriptor, so a path swapped after verification
// can never redirect it. Nothing here is touched on Windows: the declarations
// are resolved lazily by the runtime on first call only.
internal static class LinuxNative
{
    internal const int ENOENT = 2, EEXIST = 17, EINVAL = 22, ENOSYS = 38;
    private const int O_RDONLY = 0, O_WRONLY = 1, O_CREAT = 0x40, O_EXCL = 0x80, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const uint RENAME_NOREPLACE = 1;
    internal const uint FileTypeMask = 0xF000, RegularFile = 0x8000, Directory = 0x4000;

    // O_DIRECTORY and O_NOFOLLOW are architecture-specific. Only the two
    // architectures that were inspected are accepted; anything else fails closed.
    private static int directoryFlag => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x10000, Architecture.Arm64 => 0x4000, _ => throw unsupported()
    };
    private static int noFollowFlag => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x20000, Architecture.Arm64 => 0x8000, _ => throw unsupported()
    };
    private static InvalidOperationException unsupported() => new("transfiguration_config_storage_unverified");

    internal readonly record struct FileKind(uint Mode, ulong Size)
    {
        internal bool IsRegular => (Mode & FileTypeMask) == RegularFile;
        internal bool IsDirectory => (Mode & FileTypeMask) == Directory;
        internal uint Permissions => Mode & 0xFFF;
    }

    // Opens "/" or a directory below an open directory without following a symlink.
    internal static SafeFileHandle OpenDirectory(SafeFileHandle? parent, string name)
    {
        var flags = O_RDONLY | O_CLOEXEC | directoryFlag | (parent is null ? 0 : noFollowFlag);
        var descriptor = parent is null ? open(name, flags, 0) : openat(Descriptor(parent), name, flags, 0);
        return Wrap(descriptor);
    }

    // Opens every ancestor of an absolute canonical path from "/" downward, never following a symlink, and
    // returns the descriptors in order (the last one is the target directory). Throws IOException otherwise.
    internal static List<SafeFileHandle> OpenDirectoryChain(string canonicalPath)
    {
        var chain = new List<SafeFileHandle>();
        try
        {
            var current = OpenDirectory(null, "/");
            if (current.IsInvalid) { current.Dispose(); throw new IOException(); }
            chain.Add(current);
            foreach (var segment in canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                // O_NOFOLLOW rejects a symlink ancestor (ELOOP/ENOTDIR); O_DIRECTORY rejects non-directories.
                var next = OpenDirectory(current, segment);
                if (next.IsInvalid) { next.Dispose(); throw new IOException(); }
                chain.Add(next);
                if (!TryStat(next, out var kind) || !kind.IsDirectory) throw new IOException();
                current = next;
            }
            return chain;
        }
        catch { foreach (var handle in chain) handle.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenRegularForRead(SafeFileHandle directory, string name) =>
        Wrap(openat(Descriptor(directory), name, O_RDONLY | O_CLOEXEC | O_NONBLOCK | noFollowFlag, 0));

    internal static SafeFileHandle CreateExclusive(SafeFileHandle directory, string name, uint mode) =>
        Wrap(openat(Descriptor(directory), name, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | noFollowFlag, mode));

    // The handle's last OS error is preserved as errno for the caller.
    private static SafeFileHandle Wrap(int descriptor)
    {
        var error = Marshal.GetLastPInvokeError();
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        if (descriptor < 0) { handle.SetHandleAsInvalid(); Marshal.SetLastPInvokeError(error); }
        return handle;
    }

    internal static int Descriptor(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    internal static int LastError => Marshal.GetLastPInvokeError();

    internal static bool TryStat(SafeFileHandle handle, out FileKind kind)
    {
        var buffer = new byte[256];
        kind = default;
        if (statx(Descriptor(handle), string.Empty, AT_EMPTY_PATH, STATX_BASIC_STATS, buffer) != 0) return false;
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
        // STATX_TYPE | STATX_MODE | STATX_SIZE must all have been filled in.
        if ((mask & 0x203) != 0x203) return false;
        kind = new FileKind(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28, 2)), BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(40, 8)));
        return true;
    }

    internal static bool Chmod(SafeFileHandle handle, uint mode) => fchmod(Descriptor(handle), mode) == 0;
    internal static bool Sync(SafeFileHandle handle) => fsync(Descriptor(handle)) == 0;
    internal static bool Rename(SafeFileHandle directory, string from, string to) =>
        renameat(Descriptor(directory), from, Descriptor(directory), to) == 0;
    internal static void Unlink(SafeFileHandle directory, string name) => _ = unlinkat(Descriptor(directory), name, 0);

    // Atomic create-only publication: the destination is never replaced.
    internal static bool RenameNoReplace(SafeFileHandle directory, string from, string to)
    {
        var descriptor = Descriptor(directory);
        if (renameat2(descriptor, from, descriptor, to, RENAME_NOREPLACE) == 0) return true;
        var error = LastError;
        if (error is not (EINVAL or ENOSYS)) return false;
        // Filesystem or libc without RENAME_NOREPLACE: link() also refuses to replace.
        if (linkat(descriptor, from, descriptor, to, 0) != 0) return false;
        _ = unlinkat(descriptor, from, 0);
        return true;
    }

    internal static string? RealPath(string path)
    {
        var buffer = new byte[4097];
        var result = realpath(path, buffer);
        if (result == IntPtr.Zero) return null;
        var length = Array.IndexOf(buffer, (byte)0);
        return length < 0 ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    private const string libc = "libc";
    [DllImport(libc, SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport(libc, SetLastError = true)]
    private static extern int openat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport(libc, SetLastError = true)]
    private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);
    [DllImport(libc, SetLastError = true)]
    private static extern int fchmod(int descriptor, uint mode);
    [DllImport(libc, SetLastError = true)]
    private static extern int fsync(int descriptor);
    [DllImport(libc, SetLastError = true)]
    private static extern int renameat(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
    [DllImport(libc, SetLastError = true)]
    private static extern int renameat2(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    [DllImport(libc, SetLastError = true)]
    private static extern int linkat(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, int flags);
    [DllImport(libc, SetLastError = true)]
    private static extern int unlinkat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport(libc, SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] resolved);
}
