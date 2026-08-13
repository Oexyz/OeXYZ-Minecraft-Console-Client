namespace OeXYZ.Core;

public static class LogRetentionService
{
    public const long DefaultMaximumBytes = 300L * 1024L * 1024L;

    public static IReadOnlyList<string> FindExpiredFiles(
        IEnumerable<(string Path, DateTimeOffset LastWrite)> files,
        int retentionDays,
        DateTimeOffset now)
    {
        if (retentionDays == 0) return [];
        if (retentionDays is not (30 or 90)) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        DateTimeOffset cutoff = now.AddDays(-retentionDays);
        return files.Where(item => item.LastWrite < cutoff).Select(item => item.Path).ToArray();
    }

    public static IReadOnlyList<string> FindFilesOverLimit(
        IEnumerable<(string Path, DateTimeOffset LastWrite, long Length)> files,
        long maximumBytes = DefaultMaximumBytes,
        IEnumerable<string>? protectedPaths = null)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        (string Path, DateTimeOffset LastWrite, long Length)[] candidates = files
            .Select(item => (item.Path, item.LastWrite, Math.Max(0, item.Length)))
            .ToArray();
        long total = candidates.Aggregate(0L, (sum, item) =>
            item.Length > long.MaxValue - sum ? long.MaxValue : sum + item.Length);
        if (total <= maximumBytes) return [];

        HashSet<string> protectedSet = protectedPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                          ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> remove = [];
        foreach ((string path, _, long length) in candidates.OrderBy(item => item.LastWrite))
        {
            if (total <= maximumBytes) break;
            if (protectedSet.Contains(path)) continue;
            remove.Add(path);
            total = Math.Max(0, total - length);
        }
        return remove;
    }

    public static int Apply(
        string directory,
        int retentionDays,
        DateTimeOffset? now = null,
        long maximumBytes = DefaultMaximumBytes,
        IEnumerable<string>? protectedPaths = null)
    {
        if (!Directory.Exists(directory)) return 0;
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        HashSet<string> protectedSet = protectedPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                          ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileInfo[] files = new DirectoryInfo(directory).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).ToArray();
        IReadOnlyList<string> expired = FindExpiredFiles(
            files.Where(file => !protectedSet.Contains(file.FullName))
                .Select(file => (file.FullName, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero))),
            retentionDays,
            now ?? DateTimeOffset.UtcNow);
        int removed = 0;
        foreach (string path in expired)
        {
            if (TryDelete(path)) removed++;
        }

        FileInfo[] remaining = new DirectoryInfo(directory).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).ToArray();
        IReadOnlyList<string> oversized = FindFilesOverLimit(
            remaining.Select(file => (
                file.FullName,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Length)),
            maximumBytes,
            protectedSet);
        foreach (string path in oversized)
            if (TryDelete(path)) removed++;
        return removed;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
