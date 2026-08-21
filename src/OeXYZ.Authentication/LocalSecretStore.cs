using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OeXYZ.Core;

namespace OeXYZ.Authentication;

public sealed class LocalSecretStore : IDisposable
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const int MaximumSecrets = 256;
    private const int MaximumSecretBytes = 4096;
    private static readonly byte[] WindowsEntropy = "OeXYZ.ConsoleClient.Secrets.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly string path;
    private readonly byte[]? linuxSecret;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    public LocalSecretStore(string path, ReadOnlySpan<byte> linuxSecret = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            if (linuxSecret.Length < 12)
                throw new ArgumentException("Linux secret storage requires a key/passphrase of at least 12 bytes.",
                    nameof(linuxSecret));
            this.linuxSecret = linuxSecret.ToArray();
        }
    }

    public async Task SetAsync(string reference, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        if (secret.Length is < 1 or > MaximumSecretBytes)
            throw new InvalidDataException("Secret values must contain 1-4096 bytes.");
        await MutateAsync(document =>
        {
            if (!document.ContainsKey(reference) && document.Count >= MaximumSecrets)
                throw new InvalidDataException("The secret store has reached its 256-entry limit.");
            document[reference] = Convert.ToBase64String(secret.Span);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        await MutateAsync(document => document.Remove(reference), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using FileStream fileLock = AcquireLock();
            Dictionary<string, string> document = ReadDocument();
            if (!document.TryGetValue(reference, out string? encoded)) return null;
            byte[] secret = Convert.FromBase64String(encoded);
            if (secret.Length is < 1 or > MaximumSecretBytes)
            {
                CryptographicOperations.ZeroMemory(secret);
                throw new InvalidDataException("A stored secret has an invalid length.");
            }
            return secret;
        }
        finally { gate.Release(); }
    }

    private async Task MutateAsync(Action<Dictionary<string, string>> mutation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using FileStream fileLock = AcquireLock();
            Dictionary<string, string> document = ReadDocument();
            mutation(document);
            WriteDocument(document);
        }
        finally { gate.Release(); }
    }

    private Dictionary<string, string> ReadDocument()
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        byte[] clear = ReadClear();
        try
        {
            Dictionary<string, string> document = JsonSerializer.Deserialize<Dictionary<string, string>>(clear, JsonOptions)
                ?? throw new InvalidDataException("The secret store is empty or invalid.");
            if (document.Count > MaximumSecrets) throw new InvalidDataException("The secret store has too many entries.");
            foreach ((string key, string value) in document)
            {
                ValidateReference(key);
                if (value.Length > MaximumSecretBytes * 2) throw new InvalidDataException("A stored secret is oversized.");
            }
            return new Dictionary<string, string>(document, StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The secret store contains invalid JSON.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private byte[] ReadClear()
    {
        if (OperatingSystem.IsWindows())
        {
            byte[] encrypted = File.ReadAllBytes(path);
            if (encrypted.Length > MaximumPayloadBytes + 16 * 1024)
                throw new InvalidDataException("The secret store exceeds its safety limit.");
            try { return ProtectedData.Unprotect(encrypted, WindowsEntropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        using EncryptedFileStorage storage = new(path, linuxSecret!, "OEXYZSC1", "secret store", MaximumPayloadBytes);
        return storage.Read() ?? [];
    }

    private void WriteDocument(Dictionary<string, string> document)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            if (clear.Length > MaximumPayloadBytes) throw new InvalidDataException("The secret store is oversized.");
            if (OperatingSystem.IsWindows())
            {
                byte[] encrypted = ProtectedData.Protect(clear, WindowsEntropy, DataProtectionScope.CurrentUser);
                try { PrivateFileSystem.WriteAllBytesAtomically(path, encrypted, path + ".bak"); }
                finally { CryptographicOperations.ZeroMemory(encrypted); }
            }
            else
            {
                using EncryptedFileStorage storage = new(
                    path, linuxSecret!, "OEXYZSC1", "secret store", MaximumPayloadBytes);
                storage.Write(clear);
            }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private FileStream AcquireLock()
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) PrivateFileSystem.EnsurePrivateDirectory(directory);
        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path + ".lock", options);
    }

    private static void ValidateReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128 ||
            reference.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidDataException("A secret reference must use 1-128 ASCII letters, digits, '.', '-' or '_'.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (linuxSecret is not null) CryptographicOperations.ZeroMemory(linuxSecret);
        gate.Dispose();
    }
}
