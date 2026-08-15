using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using OeXYZ.Core;

namespace OeXYZ.Authentication;

public static class AccountKeyFile
{
    public static void Generate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException("The account-key path has no parent directory.");
        PrivateFileSystem.EnsurePrivateDirectory(directory);

        byte[] random = RandomNumberGenerator.GetBytes(32);
        byte[] encoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(random.Length));
        try
        {
            OperationStatus status = Base64.EncodeToUtf8(random, encoded, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != random.Length)
                throw new CryptographicException("The account key could not be encoded.");
            using FileStream file = new(fullPath, CreatePrivateFileOptions());
            file.Write(encoded, 0, written);
            file.Flush(flushToDisk: true);
            PrivateFileSystem.ProtectFile(fullPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
            CryptographicOperations.ZeroMemory(encoded);
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    internal static FileStreamOptions CreatePrivateFileOptions()
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
