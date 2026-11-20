using System.Net;
using TclRemote;

namespace TclRemote.Tests;

public class TvFollowTests
{
    private const string HomeMac = "aa:bb:cc:dd:ee:01";
    private const string OtherMac = "bb:bb:cc:dd:ee:02";

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    [InlineData("aabb.ccdd.ee01")]
    [InlineData("aa bb cc dd ee 01")]
    public void MacMatchIgnoresCaseAndSeparators(string text)
    {
        var identity = TvIdentity.FromMacs(text, null);
        Assert.NotNull(identity);
        Assert.Equal("aa:bb:cc:dd:ee:01", identity.Display);
        Assert.True(identity.Matches(Device("10.0.0.2", "aa:bb:cc:dd:ee:01")));
        Assert.True(identity.Matches(Device("10.0.0.2", "", bluetooth: "aa:bb:cc:dd:ee:01")));
    }

    [Fact]
    public void EmptyOrShortMacIsNotAnIdentity()
    {
        Assert.Null(TvIdentity.From(Device("10.0.0.2", "")));
        Assert.Null(TvIdentity.From(Device("10.0.0.2", "zz:zz:zz:zz:zz:zz")));
        Assert.Null(TvIdentity.From(Device("10.0.0.2", "aa:bb")));
    }

    [Fact]
    public void FirstUsePicksTheFirstDeviceUntilAMacIsKnown()
    {
        var choice = TvFollow.PickInitial(
            [Device("10.0.0.2", OtherMac, "Other"), Device("10.0.0.5", HomeMac, "Living")],
            identity: null);

        Assert.True(choice.Found);
        Assert.Equal("10.0.0.2", choice.Device!.Ip);
    }

    [Fact]
    public void KnownMacSkipsAnEarlierDevice()
    {
        var identity = TvIdentity.FromMacs(HomeMac, null);
        var choice = TvFollow.PickInitial(
            [Device("10.0.0.2", OtherMac, "Other"), Device("10.0.0.8", HomeMac, "Living")],
            identity);

        Assert.Equal("10.0.0.8", choice.Device!.Ip);
    }

    [Fact]
    public void MissingRememberedSetAndDuplicateMacsAreNotGuessed()
    {
        var identity = TvIdentity.FromMacs(HomeMac, null);
        var missing = TvFollow.PickInitial([Device("10.0.0.2", OtherMac)], identity);
        Assert.False(missing.Found);
        Assert.Contains("已记住", missing.Error);

        var both = TvFollow.ChooseAfterFailure(
            [Device("10.0.0.8", HomeMac), Device("10.0.0.9", HomeMac)],
            identity,
            "10.0.0.5");
        Assert.False(both.Found);
        Assert.Contains("相同的 MAC", both.Error);
    }

    [Fact]
    public void FailureFollowsTheSameMacEvenWhenAnotherSetIsListedFirst()
    {
        var identity = TvIdentity.FromMacs(null, "d6:ab:cd:2a:70:7e");
        var choice = TvFollow.ChooseAfterFailure(
            [
                Device("10.0.0.2", OtherMac, "Other"),
                Device("10.0.0.8", "", "Living", "D6-AB-CD-2A-70-7E"),
            ],
            identity,
            "10.0.0.5");

        Assert.Equal("10.0.0.8", choice.Device!.Ip);
    }

    [Fact]
    public void FailureWithoutAMacDoesNotLeaveTheOldAddressOrPickAmongSeveral()
    {
        var stillThere = TvFollow.ChooseAfterFailure(
            [Device("10.0.0.5", HomeMac), Device("10.0.0.2", OtherMac)],
            identity: null,
            "10.0.0.5");
        Assert.True(stillThere.KeepTransportError);
        Assert.False(stillThere.Found);

        var none = TvFollow.ChooseAfterFailure([], null, "10.0.0.5");
        Assert.Contains("没有找到设备", none.Error);

        var many = TvFollow.ChooseAfterFailure(
            [Device("10.0.0.2", OtherMac), Device("10.0.0.8", HomeMac)],
            null,
            "10.0.0.5");
        Assert.Contains("多台", many.Error);

        var only = TvFollow.ChooseAfterFailure([Device("10.0.0.8", HomeMac)], null, "10.0.0.5");
        Assert.Equal("10.0.0.8", only.Device!.Ip);
    }

