using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using OeXYZ.Core;
using OeXYZ.Protocol;

namespace OeXYZ.Session;

public sealed record SupportPackageRequest(
    string DestinationPath,
    string ApplicationVersion,
    ServerProfile? Server,
    string? LastDisconnectReason,
    IReadOnlyList<string>? RecentDiagnostics = null,
    IReadOnlyDictionary<string, long>? UnknownPackets = null,
    bool ResolveDns = true,
    SessionSnapshot? Snapshot = null);

public static class SupportPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim[] DestinationLocks = Enumerable.Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    public static async Task CreateAsync(SupportPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        string destination = Path.GetFullPath(request.DestinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) PrivateFileSystem.EnsurePrivateDirectory(directory);
        string temporary = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using FileStream output = new(temporary, options);
                using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteJsonAsync(archive, "environment.json", new
                    {
                        applicationVersion = request.ApplicationVersion,
                        os = RuntimeInformation.OSDescription,
                        architecture = RuntimeInformation.OSArchitecture.ToString(),
                        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        dotnet = Environment.Version.ToString(),
                        protocolCatalog = ProtocolCatalog.LoadEmbedded().Versions.Max(item => item.ProtocolVersion),
                        createdAtUtc = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);

                    if (request.Server is ServerProfile server)
                    {
                        ServerAddress parsed = ServerAddress.Parse(server.Address, server.CustomPort);
                        string[] addresses = [];
                        string? dnsError = null;
                        if (request.ResolveDns)
                        {
                            try
                            {
                                IPAddress[] resolved = await Dns.GetHostAddressesAsync(parsed.HandshakeHost, cancellationToken)
                                    .ConfigureAwait(false);
                                addresses = resolved.Select(address => address.ToString()).Take(16).ToArray();
                            }
                            catch (Exception exception) when (exception is SocketException or ArgumentException)
                            {
                                dnsError = exception.Message;
                            }
                        }
                        ServerAddress endpoint = request.ResolveDns
                            ? await parsed.ResolveSrvAsync(cancellationToken).ConfigureAwait(false)
                            : parsed;
                        await WriteJsonAsync(archive, "server-profile.json", new
                        {
                            server.DisplayName,
                            server.Address,
                            server.CustomPort,
                            server.Version,
                            server.Group,
                            server.AntiAfk,
                            server.AutoReconnect,
                            server.AutoRespawn,
                            proxyConfigured = server.ProxyProfileId is not null,
                            failoverEndpointCount = server.Endpoints.Count,
                            automationRuleCount = server.Automations.Count,
                            server.AllowServerTransfer,
                            endpoint = $"{endpoint.NetworkHost}:{endpoint.Port}",
                            endpoint.UsedSrv,
                            dnsAddresses = addresses,
                            dnsError = SensitiveDataRedactor.RedactText(dnsError ?? string.Empty)
                        }, cancellationToken).ConfigureAwait(false);
                    }

                    await WriteTextAsync(archive, "last-disconnect.txt",
                        SensitiveDataRedactor.RedactText(request.LastDisconnectReason ?? "No disconnect reason recorded."),
                        cancellationToken).ConfigureAwait(false);
                    string diagnosticText = string.Join(Environment.NewLine, (request.RecentDiagnostics ?? [])
                        .TakeLast(200)
                        .Select(line => SensitiveDataRedactor.RedactText(line.Length <= 2000 ? line : line[..2000] + "…")));
                    await WriteTextAsync(archive, "recent-diagnostics.txt", diagnosticText, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(archive, "unknown-packets.json", request.UnknownPackets ?? new Dictionary<string, long>(),
                        cancellationToken).ConfigureAwait(false);
                    SessionSnapshot? snapshot = request.Snapshot;
                    await WriteJsonAsync(archive, "diagnostic-counters.json", new
                    {
                        droppedEvents = snapshot?.DroppedEvents ?? 0,
                        droppedLogLines = snapshot?.DroppedLogLines ?? 0,
                        subscriberFailures = snapshot?.SubscriberFailures ?? 0,
                        outboundRejections = snapshot?.OutboundRejections ?? 0,
                        unknownPacketOverflow = snapshot?.UnknownPacketOverflow ?? 0
                    }, cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            SemaphoreSlim destinationLock = GetDestinationLock(destination);
            await destinationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await MoveIntoPlaceAsync(temporary, destination, cancellationToken).ConfigureAwait(false);
                PrivateFileSystem.ProtectFile(destination);
            }
            finally
            {
                destinationLock.Release();
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static SemaphoreSlim GetDestinationLock(string destination)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        uint hash = unchecked((uint)comparer.GetHashCode(destination));
        return DestinationLocks[hash % (uint)DestinationLocks.Length];
    }

    private static async Task MoveIntoPlaceAsync(
        string temporary,
        string destination,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 80;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                OperatingSystem.IsWindows() &&
                exception is IOException or UnauthorizedAccessException &&
                attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteJsonAsync(
        ZipArchive archive,
        string name,
        object value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        await using Stream stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, value.GetType(), JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        await using Stream stream = entry.Open();
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
