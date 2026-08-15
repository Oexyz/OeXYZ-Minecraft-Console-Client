using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using OeXYZ.Core;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace OeXYZ.Authentication;

internal sealed class ProtectedJsonStorage : IJsonStorage
{
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    internal const int MaximumEncryptedBytes = MaximumPayloadBytes + 16 * 1024;
    private const int MaximumJsonDepth = 64;
    private static readonly byte[] Entropy = "OeXYZ.ConsoleClient.Accounts.v1"u8.ToArray();
    private readonly string path;

    public ProtectedJsonStorage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public JsonNode? ReadAsJsonNode()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        if (!File.Exists(path)) return null;
        byte[] encrypted = ReadBounded(path);
        try
        {
            byte[] clear = UnprotectOnWindows(encrypted);
            try
            {
                if (clear.Length > MaximumPayloadBytes)
                    throw new InvalidDataException("The protected account document exceeds the 8 MiB safety limit.");
                return JsonNode.Parse(clear, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                });
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        ArgumentNullException.ThrowIfNull(node);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(node, serializerOptions);
        try
        {
            if (clear.Length > MaximumPayloadBytes)
                throw new InvalidDataException("The protected account document exceeds the 8 MiB safety limit.");
            byte[] encrypted = ProtectOnWindows(clear);
            try
            {
                if (encrypted.Length > MaximumEncryptedBytes)
                    throw new InvalidDataException("The protected account ciphertext exceeds its safety limit.");
                PrivateFileSystem.WriteAllBytesAtomically(path, encrypted);
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectOnWindows(byte[] clear) =>
        ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectOnWindows(byte[] encrypted) =>
        ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] ReadBounded(string target)
    {
        using FileStream stream = new(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumEncryptedBytes)
            throw new InvalidDataException("The protected account ciphertext exceeds its safety limit.");
        byte[] encrypted = new byte[checked((int)stream.Length)];
        try
        {
            stream.ReadExactly(encrypted);
            if (stream.ReadByte() >= 0)
                throw new InvalidDataException("The protected account ciphertext changed while it was read.");
            return encrypted;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(encrypted);
            throw;
        }
    }
}
