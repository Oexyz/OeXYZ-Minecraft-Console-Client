using System.Security.Cryptography;
using System.Text;

namespace OeXYZ.Core;

public static class ControlTokenFile
{
    public const int TokenBytes = 32;
    public const int MaximumFileBytes = 256;

    public static void Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] token = RandomNumberGenerator.GetBytes(TokenBytes);
        try
        {
            byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(token) + Environment.NewLine);
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("The control-token path has no parent.");
            PrivateFileSystem.EnsurePrivateDirectory(directory);
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using FileStream output = new(fullPath, options);
            output.Write(encoded);
            output.Flush(flushToDisk: true);
            PrivateFileSystem.ProtectFile(fullPath);
            CryptographicOperations.ZeroMemory(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    public static byte[] Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        FileInfo info = new(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The control-token file does not exist.", fullPath);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("The control-token file size is invalid.");
        if (!PrivateFileSystem.HasPrivateUnixPermissions(fullPath))
            throw new UnauthorizedAccessException("The control-token file must be private (0600 on Linux).");
        string encoded = File.ReadAllText(fullPath, Encoding.ASCII).Trim();
        byte[] token;
        try { token = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new InvalidDataException("The control-token file is invalid.", exception); }
        if (token.Length != TokenBytes)
        {
            CryptographicOperations.ZeroMemory(token);
            throw new InvalidDataException("The control token must contain exactly 256 bits.");
        }
        return token;
    }
}
