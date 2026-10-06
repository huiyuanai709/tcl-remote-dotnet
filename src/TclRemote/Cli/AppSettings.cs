using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TclRemote;

internal readonly record struct ServeSettings(string? TvIp, string Host, int Port, string ClientName);

internal sealed class HaOptions
{
    [JsonPropertyName("tv_ip")]
    public string? TvIp { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }
}

internal static class AppSettings
{
    public const string OptionsPathVariable = "TCL_OPTIONS_FILE";
    public const string DefaultOptionsPath = "/data/options.json";

    public static string? ResolveIp(ParsedCli cli)
    {
        if (cli.IpSet)
            return NullIfEmpty(cli.Ip);
        return NullIfEmpty(Environment.GetEnvironmentVariable("TCL_TV_IP"));
    }

    public static string ResolveName(ParsedCli cli, string fallback)
    {
        if (cli.NameSet && !string.IsNullOrWhiteSpace(cli.Name))
            return cli.Name.Trim();
        var env = NullIfEmpty(Environment.GetEnvironmentVariable("TCL_NAME"))
            ?? NullIfEmpty(Environment.GetEnvironmentVariable("TCL_CLIENT_NAME"));
        return env ?? fallback;
    }

    public static ServeSettings ResolveServe(ParsedCli cli)
    {
        var path = NullIfEmpty(Environment.GetEnvironmentVariable(OptionsPathVariable)) ?? DefaultOptionsPath;
        var ha = HaOptionsLoader.TryLoad(path);
        var ip = First(cli.IpSet, cli.Ip, "TCL_TV_IP", ha?.TvIp);
        var host = First(cli.HostSet, cli.Host, "TCL_HOST", null) ?? "127.0.0.1";
        int port;
        if (cli.PortSet)
        {
            port = cli.Port;
        }
        else if (int.TryParse(Environment.GetEnvironmentVariable("TCL_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
            && envPort is >= 0 and <= 65535)
        {
            port = envPort;
        }
        else if (ha is { Port: >= 1 and <= 65535 })
        {
            port = ha.Port;
        }
        else
        {
            port = 8765;
        }

        var name = First(cli.NameSet, cli.Name, "TCL_NAME", null);
        name = NullIfEmpty(name) ?? NullIfEmpty(Environment.GetEnvironmentVariable("TCL_CLIENT_NAME"));
        name = NullIfEmpty(name) ?? NullIfEmpty(ha?.ClientName) ?? "TCL Web Remote";
        return new ServeSettings(NullIfEmpty(ip), host, port, name);
    }

    private static string? First(bool cliSet, string? cliValue, string envName, string? fallback)
    {
        if (cliSet)
            return NullIfEmpty(cliValue);
        var env = NullIfEmpty(Environment.GetEnvironmentVariable(envName));
        return env ?? NullIfEmpty(fallback);
    }

    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class HaOptionsLoader
{
    public static HaOptions? TryLoad(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(bytes, AppJsonContext.Default.HaOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"[!] 无法解析 {path}: {ex.Message}");
            return null;
        }
    }
}
