namespace TclRemote;

internal static class Protocol
{
    public const int DiscoverPort = 6537;
    public const int ControlPort = 6553;
    public const int MaxFrameLength = 1024 * 1024;
    public const string PhoneProtoVersion = "1";
    public const string IdentityType = "159";
    public const string KeyType = "149";

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultDiscoverTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan KeyInterval = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(18);

    public const string AppVersion = "1.0.0";
}
