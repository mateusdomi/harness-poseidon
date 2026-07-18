namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public sealed class CodexAppServerException : Exception
{
    public CodexAppServerException(string message)
        : base(message)
    {
    }

    public CodexAppServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
