using Harness.Host.Execution;
using Harness.IntegrationTests.Persistence;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Tools.Domain;
using Harness.Persistence.Abstractions.Execution;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Security;

/// <summary>
/// Fase 0B1 — regressão permanente de BR-002/BR-013.
///
/// O control plane afirmava <c>SandboxActive: true</c> por LITERAL, inclusive com
/// `IsolatedExecution.Mode=Disabled` — ou seja, com sandbox nenhuma. A política de ferramentas lia
/// esse literal e autorizava execução de risco crítico acreditando existir uma fronteira que não
/// existia. Uma afirmação sem emissor não é evidência.
/// </summary>
public sealed class SandboxAttestationTests
{
    private const string Tenant = FoundationTransactionBehavior.TenantId;
    private const string Project = FoundationTransactionBehavior.ProjectId;
    private const string Attempt = "01ARZ3NDEKTSV4RRFFQ69G5FG1";

    [Fact]
    public async Task WithIsolationDisabledTheAbsenceOfSandboxIsRecordedAndCriticalToolsAreDenied()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var service = fixture.Service(IsolatedExecutionMode.Disabled, provider: null);

        var attestation = await service.AttestAsync(Tenant, Project, Attempt, cancellationToken: timeout.Token);

        // A ausência vira FATO gravado — não um silêncio que cada leitor interpreta como quiser.
        Assert.False(attestation.Verified);
        Assert.Equal(SandboxAttestation.NoneProvider, attestation.Provider);
        Assert.Contains("disabled", attestation.VerificationDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(await service.IsSandboxActiveAsync(Tenant, Attempt, timeout.Token));

        // E a política NEGA a ferramenta crítica. Antes ela era permitida pelo literal.
        Assert.False(Evaluate(sandboxActive: false).Allowed);
        Assert.Equal("sandbox_required", Evaluate(false).Code);
    }

    [Fact]
    public async Task AVerifiedAttestationActivatesTheSandboxOnlyForItsOwnAttempt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var service = fixture.Service(IsolatedExecutionMode.Fake, new FakeSandboxProvider());

        var attestation = await service.AttestAsync(Tenant, Project, Attempt, cancellationToken: timeout.Token);
        Assert.True(attestation.Verified);
        Assert.Equal("fake", attestation.Provider);
        Assert.True(await service.IsSandboxActiveAsync(Tenant, Attempt, timeout.Token));
        Assert.True(Evaluate(sandboxActive: true).Allowed);

        // A attestation é POR TENTATIVA: se valesse para outra, bastaria uma execução isolada no
        // passado para liberar todas as seguintes.
        Assert.False(await service.IsSandboxActiveAsync(
            Tenant, "01ARZ3NDEKTSV4RRFFQ69G5FG2", timeout.Token));
    }

    [Fact]
    public async Task AProviderThatCannotProveContainmentDoesNotActivateTheSandbox()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);

        // Container criado, mas SEM as fronteiras: a mera existência de um container não é prova.
        var service = fixture.Service(
            IsolatedExecutionMode.Docker,
            new HalfContainedProvider());

        var attestation = await service.AttestAsync(Tenant, Project, Attempt, cancellationToken: timeout.Token);
        Assert.False(attestation.Verified);
        Assert.False(await service.IsSandboxActiveAsync(Tenant, Attempt, timeout.Token));
        Assert.False(Evaluate(sandboxActive: false).Allowed);
    }

    [Fact]
    public async Task TheFirstAttestationWinsSoAnExecutionCannotBeRetroactivelyBlessed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var disabled = fixture.Service(IsolatedExecutionMode.Disabled, provider: null);
        await disabled.AttestAsync(Tenant, Project, Attempt, cancellationToken: timeout.Token);

        // Uma segunda emissão "melhor" não pode reescrever a avaliação da execução em curso.
        var later = fixture.Service(IsolatedExecutionMode.Fake, new FakeSandboxProvider());
        var second = await later.AttestAsync(Tenant, Project, Attempt, cancellationToken: timeout.Token);

        Assert.False(second.Verified);
        Assert.Equal(SandboxAttestation.NoneProvider, second.Provider);
        Assert.False(await later.IsSandboxActiveAsync(Tenant, Attempt, timeout.Token));
    }

    [Fact]
    public async Task ThereIsNoWayToExecuteWithoutAnAttestedSandbox()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var service = fixture.Service(IsolatedExecutionMode.Disabled, provider: null);
        await service.AttestAsync(Tenant, Project, Attempt, cancellationToken: timeout.Token);

        // Decisão do proprietário (31/07/2026): o contêiner é pré-requisito nos DOIS modos. Não
        // existe mais aceite de risco que dispense a sandbox — uma exceção "temporária" que o
        // produto aceita vira permanente na prática, e era o último caminho que deixava um agente
        // produzir efeito no host sem fronteira nenhuma.
        Assert.False(await service.IsSandboxActiveAsync(Tenant, Attempt, timeout.Token));
        var decision = Evaluate(sandboxActive: false);
        Assert.False(decision.Allowed);
        Assert.Equal("sandbox_required", decision.Code);

        // A única maneira de a política permitir é uma sandbox REALMENTE atestada.
        Assert.True(Evaluate(sandboxActive: true).Allowed);
    }

    private static ToolPolicyDecision Evaluate(bool sandboxActive) =>
        ToolExecutionPolicy.Evaluate(new ToolInvocationPolicyRequest(
            new ToolPolicyDescriptor("tool-critical", true, ToolRiskTier.Critical, "{}", "{}"),
            new ToolPolicyContext(
                "execution",
                ToolRiskTier.Critical,
                new HashSet<string>(["tool-critical"], StringComparer.Ordinal),
                sandboxActive),
            ToolRiskTier.Critical));

    /// <summary>Provider que cria container mas NÃO entrega as fronteiras. É o caso realista.</summary>
    private sealed class HalfContainedProvider : ISandboxProvider
    {
        public Task<ISandboxProcessSession> OpenProcessSessionAsync(
            SandboxProcessRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SandboxRunResult> RunAsync(
            SandboxRunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SandboxResourceInventory> DetectResourcesAsync(
            string attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxResourceInventory([], [], [], []));

        public Task CleanupAsync(string attemptId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SandboxAttestation> AttestAsync(
            SandboxAttestationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(new SandboxAttestation(
                request.TenantId, request.ProjectId, request.AttemptId, "docker", "27.0",
                $"container-{request.AttemptId}", ["/workspace"], "bridge",
                RootFilesystemReadOnly: true,
                WorktreeIsolated: true,
                EgressRestricted: false,
                ResourceLimitsApplied: true,
                Verified: false,
                "The container is on a bridge network: egress is not restricted.",
                request.IssuedAt));
        }
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteDispatcher _dispatcher;

        private Fixture(string root, SqliteWriteDispatcher dispatcher)
        {
            _root = root;
            _dispatcher = dispatcher;
            Store = new SqliteSandboxAttestationStore(dispatcher);
        }

        public DateTimeOffset Now { get; } = new(2026, 7, 31, 14, 0, 0, TimeSpan.Zero);

        public SqliteSandboxAttestationStore Store { get; }

        public static async Task<Fixture> StartAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory, "integration-artifacts", $"sandbox0b1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "sandbox.db"), cancellationToken);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), cancellationToken);
            return new Fixture(root, dispatcher);
        }

        public SandboxAttestationService Service(
            IsolatedExecutionMode mode, ISandboxProvider? provider) =>
            new(
                Store,
                new StubClock(Now),
                new IsolatedExecutionSettings { Mode = mode },
                NullLogger<SandboxAttestationService>.Instance,
                provider);

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
