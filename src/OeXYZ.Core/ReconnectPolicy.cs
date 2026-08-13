using System.Net.Sockets;

namespace OeXYZ.Core;

public enum DisconnectCategory
{
    Transient,
    Permanent,
    User
}

public sealed record DisconnectDecision(
    DisconnectCategory Category,
    string Summary,
    TimeSpan? MinimumRetryDelay = null)
{
    public bool MayReconnect => Category == DisconnectCategory.Transient;
}

public static class DisconnectClassifier
{
    private static readonly string[] ThrottleMarkers =
    [
        "please wait before reconnecting", "too many connections", "connection throttled",
        "reconnecting too fast", "reconnect too fast"
    ];

    private static readonly string[] PermanentMarkers =
    [
        "banned", "ban ", "whitelist", "not whitelisted", "invalid session", "failed to verify username",
        "authentication", "not authenticated", "outdated client", "outdated server", "unsupported protocol",
        "incompatible", "does not own java", "online-mode server", "code of conduct was not accepted"
    ];

    private static readonly string[] TransientMarkers =
    [
        "connection reset", "connection refused", "timed out", "timeout", "temporarily", "try again",
        "server restart", "restarting", "remote host", "closed connection", "end of stream", "eof",
        "network", "no such host", "name or service not known", "server is offline", "unreachable"
    ];

    public static DisconnectDecision Classify(Exception exception, bool requestedByUser = false)
    {
        if (requestedByUser)
            return new DisconnectDecision(DisconnectCategory.User, "Disconnected by the user.");

        Exception source = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        string message = source.Message.Trim();
        string normalized = message.ToLowerInvariant();

        if (PermanentMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            return new DisconnectDecision(DisconnectCategory.Permanent, message);

        if (ThrottleMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            return new DisconnectDecision(DisconnectCategory.Transient, message, TimeSpan.FromSeconds(60));

        if (source is SocketException or TimeoutException or OperationCanceledException ||
            TransientMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            return new DisconnectDecision(DisconnectCategory.Transient, message);

        if (source is NotSupportedException or UnauthorizedAccessException or FormatException)
            return new DisconnectDecision(DisconnectCategory.Permanent, message);

        if (source is IOException)
            return new DisconnectDecision(DisconnectCategory.Transient, message);

        return new DisconnectDecision(DisconnectCategory.Permanent, message);
    }
}

public sealed class ReconnectBackoff
{
    private readonly TimeSpan initial;
    private readonly TimeSpan maximum;
    private readonly Random random;

    public ReconnectBackoff(TimeSpan initial, TimeSpan maximum, Random? random = null)
    {
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (maximum < initial) throw new ArgumentOutOfRangeException(nameof(maximum));
        this.initial = initial;
        this.maximum = maximum;
        this.random = random ?? Random.Shared;
    }

    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt <= 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        double factor = Math.Pow(2D, Math.Min(attempt - 1, 20));
        double baseMilliseconds = Math.Min(maximum.TotalMilliseconds, initial.TotalMilliseconds * factor);
        double jitterLimit = Math.Min(initial.TotalMilliseconds * 0.2D, 1000D);
        double jitter = jitterLimit <= 0 ? 0 : random.NextDouble() * jitterLimit;
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, baseMilliseconds + jitter));
    }
}
