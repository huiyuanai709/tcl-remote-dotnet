using System.Text.Json;
using TclRemote;

namespace TclRemote.Tests;

[Collection("env")]
public class CliTests : IDisposable
{
    public CliTests()
    {
        Environment.SetEnvironmentVariable("TCL_TV_IP", null);
        Environment.SetEnvironmentVariable("TCL_HOST", null);
        Environment.SetEnvironmentVariable("TCL_PORT", null);
        Environment.SetEnvironmentVariable("TCL_NAME", null);
        Environment.SetEnvironmentVariable("TCL_CLIENT_NAME", null);
        Environment.SetEnvironmentVariable(AppSettings.OptionsPathVariable, null);
    }

    public void Dispose() => new CliTests();

    [Fact]
    public void ParsesGlobalAndCommandOptions()
    {
        var cli = CliParser.Parse(["--ip", "192.168.5.9", "send", "vol_up", "--repeat", "3", "--name=Living Room"]);
        Assert.Equal("send", cli.Command);
        Assert.Equal("192.168.5.9", cli.Ip);
        Assert.Equal("vol_up", cli.Key);
        Assert.Equal(3, cli.Repeat);
        Assert.Equal("Living Room", cli.Name);
    }

    [Fact]
    public void ExplicitIpWinsOverEnvironment()
    {
        Environment.SetEnvironmentVariable("TCL_TV_IP", "10.0.0.8");
        var cli = CliParser.Parse(["--ip", "1.2.3.4", "send", "ok"]);
        Assert.Equal("1.2.3.4", AppSettings.ResolveIp(cli));
    }

    [Fact]
    public void EnvironmentSuppliesIpAndName()
    {
        Environment.SetEnvironmentVariable("TCL_TV_IP", "10.1.1.5");
        Environment.SetEnvironmentVariable("TCL_NAME", "container");
        var cli = CliParser.Parse(["send", "ok"]);
        Assert.Equal("10.1.1.5", AppSettings.ResolveIp(cli));
        Assert.Equal("container", AppSettings.ResolveName(cli, "TCL Remote"));
    }

    [Fact]
    public void ServeSettingsPreferCliThenEnvThenOptionsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "tcl-options-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new HaOptions
        {
            TvIp = "192.168.5.9",
            Port = 9000,
            ClientName = "from-file",
        }, AppJsonContext.Default.HaOptions));
        Environment.SetEnvironmentVariable(AppSettings.OptionsPathVariable, path);
        try
        {
            var fromFile = AppSettings.ResolveServe(CliParser.Parse(["serve"]));
            Assert.Equal("192.168.5.9", fromFile.TvIp);
            Assert.Equal(9000, fromFile.Port);
            Assert.Equal("from-file", fromFile.ClientName);
            Assert.Equal("127.0.0.1", fromFile.Host);

            Environment.SetEnvironmentVariable("TCL_PORT", "9100");
            Environment.SetEnvironmentVariable("TCL_HOST", "0.0.0.0");
            var fromEnv = AppSettings.ResolveServe(CliParser.Parse(["serve"]));
            Assert.Equal(9100, fromEnv.Port);
            Assert.Equal("0.0.0.0", fromEnv.Host);
            Assert.Equal("192.168.5.9", fromEnv.TvIp);

            var fromCli = AppSettings.ResolveServe(CliParser.Parse(["serve", "--port", "9200", "--ip", "10.0.0.2", "--name", "cli"]));
            Assert.Equal(9200, fromCli.Port);
            Assert.Equal("10.0.0.2", fromCli.TvIp);
            Assert.Equal("cli", fromCli.ClientName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyTvIpInOptionsMeansDiscover()
    {
        var path = Path.Combine(Path.GetTempPath(), "tcl-options-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"tv_ip":"","port":8765,"client_name":"HA TCL Remote"}""");
        Environment.SetEnvironmentVariable(AppSettings.OptionsPathVariable, path);
        try
        {
            var settings = AppSettings.ResolveServe(CliParser.Parse(["serve"]));
            Assert.Null(settings.TvIp);
            Assert.Equal(8765, settings.Port);
            Assert.Equal("HA TCL Remote", settings.ClientName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HelpAndBadArgs()
    {
        Assert.Equal(0, CliApp.Run(["--help"]));
        Assert.Equal(0, CliApp.Run(["--version"]));
        var ex = Assert.Throws<CommandException>(() => CliApp.Run(["send"]));
        Assert.Equal(2, ex.ExitCode);
        Assert.Throws<CommandException>(() => CliParser.Parse(["--port", "nope"]));
    }
}
