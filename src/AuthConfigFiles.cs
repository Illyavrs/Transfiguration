using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Illyaverse.Transfiguration;

// Only account values live in this file. The game still reads and saves its
// original preferences, through the original parser and serializer.
internal static partial class AuthConfigFiles
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

    // Windows keeps the original handle/NT-rename implementation untouched; Linux uses descriptors
    // relative to verified directories. Both refuse links, bound reads and replace atomically.
    internal static byte[] Read(string root, string filename, int limit = MaximumSharedBytes) =>
        OperatingSystem.IsWindows() ? readWindows(root, filename, limit)
        : OperatingSystem.IsLinux() ? readLinux(root, filename, limit)
        : throw unsupportedPlatform();

    internal static void Write(string root, string filename, byte[] bytes, byte[] expected)
    {
        if (OperatingSystem.IsWindows()) writeWindows(root, filename, bytes, expected);
        else if (OperatingSystem.IsLinux()) writeLinux(root, filename, bytes, expected);
        else throw unsupportedPlatform();
    }

    internal static IDisposable LockDirectories(string path) =>
        OperatingSystem.IsWindows() ? lockDirectories(path)
        : OperatingSystem.IsLinux() ? lockDirectoriesLinux(path)
        : throw unsupportedPlatform();

    private static int limitFor(string filename) => filename == Filename ? maximumAuthBytes : MaximumSharedBytes;
    private static InvalidOperationException unsupportedPlatform() => new("transfiguration_config_storage_unverified");

    [DoesNotReturn]
    private static void fail(string reason) => throw new InvalidOperationException("transfiguration_" + reason);
}
