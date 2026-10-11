using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Illyaverse.Transfiguration;

// Windows implementation: handle-based, reparse-point refusing, atomic NT rename.
internal static partial class AuthConfigFiles
{
    private static byte[] readWindows(string root, string filename, int limit)
    {
        using var directories = lockDirectories(root);
        using var lease = readLease(root, filename, limit);
        return lease.Bytes;
    }

    private sealed class ReadLease(FileStream? stream, byte[] bytes) : IDisposable
    {
        internal readonly byte[] Bytes = bytes;
        internal bool Exists => stream is not null;
        public void Dispose() => stream?.Dispose();
    }

    private static ReadLease readLease(string root, string filename, int limit)
    {
        var path = Path.Combine(root, filename);
        var handle = CreateFile(path, 0x80000000, 0x1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 2) return new ReadLease(null, []);
            fail("config_file_unverified");
        }
        FileStream? stream = null;
        try
        {
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                fail("config_file_unverified");
            stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length > limit) fail("config_file_too_large");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return new ReadLease(stream, bytes);
        }
        catch { stream?.Dispose(); handle.Dispose(); throw; }
    }

    private static void writeWindows(string root, string filename, byte[] bytes, byte[] expected)
    {
        using var directories = lockDirectories(root);
        bool existed;
        using (var destination = readLease(root, filename, filename == Filename ? maximumAuthBytes : MaximumSharedBytes))
        {
            if (!destination.Bytes.AsSpan().SequenceEqual(expected)) fail("config_file_changed");
            if (destination.Bytes.AsSpan().SequenceEqual(bytes)) return;
            existed = destination.Exists;
        }
        var temporary = Path.Combine(root, ".illyaverse-auth-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var handle = CreateFile(temporary, 0x40000000 | 0x10000, 0, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) fail("config_file_unverified");
        var committed = false;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(handle, FileAccess.Write);
            stream.Write(bytes);
            stream.Flush(true);
            // One lazer session owns the shared preferences. This is optimistic
            // conflict detection, not compare-and-swap against another process:
            // an external replacement after this check is unsupported.
            using (var latest = readLease(root, filename, filename == Filename ? maximumAuthBytes : MaximumSharedBytes))
                if (latest.Exists != existed || !latest.Bytes.AsSpan().SequenceEqual(expected)) fail("config_file_changed");
            rename(handle, directories.Values[^1], filename, existed);
            committed = true;
        }
        finally
        {
            if (!committed) delete(handle);
            stream?.Dispose();
        }
    }

    private static Handles lockDirectories(string path)
    {
        var handles = new Handles();
        try
        {
            var paths = new Stack<string>();
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent) paths.Push(current.FullName);
            foreach (var directory in paths)
            {
                var handle = CreateFile(directory, 0x80, 0x1, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); fail("config_storage_unverified"); }
                handles.Values.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.Directory) == 0
                    || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) fail("config_storage_unverified");
            }
            return handles;
        }
        catch { handles.Dispose(); throw; }
    }

    private sealed class Handles : IDisposable
    {
        internal readonly List<SafeFileHandle> Values = new();
        public void Dispose() { foreach (var value in Values) value.Dispose(); }
    }

    private static void rename(SafeFileHandle handle, SafeFileHandle directory, string filename, bool replacing)
    {
        var encoded = Encoding.Unicode.GetBytes(filename);
        var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Filename)).ToInt32();
        var size = Marshal.SizeOf<RenameInformation>() + encoded.Length;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(memory, i, 0);
            Marshal.WriteByte(memory, replacing ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(memory, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.RootDirectory)).ToInt32(), directory.DangerousGetHandle());
            Marshal.WriteInt32(memory, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FilenameLength)).ToInt32(), encoded.Length);
            Marshal.Copy(encoded, 0, memory + offset, encoded.Length);
            // Atomic same-directory NT FileRenameInformation=10. Resolve against
            // the verified root handle and never follow a destination symlink.
            if (NtSetInformationFile(handle, out _, memory, (uint)size, 10) < 0) fail("config_save_unverified");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void delete(SafeFileHandle handle)
    {
        var memory = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(memory, 1); _ = SetFileInformationByHandle(handle, 4, memory, 1); }
        finally { Marshal.FreeHGlobal(memory); }
    }

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
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr information, uint length, uint informationClass);
}
