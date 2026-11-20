namespace TclRemote;

// Wi-Fi MAC, then Bluetooth MAC. Discovery is the only place a set is identified;
// a dotted IP is only the last place that set answered.
internal sealed record TvIdentity(string WifiMac, string BluetoothMac)
{
    public bool Matches(TvDevice device)
    {
        var wifi = Normalize(device.WifiMac);
        var bluetooth = Normalize(device.BtMac1);
        if (WifiMac.Length > 0 && (wifi == WifiMac || bluetooth == WifiMac))
            return true;
        if (BluetoothMac.Length > 0 && (wifi == BluetoothMac || bluetooth == BluetoothMac))
            return true;
        return false;
    }

    public string Display => Pretty(WifiMac.Length > 0 ? WifiMac : BluetoothMac);

    public static TvIdentity? From(TvDevice device) => FromMacs(device.WifiMac, device.BtMac1);

    public static TvIdentity? FromMacs(string? wifiMac, string? bluetoothMac)
    {
        var wifi = Normalize(wifiMac);
        var bluetooth = Normalize(bluetoothMac);
        if (wifi.Length == 0 && bluetooth.Length == 0)
            return null;
        return new TvIdentity(wifi, bluetooth);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        Span<char> buffer = stackalloc char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (ch is ':' or '-' or '.' or ' ')
                continue;
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return "";
            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return count == 12 ? new string(buffer[..count]) : "";
    }

    public static string Pretty(string normalized)
    {
        if (normalized.Length != 12)
            return normalized;
        return $"{normalized[..2]}:{normalized[2..4]}:{normalized[4..6]}:{normalized[6..8]}:{normalized[8..10]}:{normalized[10..12]}";
    }
}

internal readonly record struct TvChoice(TvDevice? Device, string? Error, bool KeepTransportError)
{
    public bool Found => Device is not null;

    public static TvChoice Use(TvDevice device) => new(device, null, false);

    public static TvChoice Fail(string error) => new(null, error, false);

    // The old address is still answering discovery, so the failure is the control
    // connection. The caller keeps that error and does not pick a different set.
    public static TvChoice Transport() => new(null, null, true);
}

internal static class TvFollow
{
    public static TvChoice PickInitial(IReadOnlyList<TvDevice> found, TvIdentity? identity)
    {
        if (found.Count == 0)
            return TvChoice.Fail("未指定电视 IP，且自动发现没有找到设备");

        if (identity is null)
            return TvChoice.Use(found[0]);

        var matches = found.Where(identity.Matches).ToList();
        if (matches.Count == 1)
            return TvChoice.Use(matches[0]);
        if (matches.Count == 0)
            return TvChoice.Fail("自动发现没有找到已记住的电视");
        return TvChoice.Fail("发现多台相同 MAC 的电视，无法确定目标");
    }

    public static TvChoice ChooseAfterFailure(IReadOnlyList<TvDevice> found, TvIdentity? identity, string failedIp)
    {
        var elsewhere = found.Where(device => !SameIp(device.Ip, failedIp)).ToList();
        if (identity is not null)
        {
            var moved = elsewhere.Where(identity.Matches).ToList();
            if (moved.Count == 1)
                return TvChoice.Use(moved[0]);
            if (moved.Count > 1)
                return TvChoice.Fail($"连接 {failedIp} 失败，有多台设备使用相同的 MAC，无法确定目标");
            return TvChoice.Fail($"连接 {failedIp} 失败，重新发现没有找到同一台电视");
        }

        // No MAC yet. A reply that still comes from the address we just failed is
        // not evidence the set moved; another answering TV must not inherit the key.
        if (found.Any(device => SameIp(device.Ip, failedIp)))
            return TvChoice.Transport();

        if (elsewhere.Count == 1)
            return TvChoice.Use(elsewhere[0]);
        if (elsewhere.Count == 0)
            return TvChoice.Fail($"连接 {failedIp} 失败，重新发现没有找到设备");
        return TvChoice.Fail($"连接 {failedIp} 失败，发现了多台电视，无法确定哪一台是原来的设备");
    }

    private static bool SameIp(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
}
