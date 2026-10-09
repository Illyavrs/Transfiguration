using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Illyaverse.Transfiguration;

// Only account values live in this file. The game still reads and saves its
// original preferences, through the original parser and serializer.
internal static class AuthConfigFiles
{
    internal const string Filename = "auth.illyaverse.json";
    internal const int MaximumSharedBytes = 1024 * 1024;
    private const int maximumAuthBytes = 64 * 1024;
    private static readonly UTF8Encoding utf8 = new(false, true);
    internal static readonly string[] Keys = ["Username", "Token", "SaveUsername", "SavePassword", "WasSupporter", "UserOnlineStatus", "PMFriendsOnly"];
    internal sealed record Account(string Username, string Token, bool SaveUsername, bool SavePassword,
        bool WasSupporter, string UserOnlineStatus, bool PMFriendsOnly)
    {
        internal static Account Empty => new(string.Empty, string.Empty, true, true, false, "Online", false);
        internal string Value(string key) => key switch
        {
            "Username" => Username, "Token" => Token,
            "SaveUsername" => SaveUsername.ToString(), "SavePassword" => SavePassword.ToString(),
            "WasSupporter" => WasSupporter.ToString(), "UserOnlineStatus" => UserOnlineStatus, "PMFriendsOnly" => PMFriendsOnly.ToString(),
            _ => throw new InvalidOperationException("transfiguration_config_key_unverified")
        };
    }
    internal sealed record Shared(byte[] Bytes, byte[] Preferences, IReadOnlyList<byte[]> AccountLines, bool Bom);

    internal static Shared Partition(byte[] bytes, Func<string, string?> accountKey)
    {
        if (bytes.Length > MaximumSharedBytes) fail("config_file_too_large");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })
            || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) || bytes.Contains((byte)0))
            fail("config_encoding_unverified");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var offset = bom ? 3 : 0;
        try { _ = utf8.GetString(bytes.AsSpan(offset)); }
        catch (DecoderFallbackException) { fail("config_encoding_unverified"); }
        var lines = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var preferences = new MemoryStream();
        if (bom) preferences.Write(bytes.AsSpan(0, 3));
        while (offset < bytes.Length)
        {
            var end = offset;
            while (end < bytes.Length && bytes[end] is not 10 and not 13) end++;
            var next = end;
            if (next < bytes.Length && bytes[next] == 13) next++;
            if (next < bytes.Length && bytes[next] == 10) next++;
            var line = utf8.GetString(bytes.AsSpan(offset, end - offset));
            string? key = null;
            var equals = line.IndexOf('=');
            if (line.Length > 0 && line[0] != '#' && equals >= 0)
                key = accountKey(line[..equals].Trim());
            if (key is not null)
            {
                if (!seen.Add(key)) fail("config_auth_ambiguous");
                lines.Add(bytes.AsSpan(offset, next - offset).ToArray());
            }
            else preferences.Write(bytes.AsSpan(offset, next - offset));
            offset = next;
        }
        return new Shared(bytes, preferences.ToArray(), lines, bom);
    }

    internal static byte[] ForLoad(Shared shared, Account account)
    {
        using var stream = new MemoryStream();
        stream.Write(shared.Preferences);
        terminateLine(stream);
        foreach (var key in Keys) stream.Write(utf8.GetBytes(key + " = " + account.Value(key) + "\n"));
        return stream.ToArray();
    }

    internal static byte[] ForSave(byte[] serialized, Shared shared, Func<string, string?> accountKey)
    {
        var preferences = Partition(serialized, accountKey);
        using var stream = new MemoryStream();
        if (shared.Bom) stream.Write(new byte[] { 0xef, 0xbb, 0xbf });
        stream.Write(preferences.Preferences.AsSpan(preferences.Bom ? 3 : 0));
        terminateLine(stream);
        foreach (var line in shared.AccountLines) stream.Write(line);
        return stream.ToArray();
    }

    private static void terminateLine(MemoryStream stream)
    {
        if (stream.Length > 0 && stream.GetBuffer()[stream.Length - 1] is not 10 and not 13) stream.WriteByte(10);
    }

    internal static Account ReadAccount(string root, string endpoint) => ReadProfiles(root).GetValueOrDefault(endpoint) ?? Account.Empty;

    private static Dictionary<string, Account> ReadProfiles(string root)
    {
        var bytes = Read(root, Filename, maximumAuthBytes);
        if (bytes.Length == 0)
        {
            // An absent store is a new private session; an existing empty file is malformed.
            if (File.Exists(Path.Combine(root, Filename))) fail("config_auth_invalid");
            return new(StringComparer.Ordinal);
        }
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) fail("config_auth_invalid");
            requireProperties(json.RootElement, ["protocol", "profiles"]);
            if (json.RootElement.GetProperty("protocol").GetInt32() != 1) fail("config_auth_invalid");
            var profiles = json.RootElement.GetProperty("profiles");
            if (profiles.ValueKind != JsonValueKind.Object || profiles.EnumerateObject().Count() > 16) fail("config_auth_invalid");
            var accounts = new Dictionary<string, Account>(StringComparer.Ordinal);
            foreach (var profile in profiles.EnumerateObject())
            {
                requireEndpoint(profile.Name);
                requireProperties(profile.Value, ["username", "token", "saveUsername", "savePassword", "wasSupporter", "userOnlineStatus", "pmFriendsOnly"]);
                var account = new Account(profile.Value.GetProperty("username").GetString()!, profile.Value.GetProperty("token").GetString()!,
                    profile.Value.GetProperty("saveUsername").GetBoolean(), profile.Value.GetProperty("savePassword").GetBoolean(),
                    profile.Value.GetProperty("wasSupporter").GetBoolean(), profile.Value.GetProperty("userOnlineStatus").GetString()!,
                    profile.Value.GetProperty("pmFriendsOnly").GetBoolean());
                Validate(account);
                if (!accounts.TryAdd(profile.Name, account)) fail("config_auth_invalid");
            }
            return accounts;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            fail("config_auth_invalid");
            return null!;
        }
    }

    private static void requireProperties(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) fail("config_auth_invalid");
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != names.Length || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length
            || properties.Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) fail("config_auth_invalid");
    }

    private static void requireEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https"
