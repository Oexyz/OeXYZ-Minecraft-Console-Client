namespace OeXYZ.Core;

public enum OeXYZExitCode
{
    Success = 0,
    ProfileNotFound = 2,
    AuthenticationError = 3,
    ProtocolUnsupported = 4,
    ConnectionFailure = 5,
    PermanentServerRejection = 6,
    InvalidArguments = 64,
    InternalError = 70
}

public static class PathRegistration
{
    public static string Update(string? currentPath, string executableDirectory, bool install)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        string normalizedDirectory = Normalize(executableDirectory);
        List<string> entries = (currentPath ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !string.Equals(Normalize(entry), normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (install) entries.Add(executableDirectory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Join(Path.PathSeparator, entries);
    }

    private static string Normalize(string value)
    {
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        try { return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }
}
