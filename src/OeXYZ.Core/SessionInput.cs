namespace OeXYZ.Core;

public enum LocalSessionCommand
{
    None,
    Respawn,
    Disconnect,
    Quit
}

public static class SessionInput
{
    public static LocalSessionCommand Classify(string? value)
    {
        string command = value?.Trim() ?? string.Empty;
        if (string.Equals(command, "/respawn", StringComparison.OrdinalIgnoreCase))
            return LocalSessionCommand.Respawn;
        if (string.Equals(command, "/disconnect", StringComparison.OrdinalIgnoreCase))
            return LocalSessionCommand.Disconnect;
        if (string.Equals(command, "/quit", StringComparison.OrdinalIgnoreCase))
            return LocalSessionCommand.Quit;
        return LocalSessionCommand.None;
    }
}
