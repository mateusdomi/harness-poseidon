using System.Diagnostics;
using System.Globalization;
using Harness.Modules.Execution.Application.Git;
using Harness.Modules.Execution.Domain.Git;
using Harness.Modules.Execution.Infrastructure.Git;

namespace Harness.IntegrationTests.Git;

public sealed class GitWorktreeClaimsPocTests
{
    [Fact]
    public async Task SupersededDocumentMergePreservesTheApprovedPublishedBodyAndRecordsAncestry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory, "poc-artifacts", "superseded", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(artifactRoot, "repository");
        var worktree = Path.Combine(artifactRoot, "worktrees", "attempt-old");
        try
        {
            await CreateFixtureRepositoryAsync(repository, timeout.Token);
            using var manager = await GitWorktreeManager.OpenAsync(repository, artifactRoot, timeout.Token);
            var created = await manager.CreateTaskWorktreeAsync(
                "task/old-document", "attempt-old", worktree,
                cancellationToken: timeout.Token);
            Directory.CreateDirectory(Path.Combine(worktree, "docs"));
            await File.WriteAllTextAsync(
                Path.Combine(worktree, "docs", "demand.md"), "# Versão antiga\n", timeout.Token);
            await RunGitAsync(worktree, ["add", "docs/demand.md"], timeout.Token);
            await RunGitAsync(worktree, ["commit", "-m", "old approved document"], timeout.Token);
            await manager.RemoveTaskWorktreeAsync(
                created.BranchName, created.WorktreePath, deleteBranch: false,
                cancellationToken: timeout.Token);

            Directory.CreateDirectory(Path.Combine(repository, "docs"));
            await File.WriteAllTextAsync(
                Path.Combine(repository, "docs", "demand.md"), "# Versão nova aprovada\n", timeout.Token);
            await RunGitAsync(repository, ["add", "docs/demand.md"], timeout.Token);
            await RunGitAsync(repository, ["commit", "-m", "new approved document"], timeout.Token);

            Assert.Equal(
                "# Versão antiga\n",
                await manager.ReadDocumentFromBranchAsync(
                    "task/old-document", "docs/demand.md", timeout.Token));
            Assert.Equal(
                "# Versão nova aprovada\n",
                await manager.ReadPublishedDocumentAsync("main", "docs/demand.md", timeout.Token));

            await manager.MergeSupersededTaskBranchAsync(
                "task/old-document", "record superseded approved document", timeout.Token);

            Assert.Equal(
                "# Versão nova aprovada\n",
                await File.ReadAllTextAsync(Path.Combine(repository, "docs", "demand.md"), timeout.Token));
            _ = await RunGitAsync(
                repository,
                ["merge-base", "--is-ancestor", "task/old-document", "main"],
                timeout.Token);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ThreeFixtureRepositoriesExerciseParallelWorktreesAndScopeClaimsWithoutTouchingHarnessRefs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var officialRepository = FindOfficialRepositoryRoot();
        var officialStateBefore = await CaptureGitStateAsync(officialRepository, timeout.Token);
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-5",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            var scenarios = new[]
            {
                new ClaimScenario("disjoint-code", "src/api/**", "src/runner/**", true),
                new ClaimScenario("intersecting-code", "src/payments/**", "src/payments/checkout.cs", false),
                new ClaimScenario("disjoint-docs", "docs/specs/**", "docs/runbooks/**", true),
            };

            foreach (var scenario in scenarios)
            {
                await ExecuteScenarioAsync(artifactRoot, scenario, timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }

            var officialStateAfter = await CaptureGitStateAsync(officialRepository, timeout.Token);
            Assert.Equal(officialStateBefore.LocalBranches, officialStateAfter.LocalBranches);
            Assert.Contains("refs/heads/develop", officialStateAfter.LocalBranches);
        }
    }

