namespace OeXYZ.Protocol;

public enum ConnectionPhase
{
    TcpConnect,
    Login,
    Configuration,
    CodeOfConductDecision
}

public sealed class ConnectionPhaseTimeoutException : TimeoutException
{
    public ConnectionPhaseTimeoutException(ConnectionPhase phase, TimeSpan timeout)
        : base($"The Minecraft {PhaseName(phase)} phase timed out after {timeout.TotalSeconds:0} seconds.")
    {
        Phase = phase;
        Timeout = timeout;
    }

    public ConnectionPhase Phase { get; }
    public TimeSpan Timeout { get; }

    private static string PhaseName(ConnectionPhase phase) => phase switch
    {
        ConnectionPhase.TcpConnect => "TCP connect",
        ConnectionPhase.Login => "login",
        ConnectionPhase.Configuration => "configuration",
        ConnectionPhase.CodeOfConductDecision => "code-of-conduct decision",
        _ => phase.ToString()
    };
}

internal sealed record ConnectionDeadlinePolicy(
    TimeSpan TcpConnect,
    TimeSpan Login,
    TimeSpan Configuration,
    TimeSpan CodeOfConductDecision)
{
    public static ConnectionDeadlinePolicy Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120));

    public void Validate()
    {
        if (TcpConnect <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TcpConnect));
        if (Login <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Login));
        if (Configuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Configuration));
        if (CodeOfConductDecision <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CodeOfConductDecision));
    }
}
