namespace TclRemote;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return CliApp.Run(args);
        }
        catch (CommandException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[!] {ex.Message}");
            return 1;
        }
    }
}
