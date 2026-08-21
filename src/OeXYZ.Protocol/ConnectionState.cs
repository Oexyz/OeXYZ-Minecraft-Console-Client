namespace OeXYZ.Protocol;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Login,
    Configuration,
    Play
}

public enum ChatMessageKind
{
    Unknown,
    Player,
    System,
    Profileless,
    Legacy
}

public sealed record ChatLine(
    DateTimeOffset Timestamp,
    string Text,
    bool IsActionBar = false,
    FormattedChatText? Formatting = null,
    string? TranslationKey = null,
    Guid? SenderUuid = null,
    string? SenderName = null,
    ChatMessageKind Kind = ChatMessageKind.Unknown,
    bool IsPrivateMessage = false);

public sealed record PlayerPosition(double X, double Y, double Z, float Yaw, float Pitch);

public sealed record ChatStyle(
    string? Color = null,
    bool Bold = false,
    bool Italic = false,
    bool Underlined = false,
    bool Strikethrough = false);

public sealed record ChatRun(string Text, ChatStyle Style);

public sealed record FormattedChatText(
    string Text,
    IReadOnlyList<ChatRun> Runs,
    string? TranslationKey = null);

public sealed record PlayerListEntry(
    Guid Uuid,
    string Name,
    int PingMilliseconds,
    int GameMode,
    bool Listed = true);

public enum PacketDirection
{
    Clientbound,
    Serverbound
}

public sealed record PacketTrace(
    DateTimeOffset Timestamp,
    PacketDirection Direction,
    ConnectionState State,
    int PacketId,
    string Name,
    int PayloadBytes,
    int WireBytes,
    bool Known);

public sealed record ServerTransferRequest(string Host, ushort Port);

public sealed class ServerTransferException : IOException
{
    public ServerTransferException(ServerTransferRequest transfer)
        : base("The server requested a controlled transfer.") => Transfer = transfer;

    public ServerTransferRequest Transfer { get; }
}

public sealed record ConnectionMetrics(
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastReceivedAt,
    DateTimeOffset? LastSentAt,
    long BytesReceived,
    long BytesSent,
    long PacketsReceived,
    long PacketsSent,
    int? PingMilliseconds,
    long DroppedEvents = 0,
    long SubscriberFailures = 0,
    long OutboundRejections = 0)
{
    public TimeSpan Uptime(DateTimeOffset now) => ConnectedAt is null ? TimeSpan.Zero : now - ConnectedAt.Value;
}
