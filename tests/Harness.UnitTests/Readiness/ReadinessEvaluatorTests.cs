using Harness.Modules.Readiness.Application;
using Harness.Modules.Readiness.Contracts;

namespace Harness.UnitTests.Readiness;

/// <summary>
/// Regras do read model de prontidão (ADR-017). O avaliador é puro: mapeia fatos coletados
/// para estados fechados, bloqueadores tipados e próximas ações, sem IO nem texto livre.
/// </summary>
public sealed class ReadinessEvaluatorTests
{
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FP1";
    private const string AccountId = "01ARZ3NDEKTSV4RRFFQ69G5FP2";
    private const string ModelId = "01ARZ3NDEKTSV4RRFFQ69G5FP3";
    private const string ChiefId = "01ARZ3NDEKTSV4RRFFQ69G5FP4";

    private static ReadinessInputs Empty() => new(
        ProfileReady: false,
        OrganizationReady: false,
        ProjectExists: false,
        ProjectId: null,
        ProviderAccount: DependencyFact.Missing,
        Model: DependencyFact.Missing,
        WorkflowBound: false,
        Chief: ChiefFact.Missing);

    private static ReadinessInputs FullyReal() => new(
        ProfileReady: true,
        OrganizationReady: true,
        ProjectExists: true,
        ProjectId: ProjectId,
        ProviderAccount: new DependencyFact(true, false, AccountId),
        Model: new DependencyFact(true, false, ModelId),
        WorkflowBound: true,
        Chief: new ChiefFact(AgentPresent: true, Healthy: true, ModelResolves: true, Simulated: false, ChiefId));

    private static ReadinessStepContract StepOf(ProjectReadinessSnapshot snapshot, ReadinessStep step) =>
        snapshot.Steps.Single(item => item.Step == step);

    [Fact]
    public void UmProjetoConfiguradoComAEsteiraDesligadaNaoEstaPronto()
    {
        // O pior estado possível para quem começa um projeto e sai do computador: tudo aparece
        // pronto, a conversa responde, o trabalho é organizado — e nada nunca é executado. O
        // silêncio fica indistinguível de trabalho em curso.
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal() with { DispatchEnabled = false });

