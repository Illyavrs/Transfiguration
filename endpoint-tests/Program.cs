using System.Net.Http;
using Illyaverse.Transfiguration;

internal static class Program
{
    private static int checks;
    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("endpoint_fixture_failed");
        checks++;
        Console.WriteLine("PASS " + name);
    }

    private static void rejected(Action action, string code, string name)
    {
        try { action(); }
        catch (TransfigurationStartupException exception) when (exception.Code == code) { require(true, name); return; }
        throw new InvalidOperationException("endpoint_fixture_rejection_missing");
    }

    private static void blocked(Action action, string name)
    {
        try { action(); }
        catch (HttpRequestException exception) when (exception.Message == "transfiguration_destination_blocked") { require(true, name); return; }
        throw new InvalidOperationException("endpoint_fixture_destination_not_blocked");
    }

    public static int Main()
    {
        var previous = Environment.GetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName);
        try
        {
            foreach (var (supplied, expected, port) in new (string, string, int)[]
            {
                ("https://illya.test", "https://illya.test", 443),
                ("HTTPS://ILLYA.TEST:443/", "https://illya.test", 443),
                ("https://play.example.test:8443/", "https://play.example.test:8443", 8443),
                ("https://play.example.test:00443", "https://play.example.test", 443),
                ("https://xn--r8jz45g.example/", "https://xn--r8jz45g.example", 443),
                ("https://" + new string('a', 63) + ".test:65535", "https://" + new string('a', 63) + ".test:65535", 65535),
            })
            {
                var parsed = DeploymentOrigin.Parse(supplied);
                require(parsed.Origin == expected && parsed.Port == port, "accepted deployment origin has canonical host, root and port");
                require(parsed.Allows(new Uri(parsed.Origin + "/oauth/token")), "selected origin accepts its OAuth path");
                foreach (var prefix in new[] { "osu", "spectator", "a", "b", "api", "bss", "c", "ce", "c4" })
                {
                    var authority = prefix + "." + parsed.Host + (port == 443 ? "" : ":" + port);
                    require(parsed.Allows(new Uri("https://" + authority + "/asset")), "known deployment subdomain preserves the selected TLS port");
                    require(parsed.Allows(new Uri("wss://" + authority + "/metadata"), true), "known deployment websocket preserves the selected TLS port");
                }
            }

            foreach (var value in new[]
            {
                "", "illya.test", "//illya.test", "http://illya.test", "wss://illya.test", "https://localhost", "https://127.0.0.1",
                "https://127.1", "https://2130706433", "https://0x7f000001", "https://[::1]", "https://[::1]:8443", "https://例え.jp",
                "https://illya.test.", "https://.illya.test", "https://illya..test", "https://-illya.test", "https://illya-.test", "https://illya_test.test",
                "https://illya.test:", "https://illya.test:0", "https://illya.test:65536", "https://illya.test:-1", "https://illya.test:+443",
                "https://user:secret@illya.test", "https://@illya.test", "https://illya.test?", "https://illya.test#", "https://illya.test/path",
                "https://illya.test//", "https://illya.test/.", "https://illya.test/..", "https://%69llya.test", "https://illya.test\\other",
                " https://illya.test", "https://illya.test ", "https://illya.test\n", "https://ill\nya.test", "https://ppy.sh", "https://osu.ppy.sh",
                "https://Spectator.OSU.PPY.SH:443/", "https://a.ppy.sh", "https://ppy.sh.", "https://ppy.sh:8443", "https://osu.ppy.sh.evil@illya.test",
                "https://" + new string('a', 64) + ".test", "https://" + new string('a', 2049),
            }) rejected(() => DeploymentOrigin.Parse(value), "server_endpoint_invalid", "unsafe, non-DNS or official deployment origin is rejected before traffic");

            var isolated = DeploymentOrigin.Parse("https://play.example.test:8443");
            foreach (var value in new[]
            {
                "https://play.example.test", "https://play.example.test:8444", "https://play.example.test.evil.invalid:8443",
                "https://evilplay.example.test:8443", "https://other.example.test:8443", "https://foo.play.example.test:8443",
                "https://play.example.test.:8443", "https://user:secret@play.example.test:8443", "https://play.example.test:8443/#fragment",
                "http://play.example.test:8443", "https://osu.ppy.sh:8443", "https://illya.test:8443",
            }) require(!isolated.Allows(new Uri(value)), "destination outside the captured deployment and exact port stays blocked");
            require(!isolated.Allows(new Uri("ws://play.example.test:8443/socket"), true), "insecure websocket never falls back");
            require(!isolated.Allows(new Uri("https://play.example.test:8443/socket"), true), "websocket requires wss");
            require(!isolated.Allows(new Uri("wss://play.example.test:8443/socket")), "HTTP requires https");

            require(!NetworkPolicy.Allows(new Uri("https://illya.test/oauth/token")), "uninitialized production policy permits no request");
            require(!ExternalBrowserPolicy.TryResolve("https://illya.test/beatmapsets/1#osu/1", out _), "uninitialized browser policy launches no destination");
            Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, null);
            rejected(NetworkPolicy.Initialize, "server_endpoint_missing", "missing origin rejects without an implicit default");
            Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, "https://osu.ppy.sh");
            rejected(NetworkPolicy.Initialize, "server_endpoint_invalid", "official environment origin rejects without fallback");
            require(NetworkPolicy.DiagnosticEndpoint is null, "rejected endpoint is never echoed into safe diagnostics");
            Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, "HTTPS://PLAY.EXAMPLE.TEST:8443/");
            NetworkPolicy.Initialize();
            require(NetworkPolicy.Website == "https://play.example.test:8443" && NetworkPolicy.Api == NetworkPolicy.Website
                && NetworkPolicy.Hubs == NetworkPolicy.Website, "API, website and hubs share one immutable deployment origin");
            Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, "https://other.example.test");
            NetworkPolicy.Initialize();
            require(NetworkPolicy.Website == "https://play.example.test:8443"
                && !NetworkPolicy.Allows(new Uri("https://other.example.test/oauth/token")), "environment changes cannot redirect a launched account");
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var official = new HttpRequestMessage(HttpMethod.Get, "https://osu.ppy.sh/oauth/token");
            blocked(() => NetworkPolicy.HttpSendPrefix(handler, official), "official HTTP rejects synchronously before any send");
            blocked(() => NetworkPolicy.SocketConnectPrefix(new Uri("wss://spectator.osu.ppy.sh/socket")), "official websocket rejects before any connect");
            using var enabledRedirect = new SocketsHttpHandler { AllowAutoRedirect = true };
            using var localRequest = new HttpRequestMessage(HttpMethod.Get, NetworkPolicy.Api + "/oauth/token");
            try { NetworkPolicy.HttpSendPrefix(enabledRedirect, localRequest); throw new InvalidOperationException(); }
            catch (HttpRequestException exception) when (exception.Message == "transfiguration_redirect_guard_invalid")
            { require(true, "re-enabled automatic redirects reject even allowed origins"); }
            var resource = "unchanged";
            require(!NetworkPolicy.ResourcePrefix("https://osu.ppy.sh/asset", ref resource) && resource == "", "official image destination is suppressed before lookup");
            require(!NetworkPolicy.ResourcePrefix(NetworkPolicy.Website + "/media/fixture", ref resource) && resource == NetworkPolicy.Website + "/media/fixture",
                "selected deployment resource stays available");
            browserLinks();
            Console.WriteLine($"PASS {checks} source-linked production endpoint policy checks; no client, player data or network operations");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL endpoint_policy_fixture " + exception.GetType().Name); return 1; }
        finally { Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, previous); }
    }

    private static void browserLinks()
    {
        var root = "https://play.example.test:8443";
        foreach (var suffix in new[] { "", "#osu", "#taiko", "#fruits", "#mania", "#osu/5914819", "#taiko/3", "#fruits/4", "#mania/5" })
        {
            var source = root + "/beatmapsets/2630282" + suffix;
            require(ExternalBrowserPolicy.TryResolve(source, out var resolved) && resolved == source,
                "browser keeps a canonical official set and native selection fragment");
        }
        foreach (var id in new[] { "2000000000", "2147483647" })
        {
            var canonical = root + "/beatmapsets/local-" + id + "#osu/2000000001";
            require(ExternalBrowserPolicy.TryResolve(root + "/beatmapsets/" + id + "#osu/2000000001", out var resolved) && resolved == canonical,
                "browser canonicalizes native local set IDs while keeping difficulty selection");
            require(ExternalBrowserPolicy.TryResolve(canonical, out resolved) && resolved == canonical,
                "canonical local URL stays stable on repeated resolution");
        }
        foreach (var suffix in new[] { "/users/2", "/users/2#activity", "/scores/7", "/b/5914819", "/?tab=maps", "/connection#lazer" })
            require(ExternalBrowserPolicy.TryResolve(root + suffix, out var resolved) && resolved == root + suffix,
                "ordinary private website navigation remains available");
        foreach (var source in new string?[]
        {
            null, "", "relative/path", "//play.example.test:8443/beatmapsets/1#osu/1", "javascript:alert(1)", "file:///C:/fixture",
            "https://osu.ppy.sh/beatmapsets/1#osu/1", "https://play.example.test/beatmapsets/1#osu/1",
            "https://api.play.example.test:8443/beatmapsets/1#osu/1", "https://play.example.test.evil.invalid:8443/",
            "https://user:secret@play.example.test:8443/", "http://play.example.test:8443/", "https://play.example.test:8444/",
            root + "/beatmapsets/0#osu/1", root + "/beatmapsets/01#osu/1", root + "/beatmapsets/2147483648#osu/1",
            root + "/beatmapsets/local-1#osu/1", root + "/beatmapsets/1#catch/1", root + "/beatmapsets/1#osu/0",
            root + "/beatmapsets/1#osu/1/extra", root + "/beatmapsets/1#osu/", root + "/beatmapsets/1/extra#osu/1",
            root + "/../beatmapsets/1#osu/1", root + "/%2e%2e/beatmapsets/1#osu/1", root + "/beatmapsets%2f1#osu/1",
            root + "/users\\2", root + "/users/2\n", root + "/users/ 2", root + "/" + new string('a', 4096),
        }) require(!ExternalBrowserPolicy.TryResolve(source, out var resolved) && resolved == string.Empty,
            "unsafe browser input is suppressed without throwing or an official fallback");

        var browserAnchor = root + "/beatmapsets/2630282#osu/5914819";
        require(!NetworkPolicy.Allows(new Uri(browserAnchor)), "browser fragment permission does not broaden HTTP destinations");
        blocked(() => NetworkPolicy.Require(new Uri("https://osu.ppy.sh/oauth/token")), "official authentication remains blocked after resolving browser links");
        blocked(() => NetworkPolicy.SocketConnectPrefix(new Uri("wss://osu.ppy.sh/socket")), "official websocket remains blocked after resolving browser links");
    }
}

// The diagnostic source references only this assembly identity. This fixture
// never calls its writer, loads a startup hook or creates any client object.
internal static class StartupHook { }
