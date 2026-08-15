using System.Text;

namespace OeXYZ.Protocol;

/// <summary>
/// Makes untrusted text safe to write to a terminal as plain text.
/// </summary>
public static class TerminalTextSanitizer
{
    /// <summary>
    /// Replaces each consecutive run of terminal control characters with one space.
    /// CR, LF, tabs, C0/C1 controls (including ESC and BEL), and Unicode line or
    /// paragraph separators are never preserved. Length limiting is intentionally
    /// left to the caller because limits depend on the field being rendered.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        int firstUnsafe = -1;
        for (int index = 0; index < text.Length; index++)
        {
            if (!RequiresReplacement(text[index])) continue;
            firstUnsafe = index;
            break;
        }
        if (firstUnsafe < 0) return text;

        StringBuilder safe = new(text.Length);
        safe.Append(text, 0, firstUnsafe);
        bool replacingControls = false;
        for (int index = firstUnsafe; index < text.Length; index++)
        {
            char value = text[index];
            if (RequiresReplacement(value))
            {
                if (!replacingControls) safe.Append(' ');
                replacingControls = true;
                continue;
            }

            replacingControls = false;
            safe.Append(value);
        }
        return safe.ToString();
    }

    private static bool RequiresReplacement(char value) =>
        char.IsControl(value) || value is '\u2028' or '\u2029';
}
