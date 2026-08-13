using System.Security.Cryptography;

namespace OeXYZ.Protocol;

internal sealed class MinecraftCfb8Stream : Stream
{
    private const int BlockBytes = 16;
    private readonly Stream inner;
    private readonly Aes aes;
    private readonly byte[] feedback;
    private readonly bool decrypt;
    private readonly bool leaveOpen;
    private bool disposed;

    public MinecraftCfb8Stream(Stream inner, byte[] secret, bool decrypt, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != BlockBytes) throw new ArgumentException("Minecraft AES secrets are 16 bytes.", nameof(secret));
        this.inner = inner;
        this.decrypt = decrypt;
        this.leaveOpen = leaveOpen;
        feedback = secret.ToArray();
        aes = Aes.Create();
        aes.Key = secret;
    }

    public override bool CanRead => decrypt && !disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !decrypt && !disposed && inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateRead();
        int read = inner.Read(buffer, offset, count);
        if (read > 0) DecryptInPlace(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ValidateRead();
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) DecryptInPlace(buffer.Span[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateWrite();
        if (count == 0) return;
        byte[] encrypted = new byte[count];
        Encrypt(buffer.AsSpan(offset, count), encrypted);
        inner.Write(encrypted);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite();
        if (buffer.Length == 0) return;
        byte[] encrypted = new byte[buffer.Length];
        Encrypt(buffer.Span, encrypted);
        await inner.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
    }

    private void Encrypt(ReadOnlySpan<byte> clear, Span<byte> encrypted)
    {
        aes.EncryptCfb(clear, feedback, encrypted, PaddingMode.None, feedbackSizeInBits: 8);
        AdvanceFeedback(encrypted);
    }

    private void DecryptInPlace(Span<byte> encrypted)
    {
        byte[] cipher = encrypted.ToArray();
        aes.DecryptCfb(cipher, feedback, encrypted, PaddingMode.None, feedbackSizeInBits: 8);
        AdvanceFeedback(cipher);
    }

    private void AdvanceFeedback(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length >= BlockBytes)
        {
            ciphertext[^BlockBytes..].CopyTo(feedback);
            return;
        }
        feedback.AsSpan(ciphertext.Length).CopyTo(feedback);
        ciphertext.CopyTo(feedback.AsSpan(BlockBytes - ciphertext.Length));
    }

    private void ValidateRead()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!decrypt) throw new NotSupportedException("This encryption stream is write-only.");
    }

    private void ValidateWrite()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (decrypt) throw new NotSupportedException("This encryption stream is read-only.");
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        if (disposing)
        {
            aes.Dispose();
            if (!leaveOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        aes.Dispose();
        if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
