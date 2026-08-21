using System.Threading.Channels;

namespace OeXYZ.Protocol;

internal enum OutboundPacketPriority
{
    Critical,
    Normal
}

internal sealed class OutboundPacketDispatcher : IAsyncDisposable
{
    internal const int CriticalCapacity = 128;
    internal const int NormalCapacity = 128;
    private const int MaximumCriticalBurst = 8;
    private readonly MinecraftPacketStream stream;
    private readonly Channel<OutboundPacketRequest> critical = CreateChannel(CriticalCapacity);
    private readonly Channel<OutboundPacketRequest> normal = CreateChannel(NormalCapacity);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task writerTask;
    private Exception? terminalException;
    private int disposed;

    public OutboundPacketDispatcher(MinecraftPacketStream stream)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        writerTask = RunWriterAsync();
    }

    public async ValueTask SendAsync(
        int packetId,
        Action<PacketWriter>? writePayload,
        CancellationToken cancellationToken,
        OutboundPacketPriority priority)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Exception? failure = Volatile.Read(ref terminalException);
        if (failure is not null) throw new IOException("The outbound packet writer has stopped.", failure);

        OutboundPacketRequest request = new(packetId, writePayload, cancellationToken);
        ChannelWriter<OutboundPacketRequest> writer = priority == OutboundPacketPriority.Critical
            ? critical.Writer
            : normal.Writer;
        try
        {
            await writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new IOException("The outbound packet queue is closed.", terminalException ?? exception);
        }
        await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWriterAsync()
    {
        int criticalBurst = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                OutboundPacketRequest? request = null;
                if (criticalBurst < MaximumCriticalBurst && critical.Reader.TryRead(out request))
                {
                    criticalBurst++;
                }
                else if (normal.Reader.TryRead(out request))
                {
                    criticalBurst = 0;
                }
                else if (critical.Reader.TryRead(out request))
                {
                    criticalBurst = 1;
                }
                else
                {
                    Task<bool> criticalReady = critical.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                    Task<bool> normalReady = normal.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                    Task<bool> completed = await Task.WhenAny(criticalReady, normalReady).ConfigureAwait(false);
                    if (!await completed.ConfigureAwait(false) &&
                        critical.Reader.Completion.IsCompleted && normal.Reader.Completion.IsCompleted)
                        break;
                    continue;
                }

                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.CancellationToken);
                    continue;
                }
                try
                {
                    await stream.WriteDirectAsync(
                        request.PacketId,
                        request.WritePayload,
                        request.CancellationToken).ConfigureAwait(false);
                    request.Completion.TrySetResult();
                }
                catch (OperationCanceledException exception)
                {
                    request.Completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                    Interlocked.CompareExchange(ref terminalException, exception, null);
                    critical.Writer.TryComplete(exception);
                    normal.Writer.TryComplete(exception);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Exception completion = terminalException ?? new OperationCanceledException(
                "The outbound packet dispatcher stopped.", lifetime.Token);
            CompletePending(critical.Reader, completion);
            CompletePending(normal.Reader, completion);
        }
    }

    private static void CompletePending(ChannelReader<OutboundPacketRequest> reader, Exception exception)
    {
        while (reader.TryRead(out OutboundPacketRequest? request))
        {
            if (exception is OperationCanceledException canceled)
                request.Completion.TrySetCanceled(canceled.CancellationToken);
            else
                request.Completion.TrySetException(exception);
        }
    }

    private static Channel<OutboundPacketRequest> CreateChannel(int capacity) =>
        Channel.CreateBounded<OutboundPacketRequest>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        critical.Writer.TryComplete();
        normal.Writer.TryComplete();
        lifetime.Cancel();
        try { await writerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
    }

    private sealed record OutboundPacketRequest(
        int PacketId,
        Action<PacketWriter>? WritePayload,
        CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
