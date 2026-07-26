namespace GeminiBridge;

public enum ExitCode
{
    Success = 0,
    Authentication = 2,
    Usage = 3,
    RemoteApi = 4,
    Agent = 5,
    Security = 6
}

public sealed class BridgeException : Exception
{
    public BridgeException(ExitCode exitCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }

    public ExitCode ExitCode { get; }
}
