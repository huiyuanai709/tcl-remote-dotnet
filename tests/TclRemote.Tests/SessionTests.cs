using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TclRemote;

namespace TclRemote.Tests;

public class SessionTests
{
    [Fact]
    public void AesSessionSendsVectorAndPlaintextStatusStillNegotiatesAes()
    {
        using var server = new FakeTvServer(algorithmType: 1, encryptStatus: false);
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var outcome = session.Send("vol_up", "127.0.0.1", 1, clampRepeat: false);

        Assert.True(outcome.Ok, outcome.Error);
        server.WaitUntil(() => server.Keys.Count >= 1);
        Assert.Equal("149>>21", Assert.Single(server.Keys));
        var cipher = Assert.Single(server.Payloads, static payload => payload.Length == 16);
        Assert.Equal(Convert.FromHexString("fe4f79c48fedbcf4dd5a3c3f1345cfa6"), cipher);
    }

    [Fact]
    public void EncryptedStatusFrameStillUsesAlgorithmFromSecondFrame()
    {
        using var server = new FakeTvServer(algorithmType: 1, encryptStatus: true);
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var outcome = session.Send("vol_down", "127.0.0.1", 1, clampRepeat: true);

        Assert.True(outcome.Ok, outcome.Error);
        server.WaitUntil(() => server.Keys.Count >= 1);
        Assert.Equal("149>>22", Assert.Single(server.Keys));
        Assert.Contains(server.Payloads, payload => payload.SequenceEqual(Convert.FromHexString("fcf2da309942a7d7f880ab43afe1ae32")));
    }

    [Fact]
    public void AlgorithmZeroSendsPlaintext()
    {
        using var server = new FakeTvServer(algorithmType: 0, encryptStatus: false);
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var outcome = session.Send("21", "127.0.0.1", 1, clampRepeat: false);

        Assert.True(outcome.Ok, outcome.Error);
        server.WaitUntil(() => !server.Payloads.IsEmpty);
        Assert.Equal("149>>21"u8.ToArray(), Assert.Single(server.Payloads));
    }

