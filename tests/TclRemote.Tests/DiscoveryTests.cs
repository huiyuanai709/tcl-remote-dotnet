using System.Net;
using System.Net.Sockets;
using System.Text;
using TclRemote;

namespace TclRemote.Tests;

public class DiscoveryTests
{
    [Fact]
    public void UnicastReplyToEphemeralPortIsDeduped()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var sample = Encoding.UTF8.GetBytes(
            "14:1791269059605:TCL 55F8-880E:TV:4:TCL 55F8-880E:0117411110101210111100000101101111010011111000100111001:0:34&#05851&#05880&#05831&#05888&#0580e:d6&#058ab&#058cd&#0582a&#05870&#0587e:d4&#058ab&#058cd&#0582a&#05870&#0587e\0");
        var sourcePort = 0;
        using var ready = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            udp.Client.ReceiveTimeout = 2000;
            ready.Set();
            try
            {
                udp.Receive(ref remote);
                sourcePort = remote.Port;
                udp.Send(sample, sample.Length, remote);
                udp.Send(sample, sample.Length, remote);
            }
            catch (Exception)
            {
                // The scan retries once; a miss is reported by the assertions below.
            }
        })
        { IsBackground = true };
        worker.Start();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        var found = DiscoveryClient.Scan(
            TimeSpan.FromSeconds(1),
            [new IPEndPoint(IPAddress.Loopback, port)]);
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));

        Assert.NotEqual(0, sourcePort);
        Assert.NotEqual(Protocol.DiscoverPort, sourcePort);
        var device = Assert.Single(found, static item => item.Ip == "127.0.0.1");
        Assert.Equal("TCL 55F8-880E", device.Name);
        Assert.Equal("34:51:80:31:88:0e", device.WifiMac);
        Assert.Equal("d6:ab:cd:2a:70:7e", device.BtMac1);
    }
}
