namespace Illyaverse.Transfiguration;

internal static class ClientAssemblyPath
{
    internal static bool Matches(string loadedLocation, string expectedPath) =>
        string.Equals(Canonical(loadedLocation), Canonical(expectedPath), StringComparison.OrdinalIgnoreCase);

    // hostfxr can preload the entry assembly using an extended drive path. Apply
    // the same normalization to both paths while still rejecting remote/device paths.
    internal static string Canonical(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw SafeStartupDiagnostic.Rejection("path_not_absolute");
        var path = Path.GetFullPath(value);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (path.Length < 7 || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
                throw SafeStartupDiagnostic.Rejection("path_not_local");
            path = path[4..];
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw SafeStartupDiagnostic.Rejection("path_not_local");
        return path;
    }
}
