using System.Diagnostics;

namespace Harness.Modules.Execution.Infrastructure.Git;

public sealed class GitWorktreeManager : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly string _controlledRoot;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    private GitWorktreeManager(string repositoryRoot, string controlledRoot)
    {
        _repositoryRoot = repositoryRoot;
        _controlledRoot = controlledRoot;
    }

    public static async Task<GitWorktreeManager> OpenAsync(
        string repositoryRoot,
        string controlledRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlledRoot);

        var fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        var fullControlledRoot = Path.GetFullPath(controlledRoot);
        EnsureContained(fullControlledRoot, fullRepositoryRoot, nameof(repositoryRoot));

        var result = await RunGitAsync(
            fullRepositoryRoot,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        if (result.ExitCode != 0 ||
            !string.Equals(Path.GetFullPath(result.StandardOutput.Trim()), fullRepositoryRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The configured path is not the root of a Git repository.");
        }

        return new GitWorktreeManager(fullRepositoryRoot, fullControlledRoot);
    }

    public async Task<GitWorktreeDescriptor> CreateTaskWorktreeAsync(
        string branchName,
        string attemptId,
        string worktreePath,
        string baseReference = "HEAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        if (!branchName.StartsWith("task/", StringComparison.Ordinal) ||
            branchName[0] == '-' ||
            baseReference[0] == '-')
        {
            throw new ArgumentException("Task branches must use the task/ prefix and safe Git references.", nameof(branchName));
        }

        var destination = EnsureContained(_controlledRoot, worktreePath, nameof(worktreePath));

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            var branchValidation = await RunGitAsync(
                _repositoryRoot,
                ["check-ref-format", "--branch", branchName],
                cancellationToken);
            if (branchValidation.ExitCode != 0)
            {
                throw new ArgumentException("The task branch name is not a valid Git branch.", nameof(branchName));
            }

            var registeredWorktrees = await ListWorktreesCoreAsync(cancellationToken);
            var existing = registeredWorktrees.SingleOrDefault(item =>
                string.Equals(item.WorktreePath, destination, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!string.Equals(existing.BranchName, branchName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The destination is registered for a different branch.");
                }

                return existing with { AttemptId = attemptId };
            }

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new InvalidOperationException("The worktree destination exists but is not registered by Git.");
            }

            var branchExists = await RunGitAsync(
                _repositoryRoot,
                ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"],
                cancellationToken);
            if (branchExists.ExitCode is not (0 or 1))
            {
                throw CreateGitException("inspect the task branch", branchExists);
            }

            var arguments = branchExists.ExitCode == 0
                ? new[] { "worktree", "add", destination, branchName }
                : ["worktree", "add", "-b", branchName, destination, baseReference];
            var creation = await RunGitAsync(_repositoryRoot, arguments, cancellationToken);
            if (creation.ExitCode != 0)
            {
                throw CreateGitException("create the task worktree", creation);
            }

            var created = (await ListWorktreesCoreAsync(cancellationToken)).Single(item =>
                string.Equals(item.WorktreePath, destination, StringComparison.Ordinal));
            return created with { AttemptId = attemptId };
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListLocalBranchesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            _repositoryRoot,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("list local branches", result);
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<GitWorktreeDescriptor>> ListWorktreesAsync(
        CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            return await ListWorktreesCoreAsync(cancellationToken);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public void Dispose() => _metadataGate.Dispose();

    private async Task<IReadOnlyList<GitWorktreeDescriptor>> ListWorktreesCoreAsync(
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            _repositoryRoot,
            ["worktree", "list", "--porcelain"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateGitException("list worktrees", result);
        }

        var descriptors = new List<GitWorktreeDescriptor>();
        string? path = null;
        string? head = null;
        string? branch = null;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            if (line.Length == 0)
            {
                AddDescriptor(descriptors, path, head, branch);
                path = null;
                head = null;
                branch = null;
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line[9..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line[5..];
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line[18..];
            }
        }

        AddDescriptor(descriptors, path, head, branch);
        return descriptors;
    }

    private static void AddDescriptor(
        List<GitWorktreeDescriptor> descriptors,
        string? path,
        string? head,
        string? branch)
    {
        if (path is not null && head is not null && branch is not null)
        {
            descriptors.Add(new GitWorktreeDescriptor(string.Empty, branch, Path.GetFullPath(path), head));
        }
    }

    private static string EnsureContained(string root, string candidate, string parameterName)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(root, fullCandidate);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException("The path must be contained by the controlled root.", parameterName);
        }

        return fullCandidate;
    }

    private static async Task<GitCommandResult> RunGitAsync(
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
        return new GitCommandResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static InvalidOperationException CreateGitException(string operation, GitCommandResult result) =>
        new($"Git could not {operation} (exit {result.ExitCode}): {result.StandardError.Trim()}");

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