#if ILLYAVERSE_FIXTURE
                && !(uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == 18376)
#endif
            )
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.GetLeftPart(UriPartial.Authority) != endpoint) fail("config_auth_invalid");
    }

    internal static void Validate(Account account)
    {
        if (account.Username is null || account.Token is null || account.UserOnlineStatus is null || account.Username.Length > 128 || account.Token.Length > 16384
            || account.Username.Any(char.IsControl) || account.Token.Any(char.IsControl)
            || account.UserOnlineStatus is not ("Online" or "DoNotDisturb" or "Offline")
            || (!account.SaveUsername && account.SavePassword)
            || (!account.SaveUsername && account.Username.Length != 0) || (!account.SavePassword && account.Token.Length != 0))
            fail("config_auth_invalid");
        if (account.Token.Length != 0)
        {
            var parts = account.Token.Split('|');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0
                || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                fail("config_auth_invalid");
        }
    }

    internal static byte[] Encode(string root, string endpoint, Account account)
    {
        Validate(account);
        requireEndpoint(endpoint);
        var accounts = ReadProfiles(root);
        accounts[endpoint] = account;
        if (accounts.Count > 16) fail("config_auth_invalid");
        var profiles = accounts.ToDictionary(pair => pair.Key, pair => new
        {
            username = pair.Value.Username, token = pair.Value.Token,
            saveUsername = pair.Value.SaveUsername, savePassword = pair.Value.SavePassword,
            wasSupporter = pair.Value.WasSupporter, userOnlineStatus = pair.Value.UserOnlineStatus, pmFriendsOnly = pair.Value.PMFriendsOnly
        }, StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { protocol = 1, profiles });
        if (bytes.Length > maximumAuthBytes) fail("config_file_too_large");
        return bytes;
    }

    internal static byte[] Read(string root, string filename, int limit = MaximumSharedBytes)
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

    internal static void Write(string root, string filename, byte[] bytes, byte[] expected)
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

    internal static IDisposable LockDirectories(string path) => lockDirectories(path);

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

    [DoesNotReturn]
    private static void fail(string reason) => throw new InvalidOperationException("transfiguration_" + reason);

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
