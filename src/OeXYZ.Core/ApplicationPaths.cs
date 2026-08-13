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
        string profiles;
        string root;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            profiles = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            root = Path.GetDirectoryName(profiles)
                   ?? throw new InvalidDataException("The configuration path has no parent directory.");
        }
        else
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OeXYZ",
                "ConsoleClient");
            profiles = Path.Combine(root, "profiles.json");
        }

        return new ApplicationPaths(
            root,
            profiles,
            Path.Combine(root, "accounts.bin"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "diagnostics"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Diagnostics);
    }
}
