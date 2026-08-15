namespace OeXYZ.Core;

public static class ProfileRules
{
    public const int MaximumProfileNameLength = 64;

    public static bool IsValidOfflineName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 16 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static void EnsureValidOfflineName(string? value)
    {
        if (!IsValidOfflineName(value))
            throw new InvalidDataException(
                "An offline player name must contain 1-16 ASCII letters, digits, or underscores.");
    }

    public static string NormalizeProfileName(string? value, string profileKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKind);
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumProfileNameLength || normalized.Any(char.IsControl))
            throw new InvalidDataException(
                $"A {profileKind} profile name of 1-{MaximumProfileNameLength} printable characters is required.");
        return normalized;
    }
}
