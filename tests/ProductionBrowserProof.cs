using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;

// This mode precedes fixture bootstrap in Program. It exercises the compiled
// production policy using synthetic values and never initializes StartupHook.
internal static class ProductionBrowserProof
{
    private static int checks;

    internal static int Run(string productionPath)
    {
        if (Environment.Version != new Version(10, 0, 12)) throw new InvalidOperationException("Production browser proof requires the verified runtime");
        if (!Path.IsPathFullyQualified(productionPath) || !File.Exists(productionPath)) throw new ArgumentException("An absolute production bridge path is required");
        var bridge = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(productionPath));
        var network = bridge.GetType("Illyaverse.Transfiguration.NetworkPolicy", true)!;
        var browser = bridge.GetType("Illyaverse.Transfiguration.ExternalBrowserPolicy", true)!;
        var environmentKey = network.GetField("EndpointEnvironmentName", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue() as string;
        var previous = Environment.GetEnvironmentVariable(environmentKey!);
        try
        {
            Environment.SetEnvironmentVariable(environmentKey!, "HTTPS://ILLYA.TEST:443/");
            method(network, "Initialize").Invoke(null, null);
            require(network.GetProperty("Website")!.GetValue(null) as string == "https://illya.test",
                "compiled production policy captures canonical HTTPS instead of the compile-time fixture");
            Environment.SetEnvironmentVariable(environmentKey!, "https://other.example.test");
            method(network, "Initialize").Invoke(null, null);
            require(network.GetProperty("Website")!.GetValue(null) as string == "https://illya.test",
                "later environment changes cannot redirect the launched production policy");

            var resolve = method(browser, "TryResolve");
            foreach (var (source, expected) in new[]
            {
                ("https://illya.test/beatmapsets/2630282#osu/5914819", "https://illya.test/beatmapsets/2630282#osu/5914819"),
                ("https://illya.test/beatmapsets/2630282#osu", "https://illya.test/beatmapsets/2630282#osu"),
                ("https://illya.test/beatmapsets/2630282#taiko/5914819", "https://illya.test/beatmapsets/2630282#taiko/5914819"),
                ("https://illya.test/beatmapsets/2630282#fruits/5914819", "https://illya.test/beatmapsets/2630282#fruits/5914819"),
                ("https://illya.test/beatmapsets/2630282#mania/5914819", "https://illya.test/beatmapsets/2630282#mania/5914819"),
                ("https://illya.test/beatmapsets/2000000000#osu/2000000001", "https://illya.test/beatmapsets/local-2000000000#osu/2000000001"),
                ("https://illya.test/beatmapsets/local-2000000000#osu/2000000001", "https://illya.test/beatmapsets/local-2000000000#osu/2000000001"),
                ("https://illya.test/beatmapsets/2147483647#mania/2147483647", "https://illya.test/beatmapsets/local-2147483647#mania/2147483647"),
                ("https://illya.test/users/2#activity", "https://illya.test/users/2#activity"),
                ("https://illya.test/scores/7", "https://illya.test/scores/7"),
                ("https://illya.test/b/5914819", "https://illya.test/b/5914819"),
                ("HTTPS://ILLYA.TEST:443/connection#lazer", "https://illya.test/connection#lazer"),
            })
            {
                object?[] values = [source, null];
                require(resolve.Invoke(null, values) is true && values[1] as string == expected,
                    "compiled browser resolver preserves private links and canonical native map selection");
            }
            foreach (var source in new string?[]
            {
                null, "", "not-a-url", "//illya.test/beatmapsets/1#osu/1", "javascript:alert(1)", "file:///C:/synthetic-proof",
                "https://osu.ppy.sh/beatmapsets/1#osu/1", "https://other.example.test/", "http://illya.test/", "https://illya.test:444/",
                "https://api.illya.test/beatmapsets/1#osu/1", "https://illya.test.evil.invalid/", "https://user:secret@illya.test/",
                "https://illya.test/beatmapsets/0#osu/1", "https://illya.test/beatmapsets/01#osu/1", "https://illya.test/beatmapsets/2147483648#osu/1",
                "https://illya.test/beatmapsets/local-1#osu/1", "https://illya.test/beatmapsets/1#catch/1", "https://illya.test/beatmapsets/1#osu/0",
                "https://illya.test/beatmapsets/1#osu/1/extra", "https://illya.test/../beatmapsets/1#osu/1",
                "https://illya.test/%2e%2e/beatmapsets/1#osu/1", "https://illya.test/beatmapsets%2f1#osu/1",
                "https://illya.test/users\\2", "https://illya.test/users/2\n", "https://illya.test/users/ 2",
            })
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    object?[] values = [source, null];
                    require(resolve.Invoke(null, values) is false && values[1] as string == string.Empty,
                        "compiled production resolver suppresses repeated unsafe input without throwing");
                }

            var allows = method(network, "Allows");
            var resourceLookup = method(network, "ResourcePrefix");
            foreach (var id in new[] { "1", "2630282", "2147483647" })
            {
                var officialPreview = "https://b.ppy.sh/preview/" + id + ".mp3";
                object?[] values = [officialPreview, "unchanged"];
                var expected = "https://illya.test/api/v2/beatmapsets/" + id + "/preview";
                require(resourceLookup.Invoke(null, values) is false && values[1] as string == expected
                    && allows.Invoke(null, [new Uri(expected), false]) is true,
                    "compiled production stock preview lookup resolves to the captured private endpoint");
                require(allows.Invoke(null, [new Uri(officialPreview), false]) is false,
                    "compiled production lookup translation leaves direct official preview HTTP blocked");
            }
            foreach (var source in new string?[]
            {
                null, "", "https://b.ppy.sh/preview/0.mp3", "https://b.ppy.sh/preview/01.mp3", "https://b.ppy.sh/preview/-1.mp3",
                "https://b.ppy.sh/preview/+1.mp3", "https://b.ppy.sh/preview/2147483648.mp3", "https://b.ppy.sh/preview/999999999999999999999.mp3",
                "http://b.ppy.sh/preview/1.mp3", "HTTPS://b.ppy.sh/preview/1.mp3", "https://B.PPY.SH/preview/1.mp3",
                "https://b.ppy.sh:443/preview/1.mp3", "https://b.ppy.sh:8443/preview/1.mp3", "https://b.ppy.sh./preview/1.mp3",
                "https://b.ppy.sh.evil.invalid/preview/1.mp3", "https://user:secret@b.ppy.sh/preview/1.mp3", "https://b.ppy.sh@other.example.test/preview/1.mp3",
                "https://b.ppy.sh/preview/1.mp3?", "https://b.ppy.sh/preview/1.mp3?token=synthetic", "https://b.ppy.sh/preview/1.mp3#",
                "https://b.ppy.sh/preview/1.mp3#fragment", "https://b.ppy.sh/preview/%31.mp3", "https://b.ppy.sh/preview/１.mp3",
                "https://b.ppy.sh/preview/1.MP3", "https://b.ppy.sh/Preview/1.mp3", "https://b.ppy.sh/preview/.mp3",
                "https://b.ppy.sh/preview/1.mp3/extra", "https://b.ppy.sh/preview//1.mp3", "https://b.ppy.sh/preview/../preview/1.mp3",
                "https://b.ppy.sh/preview/%2e%2e/preview/1.mp3", "https://b.ppy.sh/preview\\1.mp3", "https://b.ppy.sh/preview/1.mp3\n",
                " https://b.ppy.sh/preview/1.mp3", "https://b.ppy.sh/preview/ 1.mp3", "https://b.ppy.sh/covers/1.jpg",
                "https://assets.ppy.sh/preview/1.mp3", "https://osu.ppy.sh/preview/1.mp3",
            })
            {
                object?[] values = [source, "unchanged"];
                require(resourceLookup.Invoke(null, values) is false && values[1] as string == string.Empty,
                    "compiled production resource lookup suppresses noncanonical official audio and unrelated resources without throwing");
            }
            object?[] privateResource = ["https://illya.test/api/v2/beatmapsets/2630282/preview", "unresolved"];
            require(resourceLookup.Invoke(null, privateResource) is false
                && privateResource[1] as string == "https://illya.test/api/v2/beatmapsets/2630282/preview",
                "compiled production policy preserves server-provided private preview URLs");
            require(allows.Invoke(null, [new Uri("https://illya.test/beatmapsets/1#osu/1"), false]) is false,
                "production HTTP policy still rejects browser-only fragments");
            foreach (var (uri, socket) in new[]
            {
                ("https://osu.ppy.sh/oauth/token", false), ("https://assets.ppy.sh/beatmaps/1/covers/cover.jpg", false),
                ("https://b.ppy.sh/preview/2630282.mp3", false),
                ("wss://spectator.osu.ppy.sh/socket", true), ("https://illya.test:444/oauth/token", false),
                ("http://illya.test/oauth/token", false), ("https://user:secret@illya.test/oauth/token", false),
            })
            {
                require(allows.Invoke(null, [new Uri(uri), socket]) is false, "compiled production destination policy stays closed");
                try { method(network, "Require").Invoke(null, [new Uri(uri), socket]); }
                catch (TargetInvocationException exception) when (exception.InnerException is HttpRequestException cause
                    && cause.Message == "transfiguration_destination_blocked")
                {
                    require(true, "compiled strict destination rejection remains synchronous without network I/O");
                    continue;
                }
                throw new InvalidOperationException("Production destination rejection was bypassed");
            }
            require(!AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name is "osu.Game" or "osu.Framework" or "Realm"),
                "production browser proof did not load game, framework or Realm assemblies");
            Console.WriteLine($"PASS {checks} compiled production browser policy checks on .NET {Environment.Version}; no bootstrap, shell, GameHost, player data, network or fixture bridge");
            return 0;
        }
        finally { Environment.SetEnvironmentVariable(environmentKey!, previous); }
    }

    private static MethodInfo method(Type type, string name) => type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, name);

    private static void require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL production browser " + message);
        checks++;
        Console.WriteLine("PASS " + message);
    }
}
