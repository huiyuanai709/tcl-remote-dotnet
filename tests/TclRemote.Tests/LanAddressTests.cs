using System.Net;
using System.Net.NetworkInformation;
using TclRemote;

namespace TclRemote.Tests;

public class LanAddressTests
{
    [Theory]
    [InlineData("198.18.0.1", true)]
    [InlineData("198.19.255.255", true)]
    [InlineData("198.17.255.255", false)]
    [InlineData("198.20.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.17.0.1", true)]
    [InlineData("172.17.255.9", true)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.18.0.1", false)]
    [InlineData("172.30.32.1", true)]
    [InlineData("172.30.33.255", true)]
    [InlineData("172.30.31.255", false)]
    [InlineData("172.30.34.1", false)]
    [InlineData("169.254.1.1", true)]
    [InlineData("192.168.5.9", false)]
    [InlineData("10.1.2.3", false)]
    public void ExcludesFakeIpLoopbackAndBridgeRanges(string text, bool excluded)
    {
        Assert.Equal(excluded, LanAddress.IsExcluded(IPAddress.Parse(text)));
    }

    [Theory]
    [InlineData("tun0", true)]
    [InlineData("utun0", true)]
    [InlineData("Meta", true)]
    [InlineData("META1", true)]
    [InlineData("mihomo", true)]
    [InlineData("docker0", true)]
    [InlineData("br-abc", true)]
    [InlineData("veth0", true)]
    [InlineData("hassio", true)]
    [InlineData("lo", true)]
    [InlineData("eth0", false)]
    [InlineData("wlan0", false)]
    [InlineData("enp0s3", false)]
    public void SkipsTunAndBridgeNames(string name, bool virtualName)
    {
        Assert.Equal(virtualName, LanAddress.IsVirtualName(name));
    }

    [Fact]
    public void PrefersPhysicalRfc1918OverFakeIpAndBridges()
    {
        var candidates = new[]
        {
            Nic("Meta", NetworkInterfaceType.Tunnel, "198.18.0.1"),
            Nic("docker0", NetworkInterfaceType.Ethernet, "172.17.0.1"),
            Nic("hassio", NetworkInterfaceType.Ethernet, "172.30.32.1"),
            Nic("lo", NetworkInterfaceType.Loopback, "127.0.0.1"),
            Nic("eth0", NetworkInterfaceType.Ethernet, "192.168.5.4"),
            Nic("wlan0", NetworkInterfaceType.Wireless80211, "10.0.0.8"),
        };

        var chosen = LanAddress.Choose(candidates, routeSource: IPAddress.Parse("198.18.0.1"));
        Assert.Equal(IPAddress.Parse("192.168.5.4"), chosen);
    }

    [Fact]
    public void RouteTowardTheTvWinsWhenItIsARealLanAddress()
    {
        var candidates = new[]
        {
            Nic("eth0", NetworkInterfaceType.Ethernet, "192.168.5.4"),
            Nic("wlan0", NetworkInterfaceType.Wireless80211, "192.168.1.20"),
        };

        var chosen = LanAddress.Choose(candidates, routeSource: IPAddress.Parse("192.168.1.20"));
        Assert.Equal(IPAddress.Parse("192.168.1.20"), chosen);
    }

    [Fact]
    public void ReturnsNullWhenEveryAddressIsExcluded()
    {
        var candidates = new[]
        {
            Nic("Meta", NetworkInterfaceType.Tunnel, "198.18.0.1"),
            Nic("docker0", NetworkInterfaceType.Ethernet, "172.17.0.1"),
            Nic("lo", NetworkInterfaceType.Loopback, "127.0.0.1"),
        };

        Assert.Null(LanAddress.Choose(candidates, routeSource: IPAddress.Parse("198.19.0.5")));
    }

    [Fact]
    public void ListedAddressesAreNotExcludedWhenAChoiceExists()
    {
        var chosen = LanAddress.Choose(LanAddress.ListCandidates(), routeSource: null);
        if (chosen is null)
            return;

        Assert.False(LanAddress.IsExcluded(chosen));
    }

    private static LanCandidate Nic(string name, NetworkInterfaceType kind, string address) =>
        new(name, kind, IPAddress.Parse(address));
}
