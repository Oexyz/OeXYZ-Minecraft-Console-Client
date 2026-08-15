using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OeXYZ.Session;

public sealed class LoopbackHealthServer : IAsyncDisposable
{
    private const int MaximumRequestBytes = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SessionRuntimeRegistry registry;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim clientLimit = new(8, 8);
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private Task? acceptTask;
    private int nextClientId;

    public LoopbackHealthServer(SessionRuntimeRegistry registry, int port)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.registry = registry;
        listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => acceptTask is null
        ? 0
        : ((IPEndPoint)listener.LocalEndpoint).Port;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (acceptTask is not null) throw new InvalidOperationException("The health server is already running.");
        cancellationToken.ThrowIfCancellationRequested();
        listener.Start(backlog: 16);
        acceptTask = AcceptLoopAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                try { await clientLimit.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    client.Dispose();
                    throw;
                }
                int id = Interlocked.Increment(ref nextClientId);
                Task task = HandleClientSafelyAsync(client, cancellationToken);
                clients[id] = task;
                _ = task.ContinueWith(
                    completed =>
                    {
                        clients.TryRemove(id, out _);
                        clientLimit.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleClientAsync(client, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            NetworkStream stream = client.GetStream();
            byte[] request = new byte[MaximumRequestBytes];
            int length = 0;
            while (length < request.Length)
            {
                int read = await stream.ReadAsync(request.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) return;
                length += read;
                if (request.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
            }
            if (request.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            {
                await WriteResponseAsync(stream, 413, "text/plain", "Request too large.\n"u8.ToArray(), false, timeout.Token)
                    .ConfigureAwait(false);
                return;
            }

            int lineEnd = request.AsSpan(0, length).IndexOf("\r\n"u8);
            if (lineEnd <= 0)
            {
                await WriteResponseAsync(stream, 400, "text/plain", "Bad request.\n"u8.ToArray(), false, timeout.Token)
                    .ConfigureAwait(false);
                return;
            }
            string[] firstLine = Encoding.ASCII.GetString(request, 0, lineEnd)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (firstLine.Length != 3 || firstLine[0] is not ("GET" or "HEAD"))
            {
                await WriteResponseAsync(stream, 405, "text/plain", "Method not allowed.\n"u8.ToArray(), false, timeout.Token)
                    .ConfigureAwait(false);
                return;
            }

            bool head = firstLine[0] == "HEAD";
            RuntimeHealthSnapshot snapshot = registry.Snapshot();
            (int status, byte[] body) = firstLine[1] switch
            {
                "/health" => (snapshot.Healthy ? 200 : 503, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    status = snapshot.Healthy ? "healthy" : "unhealthy",
                    snapshot.ActiveSessions,
                    snapshot.ConnectedSessions,
                    snapshot.UptimeSeconds
                }, JsonOptions)),
                "/ready" => (snapshot.Ready ? 200 : 503, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    status = snapshot.Ready ? "ready" : "not-ready",
                    snapshot.ConnectedSessions,
                    snapshot.ActiveSessions
                }, JsonOptions)),
                "/status" => (200, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions)),
                _ => (404, "Not found.\n"u8.ToArray())
            };
            string contentType = firstLine[1] is "/health" or "/ready" or "/status"
                ? "application/json"
                : "text/plain";
            await WriteResponseAsync(stream, status, contentType, body, head, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int status,
        string contentType,
        byte[] body,
        bool head,
        CancellationToken cancellationToken)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            503 => "Service Unavailable",
            _ => "Error"
        };
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!head) await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        if (acceptTask is not null)
        {
            try { await acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Task[] active = clients.Values.ToArray();
        if (active.Length > 0)
        {
            try { await Task.WhenAll(active).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        clientLimit.Dispose();
        lifetime.Dispose();
    }
}