    [Fact]
    public void UnknownKeyDoesNotOpenAConnection()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var outcome = session.Send("not-a-key", "127.0.0.1", 1, clampRepeat: true);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal(0, server.Handshakes);
    }

    [Fact]
    public void ClampsHttpRepeat()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var outcome = session.Send("ok", "127.0.0.1", 25, clampRepeat: true);

        Assert.True(outcome.Ok, outcome.Error);
        server.WaitUntil(() => server.Keys.Count >= 20);
        Assert.Equal(20, outcome.Repeat);
        Assert.Equal(20, server.Keys.Count);
        Assert.All(server.Keys, key => Assert.Equal("149>>15", key));
    }

    [Fact]
    public void KeepaliveSendsZeroLengthFrames()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromMilliseconds(200));
        var outcome = session.Send("home", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(outcome.Ok, outcome.Error);

        var started = DateTime.UtcNow;
        while (server.EmptyFrames < 3 && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            Thread.Sleep(50);

        Assert.True(server.EmptyFrames >= 3, $"empty frames={server.EmptyFrames}");
    }

    [Fact]
    public void ReconnectsAfterTheTvResetsTheSocket()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1), maxIdle: TimeSpan.FromHours(1));
        var first = session.Send("vol_up", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(first.Ok, first.Error);
        server.WaitUntil(() => server.Keys.Contains("149>>21"));
        Assert.Equal(1, server.Handshakes);

        server.DropAll();
        Thread.Sleep(200);

        var second = session.Send("vol_down", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(second.Ok, second.Error);
        server.WaitUntil(() => server.Keys.Contains("149>>22"));
        Assert.True(server.Handshakes >= 2, $"handshakes={server.Handshakes}");
        Assert.Contains("149>>22", server.Keys);
    }

    [Fact]
    public void ReconnectsWhenTheSessionGoesIdle()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1), maxIdle: TimeSpan.FromMilliseconds(1));
        Assert.True(session.Send("up", "127.0.0.1", 1, clampRepeat: false).Ok);
        Thread.Sleep(40);
        var second = session.Send("down", "127.0.0.1", 1, clampRepeat: false);

        Assert.True(second.Ok, second.Error);
        server.WaitUntil(() => server.Handshakes >= 2 && server.Keys.Contains("149>>12"));
        Assert.Equal(2, server.Handshakes);
        Assert.Contains("149>>11", server.Keys);
        Assert.Contains("149>>12", server.Keys);
    }

    [Fact]
    public void HealthySendDoesNotWaitOutTheDeadPeerBudget()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var started = DateTime.UtcNow;
        var outcome = session.Send("ok", "127.0.0.1", 1, clampRepeat: false);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(1500), "a live TV should be acknowledged immediately");
    }

    [Fact]
    public void SendFailsWhenTheTvGoesAway()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var first = session.Send("vol_up", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(first.Ok, first.Error);
        server.WaitUntil(() => server.Keys.Contains("149>>21"));

        server.GoAway();
        Thread.Sleep(50);

        var started = DateTime.UtcNow;
        var second = session.Send("vol_down", "127.0.0.1", 1, clampRepeat: false);
        Assert.False(second.Ok);
        Assert.Equal(500, second.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(second.Error));
        Assert.Contains("失败", second.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "a refused reconnect should not wait on the ack budget");
    }

    [Fact]
    public void SendToAClosedPortReturnsAnError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var session = NewSession(port, keepalive: TimeSpan.FromHours(1));
        var started = DateTime.UtcNow;
        var outcome = session.Send("ok", "127.0.0.1", 1, clampRepeat: false);

        Assert.False(outcome.Ok);
        Assert.Equal(500, outcome.StatusCode);
        Assert.Contains("失败", outcome.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void SendFailsWhenThePeerStopsAcknowledging()
    {
        using var server = new FakeTvServer();
        using var session = NewSession(server.Port, keepalive: TimeSpan.FromHours(1));
        var first = session.Send("vol_up", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(first.Ok, first.Error);
        server.WaitUntil(() => server.Keys.Contains("149>>21"));

        using var drop = PortDrop.Install(server.Port);
        var started = DateTime.UtcNow;
        var second = session.Send("vol_down", "127.0.0.1", 1, clampRepeat: false);
        var elapsed = DateTime.UtcNow - started;

        Assert.False(second.Ok);
        Assert.Equal(500, second.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(second.Error));
        Assert.DoesNotContain("149>>22", server.Keys);
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"elapsed {elapsed}");
    }

    [Fact]
    public void KeepaliveStopsWhenTheTvGoesAway()
    {
        using var server = new FakeTvServer();
        var logs = new List<string>();
        using var session = NewSession(
            server.Port,
            keepalive: TimeSpan.FromMilliseconds(40),
            log: line =>
            {
                lock (logs)
                    logs.Add(line);
            });

        Assert.True(session.Send("home", "127.0.0.1", 1, clampRepeat: false).Ok);
        server.WaitUntil(() => server.Handshakes >= 1);
        var handshakes = server.Handshakes;
        server.GoAway();
        Thread.Sleep(500);

        int offline;
        lock (logs)
            offline = logs.Count(static line => line.Contains("无响应", StringComparison.Ordinal) || line.Contains("停止保活", StringComparison.Ordinal));

        Assert.Equal(1, offline);
        Assert.Equal(handshakes, server.Handshakes);
    }

    [Fact]
    public void KeepaliveDoesNotSpinWhenThePeerStopsAcknowledging()
    {
        using var server = new FakeTvServer();
        var logs = new List<string>();
        using var session = NewSession(
            server.Port,
            keepalive: TimeSpan.FromMilliseconds(100),
            log: line =>
            {
                lock (logs)
                    logs.Add(line);
            });

        Assert.True(session.Send("home", "127.0.0.1", 1, clampRepeat: false).Ok);
        server.WaitUntil(() => server.Handshakes >= 1);
        using var drop = PortDrop.Install(server.Port);
        var started = DateTime.UtcNow;
        Thread.Sleep(3500);

        int offline;
        int total;
        lock (logs)
        {
            total = logs.Count;
            offline = logs.Count(static line => line.Contains("无响应", StringComparison.Ordinal) || line.Contains("停止保活", StringComparison.Ordinal));
        }

        Assert.Equal(1, offline);
        Assert.True(total < 30, $"log lines={total}");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(6));
        Assert.Equal(1, server.Handshakes);
    }

    private static RemoteSession NewSession(int port, TimeSpan keepalive, TimeSpan? maxIdle = null, Action<string>? log = null) =>
        new("test", new SessionOptions
        {
            ControlPort = port,
            KeepaliveInterval = keepalive,
            MaxIdle = maxIdle ?? TimeSpan.FromHours(1),
            KeyInterval = TimeSpan.Zero,
            AutoDiscoverTimeout = TimeSpan.FromMilliseconds(200),
            Log = log,
        });

    private sealed class PortDrop : IDisposable
    {
        private readonly List<string[]> _deletes = [];

        public static PortDrop Install(int port)
        {
            var drop = new PortDrop();
            string[][] specs =
            [
                ["INPUT", "--dport", port.ToString()],
                ["OUTPUT", "--sport", port.ToString()],
                ["INPUT", "--sport", port.ToString()],
                ["OUTPUT", "--dport", port.ToString()],
            ];
            foreach (var spec in specs)
            {
                Run("iptables", "-I", spec[0], "-p", "tcp", spec[1], spec[2], "-j", "DROP");
                drop._deletes.Add(["iptables", "-D", spec[0], "-p", "tcp", spec[1], spec[2], "-j", "DROP"]);
            }

            return drop;
        }

        public void Dispose()
        {
            foreach (var rule in _deletes)
            {
                try
                {
                    Run(rule);
                }
                catch (Exception)
                {
                    // The rule may already be gone.
                }
            }

            _deletes.Clear();
        }

        private static void Run(params string[] iptablesArgs)
        {
            var start = new ProcessStartInfo("sudo")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-n");
            foreach (var arg in iptablesArgs)
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 sudo");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"iptables 失败 ({process.ExitCode}): {stderr}");
        }
    }
}
