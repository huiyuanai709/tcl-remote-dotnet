using System.Text.Json.Serialization;

namespace TclRemote;

internal sealed class HealthResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "healthy";
}

internal sealed class ErrorResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class SendResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("repeat")]
    public int Repeat { get; set; }
}

internal sealed class KeysResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("keys")]
    public Dictionary<string, int> Keys { get; set; } = new();

    [JsonPropertyName("default_ip")]
    public string? DefaultIp { get; set; }
}

internal sealed class DiscoverResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("devices")]
    public List<TvDeviceDto> Devices { get; set; } = [];

    [JsonPropertyName("default_ip")]
    public string? DefaultIp { get; set; }
}

internal sealed class SendRequest
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("repeat")]
    public int? Repeat { get; set; }
}

internal sealed class TvDeviceDto
{
    [JsonPropertyName("proto_ver")]
    public string ProtoVer { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("display")]
    public string Display { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("wifi_mac")]
    public string WifiMac { get; set; } = "";

    [JsonPropertyName("bt_mac1")]
    public string BtMac1 { get; set; } = "";

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    public static TvDeviceDto From(TvDevice device) => new()
    {
        ProtoVer = device.ProtoVer,
        Timestamp = device.Timestamp,
        Name = device.Name,
        Type = device.Type,
        Status = device.Status,
        Display = device.Display,
        Capability = device.Capability,
        WifiMac = device.WifiMac,
        BtMac1 = device.BtMac1,
        Ip = device.Ip,
    };
}
