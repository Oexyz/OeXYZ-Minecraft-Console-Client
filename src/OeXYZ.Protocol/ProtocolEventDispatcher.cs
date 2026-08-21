using System.Threading.Channels;

namespace OeXYZ.Protocol;

internal sealed class ProtocolEventDispatcher : IAsyncDisposable
{
    internal const int CriticalCapacity = 256;
    internal const int NormalCapacity = 1024;
    private const int MaximumCriticalBurst = 8;
    private readonly Channel<Action> critical = CreateChannel(CriticalCapacity);
    private readonly Channel<Action> normal = CreateChannel(NormalCapacity);
    private readonly Action<Exception> subscriberFailed;
    private readonly Task worker;
    private long dropped;
    private int disposed;

    public ProtocolEventDispatcher(Action<Exception> subscriberFailed)
    {
        this.subscriberFailed = subscriberFailed ?? throw new ArgumentNullException(nameof(subscriberFailed));
        worker = RunAsync();
    }

    public long Dropped => Interlocked.Read(ref dropped);

    public void Publish(Action callback, bool isCritical = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Volatile.Read(ref disposed) != 0) return;
        ChannelWriter<Action> writer = isCritical ? critical.Writer : normal.Writer;
        if (!writer.TryWrite(callback)) Interlocked.Increment(ref dropped);
    }

    private async Task RunAsync()
    {
        int criticalBurst = 0;
        while (true)
        {
            Action? callback = null;
            if (criticalBurst < MaximumCriticalBurst && critical.Reader.TryRead(out callback))
            {
                criticalBurst++;
            }
            else if (normal.Reader.TryRead(out callback))
            {
                criticalBurst = 0;
            }
            else if (critical.Reader.TryRead(out callback))
            {
                criticalBurst = 1;
            }
            else
            {
                if (critical.Reader.Completion.IsCompleted && normal.Reader.Completion.IsCompleted) break;
                Task<bool> criticalReady = critical.Reader.WaitToReadAsync().AsTask();
                Task<bool> normalReady = normal.Reader.WaitToReadAsync().AsTask();
                Task<bool> completed = await Task.WhenAny(criticalReady, normalReady).ConfigureAwait(false);
                _ = await completed.ConfigureAwait(false);
                continue;
            }

            try { callback(); }
            catch (Exception exception)
            {
                try { subscriberFailed(exception); }
                catch { }
            }
        }
    }

    private static Channel<Action> CreateChannel(int capacity) =>
        Channel.CreateBounded<Action>(new BoundedChannelOptions(capacity)
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
        await worker.ConfigureAwait(false);
    }
}
