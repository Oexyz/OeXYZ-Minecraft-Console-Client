using System.Text.Json;

namespace OeXYZ.Core;

public sealed class ProfileConcurrencyException : IOException
{
    public ProfileConcurrencyException(string message, long expectedRevision, long currentRevision)
        : base(message)
    {
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
    }

    public long ExpectedRevision { get; }
    public long CurrentRevision { get; }
}

public sealed record ProfileRecoveryState(
    bool PrimaryExists,
    bool PrimaryValid,
    bool BackupExists,
    bool BackupValid,
    string? PrimaryError,
    string? BackupError)
{
    public bool CanRestore => !PrimaryValid && BackupValid;
}

public sealed record ProfileRecoveryResult(
    ProfileDocument Document,
    string? PreservedCorruptPath);

public sealed class ProfileRecoveryAvailableException : IOException
{
    public ProfileRecoveryAvailableException(ProfileRecoveryState recovery, Exception innerException)
        : base("profiles.json is invalid, but a valid backup is available for explicit recovery.", innerException)
    {
        Recovery = recovery;
    }

    public ProfileRecoveryState Recovery { get; }
}

public sealed class ProfileRepository
{
    public const long MaximumProfileBytes = 2L * 1024L * 1024L;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    private readonly string path;
    private readonly object stateLock = new();
    private ProfileDocument? baselineView;

