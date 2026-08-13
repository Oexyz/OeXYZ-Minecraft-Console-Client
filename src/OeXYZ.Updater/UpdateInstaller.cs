using System.IO.Compression;

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
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
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
        string gui = Path.Combine(root, "OeXYZ Console Client.exe");
        string cli = Path.Combine(root, "oexyz.exe");
        if (!File.Exists(gui) || new FileInfo(gui).Length < 1024 * 1024)
            throw new InvalidDataException("The staged GUI executable is missing or invalid.");
        if (!File.Exists(cli) || new FileInfo(cli).Length < 1024 * 1024)
            throw new InvalidDataException("The staged CLI executable is missing or invalid.");
        return new PreparedUpdate(root, gui, cli);
    }

    public static string ApplyWithRollback(PreparedUpdate update, string installationDirectory)
    {
        string install = Path.GetFullPath(installationDirectory);
        Directory.CreateDirectory(install);
        string backup = Path.Combine(install, "update-backup");
        Directory.CreateDirectory(backup);
        string[] names = ["OeXYZ Console Client.exe", "oexyz.exe"];
        List<(string Destination, string Backup)> replaced = [];
        try
        {
            foreach (string name in names)
            {
                string source = Path.Combine(update.StagingDirectory, name);
                string destination = Path.Combine(install, name);
                string backupPath = Path.Combine(backup, name + ".bak");
                if (File.Exists(destination)) File.Copy(destination, backupPath, overwrite: true);
                string temporary = destination + ".new";
                File.Copy(source, temporary, overwrite: true);
                File.Move(temporary, destination, overwrite: true);
                replaced.Add((destination, backupPath));
            }
            return backup;
        }
        catch
        {
            foreach ((string destination, string backupPath) in replaced.AsEnumerable().Reverse())
                if (File.Exists(backupPath)) File.Copy(backupPath, destination, overwrite: true);
            throw;
        }
    }
}
