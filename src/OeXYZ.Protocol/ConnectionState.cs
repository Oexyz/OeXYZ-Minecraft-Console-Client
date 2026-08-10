namespace OeXYZ.Protocol;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Login,
    Configuration,
    Play
}

public sealed record ChatLine(DateTimeOffset Timestamp, string Text, bool IsActionBar = false);

public sealed record PlayerPosition(double X, double Y, double Z, float Yaw, float Pitch);
