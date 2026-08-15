namespace OeXYZ.Core;

public sealed record ApplicationPaths(
    string Root,
    string Profiles,
    string ProtectedAccounts,
    string Logs,
    string Diagnostics)
{
    public static ApplicationPaths Resolve(string? explicitConfigPath = null)
    {
        string? configuredPath = string.IsNullOrWhiteSpace(explicitConfigPath)
            ? Environment.GetEnvironmentVariable("OEXYZ_CONFIG")
            : explicitConfigPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!OperatingSystem.IsWindows())
            {
                return ResolveUnixExplicitConfig(
                    configuredPath,
                    UserDirectories.GetHomeDirectory(),
                    Environment.GetEnvironmentVariable("XDG_STATE_HOME"));
            }
            string profiles = ExpandPath(configuredPath);
            string root = Path.GetDirectoryName(profiles)
                   ?? throw new InvalidDataException("The configuration path has no parent directory.");
            return Create(root, profiles, root);
        }

        if (OperatingSystem.IsWindows())
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OeXYZ",
                "ConsoleClient");
            return Create(root, Path.Combine(root, "profiles.json"), root);
        }

        string home = UserDirectories.GetHomeDirectory();
        string configRoot = ResolveXdgDirectory(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home, ".config", "oexyz");
        string stateRoot = ResolveXdgDirectory(
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"), home, ".local/state", "oexyz");
        return Create(configRoot, Path.Combine(configRoot, "profiles.json"), stateRoot);
    }

    public void EnsureDirectories()
    {
        PrivateFileSystem.EnsurePrivateDirectory(Root);
        PrivateFileSystem.EnsurePrivateDirectory(Logs);
        PrivateFileSystem.EnsurePrivateDirectory(Diagnostics);
    }

    internal static ApplicationPaths ResolveUnixDefaults(
        string home,
        string? xdgConfigHome,
        string? xdgStateHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        string configRoot = ResolveXdgDirectory(xdgConfigHome, home, ".config", "oexyz");
        string stateRoot = ResolveXdgDirectory(xdgStateHome, home, ".local/state", "oexyz");
        return Create(configRoot, Path.Combine(configRoot, "profiles.json"), stateRoot);
    }

    internal static ApplicationPaths ResolveUnixExplicitConfig(
        string configPath,
        string home,
        string? xdgStateHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        string profiles = ExpandPath(configPath, home);
        string root = Path.GetDirectoryName(profiles)
            ?? throw new InvalidDataException("The configuration path has no parent directory.");
        string stateRoot = ResolveXdgDirectory(xdgStateHome, home, ".local/state", "oexyz");
        return Create(root, profiles, stateRoot);
    }

    private static ApplicationPaths Create(string root, string profiles, string stateRoot) => new(
        root,
        profiles,
        Path.Combine(root, "accounts.bin"),
        Path.Combine(stateRoot, "logs"),
        Path.Combine(stateRoot, "diagnostics"));

    private static string ResolveXdgDirectory(
        string? configured,
        string home,
        string fallback,
        string child)
    {
        string parent = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(home, fallback.Replace('/', Path.DirectorySeparatorChar))
            : ExpandPath(configured, home);
        return Path.GetFullPath(Path.Combine(parent, child));
    }

    private static string ExpandPath(string value, string? homeOverride = null)
    {
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded == "~" || expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            expanded.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            string home = homeOverride ?? UserDirectories.GetHomeDirectory();
            expanded = expanded.Length == 1
                ? home
                : Path.Combine(home, expanded[2..]);
        }
        return Path.GetFullPath(expanded);
    }
}