    private static async Task ExecuteScenarioAsync(
        string artifactRoot,
        ClaimScenario scenario,
        CancellationToken cancellationToken)
    {
        var scenarioRoot = Path.Combine(artifactRoot, scenario.Name);
        var repositoryPath = Path.Combine(scenarioRoot, "repository");
        var worktreeAPath = Path.Combine(scenarioRoot, "worktrees", "attempt-a");
        var worktreeBPath = Path.Combine(scenarioRoot, "worktrees", "attempt-b");
        await CreateFixtureRepositoryAsync(repositoryPath, cancellationToken);
        using var manager = await GitWorktreeManager.OpenAsync(repositoryPath, scenarioRoot, cancellationToken);
        var publishedRevision = await manager.ResolveCommitAsync(
            cancellationToken: cancellationToken);
        Assert.Equal(40, publishedRevision.Length);

        var creationA = manager.CreateTaskWorktreeAsync(
            "task/a",
            "attempt-a",
            worktreeAPath,
            cancellationToken: cancellationToken);
        var creationB = manager.CreateTaskWorktreeAsync(
            "task/b",
            "attempt-b",
            worktreeBPath,
            cancellationToken: cancellationToken);
        var worktrees = await Task.WhenAll(creationA, creationB);

        var idempotent = await manager.CreateTaskWorktreeAsync(
            "task/a",
            "attempt-a",
            worktreeAPath,
            cancellationToken: cancellationToken);
        Assert.Equal(worktrees[0], idempotent);
        Assert.Equal(["main", "task/a", "task/b"], await manager.ListLocalBranchesAsync(cancellationToken));
        Assert.Equal(3, (await manager.ListWorktreesAsync(cancellationToken)).Count);

        var claims = new ScopeClaimRegistry();
        var acquisitionA = claims.TryAcquire("attempt-a", [new ScopeClaim(scenario.ClaimA)]);
        var acquisitionB = claims.TryAcquire("attempt-b", [new ScopeClaim(scenario.ClaimB)]);
        Assert.True(acquisitionA.Acquired);
        Assert.Equal(scenario.SecondClaimAcquired, acquisitionB.Acquired);

        if (scenario.SecondClaimAcquired)
        {
            await Task.WhenAll(
                CommitInWorktreeAsync(worktreeAPath, "agent-a.txt", cancellationToken),
                CommitInWorktreeAsync(worktreeBPath, "agent-b.txt", cancellationToken));
        }
        else
        {
            Assert.Single(acquisitionB.Conflicts);
            await CommitInWorktreeAsync(worktreeAPath, "agent-a.txt", cancellationToken);
            Assert.False(File.Exists(Path.Combine(worktreeBPath, "agent-b.txt")));
        }

        Assert.True(await manager.RemoveTaskWorktreeAsync(
            worktrees[0].BranchName,
            worktrees[0].WorktreePath,
            deleteBranch: false,
            cancellationToken));
        Assert.False(await manager.RemoveTaskWorktreeAsync(
            worktrees[0].BranchName,
            worktrees[0].WorktreePath,
            deleteBranch: false,
            cancellationToken));
        Assert.DoesNotContain(
            await manager.ListWorktreesAsync(cancellationToken),
            item => item.WorktreePath == worktrees[0].WorktreePath);
    }

    private static async Task CreateFixtureRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, ["init", "--initial-branch=main"], cancellationToken);
        await RunGitAsync(repositoryPath, ["config", "user.name", "Harness PoC"], cancellationToken);
        await RunGitAsync(repositoryPath, ["config", "user.email", "poc@harness.invalid"], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "README.md"), "fixture\n", cancellationToken);
        await RunGitAsync(repositoryPath, ["add", "README.md"], cancellationToken);
        await RunGitAsync(repositoryPath, ["commit", "-m", "bootstrap"], cancellationToken);
    }

    private static async Task CommitInWorktreeAsync(
        string worktreePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(worktreePath, fileName), fileName, cancellationToken);
        await RunGitAsync(worktreePath, ["add", fileName], cancellationToken);
        await RunGitAsync(worktreePath, ["commit", "-m", $"complete {fileName}"], cancellationToken);
    }

    private static string FindOfficialRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")) &&
                (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The official Harness repository root was not found.");
    }

    private static async Task<GitRepositoryState> CaptureGitStateAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var branches = await RunGitAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname)", "refs/heads"],
            cancellationToken);
        var worktrees = await RunGitAsync(
            repositoryPath,
            ["worktree", "list", "--porcelain"],
            cancellationToken);
        return new GitRepositoryState(
            branches.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            worktrees.Trim());
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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git fixture process did not start.");
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

    private sealed record ClaimScenario(
        string Name,
        string ClaimA,
        string ClaimB,
        bool SecondClaimAcquired);

    private sealed record GitRepositoryState(
        IReadOnlyList<string> LocalBranches,
        string WorktreePorcelain);
}
