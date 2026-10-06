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

    private static RemoteSession NewSession(int port, TimeSpan keepalive, TimeSpan? maxIdle = null) =>
        new("test", new SessionOptions
        {
            ControlPort = port,
            KeepaliveInterval = keepalive,
            MaxIdle = maxIdle ?? TimeSpan.FromHours(1),
            KeyInterval = TimeSpan.Zero,
            AutoDiscoverTimeout = TimeSpan.FromMilliseconds(200),
        });
}
