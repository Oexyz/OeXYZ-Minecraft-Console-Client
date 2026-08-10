using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace OeXYZ.ConsoleClient;

internal sealed class ProtectedJsonStorage : IJsonStorage
{
    private static readonly byte[] Entropy = "OeXYZ.ConsoleClient.Accounts.v1"u8.ToArray();
    private readonly string path;

    public ProtectedJsonStorage(string path)
    {
        this.path = path;
    }

    public JsonNode? ReadAsJsonNode()
    {
        if (!File.Exists(path)) return null;
        byte[] encrypted = File.ReadAllBytes(path);
        byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return JsonNode.Parse(clear);
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(node, serializerOptions);
        byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, overwrite: true);
    }
}
