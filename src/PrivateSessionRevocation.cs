using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Illyaverse.Transfiguration;

internal static class PrivateSessionRevocation
{
    internal static bool TryCapture(string? endpoint, string? accessToken, out Uri? destination)
    {
        destination = null;
        // Illyaverse issues 64 lowercase hexadecimal access credentials. Never serialize
        // the whole OAuth token, request a refresh or send an inherited token.
        if (endpoint is null || endpoint != NetworkPolicy.DiagnosticEndpoint || accessToken is null
            || accessToken.Length != 64 || accessToken.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || accessToken.All(character => character == '0'))
            return false;
        if (!Uri.TryCreate(endpoint + "/api/v2/oauth/tokens/current", UriKind.Absolute, out var uri) || !NetworkPolicy.Allows(uri))
            return false;
        destination = uri;
        return true;
    }

    internal static void Queue(string endpoint, string accessToken)
    {
        if (!TryCapture(endpoint, accessToken, out _)) return;
        // The UI continues with native local logout immediately. This single
        // bounded request is best effort; closing the game is not a logout.
        _ = Task.Run(() => SendAsync(endpoint, accessToken));
    }

    internal static async Task<bool> SendAsync(string endpoint, string accessToken, HttpMessageHandler? testHandler = null)
    {
        if (!TryCapture(endpoint, accessToken, out var destination)) return false;
        try
        {
            using var handler = testHandler ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(2),
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Delete, destination);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception)
        {
            // No response body, credentials or exception message is logged.
            // Server-side expiry and the website session controls remain valid.
            return false;
        }
    }
}
