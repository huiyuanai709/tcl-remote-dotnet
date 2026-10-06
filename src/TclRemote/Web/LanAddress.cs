using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TclRemote;

internal readonly record struct LanCandidate(string Name, NetworkInterfaceType Kind, IPAddress Address);

internal static class LanAddress
{
    public static IPAddress? Select(string? tvIp, IReadOnlyList<LanCandidate>? candidates = null)
    {
        candidates ??= ListCandidates();
        IPAddress? route = null;
        if (!string.IsNullOrWhiteSpace(tvIp)
            && IPAddress.TryParse(tvIp, out var tv)
            && tv.AddressFamily == AddressFamily.InterNetwork)
        {
            route = RouteSource(tv);
        }

        return Choose(candidates, route);
    }

    public static IPAddress? Choose(IReadOnlyList<LanCandidate> candidates, IPAddress? routeSource)
    {
        if (routeSource is not null && !IsExcluded(routeSource))
            return routeSource;

        LanCandidate? best = null;
        var bestScore = int.MinValue;
        foreach (var candidate in candidates)
        {
            if (!IsUsable(candidate))
                continue;

            var score = Score(candidate);
            if (best is null || score > bestScore || (score == bestScore && Prefer(candidate, best.Value)))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Address;
    }

    public static IReadOnlyList<LanCandidate> ListCandidates()
    {
        var list = new List<LanCandidate>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    list.Add(new LanCandidate(nic.Name, nic.NetworkInterfaceType, unicast.Address));
                }
            }
        }
        catch (Exception)
        {
            return list;
        }

        return list;
    }

    public static IPAddress? RouteSource(IPAddress destination)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(destination, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsExcluded(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;
        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        // Mihomo / Clash fake-ip (198.18.0.0/15).
        if (bytes[0] == 198 && bytes[1] is 18 or 19)
            return true;
        // Docker bridge.
        if (bytes[0] == 172 && bytes[1] == 17)
            return true;
        // hassio bridge 172.30.32.0/23.
        if (bytes[0] == 172 && bytes[1] == 30 && bytes[2] is 32 or 33)
            return true;
        // Link-local.
        if (bytes[0] == 169 && bytes[1] == 254)
            return true;
        if (bytes[0] == 0)
            return true;
        return false;
    }

    public static bool IsVirtualName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("utun", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("meta", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("mihomo", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("clash", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("br-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("veth", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("hassio", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsable(LanCandidate candidate) =>
        !IsExcluded(candidate.Address)
        && !IsVirtualName(candidate.Name)
        && candidate.Kind is not (NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback);

    private static bool IsRfc1918(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 10)
            return true;
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;
        return bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static bool IsPhysical(NetworkInterfaceType kind) =>
        kind is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT;

    private static int Score(LanCandidate candidate)
    {
        var score = IsPhysical(candidate.Kind) ? 100 : 0;
        if (!IsRfc1918(candidate.Address))
            return score;

        score += 50;
        var bytes = candidate.Address.GetAddressBytes();
        if (bytes[0] == 192)
            score += 30;
        else if (bytes[0] == 10)
            score += 20;
        else
            score += 10;
        return score;
    }

    private static bool Prefer(LanCandidate candidate, LanCandidate current) =>
        string.Compare(candidate.Name, current.Name, StringComparison.Ordinal) < 0
        || (string.Equals(candidate.Name, current.Name, StringComparison.Ordinal)
            && string.Compare(candidate.Address.ToString(), current.Address.ToString(), StringComparison.Ordinal) < 0);
}
