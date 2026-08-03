using System.Globalization;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.CodexCli;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Execution.Infrastructure.Sandbox;

namespace Harness.IntegrationTests.Agents;

public sealed class DockerIsolatedCodexAgentExecutorTests
{
    [Fact]
    public async Task ManagedDockerSessionExecutesStructuredAgentTurnAndCleansEveryResource()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var repositoryRoot = FindRepositoryRoot();
        var attemptId = $"dog1d-{Guid.NewGuid():N}"[..18];
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "dogfood-1d",
            attemptId);
        var worktree = Path.Combine(artifactRoot, "worktree");
        var hostState = Path.Combine(artifactRoot, "host-state");
        var imageName = $"harness-dogfood-agent:{attemptId}";
        var provider = new DockerSandboxProvider();
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(hostState);

        try
        {
            await provider.BuildImageAsync(
                attemptId,
                imageName,
                Path.Combine(repositoryRoot, "infra", "sandbox", "poc6"),
                timeout.Token);
            await using (var session = await provider.OpenProcessSessionAsync(
                new SandboxProcessRequest(
                    attemptId,
                    artifactRoot,
                    worktree,
                    imageName,
                    imageName,
                    "proxy",
                    "fake-codex",
                    CpuLimit: 0.5m,
                    MemoryBytes: 64 * 1024 * 1024,
                    WritableDiskBytes: 8 * 1024 * 1024,
                    PidsLimit: 64),
                timeout.Token))
            {
                var plan = session.ProcessPlan;
                var proof = new CodexCliExternalSandboxProof(
                    plan.RootFilesystemReadOnly,
                    plan.WorktreeIsolated,
                    plan.EgressRestricted,
                    plan.ResourceLimitsApplied);
                var executor = new CodexCliAgentExecutor(
                    proof,
                    _ => new CodexCliAppServerOptions(
                        plan.HostExecutablePath,
                        artifactRoot,
                        worktree,
                        hostState,
                        TimeSpan.FromMilliseconds(100),
                        plan.ExecutablePrefixArguments,
                        plan.AgentWorkingDirectory));

                var result = await executor.ExecuteAsync(
                    new AgentExecutionRequest(
                        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                        "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                        "01ARZ3NDEKTSV4RRFFQ69G5FAX",
                        "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                        "Complete the isolated fixture.",
                        "{}",
                        worktree),
                    timeout.Token);

                Assert.Equal("codex-cli", result.Executor);
                Assert.Equal("thr_docker", result.SessionId);
                Assert.Equal("turn_docker", result.TurnId);
                Assert.Equal(
                    "Docker-isolated fixture complete.",
                    ChiefTurnOutputContract.Parse(result.StructuredOutput).Response);
                Assert.True(File.Exists(Path.Combine(worktree, "docker-codex-ran.txt")));
                var inventory = await provider.DetectResourcesAsync(attemptId, timeout.Token);
                // Proxy E sandbox vivos durante a sessão inteira: o contêiner do agente agora
                // existe antes da execução — é o que permite à attestation inspecionar a
                // fronteira real em vez de uma intenção.
                Assert.Equal(2, inventory.Containers.Count);
                Assert.Contains(inventory.Containers, name => name.StartsWith(
                    "harness-sandbox-", StringComparison.Ordinal));
                Assert.Equal(2, inventory.Networks.Count);
                Assert.Equal(2, inventory.Volumes.Count);
                Assert.Single(inventory.Images);
            }

            Assert.True((await provider.DetectResourcesAsync(attemptId, timeout.Token)).IsEmpty);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await provider.CleanupAsync(attemptId, cleanupTimeout.Token);
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Repository root not found from {AppContext.BaseDirectory}."));
    }
}
