namespace TclRemote;

internal sealed class CommandException(string message, int exitCode = 2) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
