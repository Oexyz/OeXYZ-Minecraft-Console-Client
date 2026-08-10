using System.Runtime.InteropServices;

namespace OeXYZ.Protocol;

public sealed record ServerAddress(string HandshakeHost, string NetworkHost, ushort Port, bool UsedSrv)
{
    public static ServerAddress Parse(string address, int customPort = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        string value = address.Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('/') || value.Contains('\\'))
            throw new FormatException("Enter a Minecraft host name or IP address, not a URL.");
        if (customPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(customPort));

        string host;
        int embeddedPort = 0;
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            int close = value.IndexOf(']');
            if (close < 2) throw new FormatException("The IPv6 address is missing a closing bracket.");
            host = value[1..close];
            if (close + 1 < value.Length)
            {
                if (value[close + 1] != ':' || !int.TryParse(value[(close + 2)..], out embeddedPort))
                    throw new FormatException("The port after the IPv6 address is invalid.");
            }
        }
        else
        {
            int firstColon = value.IndexOf(':');
            int lastColon = value.LastIndexOf(':');
            if (firstColon >= 0 && firstColon == lastColon)
            {
                host = value[..firstColon];
                if (!int.TryParse(value[(firstColon + 1)..], out embeddedPort))
                    throw new FormatException("The port in the server address is invalid.");
            }
            else
            {
                host = value;
            }
        }

        host = host.Trim().TrimEnd('.');
        if (host.Length is 0 or > 255 || host.Any(char.IsWhiteSpace))
            throw new FormatException("The server host name is invalid.");
        int selectedPort = customPort > 0 ? customPort : embeddedPort;
        if (selectedPort is < 0 or > 65535) throw new FormatException("The server port must be between 1 and 65535.");
        return new ServerAddress(host, host, (ushort)(selectedPort == 0 ? 25565 : selectedPort), false)
        {
            HasExplicitPort = selectedPort > 0
        };
    }

    public bool HasExplicitPort { get; init; }

    public ServerAddress ResolveSrv()
    {
        if (HasExplicitPort || !OperatingSystem.IsWindows()) return this;
        SrvRecord? record = WindowsSrvResolver.Resolve("_minecraft._tcp." + HandshakeHost);
        return record is null
            ? this
            : this with { NetworkHost = record.Target, Port = record.Port, UsedSrv = true };
    }

    private sealed record SrvRecord(string Target, ushort Port);

    private static class WindowsSrvResolver
    {
        public static SrvRecord? Resolve(string query)
        {
            IntPtr records = IntPtr.Zero;
            try
            {
                int result = DnsQuery(query, 33, 0, IntPtr.Zero, out records, IntPtr.Zero);
                if (result != 0 || records == IntPtr.Zero) return null;
                List<(string Target, ushort Port, ushort Priority, ushort Weight)> candidates = [];
                for (IntPtr current = records; current != IntPtr.Zero;)
                {
                    DnsRecord record = Marshal.PtrToStructure<DnsRecord>(current);
                    if (record.Type == 33 && record.Data.Srv.Port > 0 && record.Data.Srv.Target != IntPtr.Zero)
                    {
                        string? target = Marshal.PtrToStringUni(record.Data.Srv.Target);
                        if (!string.IsNullOrWhiteSpace(target))
                            candidates.Add((target.TrimEnd('.'), record.Data.Srv.Port, record.Data.Srv.Priority, record.Data.Srv.Weight));
                    }
                    current = record.Next;
                }
                if (candidates.Count == 0) return null;
                ushort priority = candidates.Min(value => value.Priority);
                var eligible = candidates.Where(value => value.Priority == priority).ToArray();
                int totalWeight = eligible.Sum(value => value.Weight);
                int ticket = totalWeight == 0 ? 0 : Random.Shared.Next(totalWeight);
                foreach (var candidate in eligible)
                {
                    if (ticket < candidate.Weight || totalWeight == 0) return new SrvRecord(candidate.Target, candidate.Port);
                    ticket -= candidate.Weight;
                }
                return new SrvRecord(eligible[^1].Target, eligible[^1].Port);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            finally
            {
                if (records != IntPtr.Zero) DnsRecordListFree(records, 1);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DnsSrvData
        {
            public IntPtr Target;
            public ushort Priority;
            public ushort Weight;
            public ushort Port;
            public ushort Padding;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DnsData
        {
            [FieldOffset(0)] public DnsSrvData Srv;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DnsRecord
        {
            public IntPtr Next;
            public IntPtr Name;
            public ushort Type;
            public ushort DataLength;
            public uint Flags;
            public uint Ttl;
            public uint Reserved;
            public DnsData Data;
        }

        [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode)]
        private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

        [DllImport("dnsapi.dll")]
        private static extern void DnsRecordListFree(IntPtr records, int freeType);
    }
}
