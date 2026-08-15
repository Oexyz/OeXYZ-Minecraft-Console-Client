namespace OeXYZ.Core;

public enum OeXYZExitCode
{
    Success = 0,
    ProfileNotFound = 2,
    AuthenticationError = 3,
    ProtocolUnsupported = 4,
    ConnectionFailure = 5,
    PermanentServerRejection = 6,
    DiagnosticsFailed = 7,
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
            .Where(entry => !string.Equals(
                Normalize(entry),
                normalizedDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .ToList();
        if (install) entries.Add(executableDirectory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Join(Path.PathSeparator, entries);
    }

    public static string GetUnixUserBin(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return Path.GetFullPath(Path.Combine(homeDirectory, ".local", "bin"));
    }

    public static bool UpdateUnixLink(
        string executablePath,
        string binDirectory,
        bool install)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix symbolic PATH registration is not available on Windows.");
        string executable = Path.GetFullPath(executablePath);
        string directory = Path.GetFullPath(binDirectory);
        string link = Path.Combine(directory, "oexyz");
        if (install && !File.Exists(executable))
            throw new FileNotFoundException("The running OeXYZ executable could not be found.", executable);

        FileInfo linkInfo = new(link);
        string? linkTarget = linkInfo.LinkTarget;
        bool pathExists = File.Exists(link) || Directory.Exists(link) || linkTarget is not null;
        if (!install)
        {
            if (!pathExists) return false;
            if (linkTarget is null)
                throw new IOException($"Refusing to remove {link}: it is not a symbolic link.");
            string resolved = ResolveLinkTarget(directory, linkTarget);
            if (!string.Equals(resolved, executable, StringComparison.Ordinal))
                throw new IOException($"Refusing to remove {link}: it points to a different executable.");
            File.Delete(link);
            return true;
        }

        if (pathExists)
        {
            if (linkTarget is null)
                throw new IOException($"Refusing to replace {link}: another file already exists there.");
            string resolved = ResolveLinkTarget(directory, linkTarget);
            if (!string.Equals(resolved, executable, StringComparison.Ordinal))
                throw new IOException($"Refusing to replace {link}: it points to a different executable.");
            return false;
        }

        bool createdDirectory = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (createdDirectory)
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.CreateSymbolicLink(link, executable);
        return true;
    }

    private static string ResolveLinkTarget(string directory, string target) =>
        Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(directory, target));

    private static string Normalize(string value)
    {
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        try { return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }
}
