using System.Collections.Concurrent;

namespace OeXYZ.Session;

public sealed record ManagedSessionInfo(
    string Id,
    string Name,
    string Status,
    bool Connected,
    bool Started,
    bool Completed);

public sealed record SessionControlResult(bool Success, string Code, string Message);

public interface ISessionControlManager
{
    IReadOnlyList<ManagedSessionInfo> Snapshot();
    Task<SessionControlResult> StartAsync(string sessionId, CancellationToken cancellationToken);
    Task<SessionControlResult> StopAsync(string sessionId, CancellationToken cancellationToken);
    Task<SessionControlResult> SendAsync(string sessionId, string message, CancellationToken cancellationToken);
    Task<SessionControlResult> RespawnAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class SessionControlManager : ISessionControlManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private int disposed;

    public string Register(ConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string id = $"{session.Account.Id:N}-{session.Server.Id:N}";
        if (!entries.TryAdd(id, new Entry(session)))
            throw new InvalidOperationException("The managed session ID is already registered.");
        return id;
    }

    public IReadOnlyList<ManagedSessionInfo> Snapshot() => entries
        .Select(pair => ToInfo(pair.Key, pair.Value))
        .OrderBy(info => info.Id, StringComparer.Ordinal)
        .ToArray();

    public Task<SessionControlResult> StartAsync(string sessionId, CancellationToken cancellationToken) =>
        WithEntryAsync(sessionId, cancellationToken, entry =>
        {
            if (entry.Started && !entry.Session.Completion.IsCompleted)
                return Task.FromResult(new SessionControlResult(true, "already_started", "The session is already running."));
            if (entry.Started)
                return Task.FromResult(new SessionControlResult(false, "restart_required",
                    "This session instance has completed and must be recreated by the supervisor."));
            entry.Session.Start();
            entry.Started = true;
            return Task.FromResult(new SessionControlResult(true, "started", "The session was started."));
        });

    public Task<SessionControlResult> StopAsync(string sessionId, CancellationToken cancellationToken) =>
        WithEntryAsync(sessionId, cancellationToken, entry =>
        {
            if (!entry.Started || entry.Session.Completion.IsCompleted)
                return Task.FromResult(new SessionControlResult(true, "already_stopped", "The session is already stopped."));
            entry.Session.Stop();
            return Task.FromResult(new SessionControlResult(true, "stopping", "The session is stopping."));
        });

    public Task<SessionControlResult> SendAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 256)
            return Task.FromResult(new SessionControlResult(false, "invalid_message",
                "The message must contain 1-256 characters."));
        return WithEntryAsync(sessionId, cancellationToken, async entry =>
        {
            if (!entry.Session.IsConnected)
                return new SessionControlResult(false, "not_connected", "The session is not connected.");
            await entry.Session.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return new SessionControlResult(true, "sent", "The message was queued for sending.");
        });
    }

    public Task<SessionControlResult> RespawnAsync(string sessionId, CancellationToken cancellationToken) =>
        WithEntryAsync(sessionId, cancellationToken, async entry =>
        {
            if (!entry.Session.IsConnected)
                return new SessionControlResult(false, "not_connected", "The session is not connected.");
            await entry.Session.RespawnAsync(cancellationToken).ConfigureAwait(false);
            return new SessionControlResult(true, "respawned", "The respawn request was queued.");
        });

    private async Task<SessionControlResult> WithEntryAsync(
        string sessionId,
        CancellationToken cancellationToken,
        Func<Entry, Task<SessionControlResult>> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId) || !entries.TryGetValue(sessionId, out Entry? entry))
            return new SessionControlResult(false, "not_found", "The managed session was not found.");
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action(entry).ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    private static ManagedSessionInfo ToInfo(string id, Entry entry)
    {
        SessionSnapshot snapshot = entry.Session.Snapshot;
        return new ManagedSessionInfo(
            id,
            entry.Session.Title,
            snapshot.Status,
            snapshot.IsConnected,
            entry.Started,
            entry.Started && entry.Session.Completion.IsCompleted);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (Entry entry in entries.Values) entry.Session.Stop();
        foreach (Entry entry in entries.Values)
        {
            if (entry.Started)
            {
                try { await entry.Session.Completion.ConfigureAwait(false); }
                catch { }
            }
            entry.Gate.Dispose();
        }
    }

    private sealed class Entry(ConsoleSession session)
    {
        public ConsoleSession Session { get; } = session;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Started { get; set; }
    }
}