    public ProfileRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = System.IO.Path.GetFullPath(path);
    }

    public string Path => path;
    public string BackupPath => path + ".bak";
    private string LockPath => path + ".lock";

    public ProfileDocument Load()
    {
        lock (stateLock)
        {
            ProfileDocument document;
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                document = new ProfileDocument();
            }
            else
            {
                using FileStream repositoryLock = AcquireLock();
                document = LoadUnlocked();
            }

            baselineView = DeepClone(document);
            return document;
        }
    }

    public ProfileRecoveryState InspectRecovery()
    {
        lock (stateLock)
        {
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return new ProfileRecoveryState(false, false, false, false, null, null);
            using FileStream repositoryLock = AcquireLock();
            return InspectRecoveryUnlocked();
        }
    }

    public ProfileRecoveryResult RestoreBackup()
    {
        lock (stateLock)
        {
            EnsureDirectory();
            using FileStream repositoryLock = AcquireLock();
            ProfileRecoveryState recovery = InspectRecoveryUnlocked();
            if (!recovery.CanRestore)
                throw new InvalidOperationException("A valid backup is not available for explicit profile recovery.");

            byte[] backup = ReadBoundedBytes(BackupPath, "profiles.json.bak");
            _ = Deserialize(backup, "profiles.json.bak");
            string? corruptPath = null;
            if (File.Exists(path))
            {
                corruptPath = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") +
                              "-" + Guid.NewGuid().ToString("N")[..8];
                File.Copy(path, corruptPath, overwrite: false);
                PrivateFileSystem.ProtectFile(corruptPath);
            }

            PrivateFileSystem.WriteAllBytesAtomically(path, backup);
            ProfileDocument restored = ReadProfileFile(path, "profiles.json");
            baselineView = DeepClone(restored);
            return new ProfileRecoveryResult(restored, corruptPath);
        }
    }

    public void Save(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ProfileDocument proposed = document.Normalize();

        lock (stateLock)
        {
            EnsureDirectory();
            using FileStream repositoryLock = AcquireLock();
            ProfileDocument current = LoadUnlocked();
            ProfileDocument next;

            if (baselineView is not null && baselineView.Revision == proposed.Revision)
            {
                next = MergeConcurrentChanges(baselineView, proposed, current);
            }
            else if (proposed.Revision == current.Revision)
            {
                next = proposed;
            }
            else
            {
                throw RevisionConflict(proposed.Revision, current.Revision);
            }

            ProfileDocument saved = WriteUnlocked(next, current.Revision);
            document.Revision = saved.Revision;
            proposed.Revision = saved.Revision;
            // Keep the caller's view as the next merge base. It intentionally may
            // not contain changes merged from another process, so a later Save
            // only reapplies changes the caller actually made.
            baselineView = DeepClone(proposed);
        }
    }

    public ProfileDocument Update(Func<ProfileDocument, ProfileDocument> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (stateLock)
        {
            EnsureDirectory();
            using FileStream repositoryLock = AcquireLock();
            ProfileDocument current = LoadUnlocked();
            ProfileDocument workingCopy = DeepClone(current);
            ProfileDocument proposed = update(workingCopy)
                                       ?? throw new InvalidDataException("The profile update returned no document.");
            ProfileDocument normalized = proposed.Normalize();
            normalized.Revision = current.Revision;
            ProfileDocument saved = WriteUnlocked(normalized, current.Revision);
            baselineView = DeepClone(saved);
            return saved;
        }
    }

    private void EnsureDirectory()
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) PrivateFileSystem.EnsurePrivateDirectory(directory);
    }

    private ProfileDocument LoadUnlocked()
    {
        if (!File.Exists(path))
        {
            ProfileRecoveryState missingPrimary = InspectRecoveryUnlocked();
            if (missingPrimary.CanRestore)
                throw new ProfileRecoveryAvailableException(
                    missingPrimary,
                    new FileNotFoundException("profiles.json is missing while a valid backup exists.", path));
            return new ProfileDocument();
        }
        try
        {
            return ReadProfileFile(path, "profiles.json");
        }
        catch (InvalidDataException exception)
        {
            ProfileRecoveryState recovery = InspectRecoveryUnlocked(exception.Message);
            if (recovery.CanRestore) throw new ProfileRecoveryAvailableException(recovery, exception);
            throw;
        }
    }

    private ProfileRecoveryState InspectRecoveryUnlocked(string? knownPrimaryError = null)
    {
        bool primaryExists = File.Exists(path);
        bool backupExists = File.Exists(BackupPath);
        bool primaryValid = TryValidate(path, "profiles.json", out string? primaryError);
        bool backupValid = TryValidate(BackupPath, "profiles.json.bak", out string? backupError);
        return new ProfileRecoveryState(
            primaryExists,
            primaryValid,
            backupExists,
            backupValid,
            knownPrimaryError ?? primaryError,
            backupError);
    }

    private static bool TryValidate(string file, string label, out string? error)
    {
        if (!File.Exists(file))
        {
            error = null;
            return false;
        }
        try
        {
            _ = ReadProfileFile(file, label);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static ProfileDocument ReadProfileFile(string file, string label) =>
        Deserialize(ReadBoundedBytes(file, label), label);

    private static byte[] ReadBoundedBytes(string file, string label)
    {
        long length = new FileInfo(file).Length;
        if (length > MaximumProfileBytes)
            throw new InvalidDataException($"{label} exceeds the 2 MiB safety limit.");
        if (length < 0) throw new InvalidDataException($"{label} has an invalid length.");
        return File.ReadAllBytes(file);
    }

    private static ProfileDocument Deserialize(byte[] json, string label)
    {
        try
        {
            ProfileDocument document = JsonSerializer.Deserialize<ProfileDocument>(json, Options)
                                       ?? throw new InvalidDataException($"{label} is empty or invalid.");
            return document.Normalize();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} contains invalid JSON or profile values.", exception);
        }
    }

    private ProfileDocument WriteUnlocked(ProfileDocument document, long currentRevision)
    {
        if (currentRevision == long.MaxValue)
            throw new InvalidDataException("The profile revision has reached its maximum value.");

        ProfileDocument normalized = document.Normalize();
        normalized.Revision = currentRevision + 1;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(normalized, Options);
        if (json.LongLength > MaximumProfileBytes)
            throw new InvalidDataException("profiles.json exceeds the 2 MiB safety limit.");

        PrivateFileSystem.WriteAllBytesAtomically(path, json, BackupPath);
        return normalized;
    }

    private FileStream AcquireLock()
    {
        DateTime deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
                return new FileStream(LockPath, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static ProfileDocument MergeConcurrentChanges(
        ProfileDocument baseline,
        ProfileDocument proposed,
        ProfileDocument current)
    {
        if (current.Revision < baseline.Revision)
            throw RevisionConflict(baseline.Revision, current.Revision);

        ProfileDocument merged = current with
        {
            Accounts = MergeById(
                baseline.Accounts,
                proposed.Accounts,
                current.Accounts,
                account => account.Id,
                "account profile",
                baseline.Revision,
                current.Revision),
            Servers = MergeById(
                baseline.Servers,
                proposed.Servers,
                current.Servers,
                server => server.Id,
                "server profile",
                baseline.Revision,
                current.Revision),
            ProxyProfiles = MergeById(
                baseline.ProxyProfiles,
                proposed.ProxyProfiles,
                current.ProxyProfiles,
                proxy => proxy.Id,
                "proxy profile",
                baseline.Revision,
                current.Revision),
            Settings = MergeValue(
                baseline.Settings,
                proposed.Settings,
                current.Settings,
                "application settings",
                baseline.Revision,
                current.Revision),
            ManagedSessions = MergeBookmarks(
                baseline.ManagedSessions,
                proposed.ManagedSessions,
                current.ManagedSessions),
            LastSessions = MergeBookmarks(
                baseline.LastSessions,
                proposed.LastSessions,
                current.LastSessions),
            AdditionalData = MergeValue(
                baseline.AdditionalData,
                proposed.AdditionalData,
                current.AdditionalData,
                "profile extension data",
                baseline.Revision,
                current.Revision)
        };
        return merged.Normalize();
    }

    private static List<T> MergeById<T>(
        IReadOnlyList<T> baseline,
        IReadOnlyList<T> proposed,
        IReadOnlyList<T> current,
        Func<T, Guid> idSelector,
        string description,
        long expectedRevision,
        long currentRevision)
    {
        Dictionary<Guid, T> baselineById = baseline.ToDictionary(idSelector);
        Dictionary<Guid, T> proposedById = proposed.ToDictionary(idSelector);
        List<T> result = current.ToList();

        foreach ((Guid id, T original) in baselineById)
        {
            int currentIndex = result.FindIndex(item => idSelector(item) == id);
            bool stillProposed = proposedById.TryGetValue(id, out T? changed);
            if (!stillProposed)
            {
                if (currentIndex < 0) continue;
                if (!Equivalent(result[currentIndex], original))
                    throw EntityConflict(description, id, expectedRevision, currentRevision);
                result.RemoveAt(currentIndex);
                continue;
            }

            if (Equivalent(changed, original)) continue;
            if (currentIndex < 0)
                throw EntityConflict(description, id, expectedRevision, currentRevision);
            if (Equivalent(result[currentIndex], changed)) continue;
            if (!Equivalent(result[currentIndex], original))
                throw EntityConflict(description, id, expectedRevision, currentRevision);
            result[currentIndex] = changed!;
        }

        foreach (T addition in proposed.Where(item => !baselineById.ContainsKey(idSelector(item))))
        {
            Guid id = idSelector(addition);
            int currentIndex = result.FindIndex(item => idSelector(item) == id);
            if (currentIndex < 0)
            {
                result.Add(addition);
            }
            else if (!Equivalent(result[currentIndex], addition))
            {
                throw EntityConflict(description, id, expectedRevision, currentRevision);
            }
        }

        return result;
    }

    private static List<SessionBookmark> MergeBookmarks(
        IReadOnlyList<SessionBookmark> baseline,
        IReadOnlyList<SessionBookmark> proposed,
        IReadOnlyList<SessionBookmark> current)
    {
        HashSet<SessionBookmark> original = baseline.ToHashSet();
        HashSet<SessionBookmark> wanted = proposed.ToHashSet();
        List<SessionBookmark> result = current
            .Where(bookmark => !original.Contains(bookmark) || wanted.Contains(bookmark))
            .ToList();
        HashSet<SessionBookmark> present = result.ToHashSet();
        result.AddRange(proposed.Where(bookmark => !original.Contains(bookmark) && present.Add(bookmark)));
        return result;
    }

    private static T MergeValue<T>(
        T baseline,
        T proposed,
        T current,
        string description,
        long expectedRevision,
        long currentRevision)
    {
        if (Equivalent(proposed, baseline)) return current;
        if (Equivalent(current, baseline) || Equivalent(current, proposed)) return proposed;
        throw new ProfileConcurrencyException(
            $"The {description} changed in another OeXYZ process. Reload the profiles and retry.",
            expectedRevision,
            currentRevision);
    }

    private static bool Equivalent<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, Options),
            JsonSerializer.SerializeToElement(right, Options));

    private static ProfileDocument DeepClone(ProfileDocument document)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        return JsonSerializer.Deserialize<ProfileDocument>(json, Options)!.Normalize();
    }

    private static ProfileConcurrencyException RevisionConflict(long expectedRevision, long currentRevision) =>
        new(
            $"Profiles changed in another OeXYZ process (loaded revision {expectedRevision}, current revision {currentRevision}). Reload and retry.",
            expectedRevision,
            currentRevision);

    private static ProfileConcurrencyException EntityConflict(
        string description,
        Guid id,
        long expectedRevision,
        long currentRevision) =>
        new(
            $"The {description} '{id}' changed in another OeXYZ process. Reload the profiles and retry.",
            expectedRevision,
            currentRevision);
}
