using System.Net.Sockets;
using System.Text;

namespace OeXYZ.Cli;

internal sealed class SystemdNotifier : IAsyncDisposable
{
    private readonly Socket socket;
    private readonly UnixDomainSocketEndPoint endpoint;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Task? watchdogTask;

    private SystemdNotifier(Socket socket, UnixDomainSocketEndPoint endpoint, TimeSpan? watchdogInterval)
    {
        this.socket = socket;
        this.endpoint = endpoint;
        if (watchdogInterval is TimeSpan interval) watchdogTask = WatchdogLoopAsync(interval, lifetime.Token);
    }

    public static async Task<SystemdNotifier?> TryStartAsync(
        int sessionCount,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return null;
        string? notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrWhiteSpace(notifySocket)) return null;
        string endpointPath = notifySocket[0] == '@' ? "\0" + notifySocket[1..] : notifySocket;
        Socket socket = new(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        SystemdNotifier notifier = new(socket, new UnixDomainSocketEndPoint(endpointPath), ReadWatchdogInterval());
        try
        {
            await notifier.SendAsync(
                $"READY=1\nMAINPID={Environment.ProcessId}\nSTATUS=Supervising {sessionCount} Minecraft session(s)",
                cancellationToken).ConfigureAwait(false);
            return notifier;
        }
        catch
        {
            await notifier.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendStatusAsync(string status, CancellationToken cancellationToken = default) =>
        await SendAsync("STATUS=" + status.Replace('\n', ' ').Replace('\r', ' '), cancellationToken).ConfigureAwait(false);

    private static TimeSpan? ReadWatchdogInterval()
    {
        string? watchdogPid = Environment.GetEnvironmentVariable("WATCHDOG_PID");
        if (int.TryParse(watchdogPid, out int expectedPid) && expectedPid != Environment.ProcessId) return null;
        if (!ulong.TryParse(Environment.GetEnvironmentVariable("WATCHDOG_USEC"), out ulong microseconds) || microseconds == 0)
            return null;
        double milliseconds = Math.Clamp(microseconds / 2000D, 250D, TimeSpan.FromMinutes(1).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private async Task WatchdogLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SendAsync("WATCHDOG=1", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException)
        {
            // systemd may disappear during shutdown. Notification transport failure
            // must not prevent the Minecraft sessions from stopping cleanly.
        }
        catch (ObjectDisposedException) { }
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _ = await socket.SendToAsync(data, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false); }
        finally { sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (watchdogTask is not null) await watchdogTask.ConfigureAwait(false);
        try { await SendAsync("STOPPING=1\nSTATUS=Stopping OeXYZ sessions", CancellationToken.None).ConfigureAwait(false); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        socket.Dispose();
        sendLock.Dispose();
        lifetime.Dispose();
    }
}
