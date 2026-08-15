namespace OeXYZ.Core;

public static class PrivateFileSystem
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        // The Unix mode overload applies the requested mode only to directories it
        // creates. In particular, it does not chmod an existing caller-owned parent
        // such as /tmp, ~/Downloads, or a shared mount.
        Directory.CreateDirectory(path, PrivateDirectoryMode);
    }

    public static void ProtectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, PrivateFileMode);
    }

    public static bool HasPrivateUnixPermissions(string path)
    {
        if (OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path))) return true;
        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                               UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & exposed) == 0;
    }

    public static void WriteAllBytesAtomically(
        string path,
        ReadOnlySpan<byte> content,
        string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string target = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException("The private file path has no parent directory.");
        EnsurePrivateDirectory(directory);

        string? backup = string.IsNullOrWhiteSpace(backupPath) ? null : Path.GetFullPath(backupPath);
        if (backup is not null && string.Equals(target, backup, PathComparison))
            throw new ArgumentException("The backup path must differ from the destination path.", nameof(backupPath));
        if (backup is not null)
        {
            string? backupDirectory = Path.GetDirectoryName(backup);
            if (string.IsNullOrEmpty(backupDirectory))
                throw new InvalidDataException("The private backup path has no parent directory.");
            EnsurePrivateDirectory(backupDirectory);
        }

        string temporary = UniqueTemporaryPath(target);
        string? backupTemporary = backup is not null && File.Exists(target)
            ? UniqueTemporaryPath(backup)
            : null;
        try
        {
            WriteNewPrivateFile(temporary, content);
            if (backup is not null && backupTemporary is not null)
            {
                CopyToNewPrivateFile(target, backupTemporary);
                File.Move(backupTemporary, backup, overwrite: true);
                ProtectFile(backup);
            }

            File.Move(temporary, target, overwrite: true);
            ProtectFile(target);
        }
        finally
        {
            DeleteTemporary(temporary);
            if (backupTemporary is not null) DeleteTemporary(backupTemporary);
        }
    }

    private static void WriteNewPrivateFile(string destination, ReadOnlySpan<byte> content)
    {
        using FileStream stream = CreateNewPrivateFile(destination);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        ProtectFile(destination);
    }

    private static void CopyToNewPrivateFile(string source, string destination)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using FileStream output = CreateNewPrivateFile(destination);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
        ProtectFile(destination);
    }

    private static FileStream CreateNewPrivateFile(string destination)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        return new FileStream(destination, options);
    }

    private static string UniqueTemporaryPath(string target) =>
        $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    private static void DeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
