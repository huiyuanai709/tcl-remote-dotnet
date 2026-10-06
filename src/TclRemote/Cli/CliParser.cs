using System.Globalization;

namespace TclRemote;

internal sealed class ParsedCli
{
    public string? Command { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }
    public string? Ip { get; set; }
    public bool IpSet { get; set; }
    public string? Name { get; set; }
    public bool NameSet { get; set; }
    public string? Host { get; set; }
    public bool HostSet { get; set; }
    public int Port { get; set; }
    public bool PortSet { get; set; }
    public int Repeat { get; set; } = 1;
    public bool RepeatSet { get; set; }
    public double? TimeoutSeconds { get; set; }
    public string? Key { get; set; }
}

internal static class CliParser
{
    public static ParsedCli Parse(string[] args)
    {
        var cli = new ParsedCli();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "help")
            {
                cli.Help = true;
                continue;
            }

            if (arg is "--version")
            {
                cli.Version = true;
                continue;
            }

            if (TryValue(arg, args, ref i, "ip", out var ip))
            {
                cli.Ip = ip;
                cli.IpSet = true;
                continue;
            }

            if (TryValue(arg, args, ref i, "name", out var name))
            {
                cli.Name = name;
                cli.NameSet = true;
                continue;
            }

            if (TryValue(arg, args, ref i, "host", out var host))
            {
                cli.Host = host;
                cli.HostSet = true;
                continue;
            }

            if (TryValue(arg, args, ref i, "port", out var portText))
            {
                if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535)
                    throw new CommandException($"无效端口: {portText}");
                cli.Port = port;
                cli.PortSet = true;
                continue;
            }

            if (TryValue(arg, args, ref i, "repeat", out var repeatText))
            {
                if (!int.TryParse(repeatText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeat) || repeat < 1 || repeat > 100)
                    throw new CommandException($"无效重复次数: {repeatText}");
                cli.Repeat = repeat;
                cli.RepeatSet = true;
                continue;
            }

            if (TryValue(arg, args, ref i, "timeout", out var timeoutText))
            {
                if (!double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout) || timeout <= 0)
                    throw new CommandException($"无效超时: {timeoutText}");
                cli.TimeoutSeconds = timeout;
                continue;
            }

            if (arg.StartsWith('-'))
                throw new CommandException($"未知参数: {arg}");

            if (cli.Command is null && arg is "discover" or "send" or "shell" or "serve")
            {
                cli.Command = arg;
                continue;
            }

            if (cli.Command == "send" && cli.Key is null)
            {
                cli.Key = arg;
                continue;
            }

            throw new CommandException($"未知参数: {arg}");
        }

        return cli;
    }

    private static bool TryValue(string arg, string[] args, ref int index, string name, out string value)
    {
        var flag = "--" + name;
        if (arg == flag)
        {
            if (index + 1 >= args.Length)
                throw new CommandException($"缺少 {flag} 的值");
            value = args[++index];
            return true;
        }

        var prefix = flag + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = "";
        return false;
    }
}
