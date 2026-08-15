using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace OeXYZ.Authentication;

internal sealed class EncryptedJsonStorage : IJsonStorage, IDisposable
{
    private const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private readonly EncryptedFileStorage storage;

    public EncryptedJsonStorage(string path, ReadOnlySpan<byte> secret)
    {
        storage = new EncryptedFileStorage(
            path, secret, "OEXYZAC2", "account store", MaximumPayloadBytes);
    }

    public JsonNode? ReadAsJsonNode()
    {
        byte[]? clear = storage.Read();
        if (clear is null) return null;
        try
        {
            return JsonNode.Parse(clear, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(node);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(node, serializerOptions);
        try
        {
            if (clear.Length > MaximumPayloadBytes)
                throw new InvalidDataException("The protected account document exceeds the 8 MiB safety limit.");
            storage.Write(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Dispose() => storage.Dispose();
}
