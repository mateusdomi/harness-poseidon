using System.Diagnostics;
using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.RecoveryTests;

/// <summary>
/// Fase 1A — regressão permanente da retomada por checkpoint.
///
/// `ExecutionCheckpointService` existia completo — captura, política de retomada, consumo — e não
/// tinha UM ÚNICO chamador no repositório. O produto gravava a capacidade de continuar e nunca a
/// exercia: toda queda voltava para a estaca zero, mesmo com a branch da tentativa cheia de
/// trabalho aproveitável. O agente seguinte então refazia — ou desfazia — o que o anterior deixou.
/// </summary>
public sealed class CheckpointResumeTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string Task = "01ARZ3NDEKTSV4RRFFQ69G5FK1";

    [Fact]
    public async Task ACapturedCheckpointIsResumedOnceAndOnlyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "recovery-artifacts", $"checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "checkpoint.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var store = new SqliteExecutionCheckpointStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 18, 0, 0, TimeSpan.Zero);
            var service = new ExecutionCheckpointService(
                store, new StubClock(now), NullLogger<ExecutionCheckpointService>.Instance);

            // A tentativa caiu deixando trabalho: o checkpoint é o que informa a próxima de onde
            // continuar. Sem ele, "a branch existe" não vira "alguém sabe o que fazer com ela".
            var captured = await store.SaveAsync(
                new ExecutionCheckpointSaveCommand(
                    Tenant, "01ARZ3NDEKTSV4RRFFQ69G5FK2", Project, Task,
                    "01ARZ3NDEKTSV4RRFFQ69G5FK3", "01ARZ3NDEKTSV4RRFFQ69G5FK4",
                    "worker-claude-secondary", "backend-specialist", "transient",
                    "task/agent-run-01arz3ndektsv4rrffq69g5fk4", "abc123", "/repo",
                    ["src/**"], ["src/app.cs", "src/app.tests.cs"],
                    "Endpoint criado; faltam os testes de erro.", ["cobrir 404", "cobrir 409"],
                    [], 3, now),
                timeout.Token);
            Assert.NotNull(captured);

            // A mesma conta e o mesmo papel podem continuar: falha transitória não exige troca.
            var resume = await service.TryResumeAsync(
                Tenant, Task, "backend-specialist", "worker-claude-secondary", false, timeout.Token);
            Assert.NotNull(resume);
            Assert.Equal("src/app.cs", resume!.Value.Checkpoint.ChangedFiles[0]);
            Assert.Contains("faltam os testes", resume.Value.Checkpoint.ProgressNote!, StringComparison.Ordinal);

            // Consumido, ele NÃO pode ser entregue de novo: duas tentativas simultâneas partindo do
            // mesmo ponto refariam o mesmo trabalho e colidiriam na mesma branch.
            Assert.True(await service.ConsumeAsync(
                Tenant, resume.Value.Checkpoint.CheckpointId, "01ARZ3NDEKTSV4RRFFQ69G5FK5", timeout.Token));
            Assert.Null(await service.TryResumeAsync(
                Tenant, Task, "backend-specialist", "worker-claude-secondary", false, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WithoutACheckpointTheAttemptStartsFromZeroInsteadOfFakingContinuity()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "recovery-artifacts", $"checkpoint-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "checkpoint.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var service = new ExecutionCheckpointService(
                new SqliteExecutionCheckpointStore(dispatcher),
                new StubClock(new DateTimeOffset(2026, 7, 31, 18, 0, 0, TimeSpan.Zero)),
                NullLogger<ExecutionCheckpointService>.Instance);

            // Nada gravado: recomeçar do zero é a resposta honesta. Melhor do que afirmar uma
            // continuidade que não existe e mandar o agente procurar trabalho que ninguém fez.
            Assert.Null(await service.TryResumeAsync(
                Tenant, Task, "backend-specialist", "worker-claude-secondary", false, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OrphanCheckpointCommitsUntrackedWorkBeforeCleanupCanRemoveTheWorktree()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "recovery-artifacts", $"checkpoint-orphan-{Guid.NewGuid():N}");
        var controlled = Path.Combine(root, "controlled");
        var repository = Path.Combine(controlled, "repository");
        var worktree = Path.Combine(controlled, "worktrees", "attempt");
        const string attempt = "01ARZ3NDEKTSV4RRFFQ69G5FK4";
        var branch = $"task/agent-run-{attempt.ToLowerInvariant()}";
        Directory.CreateDirectory(repository);
        try
        {
            await Git(repository, timeout.Token, "init", "--initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Base\n", timeout.Token);
            await Git(repository, timeout.Token, "add", "README.md");
            await Git(
                repository,
                timeout.Token,
                "-c", "user.name=Poseidon Test", "-c", "user.email=test@poseidon.local",
                "commit", "-m", "base");

            using (var manager = await GitWorktreeManager.OpenAsync(
                       repository, controlled, timeout.Token))
            {
                _ = await manager.CreateTaskWorktreeAsync(
                    branch, attempt, worktree, "HEAD", timeout.Token);
            }

            Directory.CreateDirectory(Path.Combine(worktree, "docs"));
            await File.WriteAllTextAsync(
                Path.Combine(worktree, "docs", "partial.md"),
                "# Trabalho parcial preservado\n",
                timeout.Token);

            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "checkpoint.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var now = new DateTimeOffset(2026, 8, 1, 22, 0, 0, TimeSpan.Zero);
            var service = new ExecutionCheckpointService(
                new SqliteExecutionCheckpointStore(dispatcher),
                new StubClock(now),
                NullLogger<ExecutionCheckpointService>.Instance);

            var captured = await service.CaptureAsync(
                Tenant,
                Project,
                Task,
                attempt,
                attempt,
                "unknown",
                "backend-specialist",
                CheckpointOrigin.Transient,
                branch,
                repository,
                controlled,
                ["docs/**"],
                2,
                "Host reiniciado.",
                worktree,
                timeout.Token);

            Assert.NotNull(captured);
            Assert.Contains("docs/partial.md", captured!.ChangedFiles);
            Assert.NotNull(captured.SourceCommit);
            Assert.Contains($"git-commit:{captured.SourceCommit}", captured.Evidence);
            Assert.Equal(
                "# Trabalho parcial preservado\n",
                await Git(repository, timeout.Token, "show", $"{branch}:docs/partial.md"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<string> Git(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git did not start");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
