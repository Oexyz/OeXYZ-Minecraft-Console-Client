using System.Net;
using System.Net.Sockets;
using System.Text;
using OeXYZ.Core;

namespace OeXYZ.Protocol;

public interface IConnectionDialer
{
    Task<Stream> ConnectAsync(string host, ushort port, CancellationToken cancellationToken);
}

public sealed class DirectConnectionDialer : IConnectionDialer
{
    public static DirectConnectionDialer Instance { get; } = new();

    public async Task<Stream> ConnectAsync(string host, ushort port, CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class ProxyConnectionDialer : IConnectionDialer, IDisposable
{
    private const int MaximumProxyResponseBytes = 8192;
    private readonly ProxyProfile profile;
    private readonly byte[]? password;
    private int disposed;

    public ProxyConnectionDialer(ProxyProfile profile, ReadOnlySpan<byte> password = default)
    {
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        this.password = password.IsEmpty ? null : password.ToArray();
        if (profile.Kind == ProxyKind.Direct) throw new ArgumentException("Use the direct dialer for Direct profiles.");
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Port is < 1 or > 65535)
            throw new ArgumentException("The proxy endpoint is invalid.", nameof(profile));
        if (profile.Username.Any(character => character is '\r' or '\n' or ':'))
            throw new ArgumentException("The proxy username contains unsafe characters.", nameof(profile));
    }

    public async Task<Stream> ConnectAsync(string host, ushort port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Stream stream = await DirectConnectionDialer.Instance.ConnectAsync(
            profile.Host, checked((ushort)profile.Port), cancellationToken).ConfigureAwait(false);
        try
        {
            string destination = host;
            if (profile.DnsMode == ProxyDnsMode.Local)
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                destination = addresses.FirstOrDefault()?.ToString()
                              ?? throw new SocketException((int)SocketError.HostNotFound);
            }
            if (profile.Kind == ProxyKind.Socks5)
                await NegotiateSocks5Async(stream, destination, port, cancellationToken).ConfigureAwait(false);
            else
                await NegotiateHttpConnectAsync(stream, destination, port, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task NegotiateSocks5Async(
        Stream stream,
        string host,
        ushort port,
        CancellationToken cancellationToken)
    {
        bool authenticate = !string.IsNullOrEmpty(profile.Username) || password is not null;
        await stream.WriteAsync(authenticate ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, cancellationToken)
            .ConfigureAwait(false);
        byte[] response = new byte[2];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 5 || response[1] == 0xFF) throw new IOException("The SOCKS5 proxy rejected authentication methods.");
        if (response[1] == 2)
        {
            byte[] username = Encoding.UTF8.GetBytes(profile.Username);
            byte[] secret = password ?? [];
            if (username.Length > 255 || secret.Length > 255) throw new InvalidDataException("SOCKS5 credentials are too long.");
            byte[] request = new byte[3 + username.Length + secret.Length];
            request[0] = 1;
            request[1] = (byte)username.Length;
            username.CopyTo(request, 2);
            request[2 + username.Length] = (byte)secret.Length;
            secret.CopyTo(request, 3 + username.Length);
            try { await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false); }
            finally { Array.Clear(request); }
            await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response[1] != 0) throw new UnauthorizedAccessException("SOCKS5 proxy authentication failed.");
        }
        else if (response[1] != 0)
        {
            throw new IOException("The SOCKS5 proxy selected an unsupported authentication method.");
        }

        using MemoryStream requestBuffer = new();
        requestBuffer.Write([5, 1, 0]);
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            byte[] bytes = address.GetAddressBytes();
            requestBuffer.WriteByte(address.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            requestBuffer.Write(bytes);
        }
        else
        {
            byte[] name = Encoding.ASCII.GetBytes(host);
            if (name.Length is < 1 or > 255) throw new InvalidDataException("The SOCKS5 destination name is invalid.");
            requestBuffer.WriteByte(3);
            requestBuffer.WriteByte((byte)name.Length);
            requestBuffer.Write(name);
        }
        requestBuffer.WriteByte((byte)(port >> 8));
        requestBuffer.WriteByte((byte)port);
        await stream.WriteAsync(requestBuffer.ToArray(), cancellationToken).ConfigureAwait(false);
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 5 || header[1] != 0) throw new IOException($"The SOCKS5 proxy rejected CONNECT ({header[1]}).");
        int addressBytes = header[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadOneAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("The SOCKS5 proxy returned an invalid address type.")
        };
        if (addressBytes is < 1 or > 255) throw new InvalidDataException("The SOCKS5 proxy response is invalid.");
        byte[] ignored = new byte[addressBytes + 2];
        await stream.ReadExactlyAsync(ignored, cancellationToken).ConfigureAwait(false);
    }

    private async Task NegotiateHttpConnectAsync(
        Stream stream,
        string host,
        ushort port,
        CancellationToken cancellationToken)
    {
        if (host.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new InvalidDataException("The HTTP CONNECT destination contains unsafe characters.");
        string authority = IPAddress.TryParse(host, out IPAddress? address) &&
                           address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
        StringBuilder request = new($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
        if (!string.IsNullOrEmpty(profile.Username) || password is not null)
        {
            byte[] username = Encoding.UTF8.GetBytes(profile.Username);
            byte[] credential = new byte[username.Length + 1 + (password?.Length ?? 0)];
            username.CopyTo(credential, 0);
            credential[username.Length] = (byte)':';
            password?.CopyTo(credential, username.Length + 1);
            try { request.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(credential)).Append("\r\n"); }
            finally
            {
                Array.Clear(username);
                Array.Clear(credential);
            }
        }
        request.Append("Proxy-Connection: Keep-Alive\r\n\r\n");
        byte[] bytes = Encoding.ASCII.GetBytes(request.ToString());
        try { await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
        finally { Array.Clear(bytes); }

        byte[] response = new byte[MaximumProxyResponseBytes];
        int length = 0;
        while (length < response.Length)
        {
            int read = await stream.ReadAsync(response.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The HTTP proxy closed during CONNECT.");
            length += read;
            if (response.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        if (response.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            throw new InvalidDataException("The HTTP proxy response headers exceed the safety limit.");
        int lineEnd = response.AsSpan(0, length).IndexOf("\r\n"u8);
        string statusLine = lineEnd > 0 ? Encoding.ASCII.GetString(response, 0, lineEnd) : string.Empty;
        if (!statusLine.StartsWith("HTTP/1.", StringComparison.Ordinal) ||
            statusLine.Length < 12 || statusLine[9..12] != "200")
            throw new IOException("The HTTP proxy rejected CONNECT.");
    }

    private static async Task<int> ReadOneAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        return one[0];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (password is not null) Array.Clear(password);
    }
}
