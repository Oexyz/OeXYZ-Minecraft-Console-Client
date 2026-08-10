using System.Text.Json;

namespace OeXYZ.ConsoleClient;

internal sealed class ProfileRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ProfileDocument Load()
    {
        if (!File.Exists(AppPaths.Profiles)) return new ProfileDocument();
        using FileStream stream = File.OpenRead(AppPaths.Profiles);
        return JsonSerializer.Deserialize<ProfileDocument>(stream, Options)
               ?? throw new InvalidDataException("The profile file is empty or invalid.");
    }

    public void Save(ProfileDocument document)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temporary = AppPaths.Profiles + ".tmp";
        using (FileStream stream = File.Create(temporary)) JsonSerializer.Serialize(stream, document, Options);
        File.Move(temporary, AppPaths.Profiles, overwrite: true);
    }
}
