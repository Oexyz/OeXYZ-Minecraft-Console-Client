using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OeXYZ.Core;

namespace OeXYZ.Authentication;

internal sealed class EncryptedFileStorage : IDisposable
{
    private const byte FormatVersion = 1;
    private const int MagicBytes = 8;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = MagicBytes + 1 + SaltBytes + NonceBytes + sizeof(int);
    private const int Pbkdf2Iterations = 600_000;

    private readonly string path;
    private readonly byte[] magic;
    private readonly string description;
    private readonly int maximumPayloadBytes;
    private readonly byte[] salt;
    private readonly byte[] key;
    private int disposed;

    public EncryptedFileStorage(
        string path,
        ReadOnlySpan<byte> secret,
        string magic,
        string description,
        int maximumPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(magic);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (Encoding.ASCII.GetByteCount(magic) != MagicBytes || magic.Any(character => character > 0x7f))
            throw new ArgumentException("The encrypted-file magic must contain exactly eight ASCII characters.", nameof(magic));
        if (secret.Length < 12)
            throw new ArgumentException("The Linux account-storage passphrase must contain at least 12 bytes.", nameof(secret));
        if (maximumPayloadBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        this.path = Path.GetFullPath(path);
        this.magic = Encoding.ASCII.GetBytes(magic);
        this.description = description;
        this.maximumPayloadBytes = maximumPayloadBytes;
        salt = File.Exists(this.path) ? ReadSalt(this.path) : RandomNumberGenerator.GetBytes(SaltBytes);
        key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
    }

    public byte[]? Read()
    {
        ThrowIfDisposed();
        if (!File.Exists(path)) return null;
        byte[] envelope = ReadBounded(path);
        try
        {
            ValidateEnvelope(envelope);
            int length = BinaryPrimitives.ReadInt32LittleEndian(
                envelope.AsSpan(HeaderBytes - sizeof(int), sizeof(int)));
            byte[] clear = new byte[length];
            try
            {
                using AesGcm aes = new(key, TagBytes);
                aes.Decrypt(
                    envelope.AsSpan(MagicBytes + 1 + SaltBytes, NonceBytes),
                    envelope.AsSpan(HeaderBytes, length),
                    envelope.AsSpan(HeaderBytes + length, TagBytes),
                    clear,
                    envelope.AsSpan(0, HeaderBytes));
                return clear;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(clear);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public void Write(ReadOnlySpan<byte> clear)
    {
        ThrowIfDisposed();
        if (clear.Length > maximumPayloadBytes)
            throw new InvalidDataException($"The {description} exceeds its {FormatBytes(maximumPayloadBytes)} safety limit.");

        byte[] envelope = new byte[HeaderBytes + clear.Length + TagBytes];
        try
        {
            magic.CopyTo(envelope, 0);
            envelope[MagicBytes] = FormatVersion;
            salt.CopyTo(envelope, MagicBytes + 1);
            Span<byte> nonce = envelope.AsSpan(MagicBytes + 1 + SaltBytes, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            BinaryPrimitives.WriteInt32LittleEndian(
                envelope.AsSpan(HeaderBytes - sizeof(int), sizeof(int)), clear.Length);
            using (AesGcm aes = new(key, TagBytes))
            {
                aes.Encrypt(
                    nonce,
                    clear,
                    envelope.AsSpan(HeaderBytes, clear.Length),
                    envelope.AsSpan(HeaderBytes + clear.Length, TagBytes),
                    envelope.AsSpan(0, HeaderBytes));
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) PrivateFileSystem.EnsurePrivateDirectory(directory);
            string backup = path + ".bak";
            PrivateFileSystem.WriteAllBytesAtomically(path, envelope, backup);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private void ValidateEnvelope(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderBytes + TagBytes || !envelope[..MagicBytes].SequenceEqual(magic))
            throw new InvalidDataException($"The {description} is not a compatible encrypted OeXYZ file.");
        if (envelope[MagicBytes] != FormatVersion)
            throw new InvalidDataException($"Unsupported encrypted {description} format {envelope[MagicBytes]}.");
        if (!envelope.Slice(MagicBytes + 1, SaltBytes).SequenceEqual(salt))
            throw new InvalidDataException($"The encrypted {description} salt changed unexpectedly.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(
            envelope.Slice(HeaderBytes - sizeof(int), sizeof(int)));
        if (length < 0 || length > maximumPayloadBytes || envelope.Length != HeaderBytes + length + TagBytes)
            throw new InvalidDataException($"The encrypted {description} length is invalid or exceeds the safety limit.");
    }

    private byte[] ReadBounded(string target)
    {
        FileInfo info = new(target);
        if (info.Length > HeaderBytes + maximumPayloadBytes + TagBytes)
            throw new InvalidDataException($"The encrypted {description} exceeds the {FormatBytes(maximumPayloadBytes)} safety limit.");
        return File.ReadAllBytes(target);
    }

    private byte[] ReadSalt(string target)
    {
        byte[] envelope = ReadBounded(target);
        try
        {
            if (envelope.Length < HeaderBytes + TagBytes || !envelope.AsSpan(0, MagicBytes).SequenceEqual(magic))
                throw new InvalidDataException($"The {description} is not a compatible encrypted OeXYZ file.");
            if (envelope[MagicBytes] != FormatVersion)
                throw new InvalidDataException($"Unsupported encrypted {description} format {envelope[MagicBytes]}.");
            return envelope.AsSpan(MagicBytes + 1, SaltBytes).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static string FormatBytes(int bytes) => bytes % (1024 * 1024) == 0
        ? $"{bytes / (1024 * 1024)} MiB"
        : $"{bytes} byte";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(magic);
    }
}
