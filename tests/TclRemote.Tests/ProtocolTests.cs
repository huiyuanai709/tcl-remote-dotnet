using System.Net;
using System.Text;
using TclRemote;

namespace TclRemote.Tests;

public class ProtocolTests
{
    private const string Capability =
        "159>>TCL-CN-T972-F8>>1460013969:5.27_260302>>V8-A972T01-LF1V396>>570585077>>34:51:80:31:88:0e>>1>>null>>1>>0>>535>>>>>>>>>>d6:ab:cd:2a:70:7e>>d4:ab:cd:2a:70:7e>>6a44fa0f1697892051ce0504>>[\"192.168.5.9\"]";

    private const string DiscoverSample =
        "14:1791269059605:TCL 55F8-880E:TV:4:TCL 55F8-880E:0117411110101210111100000101101111010011111000100111001:0:34&#05851&#05880&#05831&#05888&#0580e:d6&#058ab&#058cd&#0582a&#05870&#0587e:d4&#058ab&#058cd&#0582a&#05870&#0587e\0";

    [Fact]
    public void EncryptsVolumeKeysToKnownVectors()
    {
        Assert.Equal(Hex("fe4f79c48fedbcf4dd5a3c3f1345cfa6"), AesCipher.Encrypt("149>>21"u8));
        Assert.Equal(Hex("fcf2da309942a7d7f880ab43afe1ae32"), AesCipher.Encrypt("149>>22"u8));
    }

    [Fact]
    public void DecryptsStatusFrameAndStripsPkcs7()
    {
        var plain = AesCipher.Decrypt(Hex("db9ae78f362343a73fb8cd6c9f5f41b7"));
        Assert.Equal("253>>1>>0"u8.ToArray(), plain);
    }

    [Fact]
    public void RoundTripsArbitraryPlaintext()
    {
        var text = "hello tcl remote"u8.ToArray();
        Assert.Equal(text, AesCipher.Decrypt(AesCipher.Encrypt(text)));
    }

    [Fact]
    public void StripsPkcs7OnlyWhenPaddingIsValid()
    {
        Assert.Equal(new byte[] { 9, 9 }, AesCipher.StripPkcs7IfValid([9, 9, 2, 2]));
        var invalid = new byte[] { 1, 2, 3, 4, 5, 3 };
        Assert.Equal(invalid, AesCipher.StripPkcs7IfValid(invalid));
    }

    [Fact]
    public void EncodesPlaintextVolumeFrame()
    {
        Assert.Equal(Hex("000000073134393e3e3231"), FrameCodec.Encode("149>>21"u8));
    }

    [Fact]
    public void RoundTripsFramesIncludingEmptyAndSplitReads()
    {
        var payload = "149>>15"u8.ToArray();
        using var stream = new OneByteStream(FrameCodec.Encode(payload));
        Assert.Equal(payload, FrameCodec.Read(stream));

        using var empty = new MemoryStream(FrameCodec.Encode([]));
        Assert.Empty(FrameCodec.Read(empty));
    }

    [Fact]
    public void RejectsOversizeFrameBeforeReadingPayload()
    {
        var header = new byte[] { 0x00, 0x10, 0x00, 0x01 };
        using var stream = new MemoryStream(header);
        var ex = Assert.Throws<InvalidDataException>(() => FrameCodec.Read(stream));
        Assert.Contains("1048577", ex.Message);
    }

