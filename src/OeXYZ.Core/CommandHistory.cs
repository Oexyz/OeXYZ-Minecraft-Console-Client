namespace OeXYZ.Core;

public sealed class CommandHistory
{
    private readonly int capacity;
    private readonly List<string> entries = [];
    private int cursor;

    public CommandHistory(int capacity = 100)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public IReadOnlyList<string> Entries => entries;

    public void Add(string value)
    {
        string normalized = value.Trim();
        if (normalized.Length == 0 || SensitiveDataRedactor.IsSensitiveCommand(normalized))
        {
            cursor = entries.Count;
            return;
        }

        if (entries.Count == 0 || !string.Equals(entries[^1], normalized, StringComparison.Ordinal))
        {
            entries.Add(normalized);
            if (entries.Count > capacity) entries.RemoveAt(0);
        }
        cursor = entries.Count;
    }

    public string Previous(string currentText = "")
    {
        if (entries.Count == 0) return currentText;
        cursor = Math.Max(0, cursor - 1);
        return entries[cursor];
    }

    public string Next()
    {
        if (entries.Count == 0) return string.Empty;
        cursor = Math.Min(entries.Count, cursor + 1);
        return cursor == entries.Count ? string.Empty : entries[cursor];
    }

    public void ResetNavigation() => cursor = entries.Count;

}
