using System.IO.Compression;
using System.Runtime.ExceptionServices;

namespace OeXYZ.Updater;

public sealed record PreparedUpdate(string StagingDirectory, string GuiExecutable, string CliExecutable);

public static class UpdateInstaller
{
    private const long MaximumExtractedBytes = 750L * 1024 * 1024;

    public static void ExtractVerifiedArchive(string archivePath, string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        string root = Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(root);
        EnsureNoReparsePoint(root);
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
                throw new InvalidDataException("The update contains a symbolic-link entry.");
            if (entry.Length < 0 || entry.Length > MaximumExtractedBytes ||
                total > MaximumExtractedBytes - entry.Length)
                throw new InvalidDataException("The update exceeds the extraction safety limit.");
            total += entry.Length;
            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update contains an unsafe path.");
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    public static PreparedUpdate ValidateStage(string stagingDirectory)
    {
        string root = Path.GetFullPath(stagingDirectory);
        EnsureNoReparsePoint(root);
        string gui = Path.Combine(root, "OeXYZ Console Client.exe");
        string cli = Path.Combine(root, "oexyz.exe");
        if (!File.Exists(gui) || new FileInfo(gui).Length < 1024 * 1024)
            throw new InvalidDataException("The staged GUI executable is missing or invalid.");
        if (!File.Exists(cli) || new FileInfo(cli).Length < 1024 * 1024)
            throw new InvalidDataException("The staged CLI executable is missing or invalid.");
        return new PreparedUpdate(root, gui, cli);
    }

    public static string ApplyWithRollback(PreparedUpdate update, string installationDirectory)
        => ApplyWithRollback(update, installationDirectory, PhysicalUpdateFileSystem.Instance);

    internal static string ApplyWithRollback(
        PreparedUpdate update,
        string installationDirectory,
        IUpdateFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(fileSystem);
        string install = Path.GetFullPath(installationDirectory);
        fileSystem.CreateDirectory(install);
        EnsureNoReparsePoint(install, fileSystem);
        EnsureNoReparsePoint(update.StagingDirectory, fileSystem);
        string transactionId = Guid.NewGuid().ToString("N");
        string backup = Path.Combine(install, "update-backup-" + transactionId);
        fileSystem.CreateDirectory(backup);
        string[] names = ["OeXYZ Console Client.exe", "oexyz.exe"];
        List<Replacement> replacements = [];
        Exception? originalFailure = null;
        List<Exception> rollbackFailures = [];
        try
        {
            foreach (string name in names)
            {
                string source = Path.Combine(update.StagingDirectory, name);
                string destination = Path.Combine(install, name);
                string backupPath = Path.Combine(backup, name + ".bak");
                string temporary = destination + ".new-" + transactionId;
                if (!fileSystem.FileExists(source)) throw new FileNotFoundException("A staged update file is missing.", source);
                EnsureNoReparsePoint(source, fileSystem);
                if (fileSystem.FileExists(destination)) EnsureNoReparsePoint(destination, fileSystem);
                bool existed = fileSystem.FileExists(destination);
                Replacement replacement = new(destination, backupPath, temporary, existed);
                replacements.Add(replacement);
                if (existed) fileSystem.CopyFile(destination, backupPath, overwrite: false);
                fileSystem.CopyFile(source, temporary, overwrite: false);
                fileSystem.MoveFile(temporary, destination, overwrite: true);
                replacement.Applied = true;
            }
            return backup;
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            foreach (Replacement replacement in replacements.AsEnumerable().Reverse())
            {
                if (!replacement.Applied) continue;
                try
                {
                    if (replacement.ExistedBefore)
                    {
                        string restore = replacement.Destination + ".rollback-" + transactionId;
                        fileSystem.CopyFile(replacement.Backup, restore, overwrite: false);
                        try { fileSystem.MoveFile(restore, replacement.Destination, overwrite: true); }
                        finally { TryDeleteFile(restore, rollbackFailures, fileSystem); }
                    }
                    else if (fileSystem.FileExists(replacement.Destination))
                    {
                        fileSystem.DeleteFile(replacement.Destination);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(new IOException(
                        $"Rollback failed for '{Path.GetFileName(replacement.Destination)}'.",
                        rollbackException));
                }
            }
        }
        finally
        {
            foreach (Replacement replacement in replacements)
                TryDeleteFile(replacement.Temporary, rollbackFailures, fileSystem);
        }

        if (originalFailure is null) throw new InvalidOperationException("The update transaction ended unexpectedly.");
        if (rollbackFailures.Count == 0)
        {
            try { fileSystem.DeleteDirectory(backup, recursive: true); }
            catch (Exception cleanupException)
            {
                rollbackFailures.Add(new IOException("The failed update backup could not be cleaned up.", cleanupException));
            }
        }
        if (rollbackFailures.Count > 0)
        {
            List<Exception> failures = [originalFailure, .. rollbackFailures];
            throw new AggregateException("The update failed and rollback or cleanup was incomplete.", failures);
        }
        ExceptionDispatchInfo.Capture(originalFailure).Throw();
        throw new InvalidOperationException("Unreachable update rollback path.");
    }

    private static void TryDeleteFile(
        string path,
        List<Exception> failures,
        IUpdateFileSystem fileSystem)
    {
        try
        {
            if (fileSystem.FileExists(path)) fileSystem.DeleteFile(path);
        }
        catch (Exception exception)
        {
            failures.Add(new IOException($"Temporary update file '{Path.GetFileName(path)}' could not be removed.",
                exception));
        }
    }

    private static void EnsureNoReparsePoint(string path) =>
        EnsureNoReparsePoint(path, PhysicalUpdateFileSystem.Instance);

    private static void EnsureNoReparsePoint(string path, IUpdateFileSystem fileSystem)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((fileSystem.FileExists(current) || fileSystem.DirectoryExists(current)) &&
                (fileSystem.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths may not contain symbolic links or reparse points.");
            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    internal interface IUpdateFileSystem
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        FileAttributes GetAttributes(string path);
        void CreateDirectory(string path);
        void CopyFile(string source, string destination, bool overwrite);
        void MoveFile(string source, string destination, bool overwrite);
        void DeleteFile(string path);
        void DeleteDirectory(string path, bool recursive);
    }

    private sealed class PhysicalUpdateFileSystem : IUpdateFileSystem
    {
        public static PhysicalUpdateFileSystem Instance { get; } = new();
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void CopyFile(string source, string destination, bool overwrite) =>
            File.Copy(source, destination, overwrite);
        public void MoveFile(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    }

    private sealed class Replacement(
        string destination,
        string backup,
        string temporary,
        bool existedBefore)
    {
        public string Destination { get; } = destination;
        public string Backup { get; } = backup;
        public string Temporary { get; } = temporary;
        public bool ExistedBefore { get; } = existedBefore;
        public bool Applied { get; set; }
    }
}
