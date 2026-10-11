namespace Illyaverse.Transfiguration;

// Exact stock-client identity per platform. Each platform ships its own build of the same lazer
// release, so every hash is pinned separately; nothing is shared or inferred between platforms.
// The Windows values are unchanged. The Linux values belong to the osulazer 2026.1005.1-tachyon
// linux-x64 AppImage (usr/bin) and equal the constants compiled into Kaleidoscope.
internal sealed record ClientProfile(string Platform, string GameSha256, string FrameworkSha256, string EntrySha256,
    string HttpSha256, string WebSocketsSha256)
{
    internal const string GameVersion = "2026.1005.1.0", FrameworkVersion = "2026.1005.0.0", EntryVersion = "2026.1005.1.0", NetworkVersion = "10.0.0.0";

    internal static readonly ClientProfile Windows = new("windows-x64",
        "AD9527E411111CB4B4045C76DF61C3474FD872A04AF73160C05D24E44A588249",
        "93B62F61C72539ED6FDB3C8EBA18D276A74DE8E1C094280B2531F5A76CAEA528",
        "18AE9BB6BB441E9A654AC72C859F527041BDE52E5DCE4109F8D9074584FA57A4",
        "B98ED0CB80DD54C33FC049F33AF5448710752155BBD5CC8CBCD932E01F066801",
        "F308A69B9EE1AC982F53D1D94374F4972A7E3C700742901FF7C2BBFB570019D2");

    internal static readonly ClientProfile Linux = new("linux-x64",
        "D2B1B9D93801B22465252EC95A60FD398F6AAD6EF117CD5ED9846BC9922BCE86",
        "3D49CA53B034FD93010456D3B7156D9E7FD2DC661181AA61BF55B307AB4D6280",
        "1A03472241C2B08E62E91BB235CE4BBF7706E611DA29B3869F4C9033BDB1AFC2",
        "E8BEEB63DB39E7D540AA711C701860C5DDB8826F2BFFF62AF2ABFA74EAA7A055",
        "F0AE347894788710AEC8B5A03BB42A00CCC1520DFB42E34350CBBE7D5D619235");

    internal static ClientProfile Current => OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 ? Linux
        : throw SafeStartupDiagnostic.Rejection("client_version_unverified");
}
