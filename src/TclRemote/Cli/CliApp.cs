using System.Text.RegularExpressions;

namespace TclRemote;

internal static partial class CliApp
{
    public static int Run(string[] args)
    {
        var cli = CliParser.Parse(args);
        if (cli.Version && !cli.Help && cli.Command is null)
        {
            Console.WriteLine($"tcl-remote {Protocol.AppVersion}");
            return 0;
        }

        if (cli.Help || cli.Command is null && args.Any(static arg => arg is "--help" or "-h"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        if (cli.Command is null)
            cli.Command = "discover";

        return cli.Command switch
        {
            "discover" => Discover(cli),
            "send" => Send(cli),
            "shell" => Shell(cli),
            "serve" => Serve(cli),
            _ => throw new CommandException($"未知命令: {cli.Command}"),
        };
    }

    private static int Discover(ParsedCli cli)
    {
        var timeout = TimeSpan.FromSeconds(cli.TimeoutSeconds ?? Protocol.DefaultDiscoverTimeout.TotalSeconds);
        Console.WriteLine($"[*] 扫描局域网 TCL 电视（{timeout.TotalSeconds:0.#} 秒）...");
        var devices = DiscoveryClient.Scan(timeout, onFound: PrintFound);
        if (devices.Count == 0)
        {
            Console.WriteLine("[-] 未发现任何设备");
            Console.WriteLine("    请确认:");
            Console.WriteLine("    1. 电视与本机在同一局域网");
            Console.WriteLine("    2. 电视已开启 TCL 局域网遥控");
            Console.WriteLine("    3. 防火墙未屏蔽 UDP 6537");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"[+] 共发现 {devices.Count} 台设备:");
        foreach (var device in devices)
            Console.WriteLine($"    {device.Name,-30} IP={device.Ip,-16} MAC={device.WifiMac}");
        return 0;
    }

    private static int Send(ParsedCli cli)
    {
        if (string.IsNullOrWhiteSpace(cli.Key))
            throw new CommandException("缺少按键。用法: tcl-remote send <按键|按键码> [--repeat N]");

        var ip = RequireIp(AppSettings.ResolveIp(cli));
        if (ip is null)
            return 1;

        var name = AppSettings.ResolveName(cli, "TCL Remote");
        using var session = new RemoteSession(name, ConsoleSession());
        var outcome = session.Send(cli.Key, ip, cli.Repeat, clampRepeat: false);
        if (!outcome.Ok)
        {
            Console.Error.WriteLine($"[!] {outcome.Error}");
            return 1;
        }

        var suffix = outcome.Repeat > 1 ? $" x{outcome.Repeat}" : "";
        Console.WriteLine($"[+] 已发送: {cli.Key}{suffix}");
        return 0;
    }

    private static int Shell(ParsedCli cli)
    {
        var ip = RequireIp(AppSettings.ResolveIp(cli));
        if (ip is null)
            return 1;

        var name = AppSettings.ResolveName(cli, "TCL Remote");
        using var session = new RemoteSession(name, ConsoleSession());
        if (!session.TryConnect(ip, out var connectError))
        {
            Console.Error.WriteLine($"[!] {connectError}");
            Console.Error.WriteLine("[!] 连接失败，请检查电视 IP、是否开机，以及 TCP 6553 是否可达");
            return 1;
        }

        Console.WriteLine(ShellHelp);
        while (true)
        {
            Console.Write("tcl> ");
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                break;
            }

            if (line is null)
                break;
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (line is "q" or "quit" or "exit")
                break;
            if (line is "?" or "help")
            {
                Console.WriteLine(ShellHelp);
                continue;
            }

            if (line == "keys")
            {
                var names = KeyTable.Entries.Select(static entry => entry.Name).Concat(MacroTable.Names).ToList();
                for (var i = 0; i < names.Count; i += 4)
                    Console.WriteLine("  " + string.Join("  ", names.Skip(i).Take(4).Select(static name => name.PadRight(14))));
                continue;
            }

            var volume = VolumePattern().Match(line);
            if (volume.Success)
            {
                var level = int.Parse(volume.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (level == 0)
                    continue;
                var key = level > 0 ? "vol_up" : "vol_down";
                Report(session.Send(key, ip, Math.Abs(level), clampRepeat: false));
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var repeat = 1;
            if (parts.Length >= 2)
            {
                if (!int.TryParse(parts[1], out repeat) || repeat < 1)
                {
                    Console.WriteLine($"[!] 无效重复次数: {parts[1]}");
                    continue;
                }
            }

            Report(session.Send(parts[0], ip, repeat, clampRepeat: false));
        }

        return 0;
    }

    private static int Serve(ParsedCli cli)
    {
        var settings = AppSettings.ResolveServe(cli);
        using var session = new RemoteSession(settings.ClientName, ConsoleSession());
        session.DefaultIp = settings.TvIp;
        var app = WebServer.Build(settings, session);
        WebServer.PrintListening(settings);
        app.Run();
        return 0;
    }

    private static void Report(SendOutcome outcome)
    {
        if (outcome.Ok)
            Console.WriteLine($"[+] {outcome.Key}" + (outcome.Repeat > 1 ? $" x{outcome.Repeat}" : ""));
        else
            Console.WriteLine($"[!] {outcome.Error}");
    }

    private static string? RequireIp(string? ip)
    {
        if (!string.IsNullOrWhiteSpace(ip))
            return ip;

        Console.WriteLine("[*] 未指定 --ip，自动扫描...");
        var devices = DiscoveryClient.Scan(Protocol.DefaultDiscoverTimeout, onFound: PrintFound);
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("[!] 未发现电视，请使用 --ip 手动指定");
            return null;
        }

        if (devices.Count == 1)
        {
            Console.WriteLine($"[*] 使用 IP: {devices[0].Ip}");
            return devices[0].Ip;
        }

        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"[*] 发现 {devices.Count} 台，使用第一台 {devices[0].Ip}");
            return devices[0].Ip;
        }

        Console.WriteLine();
        Console.WriteLine("发现多台设备，请选择:");
        for (var i = 0; i < devices.Count; i++)
            Console.WriteLine($"  {i + 1}. {devices[i].Name}  ({devices[i].Ip})");
        Console.Write("输入编号: ");
        var line = Console.ReadLine();
        if (int.TryParse(line, out var index) && index >= 1 && index <= devices.Count)
            return devices[index - 1].Ip;
        Console.WriteLine($"[*] 使用 IP: {devices[0].Ip}");
        return devices[0].Ip;
    }

