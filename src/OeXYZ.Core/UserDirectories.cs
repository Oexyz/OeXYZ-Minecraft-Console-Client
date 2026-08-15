namespace OeXYZ.Core;

public static class UserDirectories
{
    public static string GetHomeDirectory()
    {
        string?[] candidates =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
        ];
        string? value = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("The current user's home directory could not be resolved.");
        return Path.GetFullPath(value);
    }
}
