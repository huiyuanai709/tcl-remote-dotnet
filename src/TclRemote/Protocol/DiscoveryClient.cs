using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TclRemote;

internal sealed class TvDevice
{
    public string ProtoVer { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string Display { get; set; } = "";
    public string Capability { get; set; } = "";
    public string WifiMac { get; set; } = "";
    public string BtMac1 { get; set; } = "";
    public string Ip { get; set; } = "";
}

internal static partial class DiscoveryClient
{
    public static double ClampTimeoutSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return Protocol.DefaultDiscoverTimeout.TotalSeconds;
        if (seconds < 0.5)
            return 0.5;
        if (seconds > 10)
            return 10;
        return seconds;
    }

    public static List<TvDevice> Scan(
        TimeSpan timeout,
        IReadOnlyList<IPEndPoint>? extraUnicast = null,
        Action<TvDevice>? onFound = null)
    {
        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromMilliseconds(500);

        var packet = BuildPacket();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        // Ephemeral source port. The TV unicasts the reply back to it, so UDP 6537
        // does not need to be bound.
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.ReceiveTimeout = 200;

        var targets = BuildTargets(extraUnicast);
        SendAll(socket, packet, targets);

        var found = new Dictionary<string, TvDevice>(StringComparer.Ordinal);
        var started = DateTime.UtcNow;
        var resent = false;
        var buffer = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (DateTime.UtcNow - started < timeout)
        {
            if (!resent && timeout >= TimeSpan.FromSeconds(1) && DateTime.UtcNow - started > timeout / 2)
            {
                resent = true;
                SendAll(socket, packet, targets);
            }

            int received;
            try
            {
                received = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException)
            {
                continue;
            }

            if (remote is not IPEndPoint ipEndPoint || received <= 0)
                continue;

            var device = Parse(buffer.AsSpan(0, received));
            if (device is null)
                continue;

            var ip = ipEndPoint.Address.ToString();
            if (found.ContainsKey(ip))
                continue;

            device.Ip = ip;
            found.Add(ip, device);
            onFound?.Invoke(device);
        }

        return found.Values.ToList();
    }

    public static byte[] BuildPacket(string? deviceName = null, string? deviceUuid = null)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        if (string.IsNullOrWhiteSpace(name))
            name = "TCL Remote";
        var uuid = string.IsNullOrWhiteSpace(deviceUuid) ? Guid.NewGuid().ToString() : deviceUuid;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var message = $"{Protocol.PhoneProtoVersion}:{timestamp}:{name}:PHONE:1:{name}:{uuid}:0:0";
        var body = Encoding.UTF8.GetBytes(message);
        var packet = new byte[body.Length + 1];
        body.CopyTo(packet, 0);
        return packet;
    }

    public static TvDevice? Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return null;

        var text = Encoding.UTF8.GetString(data).TrimEnd('\0');
        var fields = text.Split(':');
        if (fields.Length < 4 || !string.Equals(fields[3], "TV", StringComparison.Ordinal))
            return null;

        return new TvDevice
        {
            ProtoVer = fields[0],
            Timestamp = Field(fields, 1),
            Name = Field(fields, 2),
            Type = fields[3],
            Status = Field(fields, 4),
            Display = Field(fields, 5),
            Capability = Field(fields, 6),
            WifiMac = MacDecoder.Decode(Field(fields, 8)),
            BtMac1 = MacDecoder.Decode(Field(fields, 9)),
        };
    }

    public static IPAddress BroadcastAddress(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var bits = mask.GetAddressBytes();
        if (ip.Length != 4 || bits.Length != 4)
            throw new ArgumentException("Only IPv4 broadcast addresses are supported.");

        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
            broadcast[i] = (byte)(ip[i] | (byte)~bits[i]);
        return new IPAddress(broadcast);
    }

    private static List<IPEndPoint> BuildTargets(IReadOnlyList<IPEndPoint>? extraUnicast)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, Protocol.DiscoverPort) };
        foreach (var endpoint in EnumerateSubnetBroadcasts())
            targets.Add(endpoint);
        if (extraUnicast is not null)
            targets.AddRange(extraUnicast);

        return targets
            .DistinctBy(static endpoint => endpoint.ToString())
            .ToList();
    }

    private static IEnumerable<IPEndPoint> EnumerateSubnetBroadcasts()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(unicast.Address))
                    continue;
                var mask = unicast.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any))
                    continue;

                IPAddress broadcast;
                try
                {
                    broadcast = BroadcastAddress(unicast.Address, mask);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return new IPEndPoint(broadcast, Protocol.DiscoverPort);
            }
        }
    }

    private static void SendAll(Socket socket, byte[] packet, List<IPEndPoint> targets)
    {
        foreach (var target in targets)
        {
            try
            {
                socket.SendTo(packet, target);
            }
            catch (SocketException)
            {
                // A missing route or a denied broadcast should not hide other targets.
            }
        }
    }

    private static string Field(string[] fields, int index) =>
        index < fields.Length ? fields[index] : "";
}

internal static partial class MacDecoder
{
    public static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return Entity().Replace(value, static match => ":" + match.Groups[1].Value);
    }

    [GeneratedRegex(@"&#058([0-9a-fA-F]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Entity();
}
