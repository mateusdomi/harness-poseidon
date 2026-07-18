namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public sealed class CodexCliAppServerOptions
{
    public CodexCliAppServerOptions(
        string executablePath,
        string executionRoot,
        string workingDirectory,
        string stateDirectory,
        TimeSpan heartbeatInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);

        ExecutablePath = Path.GetFullPath(executablePath);
        ExecutionRoot = Path.GetFullPath(executionRoot);
        WorkingDirectory = EnsureContained(ExecutionRoot, workingDirectory, nameof(workingDirectory));
        StateDirectory = EnsureContained(ExecutionRoot, stateDirectory, nameof(stateDirectory));
        HeartbeatInterval = heartbeatInterval;

        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException("The configured Codex CLI executable was not found.", ExecutablePath);
        }
    }

    public string ExecutablePath { get; }

    public string ExecutionRoot { get; }

    public string WorkingDirectory { get; }

    public string StateDirectory { get; }

    public TimeSpan HeartbeatInterval { get; }

    private static string EnsureContained(string root, string candidate, string parameterName)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(root, fullCandidate);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException("The path must be contained by the execution root.", parameterName);
        }

        return fullCandidate;
    }
}
