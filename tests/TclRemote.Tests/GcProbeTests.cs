namespace TclRemote.Tests;

public class GcProbeTests
{
    [Fact]
    public void StartupLogReportsWhetherSatoriGcKeyIsPresent()
    {
        var previous = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            GcProbe.WriteStartupLog();
        }
        finally
        {
            Console.SetError(previous);
        }

        var expected = GC.GetConfigurationVariables().ContainsKey("SatoriGC") ? "true" : "false";
        Assert.Contains($"[*] SatoriGC={expected}", writer.ToString(), StringComparison.Ordinal);
    }
}
