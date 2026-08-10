using OeXYZ.Protocol;

if (string.Equals(args.ElementAtOrDefault(0), "--status", StringComparison.OrdinalIgnoreCase))
{
    string statusAddress = args.ElementAtOrDefault(1) ?? "127.0.0.1";
    int statusPort = int.TryParse(args.ElementAtOrDefault(2), out int requestedPort) ? requestedPort : 0;
    MinecraftServerStatus status = await MinecraftServerDiscovery.QueryAsync(statusAddress, statusPort);
    Console.WriteLine($"STATUS={status.VersionName};PROTOCOL={status.ProtocolVersion};PLAYERS={status.PlayersOnline}/{status.PlayersMaximum}");
    Console.WriteLine($"ENDPOINT={status.Address.NetworkHost}:{status.Address.Port};SRV={status.Address.UsedSrv}");
    return;
}

string host = args.ElementAtOrDefault(0) ?? "127.0.0.1";
ushort port = ushort.TryParse(args.ElementAtOrDefault(1), out ushort parsedPort) ? parsedPort : (ushort)25566;
string message = args.ElementAtOrDefault(2) ?? "OeXYZ integration test";
bool testRespawn = args.Any(argument => string.Equals(argument, "--respawn-test", StringComparison.OrdinalIgnoreCase));
bool tracePackets = args.Any(argument => string.Equals(argument, "--trace-packets", StringComparison.OrdinalIgnoreCase));
bool observeOnly = args.Any(argument => string.Equals(argument, "--observe-only", StringComparison.OrdinalIgnoreCase));
bool resolveSrv = args.Any(argument => string.Equals(argument, "--srv", StringComparison.OrdinalIgnoreCase));
int waitSeconds = int.TryParse(
    args.FirstOrDefault(argument => argument.StartsWith("--wait-seconds=", StringComparison.OrdinalIgnoreCase))?[15..],
    out int parsedWaitSeconds)
    ? Math.Clamp(parsedWaitSeconds, 0, 120)
    : 20;
string username = args.FirstOrDefault(argument => argument.StartsWith("--username=", StringComparison.OrdinalIgnoreCase))?[11..]
                  ?? "OeXYZTest";
string requestedVersion = args.FirstOrDefault(argument => argument.StartsWith("--version=", StringComparison.OrdinalIgnoreCase))?[10..] ?? "26.2";
int? requestedProtocol = int.TryParse(
    args.FirstOrDefault(argument => argument.StartsWith("--protocol=", StringComparison.OrdinalIgnoreCase))?[11..],
    out int parsedProtocol)
    ? parsedProtocol
    : null;
TaskCompletionSource died = new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource positioned = new(TaskCreationOptions.RunContinuationsAsynchronously);
bool deathObserved = false;

ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
ProtocolDefinition protocol = requestedProtocol is int protocolNumber
    ? catalog.Resolve(protocolNumber)
    : catalog.Resolve(requestedVersion);
ServerAddress endpoint = resolveSrv
    ? ServerAddress.Parse(host).ResolveSrv()
    : ServerAddress.Parse(host, port);
await using MinecraftConnection client = new(endpoint, MinecraftIdentity.Offline(username), protocol);
client.Log += line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
client.StateChanged += state => Console.WriteLine($"STATE={state}");
client.PositionChanged += value =>
{
    Console.WriteLine(FormattableString.Invariant($"POSITION={value.X:F2},{value.Y:F2},{value.Z:F2}"));
    positioned.TrySetResult();
    if (deathObserved) returned.TrySetResult();
};
client.Died += () =>
{
    deathObserved = true;
    died.TrySetResult();
};
client.HealthChanged += (health, food) =>
{
    Console.WriteLine($"HEALTH={health:F1} FOOD={food}");
    if (health <= 0)
    {
        deathObserved = true;
        died.TrySetResult();
    }
};
client.ChatReceived += line => Console.WriteLine($"CHAT={line.Text}");
if (tracePackets) client.PacketObserved += (state, id, length) => Console.WriteLine($"PACKET={state}:0x{id:X2}:{length}");
if (tracePackets) client.ConnectionFaulted += exception => Console.WriteLine(exception);

using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
await client.ConnectAsync(timeout.Token);
Console.WriteLine("PLAY_READY");
if (observeOnly)
{
    Console.WriteLine($"OBSERVING_AS={username}");
    await Task.WhenAny(client.Completion, Task.Delay(TimeSpan.FromSeconds(12), timeout.Token));
    Console.WriteLine(client.State == ConnectionState.Play
        ? "PUBLIC_PLAY_REACHED_AND_STILL_CONNECTED"
        : "PUBLIC_PLAY_REACHED_THEN_CONNECTION_CLOSED");
    return;
}
await positioned.Task.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
await client.SendChatAsync(message, timeout.Token);
if (testRespawn)
{
    await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token);
    await client.SendChatAsync($"/kill {username}", timeout.Token);
    try
    {
        await died.Task.WaitAsync(TimeSpan.FromSeconds(4), timeout.Token);
        Console.WriteLine("DEATH_OBSERVED");
    }
    catch (TimeoutException) when (protocol.ProtocolVersion == 47)
    {
        deathObserved = true;
        Console.WriteLine("LEGACY_DEATH_STATE_HAS_NO_DEDICATED_PACKET");
    }
    await client.RespawnAsync(timeout.Token);
    await returned.Task.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
    Console.WriteLine("RESPAWN_OK");
}
else
{
    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), timeout.Token);
}
Console.WriteLine("INTEGRATION_OK");
