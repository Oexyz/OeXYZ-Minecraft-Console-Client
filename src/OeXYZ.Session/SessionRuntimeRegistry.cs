using System.Collections.Concurrent;
using System.Diagnostics;
using OeXYZ.Core;

namespace OeXYZ.Session;

public sealed record RuntimeSessionStatus(
    string Name,
    string Status,
    bool Connected,
    bool Completed,
    string? MinecraftVersion,
    int? ProtocolVersion,
    long? PingMilliseconds,
    float? Health,
    int? Food,
    double? X,
    double? Y,
    double? Z,
    float? Yaw,
    float? Pitch,
    long BytesReceived,
    long BytesSent,
    long PacketsReceived,
    long PacketsSent,
    int ReconnectCount,
    DateTimeOffset? LastPacketAt,
    long DroppedEvents = 0,
    long DroppedLogLines = 0,
    long SubscriberFailures = 0,
    long OutboundRejections = 0,
    long UnknownPacketOverflow = 0);

public sealed record RuntimeHealthSnapshot(
    DateTimeOffset Timestamp,
    long UptimeSeconds,
    bool Healthy,
    bool Ready,
    int ActiveSessions,
    int ConnectedSessions,
    int CompletedSessions,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    double CpuPercent,
    IReadOnlyList<RuntimeSessionStatus> Sessions);

public sealed class SessionRuntimeRegistry
{
    private sealed record Entry(string Name, SessionSnapshot Snapshot, bool Completed);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly object processGate = new();
    private TimeSpan lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTimeOffset lastCpuSample = DateTimeOffset.UtcNow;
    private double lastCpuPercent;

    public void Register(ConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string id = $"{session.Account.Id:N}:{session.Server.Id:N}";
        string name = session.Title;
        Update(id, name, session.Snapshot, completed: false);
        session.SnapshotChanged += snapshot => Update(id, name, snapshot, completed: false);
        _ = ObserveCompletionAsync(id, name, session);
    }

    internal void Update(string id, string name, SessionSnapshot snapshot, bool completed) =>
        entries.AddOrUpdate(
            id,
            _ => new Entry(name, snapshot, completed),
            (_, previous) => new Entry(name, snapshot, completed || previous.Completed));

    internal void MarkCompleted(string id) =>
        entries.AddOrUpdate(
            id,
            _ => throw new InvalidOperationException("Cannot complete an unregistered session."),
            (_, previous) => previous with { Completed = true });

    public RuntimeHealthSnapshot Snapshot()
    {
        Entry[] values = entries.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        int completed = values.Count(entry => entry.Completed);
        int connected = values.Count(entry => entry.Snapshot.IsConnected && !entry.Completed);
        int active = values.Length - completed;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        double cpu = SampleCpu(process.TotalProcessorTime);
        RuntimeSessionStatus[] sessions = values.Select(ToStatus).ToArray();
        return new RuntimeHealthSnapshot(
            DateTimeOffset.UtcNow,
            Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
            Healthy: values.Length == 0 || active > 0,
            Ready: connected > 0,
            ActiveSessions: active,
            ConnectedSessions: connected,
            CompletedSessions: completed,
            WorkingSetBytes: process.WorkingSet64,
            PrivateMemoryBytes: process.PrivateMemorySize64,
            ThreadCount: process.Threads.Count,
            CpuPercent: cpu,
            Sessions: sessions);
    }

    private static RuntimeSessionStatus ToStatus(Entry entry)
    {
        SessionSnapshot snapshot = entry.Snapshot;
        return new RuntimeSessionStatus(
            entry.Name,
            snapshot.Status,
            snapshot.IsConnected,
            entry.Completed,
            snapshot.MinecraftVersion,
            snapshot.ProtocolVersion,
            snapshot.Metrics.PingMilliseconds,
            snapshot.Health,
            snapshot.Food,
            snapshot.Position?.X,
            snapshot.Position?.Y,
            snapshot.Position?.Z,
            snapshot.Position?.Yaw,
            snapshot.Position?.Pitch,
            snapshot.Metrics.BytesReceived,
            snapshot.Metrics.BytesSent,
            snapshot.Metrics.PacketsReceived,
            snapshot.Metrics.PacketsSent,
            snapshot.ReconnectCount,
            snapshot.Metrics.LastReceivedAt,
            snapshot.DroppedEvents,
            snapshot.DroppedLogLines,
            snapshot.SubscriberFailures,
            snapshot.OutboundRejections,
            snapshot.UnknownPacketOverflow);
    }

    private double SampleCpu(TimeSpan totalProcessorTime)
    {
        lock (processGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            double elapsedMilliseconds = (now - lastCpuSample).TotalMilliseconds;
            if (elapsedMilliseconds >= 250)
            {
                double cpuMilliseconds = (totalProcessorTime - lastCpuTime).TotalMilliseconds;
                lastCpuPercent = Math.Clamp(
                    cpuMilliseconds / elapsedMilliseconds / Math.Max(1, Environment.ProcessorCount) * 100D,
                    0D,
                    100D);
                lastCpuSample = now;
                lastCpuTime = totalProcessorTime;
            }
            return Math.Round(lastCpuPercent, 2);
        }
    }

    private async Task ObserveCompletionAsync(string id, string name, ConsoleSession session)
    {
        try { await session.Completion.ConfigureAwait(false); }
        catch { }
        entries.AddOrUpdate(
            id,
            _ => new Entry(name, session.Snapshot, Completed: true),
            (_, previous) => previous with { Snapshot = session.Snapshot, Completed = true });
    }
}
