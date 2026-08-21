using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OeXYZ.Core;

namespace OeXYZ.Session;

public sealed class LoopbackHealthServer : IAsyncDisposable
{
    internal const int MaximumHeaderBytes = 8192;
    internal const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumConcurrentClients = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = 16
    };

    private readonly SessionRuntimeRegistry registry;
    private readonly ISessionControlManager? sessionManager;
    private readonly byte[]? tokenHash;
    private readonly bool requireReadAuthentication;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim clientLimit = new(MaximumConcurrentClients, MaximumConcurrentClients);
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly object rateGate = new();
    private DateTimeOffset rateWindow = DateTimeOffset.UtcNow;
    private int writesInWindow;
    private long authenticationFailures;
    private Task? acceptTask;
    private int nextClientId;

    public LoopbackHealthServer(SessionRuntimeRegistry registry, int port)
        : this(registry, port, null, null, allowRemote: false)
    {
    }

    public LoopbackHealthServer(
        SessionRuntimeRegistry registry,
        int port,
        ISessionControlManager? sessionManager,
        byte[]? controlToken,
        bool allowRemote = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (allowRemote && (controlToken is null || controlToken.Length != 32))
            throw new InvalidOperationException("Remote management requires a valid 256-bit control token.");
        if (controlToken is not null && controlToken.Length != 32)
            throw new ArgumentException("The control token must contain 256 bits.", nameof(controlToken));
        this.registry = registry;
        this.sessionManager = sessionManager;
        tokenHash = controlToken is null ? null : SHA256.HashData(controlToken);
        requireReadAuthentication = allowRemote;
        listener = new TcpListener(allowRemote ? IPAddress.Any : IPAddress.Loopback, port);
    }

    public int Port => acceptTask is null ? 0 : ((IPEndPoint)listener.LocalEndpoint).Port;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (acceptTask is not null) throw new InvalidOperationException("The management server is already running.");
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
                _ = task.ContinueWith(completed =>
                {
                    clients.TryRemove(id, out _);
                    clientLimit.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleClientAsync(client, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or SocketException or JsonException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            NetworkStream stream = client.GetStream();
            byte[] request = new byte[MaximumHeaderBytes + MaximumBodyBytes];
            int length = 0;
            int headerEnd = -1;
            while (length < request.Length)
            {
                int read = await stream.ReadAsync(request.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) return;
                length += read;
                int marker = request.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (marker >= 0)
                {
                    headerEnd = marker + 4;
                    if (headerEnd > MaximumHeaderBytes)
                    {
                        await WriteJsonErrorAsync(stream, 431, "headers_too_large", timeout.Token)
                            .ConfigureAwait(false);
                        return;
                    }
                    break;
                }
                if (length >= MaximumHeaderBytes)
                {
                    await WriteJsonErrorAsync(stream, 431, "headers_too_large", timeout.Token).ConfigureAwait(false);
                    return;
                }
            }
            if (headerEnd < 0)
            {
                await WriteJsonErrorAsync(stream, 400, "bad_request", timeout.Token).ConfigureAwait(false);
                return;
            }

            string headerText = Encoding.ASCII.GetString(request, 0, headerEnd - 2);
            string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
            string[] firstLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (firstLine.Length != 3 || firstLine[2] != "HTTP/1.1" ||
                firstLine[0] is not ("GET" or "HEAD" or "POST"))
            {
                await WriteJsonErrorAsync(stream, 405, "method_not_allowed", timeout.Token).ConfigureAwait(false);
                return;
            }
            Dictionary<string, List<string>> headers = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < lines.Length; index++)
            {
                int colon = lines[index].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[index][..colon].Trim();
                string value = lines[index][(colon + 1)..].Trim();
                if (!headers.TryGetValue(name, out List<string>? values)) headers[name] = values = [];
                values.Add(value);
            }
            if (headers.TryGetValue("Transfer-Encoding", out List<string>? transfer) && transfer.Count > 0)
            {
                await WriteJsonErrorAsync(stream, 400, "transfer_encoding_not_supported", timeout.Token)
                    .ConfigureAwait(false);
                return;
            }
            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out List<string>? lengths))
            {
                if (lengths.Count != 1 || !int.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture,
                        out contentLength) || contentLength < 0 || contentLength > MaximumBodyBytes)
                {
                    await WriteJsonErrorAsync(stream, 413, "invalid_content_length", timeout.Token)
                        .ConfigureAwait(false);
                    return;
                }
            }
            while (length - headerEnd < contentLength)
            {
                int read = await stream.ReadAsync(request.AsMemory(length, headerEnd + contentLength - length), timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0) return;
                length += read;
            }

            ReadOnlyMemory<byte> body = request.AsMemory(headerEnd, contentLength);
            HttpResult result = await RouteAsync(
                firstLine[0], firstLine[1], headers, body, timeout.Token).ConfigureAwait(false);
            await WriteResponseAsync(stream, result.Status, result.ContentType, result.Body,
                firstLine[0] == "HEAD", timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<HttpResult> RouteAsync(
        string method,
        string path,
        Dictionary<string, List<string>> headers,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        RuntimeHealthSnapshot snapshot = registry.Snapshot();
        if (requireReadAuthentication && !RequireAuthorization(headers))
            return JsonError(401, "unauthorized");
        if (method is "GET" or "HEAD")
        {
            if (path == "/health") return Json(snapshot.Healthy ? 200 : 503, new
            {
                status = snapshot.Healthy ? "healthy" : "unhealthy",
                snapshot.ActiveSessions,
                snapshot.ConnectedSessions,
                snapshot.UptimeSeconds
            });
            if (path == "/ready") return Json(snapshot.Ready ? 200 : 503, new
            {
                status = snapshot.Ready ? "ready" : "not-ready",
                snapshot.ConnectedSessions,
                snapshot.ActiveSessions
            });
            if (path == "/status") return Json(200, snapshot);
            if (path == "/metrics") return new HttpResult(200, "text/plain; version=0.0.4", Metrics(snapshot));
        }

        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || sessionManager is null)
            return JsonError(404, "not_found");
        if (!RequireAuthorization(headers)) return JsonError(401, "unauthorized");
        const string sessionsPath = "/v1/sessions";
        if (method is "GET" or "HEAD")
        {
            IReadOnlyList<ManagedSessionInfo> sessions = sessionManager.Snapshot();
            if (path == sessionsPath) return Json(200, sessions);
            if (!path.StartsWith(sessionsPath + "/", StringComparison.Ordinal))
                return JsonError(404, "not_found");
            string requested = path[(sessionsPath.Length + 1)..];
            if (requested.Length == 0 || requested.Contains('/', StringComparison.Ordinal))
                return JsonError(404, "not_found");
            ManagedSessionInfo? session = sessions.SingleOrDefault(item => item.Id == requested);
            return session is null ? JsonError(404, "not_found") : Json(200, session);
        }
        if (method != "POST") return JsonError(405, "method_not_allowed");
        if (!path.StartsWith(sessionsPath + "/", StringComparison.Ordinal))
            return JsonError(404, "not_found");
        if (!TakeWritePermit()) return JsonError(429, "rate_limited");

        string suffix = path[(sessionsPath.Length + 1)..];
        int slash = suffix.IndexOf('/');
        if (slash <= 0) return JsonError(404, "not_found");
        string id = suffix[..slash];
        string action = suffix[(slash + 1)..];
        SessionControlResult operation = action switch
        {
            "start" => await sessionManager.StartAsync(id, cancellationToken).ConfigureAwait(false),
            "stop" => await sessionManager.StopAsync(id, cancellationToken).ConfigureAwait(false),
            "respawn" => await sessionManager.RespawnAsync(id, cancellationToken).ConfigureAwait(false),
            "send" => await SendAsync(id, body, cancellationToken).ConfigureAwait(false),
            _ => new SessionControlResult(false, "not_found", "The action was not found.")
        };
        return Json(operation.Success ? 200 : operation.Code == "not_found" ? 404 : 409, operation);
    }

    private async Task<SessionControlResult> SendAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.String)
                return new SessionControlResult(false, "invalid_json", "A string message is required.");
            return await sessionManager!.SendAsync(id, message.GetString() ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new SessionControlResult(false, "invalid_json", "The request JSON is invalid.");
        }
    }

    private bool Authorized(Dictionary<string, List<string>> headers)
    {
        if (tokenHash is null || !headers.TryGetValue("Authorization", out List<string>? values) || values.Count != 1 ||
            !values[0].StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        byte[] raw;
        try { raw = Convert.FromBase64String(values[0][7..]); }
        catch (FormatException) { return false; }
        byte[] presented = SHA256.HashData(raw);
        try
        {
            bool correctLength = raw.Length == ControlTokenFile.TokenBytes;
            bool matches = CryptographicOperations.FixedTimeEquals(presented, tokenHash);
            return correctLength & matches;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            CryptographicOperations.ZeroMemory(presented);
        }
    }

    private bool RequireAuthorization(Dictionary<string, List<string>> headers)
    {
        bool authorized = Authorized(headers);
        if (!authorized) Interlocked.Increment(ref authenticationFailures);
        return authorized;
    }

    private bool TakeWritePermit()
    {
        lock (rateGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - rateWindow >= TimeSpan.FromMinutes(1))
            {
                rateWindow = now;
                writesInWindow = 0;
            }
            if (writesInWindow >= 30) return false;
            writesInWindow++;
            return true;
        }
    }

    private byte[] Metrics(RuntimeHealthSnapshot snapshot)
    {
        long packetsReceived = snapshot.Sessions.Sum(item => item.PacketsReceived);
        long packetsSent = snapshot.Sessions.Sum(item => item.PacketsSent);
        long bytesReceived = snapshot.Sessions.Sum(item => item.BytesReceived);
        long bytesSent = snapshot.Sessions.Sum(item => item.BytesSent);
        long reconnects = snapshot.Sessions.Sum(item => (long)item.ReconnectCount);
        long droppedEvents = snapshot.Sessions.Sum(item => item.DroppedEvents);
        long droppedLogs = snapshot.Sessions.Sum(item => item.DroppedLogLines);
        long outboundRejections = snapshot.Sessions.Sum(item => item.OutboundRejections);
        long unknownPackets = snapshot.Sessions.Sum(item => item.UnknownPacketOverflow);
        StringBuilder output = new();
        Metric(output, "oexyz_sessions_active", snapshot.ActiveSessions);
        Metric(output, "oexyz_sessions_connected", snapshot.ConnectedSessions);
        Metric(output, "oexyz_sessions_completed_total", snapshot.CompletedSessions);
        Metric(output, "oexyz_reconnects_total", reconnects);
        Metric(output, "oexyz_disconnects_total", reconnects + snapshot.CompletedSessions);
        Metric(output, "oexyz_authentication_failures_total", Interlocked.Read(ref authenticationFailures));
        Metric(output, "oexyz_packets_received_total", packetsReceived);
        Metric(output, "oexyz_packets_sent_total", packetsSent);
        Metric(output, "oexyz_bytes_received_total", bytesReceived);
        Metric(output, "oexyz_bytes_sent_total", bytesSent);
        Metric(output, "oexyz_unknown_packets_total", unknownPackets);
        Metric(output, "oexyz_dropped_events_total", droppedEvents);
        Metric(output, "oexyz_dropped_log_lines_total", droppedLogs);
        Metric(output, "oexyz_outbound_rejections_total", outboundRejections);
        Metric(output, "oexyz_process_working_set_bytes", snapshot.WorkingSetBytes);
        Metric(output, "oexyz_process_private_memory_bytes", snapshot.PrivateMemoryBytes);
        Metric(output, "oexyz_process_cpu_percent", snapshot.CpuPercent);
        Metric(output, "oexyz_process_threads", snapshot.ThreadCount);
        Metric(output, "oexyz_uptime_seconds", snapshot.UptimeSeconds);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void Metric(StringBuilder output, string name, double value) =>
        output.Append(name).Append(' ').Append(value.ToString("0.########", CultureInfo.InvariantCulture)).Append('\n');

    private static HttpResult Json(int status, object value) =>
        new(status, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static HttpResult JsonError(int status, string code) => Json(status, new { error = code });

    private static Task WriteJsonErrorAsync(Stream stream, int status, string code, CancellationToken cancellationToken) =>
        WriteResponseAsync(stream, status, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { error = code }, JsonOptions), false, cancellationToken);

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
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            413 => "Payload Too Large",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
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
        if (tokenHash is not null) CryptographicOperations.ZeroMemory(tokenHash);
        clientLimit.Dispose();
        lifetime.Dispose();
    }

    private sealed record HttpResult(int Status, string ContentType, byte[] Body);
}
