using System.Diagnostics;

namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public static class GitCheckpointContextBuilder
{
    public static async Task<GitCheckpointContext> CaptureAsync(
        string repositoryPath,
        string checkpoint = "HEAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
        if (checkpoint[0] == '-')
        {
            throw new ArgumentException("The checkpoint cannot start with an option prefix.", nameof(checkpoint));
        }

        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        var reportedRoot = await RunGitAsync(fullRepositoryPath, ["rev-parse", "--show-toplevel"], cancellationToken);
        var canonicalRoot = Path.GetFullPath(reportedRoot.Trim());
        if (!string.Equals(fullRepositoryPath, canonicalRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The checkpoint path must be the root of its Git repository.");
        }

        var commitSha = await RunGitAsync(
            fullRepositoryPath,
            ["rev-parse", "--verify", $"{checkpoint}^{{commit}}"],
            cancellationToken);
        var status = await RunGitAsync(
            fullRepositoryPath,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken);

        return new GitCheckpointContext(
            fullRepositoryPath,
            commitSha.Trim(),
            string.IsNullOrWhiteSpace(status));
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git exited with code {process.ExitCode}: {error.Trim()}");
        }

        return output;
    }
}
