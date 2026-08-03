using System.Globalization;
using System.Text.Json;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Execution.Infrastructure.Sandbox;

namespace Harness.IntegrationTests.Sandbox;

public sealed class DockerSandboxProviderPocTests
{
    [Fact]
    public async Task ManagedSandboxEnforcesLimitsProxyOnlyEgressAndLabelGuardedCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var repositoryRoot = FindRepositoryRoot();
        var attemptId = $"poc6-{Guid.NewGuid():N}"[..17];
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-6",
            attemptId);
        var worktreePath = Path.Combine(artifactRoot, "worktree");
        var imageName = $"harness-sandbox-poc6:{attemptId}";
        var provider = new DockerSandboxProvider();

        Directory.CreateDirectory(worktreePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(worktreePath, ".git-worktree-marker"),
                "fixture worktree\n",
                timeout.Token);
            await provider.BuildImageAsync(
                attemptId,
                imageName,
                Path.Combine(repositoryRoot, "infra", "sandbox", "poc6"),
                timeout.Token);

            const long memoryBytes = 64 * 1024 * 1024;
            const long diskBytes = 8 * 1024 * 1024;
            const int pidsLimit = 64;
            const decimal cpuLimit = 0.5m;
            var result = await provider.RunAsync(
                new SandboxRunRequest(
                    attemptId,
                    artifactRoot,
                    worktreePath,
                    imageName,
                    cpuLimit,
                    memoryBytes,
                    diskBytes,
                    pidsLimit),
                timeout.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(cpuLimit, result.CpuLimit);
            Assert.Equal(memoryBytes, result.MemoryBytes);
            Assert.Equal(diskBytes, result.WritableDiskBytes);
            Assert.Equal(pidsLimit, result.PidsLimit);
            Assert.True(result.RootFilesystemReadOnly);
            Assert.StartsWith("harness-internal-", result.NetworkMode, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(worktreePath, "sandbox-mounted.txt")));

            using var probe = JsonDocument.Parse(result.StandardOutput);
            Assert.True(probe.RootElement.GetProperty("directBlocked").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("directInternetBlocked").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("proxyAllowed").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("proxyDenied").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("connectTunnelAllowed").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("diskLimited").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("worktreeWritable").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("httpProxyConfigured").GetBoolean());
            Assert.True(probe.RootElement.GetProperty("httpsProxyConfigured").GetBoolean());

            var resources = await provider.DetectResourcesAsync(attemptId, timeout.Token);
            Assert.Equal(3, resources.Containers.Count);
            Assert.Equal(2, resources.Networks.Count);
            Assert.Single(resources.Volumes);
            Assert.Single(resources.Images);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await provider.CleanupAsync(attemptId, cleanupTimeout.Token);
            Assert.True((await provider.DetectResourcesAsync(attemptId, cleanupTimeout.Token)).IsEmpty);
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessSessionContainerIsAttestedVerifiedThroughTheInternalNetworkProof()
    {
        // OPS-023: a attestation de uma sessão de processo resolvia `unverified:none` por dois
        // motivos — o contêiner só nasceria no spawn do agente (depois da autorização) e a prova
        // de egresso só aceitava network='none', enquanto a sessão usa rede --internal + proxy.
        // Este teste trava a nova verdade: sessão aberta → contêiner VIVO → attestation
        // VERIFICADA, com a rede interna provada pelo flag Internal do próprio runtime.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var repositoryRoot = FindRepositoryRoot();
        var attemptId = $"poc6a-{Guid.NewGuid():N}"[..17];
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-6-attestation",
            attemptId);
        var worktreePath = Path.Combine(artifactRoot, "worktree");
        var imageName = $"harness-sandbox-poc6:{attemptId}";
        var provider = new DockerSandboxProvider();

        Directory.CreateDirectory(worktreePath);
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
                    worktreePath,
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
                var attestation = await provider.AttestAsync(
                    new SandboxAttestationRequest(
                        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                        "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                        attemptId,
                        DateTimeOffset.UtcNow),
                    timeout.Token);

                Assert.True(attestation.Verified, attestation.VerificationDetail);
                Assert.True(attestation.IsEffective, attestation.VerificationDetail);
                Assert.Equal("docker", attestation.Provider);
                Assert.Equal($"harness-sandbox-{attemptId}", attestation.SandboxIdentity);
                Assert.StartsWith(
                    "harness-internal-", attestation.NetworkPolicy, StringComparison.Ordinal);
            }

            // Depois da sessão, nada sobra para ser atestado: a fronteira morre com ela.
            var after = await provider.AttestAsync(
                new SandboxAttestationRequest(
                    "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                    attemptId,
                    DateTimeOffset.UtcNow),
                timeout.Token);
            Assert.False(after.Verified);
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
