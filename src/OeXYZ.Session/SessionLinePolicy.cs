using OeXYZ.Core;
using OeXYZ.Protocol;

namespace OeXYZ.Session;

internal static class SessionLinePolicy
{
    internal const int MaximumTextCharacters = 4096;
    internal const int MaximumFormattingRuns = 256;
    internal const string TruncationMarker = " … [truncated]";

    public static SessionLine? Create(
        DateTimeOffset timestamp,
        SessionLineKind kind,
        SessionLineCategory category,
        string? text,
        FormattedChatText? formatting = null)
    {
        string original = text ?? string.Empty;
        bool changed = original.Length > MaximumTextCharacters;
        string boundedInput = changed ? Truncate(original) : original;
        string normalized = SensitiveDataRedactor.RedactText(
            TerminalTextSanitizer.Sanitize(boundedInput)).Trim();
        if (normalized.Length > MaximumTextCharacters)
        {
            normalized = Truncate(normalized);
            changed = true;
        }
        if (normalized.Length == 0) return null;
        changed |= !string.Equals(normalized, original, StringComparison.Ordinal);

        FormattedChatText? retainedFormatting = changed
            ? null
            : ValidateFormatting(formatting, normalized);
        return new SessionLine(timestamp, kind, category, normalized, retainedFormatting);
    }

    public static string NormalizeText(string? text)
    {
        SessionLine? line = Create(
            DateTimeOffset.MinValue,
            SessionLineKind.Information,
            SessionLineCategory.System,
            text);
        return line?.Text ?? string.Empty;
    }

    private static string Truncate(string text)
    {
        int prefixLength = MaximumTextCharacters - TruncationMarker.Length;
        if (prefixLength > 0 && prefixLength < text.Length && char.IsHighSurrogate(text[prefixLength - 1]))
            prefixLength--;
        return text[..prefixLength] + TruncationMarker;
    }

    private static FormattedChatText? ValidateFormatting(FormattedChatText? formatting, string text)
    {
        if (formatting is null || formatting.Runs.Count is 0 or > MaximumFormattingRuns)
            return null;
        if (!string.Equals(formatting.Text, text, StringComparison.Ordinal)) return null;

        int offset = 0;
        foreach (ChatRun? run in formatting.Runs)
        {
            if (run?.Text is null || run.Text.Length > text.Length - offset) return null;
            if (!text.AsSpan(offset, run.Text.Length).SequenceEqual(run.Text.AsSpan())) return null;
            offset += run.Text.Length;
        }
        return offset == text.Length ? formatting : null;
    }
}
