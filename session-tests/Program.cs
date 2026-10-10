using System.Diagnostics;
using System.Net;
using Illyaverse.Transfiguration;

internal static class Program
{
    private const string credential = "a0000000000040008000000000000001b0000000000040008000000000000002";
    private static int checks;

    public static async Task<int> Main()
    {
        var previous = Environment.GetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName);
        try
        {
            Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, "https://illya.test");
            NetworkPolicy.Initialize();
            require(PrivateSessionRevocation.TryCapture("https://illya.test", credential, out var destination)
                && destination!.AbsoluteUri == "https://illya.test/api/v2/oauth/tokens/current", "current-session destination binds the captured private origin");
            foreach (var endpoint in new[] { "https://osu.ppy.sh", "https://other.illya.test", "https://api.illya.test", "https://illya.test:444", "http://illya.test", "https://illya.test/", "https://illya.test/path" })
                require(!PrivateSessionRevocation.TryCapture(endpoint, credential, out _), "foreign, service-alias and ambiguous origins cannot receive a credential");
            foreach (var token in new[] { "", "opaque-official-credential", credential.ToUpperInvariant(), credential + "\r\nX-Test: invalid", credential + "|refresh-must-not-send", new string('0', 64), "a0000000-0000-4000-8000-000000000001", credential[..63], credential + "0" })
                require(!PrivateSessionRevocation.TryCapture("https://illya.test", token, out _), "invalid or combined OAuth credentials cannot be sent");

            var calls = 0;
            var success = new FakeHandler((request, _) =>
            {
                calls++;
                require(request.Method == HttpMethod.Delete && request.RequestUri == destination, "one request uses the exact current-session DELETE route");
                require(request.Content is null, "logout request has no body or refresh credential");
                require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == credential,
                    "only the captured current access credential is transmitted");
                require(!request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Client-Secret"), "logout request inherits no cookies or other credentials");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });
            require(await PrivateSessionRevocation.SendAsync("https://illya.test", credential, success), "204 acknowledges the private current-session revocation");
            require(calls == 1, "successful revocation is sent once without a retry");

            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Found, HttpStatusCode.InternalServerError })
            {
                var attempts = 0;
                var failure = new FakeHandler((_, _) =>
                {
                    attempts++;
                    var response = new HttpResponseMessage(status);
                    response.Headers.Location = new Uri("https://osu.ppy.sh/oauth/token");
                    return Task.FromResult(response);
                });
                require(!await PrivateSessionRevocation.SendAsync("https://illya.test", credential, failure) && attempts == 1,
                    "stale access, redirects and server failure finish quietly without retry or fallback");
            }
            var invalidCalls = 0;
            var blocked = new FakeHandler((_, _) => { invalidCalls++; throw new InvalidOperationException("no request expected"); });
            require(!await PrivateSessionRevocation.SendAsync("https://osu.ppy.sh", credential, blocked) && invalidCalls == 0,
                "unsafe capture stops before invoking the sender");
            var throwing = new FakeHandler((_, _) => throw new HttpRequestException("synthetic failure, not forwarded"));
            require(!await PrivateSessionRevocation.SendAsync("https://illya.test", credential, throwing), "transport exceptions are observed without reaching the UI");
            var deadline = Stopwatch.StartNew();
            var delayed = new FakeHandler(async (_, cancellation) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            require(!await PrivateSessionRevocation.SendAsync("https://illya.test", credential, delayed)
                && deadline.Elapsed >= TimeSpan.FromSeconds(2) && deadline.Elapsed < TimeSpan.FromSeconds(6), "a stalled transport is bounded by the three-second deadline");
            require(!NetworkPolicy.Allows(new Uri("https://osu.ppy.sh/oauth/token")), "logout support never permits official authentication traffic");
            Console.WriteLine($"PASS {checks} source-linked private session revocation checks; synthetic handler only, no game, player data or network");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL session_revocation_fixture " + exception.GetType().Name); return 1; }
        finally { Environment.SetEnvironmentVariable(NetworkPolicy.EndpointEnvironmentName, previous); }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Session revocation fixture failed");
        checks++;
        Console.WriteLine("PASS " + name);
    }
}

// The source-linked diagnostic type identity does not execute its writer.
internal static class StartupHook { }
