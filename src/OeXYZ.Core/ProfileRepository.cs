using System.Text.Json;

namespace OeXYZ.Core;

public sealed class ProfileRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string path;

    public ProfileRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = System.IO.Path.GetFullPath(path);
    }

    public string Path => path;
    public string BackupPath => path + ".bak";

    public ProfileDocument Load()
    {
        if (!File.Exists(path)) return new ProfileDocument();
        using FileStream stream = File.OpenRead(path);
        ProfileDocument document = JsonSerializer.Deserialize<ProfileDocument>(stream, Options)
                                   ?? throw new InvalidDataException("The profile file is empty or invalid.");
        return document.Normalize();
    }

    public void Save(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ProfileDocument normalized = document.Normalize();
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";

        try
        {
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, normalized, Options);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Copy(path, BackupPath, overwrite: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