    [Fact]
    public void ADifferentMacIsNotTheSameSet()
    {
        var identity = TvIdentity.FromMacs(HomeMac, null);
        var choice = TvFollow.ChooseAfterFailure([Device("10.0.0.8", OtherMac)], identity, "10.0.0.5");
        Assert.False(choice.Found);
        Assert.Contains("同一台", choice.Error);
    }

    private static TvDevice Device(string ip, string wifi, string name = "TCL", string bluetooth = "") => new()
    {
        Name = name,
        Ip = ip,
        WifiMac = wifi,
        BtMac1 = bluetooth,
        Type = "TV",
    };
}

public class FollowSessionTests
{
    private const string HomeMac = "AA-BB-CC-DD-EE-01";
    private const string HomePretty = "aa:bb:cc:dd:ee:01";
    private const string OtherMac = "bb:bb:cc:dd:ee:02";

    [Fact]
    public void FollowsTheSameMacAfterTheAddressChanges()
    {
        using var home = new FakeTvServer();
        using var decoy = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"), port: home.Port);
        using var moved = new FakeTvServer(address: IPAddress.Parse("127.0.0.3"), port: home.Port);
        var logs = new List<string>();
        var scans = new Queue<IReadOnlyList<TvDevice>>([
            [
                Device("127.0.0.1", HomeMac, "Living Room"),
                Device("127.0.0.2", OtherMac, "Other"),
            ],
            [
                Device("127.0.0.2", OtherMac, "Other"),
                Device("127.0.0.3", HomeMac, "Living Room"),
            ],
        ]);
        var calls = 0;
        using var session = NewSession(home.Port, line => logs.Add(line), timeout =>
        {
            calls++;
            return scans.Dequeue();
        });

        var first = session.Send("vol_up", null, 1, clampRepeat: false);
        Assert.True(first.Ok, first.Error);
        Assert.Equal("127.0.0.1", first.Ip);
        Assert.Equal("127.0.0.1", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
        home.WaitUntil(() => home.Keys.Contains("149>>21"));
        Assert.Empty(decoy.Keys);
        Assert.Empty(moved.Keys);

        home.GoAway();
        Thread.Sleep(50);

        var second = session.Send("vol_down", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(second.Ok, second.Error);
        Assert.Equal("127.0.0.3", second.Ip);
        Assert.Equal("127.0.0.3", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
        moved.WaitUntil(() => moved.Keys.Contains("149>>22"));
        Assert.Empty(decoy.Keys);
        Assert.Equal(0, decoy.Handshakes);
        Assert.DoesNotContain("149>>22", home.Keys);

        var third = session.Send("mute", "127.0.0.1", 1, clampRepeat: false);
        Assert.True(third.Ok, third.Error);
        Assert.Equal("127.0.0.3", third.Ip);
        moved.WaitUntil(() => moved.Keys.Contains("149>>23"));
        Assert.Equal(2, calls);
        Assert.Contains(logs, line => line.Contains("127.0.0.3", StringComparison.Ordinal) && line.Contains(HomePretty, StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsAWorkingAddressWithoutDiscovery()
    {
        using var home = new FakeTvServer();
        using var decoy = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"), port: home.Port);
        using var session = NewSession(home.Port, log: null, discover: _ => throw new InvalidOperationException("discovery is not needed"));
        session.DefaultIp = "127.0.0.1";

        var outcome = session.Send("ok", "127.0.0.1", 1, clampRepeat: false);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("127.0.0.1", outcome.Ip);
        Assert.Equal("127.0.0.1", session.DefaultIp);
        Assert.Null(session.RememberedMac);
        home.WaitUntil(() => home.Keys.Contains("149>>15"));
        Assert.Empty(decoy.Keys);
        Assert.Equal(0, decoy.Handshakes);
    }

    [Fact]
    public void ReportsNoDeviceWhenRediscoveryIsEmpty()
    {
        using var session = NewSession(port: 9, log: null, discover: _ => []);
        session.DefaultIp = "127.0.0.9";

        var outcome = session.Send("ok", null, 1, clampRepeat: false);

        Assert.False(outcome.Ok);
        Assert.Equal(500, outcome.StatusCode);
        Assert.Contains("没有找到设备", outcome.Error);
        Assert.Equal("127.0.0.9", session.DefaultIp);
        Assert.Null(session.RememberedMac);
    }

    [Fact]
    public void DoesNotGuessAmongSeveralSetsWhenNoMacIsKnown()
    {
        using var left = new FakeTvServer();
        using var right = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"), port: left.Port);
        using var session = NewSession(left.Port, log: null, discover: _ =>
        [
            Device("127.0.0.1", HomeMac, "Living Room"),
            Device("127.0.0.2", OtherMac, "Other"),
        ]);
        session.DefaultIp = "127.0.0.9";

        var outcome = session.Send("vol_up", "127.0.0.9", 1, clampRepeat: false);

        Assert.False(outcome.Ok);
        Assert.Equal(500, outcome.StatusCode);
        Assert.Contains("多台", outcome.Error);
        Assert.Equal("127.0.0.9", session.DefaultIp);
        Assert.Null(session.RememberedMac);
        Assert.Empty(left.Keys);
        Assert.Empty(right.Keys);
        Assert.Equal(0, left.Handshakes);
        Assert.Equal(0, right.Handshakes);
    }

    [Fact]
    public void DoesNotFollowADifferentMac()
    {
        using var home = new FakeTvServer();
        using var other = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"), port: home.Port);
        var scans = new Queue<IReadOnlyList<TvDevice>>([
            [Device("127.0.0.1", HomeMac, "Living Room")],
            [Device("127.0.0.2", OtherMac, "Other")],
        ]);
        using var session = NewSession(home.Port, log: null, discover: _ => scans.Dequeue());

        var first = session.Send("vol_up", null, 1, clampRepeat: false);
        Assert.True(first.Ok, first.Error);
        home.WaitUntil(() => home.Keys.Contains("149>>21"));
        home.GoAway();
        Thread.Sleep(50);

        var second = session.Send("vol_down", null, 1, clampRepeat: false);

        Assert.False(second.Ok);
        Assert.Contains("同一台", second.Error);
        Assert.Equal("127.0.0.1", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
        Assert.Empty(other.Keys);
        Assert.Equal(0, other.Handshakes);
    }

    [Fact]
    public void PinnedAddressFollowsTheOnlyDiscoveredSet()
    {
        using var tv = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"));
        using var session = NewSession(tv.Port, log: null, discover: _ => [Device("127.0.0.2", HomeMac, "Living Room")]);
        session.DefaultIp = "127.0.0.9";

        var outcome = session.Send("vol_up", null, 1, clampRepeat: false);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("127.0.0.2", outcome.Ip);
        Assert.Equal("127.0.0.2", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
        tv.WaitUntil(() => tv.Keys.Contains("149>>21"));
    }

    [Fact]
    public void DoesNotReplaceAnAddressThatStillAnswersDiscovery()
    {
        using var other = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"));
        using var session = NewSession(other.Port, log: null, discover: _ =>
        [
            Device("127.0.0.1", HomeMac, "Living Room"),
            Device("127.0.0.2", OtherMac, "Other"),
        ]);
        session.DefaultIp = "127.0.0.1";

        var outcome = session.Send("ok", null, 1, clampRepeat: false);

        Assert.False(outcome.Ok);
        Assert.Contains("失败", outcome.Error);
        Assert.DoesNotContain("多台", outcome.Error);
        Assert.Equal("127.0.0.1", session.DefaultIp);
        Assert.Null(session.RememberedMac);
        Assert.Empty(other.Keys);
        Assert.Equal(0, other.Handshakes);
    }

    [Fact]
    public void ExplicitOtherAddressIsNotRewrittenToThePreferredSet()
    {
        using var home = new FakeTvServer();
        using var other = new FakeTvServer(address: IPAddress.Parse("127.0.0.2"), port: home.Port);
        var calls = 0;
        using var session = NewSession(home.Port, log: null, discover: _ =>
        {
            calls++;
            return [];
        });
        session.DefaultIp = "127.0.0.1";

        var sent = session.Send("vol_up", "127.0.0.2", 1, clampRepeat: false);
        Assert.True(sent.Ok, sent.Error);
        Assert.Equal("127.0.0.2", sent.Ip);
        Assert.Equal("127.0.0.1", session.DefaultIp);
        other.WaitUntil(() => other.Keys.Contains("149>>21"));
        Assert.Empty(home.Keys);

        var missed = session.Send("vol_down", "127.0.0.9", 1, clampRepeat: false);
        Assert.False(missed.Ok);
        Assert.Equal(500, missed.StatusCode);
        Assert.Equal("127.0.0.1", session.DefaultIp);
        Assert.Empty(home.Keys);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ManualDiscoveryRemembersAMacWithoutMovingAWorkingAddress()
    {
        var calls = 0;
        using var session = NewSession(port: 9, log: null, discover: _ =>
        {
            calls++;
            return calls == 1
                ?
                [
                    Device("10.0.0.5", HomeMac, "Living Room"),
                    Device("10.0.0.6", OtherMac, "Other"),
                ]
                :
                [
                    Device("10.0.0.6", OtherMac, "Other"),
                    Device("10.0.0.5", HomeMac, "Living Room"),
                ];
        });
        session.DefaultIp = "10.0.0.5";

        var first = session.Discover(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, first.Count);
        Assert.Equal("10.0.0.5", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);

        var second = session.Discover(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, second.Count);
        Assert.Equal("10.0.0.5", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
    }

    [Fact]
    public void ManualDiscoveryDoesNotMoveWhenTheSameMacIsReportedTwice()
    {
        var calls = 0;
        using var session = NewSession(port: 9, log: null, discover: _ =>
        {
            calls++;
            return calls == 1
                ? [Device("10.0.0.5", HomeMac, "Living Room")]
                :
                [
                    Device("10.0.0.8", HomeMac, "Living Room"),
                    Device("10.0.0.9", HomeMac, "Living Room"),
                ];
        });
        session.DefaultIp = "10.0.0.5";

        session.Discover(TimeSpan.FromMilliseconds(1));
        session.Discover(TimeSpan.FromMilliseconds(1));

        Assert.Equal("10.0.0.5", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
    }

    [Fact]
    public void ManualDiscoveryFollowsAMacThatMoved()
    {
        var calls = 0;
        using var session = NewSession(port: 9, log: null, discover: _ =>
        {
            calls++;
            return calls == 1
                ? [Device("10.0.0.5", HomeMac, "Living Room"), Device("10.0.0.6", OtherMac, "Other")]
                : [Device("10.0.0.6", OtherMac, "Other"), Device("10.0.0.8", HomeMac, "Living Room")];
        });
        session.DefaultIp = "10.0.0.5";

        session.Discover(TimeSpan.FromMilliseconds(1));
        session.Discover(TimeSpan.FromMilliseconds(1));

        Assert.Equal("10.0.0.8", session.DefaultIp);
        Assert.Equal(HomePretty, session.RememberedMac);
    }

    [Fact]
    public void EmptyDiscoveryLeavesThePreferredAddressUnset()
    {
        using var session = NewSession(port: 9, log: null, discover: _ => []);
        var found = session.Discover(TimeSpan.FromMilliseconds(1));
        Assert.Empty(found);
        Assert.Null(session.DefaultIp);
        Assert.Null(session.RememberedMac);

        var outcome = session.Send("ok", null, 1, clampRepeat: false);
        Assert.False(outcome.Ok);
        Assert.Contains("未指定电视 IP", outcome.Error);
    }

    private static RemoteSession NewSession(
        int port,
        Action<string>? log,
        Func<TimeSpan, IReadOnlyList<TvDevice>> discover) =>
        new("test", new SessionOptions
        {
            ControlPort = port,
            KeepaliveInterval = TimeSpan.FromHours(1),
            MaxIdle = TimeSpan.FromHours(1),
            KeyInterval = TimeSpan.Zero,
            AutoDiscoverTimeout = TimeSpan.FromMilliseconds(50),
            Log = log,
            DiscoverDevices = discover,
        });

    private static TvDevice Device(string ip, string wifi, string name = "TCL", string bluetooth = "") => new()
    {
        Name = name,
        Ip = ip,
        WifiMac = wifi,
        BtMac1 = bluetooth,
        Type = "TV",
    };
}
