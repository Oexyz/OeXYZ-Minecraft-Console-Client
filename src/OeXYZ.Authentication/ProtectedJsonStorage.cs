using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace OeXYZ.Authentication;

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
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        if (!File.Exists(path)) return null;
        byte[] encrypted = File.ReadAllBytes(path);
        byte[] clear = UnprotectOnWindows(encrypted);
        return JsonNode.Parse(clear);
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(node, serializerOptions);
        byte[] encrypted = ProtectOnWindows(clear);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, overwrite: true);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectOnWindows(byte[] clear) =>
        ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectOnWindows(byte[] encrypted) =>
        ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
}