    private static SessionOptions ConsoleSession()
    {
        TimeSpan? pause = MacroTable.TryParsePause(Environment.GetEnvironmentVariable("TCL_MACRO_PAUSE_MS"), out var parsed)
            ? parsed
            : null;
        return new SessionOptions
        {
            Log = Console.WriteLine,
            MacroPause = pause,
        };
    }

    private static void PrintFound(TvDevice device) =>
        Console.WriteLine($"  [+] 发现: {device.Name}  IP={device.Ip}  MAC={device.WifiMac}");

    [GeneratedRegex(@"^vol\s+([+-]?\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumePattern();

    private const string HelpText = """
        TCL 电视局域网遥控器

        用法:
          tcl-remote discover [--timeout 秒]
          tcl-remote [--ip IP] [--name 名称] send <按键|按键码> [--repeat N]
          tcl-remote [--ip IP] [--name 名称] shell
          tcl-remote serve [--ip IP] [--host 127.0.0.1] [--port 8765] [--name 名称]

        命令:
          discover   扫描局域网中的 TCL 电视（UDP 6537，不占用该端口）
          send       发送一个按键；名称见下，或直接给十进制按键码
          shell      交互式遥控
          serve      启动 Web 遥控和 HTTP API

        按键:
          power
          up down left right ok enter
          back menu home
          source input          信源键（29）。HDMI 上打开信源面板，桌面上回到上次输入
          hdmi1                 宏：home，等待 2.5 秒，再 source。回到上次输入
          tv tv_home launcher   回到 TCL 桌面（home，19）
          vol_up vol_down mute
          ch_up ch_down
          mouse_left mouse_right

        hdmi1 回到的是上次使用的输入。已经在 HDMI1 上再发 hdmi1，会先经过主页再回来。
        宏忽略 repeat。等待毫秒数可用 TCL_MACRO_PAUSE_MS 覆盖，默认 2500。

        环境变量（便于容器）:
          TCL_TV_IP    电视 IP，对应 --ip
          TCL_HOST     Web 监听地址，对应 --host
          TCL_PORT     Web 监听端口，对应 --port
          TCL_NAME     控制端名称，对应 --name
          TCL_MACRO_PAUSE_MS   宏步骤之间的等待，默认 2500

        Home Assistant 插件还会读取 /data/options.json（可用 TCL_OPTIONS_FILE 改路径）。

        示例:
          tcl-remote discover
          tcl-remote --ip 192.168.5.9 send vol_up --repeat 3
          tcl-remote --ip 192.168.5.9 shell
          tcl-remote serve --ip 192.168.5.9 --host 0.0.0.0 --port 8765
        """;

    private const string ShellHelp = """
        TCL 电视遥控终端（输入 ? 帮助）

          <按键>            发送一次，例如 ok、vol_up
          <按键> <次数>     重复发送，例如 vol_up 5
          vol +N / vol -N   连续调音量
          keys              列出按键名
          q                 退出
        """;
}