    [Fact]
    public void TruncatedFrameThrows()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 4, 1, 2 });
        Assert.Throws<EndOfStreamException>(() => FrameCodec.Read(stream));
    }

    [Theory]
    [InlineData("power", 20)]
    [InlineData("up", 11)]
    [InlineData("down", 12)]
    [InlineData("left", 13)]
    [InlineData("right", 14)]
    [InlineData("ok", 15)]
    [InlineData("enter", 15)]
    [InlineData("back", 16)]
    [InlineData("menu", 18)]
    [InlineData("home", 19)]
    [InlineData("vol_up", 21)]
    [InlineData("VOL-UP", 21)]
    [InlineData("vol_down", 22)]
    [InlineData("mute", 23)]
    [InlineData("ch_up", 27)]
    [InlineData("ch_down", 28)]
    [InlineData("mouse_left", 39)]
    [InlineData("mouse_right", 40)]
    [InlineData("15", 15)]
    [InlineData(" 21 ", 21)]
    public void ResolvesKeys(string key, int code)
    {
        Assert.True(KeyTable.TryResolve(key, out var actual));
        Assert.Equal(code, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("-1")]
    [InlineData("vol")]
    public void RejectsUnknownKeys(string key)
    {
        Assert.False(KeyTable.TryResolve(key, out _));
    }

    [Fact]
    public void ClassifiesPlaintextBeforeTryingAes()
    {
        var plain = "253>>1>>0"u8.ToArray();
        var encrypted = Hex("db9ae78f362343a73fb8cd6c9f5f41b7");
        Assert.True(PayloadCodec.IsPlaintext(plain));
        Assert.False(PayloadCodec.IsPlaintext(encrypted));
        Assert.Equal("253>>1>>0", PayloadCodec.Decode(plain));
        Assert.Equal("253>>1>>0", PayloadCodec.Decode(encrypted));

        var aligned = Encoding.UTF8.GetBytes("159>>NAME>>1>>0123456789abcdef>>1");
        while (aligned.Length % 16 != 0)
            aligned = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(aligned) + " ");
        Assert.Equal(0, aligned.Length % 16);
        Assert.True(PayloadCodec.IsPlaintext(aligned));
        Assert.StartsWith("159>>", PayloadCodec.Decode(aligned));
    }

    [Fact]
    public void HandshakeUsesCapabilityFrameNotTheStatusFrame()
    {
        var encryptedStatus = Hex("db9ae78f362343a73fb8cd6c9f5f41b7");
        var capability = Encoding.UTF8.GetBytes(Capability);
        var fromCipher = PayloadCodec.Interpret(encryptedStatus, capability);
        var fromPlain = PayloadCodec.Interpret("253>>1>>0"u8.ToArray(), capability);
        Assert.Equal(1, fromCipher.AlgorithmType);
        Assert.Equal(1, fromPlain.AlgorithmType);
        Assert.Contains("TCL-CN-T972-F8", fromCipher.CapabilityText);

        var algorithmZero = Encoding.UTF8.GetBytes(
            "159>>TEST>>1>>model>>1>>00:11:22:33:44:55>>0>>null>>1>>0");
        Assert.Equal(0, PayloadCodec.Interpret("253>>1>>0"u8.ToArray(), algorithmZero).AlgorithmType);
        Assert.True(PayloadCodec.TryGetAlgorithmType(Capability, out var parsed));
        Assert.Equal(1, parsed);
    }

    [Fact]
    public void ParsesDiscoveryReplyAndDecodesMacEntities()
    {
        var device = DiscoveryClient.Parse(Encoding.UTF8.GetBytes(DiscoverSample));
        Assert.NotNull(device);
        Assert.Equal("14", device.ProtoVer);
        Assert.Equal("1791269059605", device.Timestamp);
        Assert.Equal("TCL 55F8-880E", device.Name);
        Assert.Equal("TV", device.Type);
        Assert.Equal("4", device.Status);
        Assert.Equal("TCL 55F8-880E", device.Display);
        Assert.StartsWith("011741111010", device.Capability);
        Assert.Equal("34:51:80:31:88:0e", device.WifiMac);
        Assert.Equal("d6:ab:cd:2a:70:7e", device.BtMac1);
    }

    [Fact]
    public void IgnoresNonTvDiscoveryPackets()
    {
        var phone = "1:1:host:PHONE:1:host:uuid:0:0\0"u8.ToArray();
        Assert.Null(DiscoveryClient.Parse(phone));
        Assert.Null(DiscoveryClient.Parse("1:2:3"u8));
        Assert.Null(DiscoveryClient.Parse([]));
    }

    [Fact]
    public void DiscoverPacketIsPhoneBroadcastWithTrailingNul()
    {
        var packet = DiscoveryClient.BuildPacket("Desk", "11111111-1111-1111-1111-111111111111");
        Assert.Equal(0, packet[^1]);
        var text = Encoding.UTF8.GetString(packet[..^1]);
        var fields = text.Split(':');
        Assert.Equal("1", fields[0]);
        Assert.Equal("Desk", fields[2]);
        Assert.Equal("PHONE", fields[3]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", fields[6]);
    }

    [Theory]
    [InlineData("192.168.5.9", "255.255.255.0", "192.168.5.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    [InlineData("172.16.8.9", "255.255.255.128", "172.16.8.127")]
    public void ComputesSubnetBroadcast(string ip, string mask, string broadcast)
    {
        Assert.Equal(broadcast, DiscoveryClient.BroadcastAddress(IPAddress.Parse(ip), IPAddress.Parse(mask)).ToString());
    }

    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(3, 3)]
    [InlineData(99, 10)]
    public void ClampsDiscoverTimeout(double input, double expected)
    {
        Assert.Equal(expected, DiscoveryClient.ClampTimeoutSeconds(input));
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;
            var one = new byte[1];
            var n = Read(one, 0, 1);
            if (n == 0)
                return 0;
            buffer[0] = one[0];
            return 1;
        }
    }
}
