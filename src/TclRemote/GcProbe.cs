namespace TclRemote;

internal static class GcProbe
{
    internal static bool IsSatoriGc =>
        GC.GetConfigurationVariables().ContainsKey("SatoriGC");

    internal static void WriteStartupLog() =>
        Console.Error.WriteLine($"[*] SatoriGC={(IsSatoriGc ? "true" : "false")}");
}
