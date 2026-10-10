using System.Globalization;

namespace Illyaverse.Transfiguration;

// Browser anchors carry client-side selection fragments. They must not use the
// authenticated HTTP policy, where fragments and foreign destinations reject.
internal static class ExternalBrowserPolicy
{
    private const int firstLocalId = 2_000_000_000;

    internal static bool TryResolve(string? supplied, out string resolved)
    {
        resolved = string.Empty;
        var origin = NetworkPolicy.DiagnosticEndpoint;
        if (origin is null || supplied is null || supplied.Length is 0 or > 4096
            || supplied.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || supplied.Contains('\\')
            || !Uri.TryCreate(supplied, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || !Uri.TryCreate(origin, UriKind.Absolute, out var website)
            || uri.Scheme != website.Scheme || uri.Host != website.Host || uri.Port != website.Port
            || uri.UserInfo.Length != 0) return false;

        // A link is an absolute website URL, never an operating-system path or
        // another service origin. Reject ambiguous path spellings before shelling.
        var pathStart = supplied.IndexOfAny(['/', '?', '#'], supplied.IndexOf("://", StringComparison.Ordinal) + 3);
        var suppliedPath = pathStart < 0 || supplied[pathStart] != '/' ? "/" : supplied[pathStart..].Split('?', '#')[0];
        if (suppliedPath.Contains("//", StringComparison.Ordinal) || suppliedPath.Contains("/./", StringComparison.Ordinal)
            || suppliedPath.Contains("/../", StringComparison.Ordinal) || suppliedPath.EndsWith("/.", StringComparison.Ordinal)
            || suppliedPath.EndsWith("/..", StringComparison.Ordinal)
            || suppliedPath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || suppliedPath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || suppliedPath.Contains("%2e", StringComparison.OrdinalIgnoreCase)) return false;

        var path = uri.AbsolutePath;
        if (path.StartsWith("/beatmapsets/", StringComparison.Ordinal))
        {
            var token = path["/beatmapsets/".Length..];
            var local = token.StartsWith("local-", StringComparison.Ordinal);
            var digits = local ? token["local-".Length..] : token;
            if (!positiveId(digits, out var setId) || (local && setId < firstLocalId)
                || !beatmapFragment(uri.Fragment)) return false;
            path = "/beatmapsets/" + (setId >= firstLocalId ? "local-" : string.Empty) + digits;
        }

        resolved = origin + path + uri.Query + uri.Fragment;
        return true;
    }

    private static bool beatmapFragment(string fragment)
    {
        if (fragment.Length == 0) return true;
        var pieces = fragment[1..].Split('/');
        return pieces.Length is 1 or 2 && pieces[0] is "osu" or "taiko" or "fruits" or "mania"
            && (pieces.Length == 1 || positiveId(pieces[1], out _));
    }

    private static bool positiveId(string value, out int id) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
        && id > 0 && id.ToString(CultureInfo.InvariantCulture) == value;
}
