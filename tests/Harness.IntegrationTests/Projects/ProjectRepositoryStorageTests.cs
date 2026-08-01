using Harness.Host.Projects;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Projects;

public sealed class ProjectRepositoryStorageTests
{
    [Fact]
    public async Task MissingRepositoryIsInitializedUnderTheControlledTenantRoot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var root = Path.Combine(
            Path.GetTempPath(),
            "harness-tests",
            $"project-repositories-{Guid.NewGuid():N}");
        var tenantId = UlidValue.New(DateTimeOffset.UtcNow).ToString();

        try
        {
            var storage = new ProjectRepositoryStorage(root);

            var repository = await storage.EnsureInitializedAsync(
                tenantId,
                "PORTAL-CLIENTE",
                timeout.Token);
            var replay = await storage.EnsureInitializedAsync(
                tenantId,
                "PORTAL-CLIENTE",
                timeout.Token);

            Assert.Equal(repository, replay);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, tenantId, "portal-cliente")),
                repository);
            Assert.True(Directory.Exists(Path.Combine(repository, ".git")));
            Assert.True(Path.IsPathRooted(repository));
            Assert.Equal("main", (await GitAsync(repository, "branch", "--show-current")).Trim());
            Assert.Equal(
                "chore: initialize project",
                (await GitAsync(repository, "log", "-1", "--pretty=%s")).Trim());

            // A prova que faltava: o repositório criado pelo produto já aceita a operação que o
            // primeiro profissional executa. `git init` sem commit passa no teste superficial,
            // mas `worktree add` falha porque não há revisão-base.
            var worktree = Path.Combine(root, "worktree-proof");
            using var manager = await GitWorktreeManager.OpenAsync(
                repository, root, timeout.Token);
            var descriptor = await manager.CreateTaskWorktreeAsync(
                "task/proof", "attempt-proof", worktree, cancellationToken: timeout.Token);
            Assert.True(Directory.Exists(worktree));
            Assert.Equal("task/proof", descriptor.BranchName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    private static async Task<string> GitAsync(string repository, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