        var execution = StepOf(snapshot, ReadinessStep.ExecutionReady);
        Assert.NotEqual(ConfigurationState.Ready, execution.State);
        Assert.Contains(execution.Blockers, blocker => blocker.Code == "dispatch.disabled");
        Assert.Equal("dispatch.enable", execution.NextAction?.Code);
    }

    [Fact]
    public void UmProjetoSemPastaDeTrabalhoNaoEstaPronto()
    {
        // Sem repositório toda tentativa falha na largada, uma depois da outra, e a causa só
        // aparece no log de execução — nunca para quem pediu o projeto.
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal() with { RepositoryReachable = false });

        var execution = StepOf(snapshot, ReadinessStep.ExecutionReady);
        Assert.NotEqual(ConfigurationState.Ready, execution.State);
        var blocker = Assert.Single(
            execution.Blockers, candidate => candidate.Code == "repository.unreachable");
        Assert.Equal([ProjectId], blocker.RelatedIds);
        Assert.Equal("project.fixRepository", execution.NextAction?.Code);
    }

    [Fact]
    public void UmProjetoSemRuntimeDeExecucaoNaoEstaPronto()
    {
        // Observado ao vivo: a prontidão respondeu `Ready`, com ZERO bloqueadores, numa
        // instalação onde a imagem do agente não existia — o contêiner não subia, a atestação
        // de sandbox não se verificava e nenhum card rodava. É a pior resposta possível para
        // quem confia nela e sai do computador, porque é exatamente a promessa do produto.
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal() with { ExecutionRuntimeReady = false });

        var execution = StepOf(snapshot, ReadinessStep.ExecutionReady);
        Assert.NotEqual(ConfigurationState.Ready, execution.State);
        Assert.Contains(
            execution.Blockers, blocker => blocker.Code == "execution_runtime.unavailable");
    }

    [Fact]
    public void ComTudoRealEOperacionalEmPeAExecucaoEstaPronta()
    {
        var execution = StepOf(
            ReadinessEvaluator.Evaluate(FullyReal()), ReadinessStep.ExecutionReady);

        Assert.Equal(ConfigurationState.Ready, execution.State);
        Assert.Empty(execution.Blockers);
    }

    [Fact]
    public void EmptyInstallReportsEverythingUnconfiguredWithTypedBlockers()
    {
        var snapshot = ReadinessEvaluator.Evaluate(Empty());

        Assert.Equal(ConfigurationState.Unconfigured, snapshot.OverallState);
        Assert.All(snapshot.Steps, step => Assert.Equal(ConfigurationState.Unconfigured, step.State));
        Assert.All(snapshot.Steps, step => Assert.Equal("unconfigured", step.ExecutionMode));
        Assert.All(snapshot.Steps, step => Assert.NotEmpty(step.Blockers));
        // Nenhum bloqueador carrega texto livre: apenas códigos estáveis.
        Assert.All(snapshot.Steps.SelectMany(step => step.Blockers),
            blocker => Assert.False(string.IsNullOrWhiteSpace(blocker.Code)));
        Assert.Equal(
            "profile.missing",
            StepOf(snapshot, ReadinessStep.ProfileReady).Blockers.Single().Code);
    }

    [Fact]
    public void StepsFollowTheCanonicalGoldenPathOrder()
    {
        var snapshot = ReadinessEvaluator.Evaluate(Empty());

        Assert.Equal(
            new[]
            {
                ReadinessStep.ProfileReady, ReadinessStep.OrganizationReady, ReadinessStep.ProjectReady,
                ReadinessStep.ProviderAccountReady, ReadinessStep.ModelReady, ReadinessStep.WorkflowReady,
                ReadinessStep.ChiefDefinitionReady, ReadinessStep.AgentPoolReady, ReadinessStep.ExecutionReady,
            },
            snapshot.Steps.Select(step => step.Step));
    }

    [Fact]
    public void ProjectRequiresOrganizationAsATypedPrecondition()
    {
        // S6.1: sem organização, criar projeto é uma precondição bloqueada — nunca um
        // formulário que parece preenchível.
        var snapshot = ReadinessEvaluator.Evaluate(Empty() with { ProfileReady = true });

        var project = StepOf(snapshot, ReadinessStep.ProjectReady);
        Assert.Equal(ConfigurationState.Unconfigured, project.State);
        Assert.Equal("organization.required", project.Blockers.Single().Code);
        Assert.Equal("organization.create", project.NextAction!.Code);
        Assert.Equal("/organizations", project.NextAction.Route);
    }

    [Fact]
    public void SimulatedDependenciesAreNeverPresentedAsReal()
    {
        // ADR-018: dado simulado é identificável e nunca vira execução pronta.
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal() with
        {
            ProviderAccount = new DependencyFact(true, true, AccountId),
            Model = new DependencyFact(true, true, ModelId),
            Chief = new ChiefFact(true, true, true, Simulated: true, ChiefId),
        });

        var account = StepOf(snapshot, ReadinessStep.ProviderAccountReady);
        Assert.Equal(ConfigurationState.Simulated, account.State);
        Assert.Equal("simulated", account.ExecutionMode);
        Assert.Equal(ConfigurationState.Simulated, StepOf(snapshot, ReadinessStep.ModelReady).State);
        // A execução não é Ready só porque as dependências simuladas "existem".
        Assert.Equal(ConfigurationState.Simulated, StepOf(snapshot, ReadinessStep.ExecutionReady).State);
        Assert.Equal(ConfigurationState.Simulated, snapshot.OverallState);
    }

    [Fact]
    public void ExecutionIsReadyOnlyWithRealProviderModelWorkflowAndChief()
    {
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal());

        var execution = StepOf(snapshot, ReadinessStep.ExecutionReady);
        Assert.Equal(ConfigurationState.Ready, execution.State);
        Assert.Equal("real", execution.ExecutionMode);
        Assert.Empty(execution.Blockers);
        Assert.Equal("conversation.start", execution.NextAction!.Code);
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("account")]
    [InlineData("model")]
    [InlineData("chief")]
    public void AnyMissingRealDependencyBlocksExecution(string missing)
    {
        var inputs = FullyReal();
        inputs = missing switch
        {
            "workflow" => inputs with { WorkflowBound = false },
            "account" => inputs with { ProviderAccount = DependencyFact.Missing },
            "model" => inputs with { Model = DependencyFact.Missing },
            _ => inputs with { Chief = ChiefFact.Missing },
        };

        var execution = StepOf(ReadinessEvaluator.Evaluate(inputs), ReadinessStep.ExecutionReady);
        Assert.NotEqual(ConfigurationState.Ready, execution.State);
        Assert.NotEmpty(execution.Blockers);
        Assert.NotNull(execution.NextAction);
    }

    [Fact]
    public void UnhealthyChiefDegradesTheAgentPoolWithARecoveryAction()
    {
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal() with
        {
            Chief = new ChiefFact(AgentPresent: true, Healthy: false, ModelResolves: true, Simulated: false, ChiefId),
        });

        var pool = StepOf(snapshot, ReadinessStep.AgentPoolReady);
        Assert.Equal(ConfigurationState.Degraded, pool.State);
        Assert.Equal("agent.degraded", pool.Blockers.Single().Code);
        Assert.Equal("agent.recover", pool.NextAction!.Code);
        Assert.Equal(ConfigurationState.Degraded, snapshot.OverallState);
    }

    [Fact]
    public void ChiefWithoutAResolvableModelIsNotReady()
    {
        var snapshot = ReadinessEvaluator.Evaluate(FullyReal() with
        {
            Chief = new ChiefFact(AgentPresent: true, Healthy: true, ModelResolves: false, Simulated: false, ChiefId),
        });

        var chief = StepOf(snapshot, ReadinessStep.ChiefDefinitionReady);
        Assert.Equal(ConfigurationState.Unconfigured, chief.State);
        Assert.Equal("chief.model_unresolved", chief.Blockers.Single().Code);
        Assert.Equal("chief.configureModel", chief.NextAction!.Code);
    }

    [Fact]
    public void OverallStateIsTheWeakestLinkAcrossSteps()
    {
        // Conta ativa é `Configured`, não `Ready`: o usuário a configurou, mas a credencial só
        // é comprovada por uma invocação real (a saúde nasce `unknown`). Portanto o elo mais
        // fraco de uma instalação totalmente configurada é `Configured`, mesmo com a execução
        // liberada — uma leitura conservadora e honesta, não um "pronto" otimista.
        var real = ReadinessEvaluator.Evaluate(FullyReal());
        Assert.Equal(ConfigurationState.Configured, real.OverallState);
        Assert.Equal(
            ConfigurationState.Configured,
            StepOf(real, ReadinessStep.ProviderAccountReady).State);
        Assert.Equal(ConfigurationState.Ready, StepOf(real, ReadinessStep.ExecutionReady).State);

        Assert.Equal(
            ConfigurationState.Unconfigured,
            ReadinessEvaluator.Evaluate(FullyReal() with { WorkflowBound = false }).OverallState);
    }

    [Fact]
    public void NextActionsAreDeduplicatedByCodeAndOnlyForUnreadySteps()
    {
        var snapshot = ReadinessEvaluator.Evaluate(Empty());

        var codes = snapshot.NextActions.Select(action => action.Code).ToArray();
        Assert.Equal(codes.Distinct(StringComparer.Ordinal).Count(), codes.Length);
        Assert.NotEmpty(codes);
        Assert.DoesNotContain(
            ReadinessEvaluator.Evaluate(FullyReal()).NextActions,
            action => action.Code != "conversation.start");
    }

    [Fact]
    public void MessageCodesAreStableLocalizableCodesPerStepAndState()
    {
        var snapshot = ReadinessEvaluator.Evaluate(Empty());

        Assert.Equal(
            "readiness.providerAccount.unconfigured",
            StepOf(snapshot, ReadinessStep.ProviderAccountReady).MessageCode);
        Assert.Equal(
            "readiness.execution.ready",
            StepOf(ReadinessEvaluator.Evaluate(FullyReal()), ReadinessStep.ExecutionReady).MessageCode);
    }
}
