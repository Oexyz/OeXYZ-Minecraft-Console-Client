using OeXYZ.Core;

namespace OeXYZ.Authentication;

/// <summary>
/// Serializes complete account-store read/modify/write transactions across
/// OeXYZ processes. The sidecar is intentionally retained so its path and
/// inode remain stable for every participant.
/// </summary>
internal static class AccountStoreLock
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static ValueTask<FileStream> AcquireAsync(
        string accountStorePath,
        CancellationToken cancellationToken) =>
        AcquireAsync(
            accountStorePath,
            cancellationToken,
            static (path, options) => new FileStream(path, options));

    internal static async ValueTask<FileStream> AcquireAsync(
        string accountStorePath,
        CancellationToken cancellationToken,
        Func<string, FileStreamOptions, FileStream> openLockFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountStorePath);
        ArgumentNullException.ThrowIfNull(openLockFile);
        string storePath = Path.GetFullPath(accountStorePath);
        string? directory = Path.GetDirectoryName(storePath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException("The account-store path has no parent directory.");
        PrivateFileSystem.EnsurePrivateDirectory(directory);

        string lockPath = storePath + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
                return openLockFile(lockPath, options);
            }
            catch (IOException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsRetryableLockException(exception)) throw;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsRetryableLockException(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int nativeError = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? nativeError is 32 or 33 // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
            : nativeError is 11 or 35; // EAGAIN/EWOULDBLOCK on Linux/macOS
    }
}
