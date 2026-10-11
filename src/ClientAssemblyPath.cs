namespace Illyaverse.Transfiguration;

internal static class ClientAssemblyPath
{
    // Windows paths are case-insensitive; POSIX paths are exact byte-for-byte names.
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool Matches(string loadedLocation, string expectedPath) =>
        string.Equals(Canonical(loadedLocation), Canonical(expectedPath), Comparison);

    internal static string Canonical(string value) => OperatingSystem.IsWindows() ? CanonicalWindows(value) : CanonicalPosix(value);

    // Per-user data folder that Kaleidoscope passes as the parent of its control directory. On Linux
    // .NET reports $XDG_DATA_HOME (or ~/.local/share) unresolved while the launcher canonicalizes it,
    // so the comparison must use the resolved form. Windows behaviour is unchanged.
    internal static string LocalDataHome()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!OperatingSystem.IsLinux()) return folder;
        return LinuxNative.RealPath(folder) is { } resolved ? CanonicalPosix(resolved) : folder;
    }

    // hostfxr can preload the entry assembly using an extended drive path. Apply
    // the same normalization to both paths while still rejecting remote/device paths.
    internal static string CanonicalWindows(string value)
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

    // Purely lexical, so it behaves the same wherever it is evaluated: an absolute
    // path with "." and ".." resolved, duplicate separators collapsed and no trailing
    // separator. A backslash is an ordinary file-name character here. Symlinks are never
    // resolved (they are rejected separately by the descriptor walk).
    internal static string CanonicalPosix(string value)
    {
        if (value.Length == 0 || value[0] != '/') throw SafeStartupDiagnostic.Rejection("path_not_absolute");
        if (value.Contains('\0')) throw SafeStartupDiagnostic.Rejection("path_not_local");
        var parts = new List<string>();
        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(segment);
        }
        return "/" + string.Join('/', parts);
    }
}
