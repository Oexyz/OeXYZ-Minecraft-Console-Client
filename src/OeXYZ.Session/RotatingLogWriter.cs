using System.Text;
using OeXYZ.Core;

namespace OeXYZ.Session;

internal sealed class RotatingLogWriter : IAsyncDisposable
{
    private readonly string basePath;
    private readonly long maximumBytes;
    private int part = 1;
    private StreamWriter writer;

    public RotatingLogWriter(string basePath, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.basePath = Path.GetFullPath(basePath);
        this.maximumBytes = maximumBytes;
        CurrentPath = this.basePath;
        writer = Open(CurrentPath);
    }

    public string CurrentPath { get; private set; }

    public static string ReserveUniquePath(string directory, string fileStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);
        string fullDirectory = Path.GetFullPath(directory);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string candidate = Path.Combine(fullDirectory, $"{fileStem}-{Guid.NewGuid():N}.log");
            try
            {
                using FileStream reservation = new(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.None);
                PrivateFileSystem.ProtectFile(candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // The CreateNew reservation is the authority. A collision is
                // extraordinarily unlikely, but retry without ever sharing a log.
            }
        }
        throw new IOException("A unique session log path could not be reserved.");
    }

    public async ValueTask WriteLineAsync(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        long encodedLength = Encoding.UTF8.GetByteCount(value) + 2L;
        if (writer.BaseStream.Length > 0 && writer.BaseStream.Length + encodedLength > maximumBytes)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            part++;
            CurrentPath = Path.Combine(
                Path.GetDirectoryName(basePath)!,
                $"{Path.GetFileNameWithoutExtension(basePath)}-part{part}.log");
            writer = Open(CurrentPath);
        }
        await writer.WriteLineAsync(value).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static StreamWriter Open(string path)
    {
        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        PrivateFileSystem.ProtectFile(path);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public async ValueTask DisposeAsync() => await writer.DisposeAsync().ConfigureAwait(false);
}
