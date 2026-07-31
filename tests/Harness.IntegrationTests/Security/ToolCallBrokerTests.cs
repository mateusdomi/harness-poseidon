using Harness.Host.Agents;
using Harness.IntegrationTests.Persistence;
using Harness.Modules.Tools.Application;
using Harness.Modules.Tools.Domain;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Security;

/// <summary>
/// Fase 0B2 — regressão permanente de BR-002/BR-013 na camada de ferramentas.
///
/// O PEP autorizava o BINÁRIO executor UMA vez, no início da tentativa; nenhuma chamada posterior
/// voltava a passar por política. Autorizar o processo e não os efeitos é autorizar a intenção e
/// não o ato. O broker fecha isso para todo efeito que o control plane controla.
/// </summary>
public sealed class ToolCallBrokerTests
{
    private const string Tenant = FoundationTransactionBehavior.TenantId;
    private const string Project = FoundationTransactionBehavior.ProjectId;
    private const string Card = "01ARZ3NDEKTSV4RRFFQ69G5FH1";
    private const string Attempt = "01ARZ3NDEKTSV4RRFFQ69G5FH2";
    private const string Agent = "01ARZ3NDEKTSV4RRFFQ69G5FH3";

    [Fact]
    public async Task TheChiefNeverExecutesAToolAndTheCriticNeverWrites()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);

        // A Bruna delega. Se ela executasse, a fronteira entre quem decide e quem faz sumiria.
        var chief = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Chief), timeout.Token);
        Assert.False(chief.Allowed);
        Assert.Equal("chief_cannot_execute_tools", chief.Code);

        var criticWrite = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Critic) with { Mutating = true }, timeout.Token);
        Assert.False(criticWrite.Allowed);
        Assert.Equal("critic_is_read_only", criticWrite.Code);

        // O crítico LÊ normalmente: read-only não é "não pode nada".
        var criticRead = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Critic), timeout.Token);
        Assert.True(criticRead.Allowed);
    }

    [Fact]
    public async Task ACallWithoutCapabilityOrWithAnotherAttemptIsDenied()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);

        // Capability emitida para OUTRA tentativa não vale aqui.
        var foreign = fixture.IssueCapability(attemptId: "01ARZ3NDEKTSV4RRFFQ69G5FH9");
        var denied = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with { Capability = foreign }, timeout.Token);
        Assert.False(denied.Allowed);

        // Fencing antigo também é recusado: quem perdeu a vez não age.
        var stale = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with { FencingToken = 1 }, timeout.Token);
        Assert.False(stale.Allowed);

        // Ferramenta fora da allowlist da capability é recusada.
        var otherTool = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with { ToolId = "tool-nao-concedida" },
            timeout.Token);
        Assert.False(otherTool.Allowed);
    }

    [Fact]
    public async Task EveryPathIsCheckedNotOnlyTheFirst()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);

        // Autorizar pelo primeiro caminho e executar sobre todos seria uma porta aberta com
        // aparência de política. O segundo caminho está fora do ScopeClaim.
        var result = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with
            {
                Paths = ["src/app.ts", "../../etc/passwd"],
            },
            timeout.Token);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task NetworkRequiresAnExplicitAllowlistAndSecretsNeverTravelAsArguments()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);

        var openNetwork = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with
            {
                Network = new ToolNetworkPolicy(true, []),
            },
            timeout.Token);
        Assert.False(openNetwork.Allowed);
        Assert.Equal("network_allowlist_required", openNetwork.Code);

        var withSecret = await fixture.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with
            {
                Arguments = ["--token", "AKI" + "A" + "1234567890ABCDEF"],
            },
            timeout.Token);
        Assert.False(withSecret.Allowed);
        Assert.Equal("tool_call_argument_sensitive", withSecret.Code);
    }

    [Fact]
    public async Task OutputIsTruncatedRedactedAndTheRepeatedCallDoesNotRepeatTheEffect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var effects = 0;
        var canary = "AKI" + "A" + "1234567890ABCDEF";

        Task<ToolCallEffectResult> Effect(ToolCallRequest _, CancellationToken __)
        {
            effects++;
            return Task.FromResult(new ToolCallEffectResult(
                0, new string('x', 5_000) + $" a chave é {canary}"));
        }

        var request = fixture.Request(ToolCallProfile.Actor) with { MaximumOutputBytes = 1_024 };
        var first = await fixture.Broker.InvokeAsync(request, Effect, timeout.Token);
        Assert.True(first.Allowed);
        Assert.True(first.OutputTruncated);
        Assert.True(first.Output.Length <= 1_100);
        Assert.DoesNotContain(canary, first.Output, StringComparison.Ordinal);

        // Repetir a MESMA chave devolve o resultado anterior sem repetir o efeito. É o que separa
        // "tentar de novo" de "fazer duas vezes".
        var second = await fixture.Broker.InvokeAsync(request, Effect, timeout.Token);
        Assert.True(second.Replayed);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task ACallThatOverrunsItsTimeoutIsCutAndRecorded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);

        var result = await fixture.Broker.InvokeAsync(
            fixture.Request(ToolCallProfile.Actor) with { Timeout = TimeSpan.FromMilliseconds(50) },
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return new ToolCallEffectResult(0, "nunca chega aqui");
            },
            timeout.Token);

        Assert.False(result.Allowed);
        Assert.Equal("tool_call_timeout", result.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteDispatcher _dispatcher;

        private Fixture(string root, SqliteWriteDispatcher dispatcher, ToolCallBroker broker,
            SecurityPolicyEnforcementPoint pep)
        {
            _root = root;
            _dispatcher = dispatcher;
            Broker = broker;
            Pep = pep;
            Capability = IssueCapability(Attempt);
        }

        public ToolCallBroker Broker { get; }

        public SecurityPolicyEnforcementPoint Pep { get; }

        public CapabilityToken Capability { get; }

        public static async Task<Fixture> StartAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory, "integration-artifacts", $"broker0b2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "broker.db"), cancellationToken);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken);
            var pep = new SecurityPolicyEnforcementPoint(new NullAuditSink(), TimeProvider.System);
            var broker = new ToolCallBroker(
                pep,
                new ToolCallJournalAdapter(new SqliteToolCallJournalStore(dispatcher)),
                TimeProvider.System);
            return new Fixture(root, dispatcher, broker, pep);
        }

        public CapabilityToken IssueCapability(
            string attemptId, CapabilityActorKind actor = CapabilityActorKind.Worker) =>
            Pep.Issue(new CapabilityGrantRequest(
                actor,
                Agent,
                Tenant,
                Project,
                Card,
                attemptId,
                CapabilityOperation.ToolExecution,
                ["tool-edit"],
                ["tool-edit"],
                ["src/**"],
                DateTimeOffset.UtcNow.AddMinutes(30),
                7));

        /// <summary>
        /// Cada PAPEL carrega a própria capability: o crítico não age com a credencial do ator.
        /// Reaproveitar uma só entre perfis testaria menos do que o produto exige.
        /// </summary>
        public ToolCallRequest Request(ToolCallProfile profile) => new(
            Tenant, Project, Card, Attempt, Agent, profile, "tool-edit",
            profile == ToolCallProfile.Critic
                ? IssueCapability(Attempt, CapabilityActorKind.Specialist)
                : Capability,
            7,
            ["--check"], ["src/app.ts"], ToolNetworkPolicy.Denied, TimeSpan.FromSeconds(30),
            64 * 1024, ToolCallBroker.ComposeIdempotencyKey(Attempt, "tool-edit", [profile.ToString()]),
            Mutating: false, ToolRiskTier.Medium);

        public Task<ToolCallResult> InvokeAsync(
            ToolCallRequest request, CancellationToken cancellationToken) =>
            Broker.InvokeAsync(
                request,
                (_, _) => Task.FromResult(new ToolCallEffectResult(0, "ok")),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed class NullAuditSink : ICapabilityDecisionAuditSink
        {
            public ValueTask RecordAsync(
                CapabilityDecisionAuditRecord record, CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;
        }
    }
}
