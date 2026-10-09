using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;

namespace Illyaverse.Transfiguration;

internal static class NetworkPolicy
{
    internal const string EndpointEnvironmentName = "ILLYAVERSE_LAZER_SERVER_ENDPOINT";
    public const string ClientId = "illyaverse-local-client";
    public const string ClientSecret = "illyaverse-public-test-client";
#if ILLYAVERSE_FIXTURE
    private const string fixtureOrigin = "http://127.0.0.1:18376";
    public static string Website => fixtureOrigin;
    internal static string? DiagnosticEndpoint => fixtureOrigin;
#else
    private static readonly object endpointGate = new();
    private static DeploymentOrigin? endpoint;
    public static string Website => Volatile.Read(ref endpoint)?.Origin
        ?? throw SafeStartupDiagnostic.Rejection("server_endpoint_missing");
    internal static string? DiagnosticEndpoint => Volatile.Read(ref endpoint)?.Origin;
#endif
    public static string Api => Website;
    public static string Hubs => Website;

    // A launch selects one deployment. Later settings or environment changes
    // cannot redirect the authenticated client to another origin.
    internal static void Initialize()
    {
#if !ILLYAVERSE_FIXTURE
        lock (endpointGate)
        {
            if (endpoint is not null) return;
            var supplied = Environment.GetEnvironmentVariable(EndpointEnvironmentName);
            if (string.IsNullOrEmpty(supplied)) throw SafeStartupDiagnostic.Rejection("server_endpoint_missing");
            var selected = DeploymentOrigin.Parse(supplied);
            Volatile.Write(ref endpoint, selected);
        }
#endif
    }

    internal static bool Allows(Uri? uri, bool socket = false)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
#if ILLYAVERSE_FIXTURE
        return uri.Host == "127.0.0.1" && uri.Port == 18376 && uri.Scheme == (socket ? "ws" : "http");
#else
        return Volatile.Read(ref endpoint)?.Allows(uri, socket) == true;
#endif
    }

    internal static void Require(Uri? uri, bool socket = false)
    {
        if (!Allows(uri, socket)) throw new HttpRequestException("transfiguration_destination_blocked");
    }

    // ClientWebSocket uses SocketsHttpHandler with the original ws/wss request URI.
    internal static void HttpSendPrefix(SocketsHttpHandler __instance, HttpRequestMessage request)
    {
        if (__instance.AllowAutoRedirect) throw new HttpRequestException("transfiguration_redirect_guard_invalid");
        Require(request.RequestUri, socket: request.RequestUri?.Scheme is "ws" or "wss");
    }
    internal static void SocketConnectPrefix(Uri uri) => Require(uri, socket: true);
    internal static void SocketsHandlerPostfix(SocketsHttpHandler __instance) => __instance.AllowAutoRedirect = false;
    internal static void HttpHandlerPostfix(HttpClientHandler __instance) => __instance.AllowAutoRedirect = false;

    internal static bool ResourcePrefix(string url, ref string __result)
    {
        __result = Uri.TryCreate(url, UriKind.Absolute, out var uri) && Allows(uri) ? url : string.Empty;
        return false;
    }
}

internal sealed class DeploymentOrigin
{
    private static readonly string[] deploymentPrefixes = ["osu", "spectator", "a", "b", "api", "bss", "c", "ce", "c4"];
    private readonly HashSet<string> hosts;
    internal string Origin { get; }
    internal string Host { get; }
    internal int Port { get; }

    private DeploymentOrigin(string host, int port)
    {
        Host = host;
        Port = port;
        Origin = "https://" + host + (port == 443 ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture));
        hosts = new(StringComparer.OrdinalIgnoreCase) { host };
        foreach (var prefix in deploymentPrefixes) hosts.Add(prefix + "." + host);
    }

    internal static DeploymentOrigin Parse(string value)
    {
        // Inspect the spelling before normalization: /. and escaped hosts must
        // not silently become an acceptable root or a different destination.
        if (value.Length is 0 or > 2048 || value.Any(c => c > 0x7f || char.IsWhiteSpace(c) || char.IsControl(c))
            || value.IndexOfAny(['\\', '%', '@', '?', '#']) >= 0 || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return reject();
        var authority = value[8..];
        if (authority.EndsWith('/')) authority = authority[..^1];
        if (authority.Length == 0 || authority.Contains('/')) return reject();
        var separator = authority.IndexOf(':');
        var rawHost = separator < 0 ? authority : authority[..separator];
        var port = 443;
        if (separator >= 0)
        {
            var suppliedPort = authority[(separator + 1)..];
            if (suppliedPort.Length == 0 || suppliedPort.Any(c => c is < '0' or > '9')
                || !int.TryParse(suppliedPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535) return reject();
        }
        if (rawHost.Length is 0 or > 253 || rawHost.EndsWith('.') || !rawHost.Contains('.')) return reject();
        foreach (var label in rawHost.Split('.'))
            if (label.Length is 0 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])
                || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return reject();
        var host = rawHost.ToLowerInvariant();
        if (host == "ppy.sh" || host.EndsWith(".ppy.sh", StringComparison.Ordinal)) return reject();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns
            || uri.Scheme != "https" || uri.Host != host || uri.Port != port || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") return reject();
        return new(host, port);
    }

    internal bool Allows(Uri uri, bool socket = false) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Scheme == (socket ? "wss" : "https") && uri.Port == Port && hosts.Contains(uri.Host);

    private static DeploymentOrigin reject() => throw SafeStartupDiagnostic.Rejection("server_endpoint_invalid");
}
