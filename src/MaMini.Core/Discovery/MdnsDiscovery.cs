using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MaMini.Core.Diagnostics;

namespace MaMini.Core.Discovery;

public sealed record DiscoveredServer(string Name, Uri BaseUrl, string? Version);

/// <summary>
/// Minimal one-shot mDNS browser for Music Assistant (<c>_mass._tcp.local</c>). Queries are sent from an
/// ephemeral port, so responders reply by unicast ("legacy unicast", RFC 6762 §6.7) and no port 5353 socket
/// is needed.
/// </summary>
public static class MdnsDiscovery
{
    public const string ServiceType = "_mass._tcp.local";

    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(ServiceType);
        var found = new Dictionary<string, DiscoveredServer>(StringComparer.OrdinalIgnoreCase);
        var clients = new List<UdpClient>();
        try
        {
            foreach (var address in LocalIPv4Addresses())
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(address, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    clients.Add(client);
                }
                catch (SocketException ex)
                {
                    Log.Info($"mDNS: skipping interface {address}: {ex.Message}");
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            var receivers = clients.Select(c => ReceiveAsync(c, found, cts.Token)).ToList();

            // Send twice in case the first packet is lost.
            for (var i = 0; i < 2 && !cts.IsCancellationRequested; i++)
            {
                foreach (var client in clients)
                {
                    try
                    {
                        await client.SendAsync(query, MulticastEndpoint, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Task.WhenAll(receivers).ConfigureAwait(false);
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        lock (found)
        {
            return found.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    private static async Task ReceiveAsync(UdpClient client, Dictionary<string, DiscoveredServer> found, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                foreach (var server in ParseResponse(result.Buffer, result.RemoteEndPoint.Address))
                {
                    lock (found)
                    {
                        found[server.BaseUrl.ToString()] = server;
                    }
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
            {
                Log.Info($"mDNS: ignoring malformed packet from {result.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !nic.SupportsMulticast)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    internal static byte[] BuildQuery(string name)
    {
        var bytes = new List<byte>(64)
        {
            0, 0, // id
            0, 0, // flags
            0, 1, // qdcount
            0, 0, 0, 0, 0, 0, // an/ns/ar counts
        };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 12 }); // PTR
        bytes.AddRange(new byte[] { 0x80, 1 }); // QU bit + IN
        return bytes.ToArray();
    }

    /// <summary>Extracts Music Assistant servers from a DNS response packet.</summary>
    internal static IReadOnlyList<DiscoveredServer> ParseResponse(byte[] packet, IPAddress? sender)
    {
        var reader = new DnsReader(packet);
        reader.Skip(4); // id + flags
        var qd = reader.ReadUInt16();
        var an = reader.ReadUInt16();
        var ns = reader.ReadUInt16();
        var ar = reader.ReadUInt16();
        for (var i = 0; i < qd; i++)
        {
            reader.ReadName();
            reader.Skip(4);
        }

        var instances = new List<string>();
        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < an + ns + ar; i++)
        {
            var name = reader.ReadName();
            var type = reader.ReadUInt16();
            reader.ReadUInt16(); // class
            reader.Skip(4); // ttl
            var length = reader.ReadUInt16();
            var end = reader.Position + length;
            switch (type)
            {
                case 12 when name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase):
                    instances.Add(reader.ReadName());
                    break;
                case 33:
                    reader.Skip(4); // priority + weight
                    var port = reader.ReadUInt16();
                    srv[name] = (reader.ReadName(), port);
                    break;
                case 16:
                    txt[name] = reader.ReadTxt(end);
                    break;
                case 1 when length == 4:
                    hosts[name] = new IPAddress(reader.ReadBytes(4));
                    break;
            }

            reader.Position = end;
        }

        // Some responders omit the PTR in the answer but still send SRV/TXT for the instance.
        foreach (var key in srv.Keys.Concat(txt.Keys))
        {
            if (key.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) && !instances.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                instances.Add(key);
            }
        }

        var results = new List<DiscoveredServer>();
        foreach (var instance in instances)
        {
            txt.TryGetValue(instance, out var props);
            props ??= new Dictionary<string, string>();
            Uri? baseUrl = null;
            foreach (var key in new[] { "base_url", "internal_url" })
            {
                if (props.TryGetValue(key, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                    parsed.Scheme is "http" or "https")
                {
                    baseUrl = parsed;
                    break;
                }
            }

            if (baseUrl is null && srv.TryGetValue(instance, out var service))
            {
                var address = hosts.TryGetValue(service.Target, out var ip) ? ip : sender;
                if (address is not null)
                {
                    baseUrl = new Uri($"http://{address}:{service.Port}");
                }
            }

            if (baseUrl is null)
            {
                continue;
            }

            var friendly = props.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : instance.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) ? instance[..^(ServiceType.Length + 1)] : instance;
            props.TryGetValue("server_version", out var version);
            results.Add(new DiscoveredServer(friendly, baseUrl, version));
        }

        return results;
    }

    private sealed class DnsReader
    {
        private readonly byte[] _data;

        public DnsReader(byte[] data) => _data = data;

        public int Position { get; set; }

        public void Skip(int count) => Position += count;

        public int ReadUInt16()
        {
            var value = (_data[Position] << 8) | _data[Position + 1];
            Position += 2;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            var bytes = _data.AsSpan(Position, count).ToArray();
            Position += count;
            return bytes;
        }

        public string ReadName()
        {
            var labels = new List<string>();
            var position = Position;
            var jumped = false;
            var jumps = 0;
            while (true)
            {
                var length = _data[position];
                if (length == 0)
                {
                    position++;
                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (++jumps > 20)
                    {
                        throw new FormatException("Too many name compression pointers.");
                    }

                    var pointer = ((length & 0x3F) << 8) | _data[position + 1];
                    if (!jumped)
                    {
                        Position = position + 2;
                    }

                    jumped = true;
                    position = pointer;
                    continue;
                }

                labels.Add(Encoding.UTF8.GetString(_data, position + 1, length));
                position += length + 1;
            }

            if (!jumped)
            {
                Position = position;
            }

            return string.Join('.', labels);
        }

        public Dictionary<string, string> ReadTxt(int end)
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (Position < end)
            {
                var length = _data[Position++];
                var entry = Encoding.UTF8.GetString(_data, Position, length);
                Position += length;
                var eq = entry.IndexOf('=');
                if (eq > 0)
                {
                    props[entry[..eq]] = entry[(eq + 1)..];
                }
            }

            return props;
        }
    }
}
