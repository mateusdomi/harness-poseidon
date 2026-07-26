using Harness.Host.Workflows;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Time;

namespace Harness.Host.Providers;

/// <summary>Contagem do que esta chamada efetivamente semeou/ajustou (0 quando já convergido).</summary>
public sealed record ChiefCliCatalogSeedResult(
    bool AccountCreated,
    bool AccountActivated,
    bool ModelCreated,
    bool ModelEnabled,
    int AgentsWired,
    int WorkflowsBound)
{
    public static ChiefCliCatalogSeedResult None { get; } =
        new(false, false, false, false, 0, 0);

    /// <summary>Verdadeiro quando algo mudou — usado apenas para logar de forma útil.</summary>
    public bool ChangedAnything =>
        AccountCreated || AccountActivated || ModelCreated || ModelEnabled ||
        AgentsWired > 0 || WorkflowsBound > 0;
}

/// <summary>
/// GP-06 (fecho): torna o Chefe EXECUTÁVEL a partir da conta CLI de assinatura já autenticada
/// (<c>chief-claude-primary</c>, papel <c>chief-orchestrator</c>), semeando de forma idempotente
/// o catálogo de providers que o gate de prontidão (<see cref="ReadinessEvaluator"/>) e o
/// roteamento (<see cref="ChiefInvocationRoutingService"/>) exigem.
///
/// O caminho de execução do turno de conversa NÃO usa a chave/token deste catálogo: o
/// <see cref="ConversationChiefAgentExecutor"/> executa pela CLI (login de assinatura, isolado por
/// config home). A conta e o modelo semeados aqui representam esse caminho — a conta guarda apenas
/// a REFERÊNCIA opaca do keychain (nunca a credencial), e o nome do modelo é o alias que o
/// <c>claude --model</c> resolve. Assim o gate reflete a verdade (o Chefe consegue executar) em vez
/// de barrar por falta de uma conta de API que este produto não usa para o Chefe.
///
/// Os ids da conta e do modelo ficam DELIBERADAMENTE fora das faixas simuladas rastreadas por
/// <c>ProjectReadinessService</c>, para que a prontidão os reporte como REAIS (não Simulated).
/// </summary>
public sealed class ChiefCliProviderCatalogSeeder(
    IProviderCatalogStore providers,
    IAgentCatalogStore agents,
    IProjectStore projects,
    IWorkflowCatalogStore workflows,
    WorkflowTemplateSeeder workflowSeeder,
    Harness.Persistence.Abstractions.Workflows.IWorkflowStore workflowRuns,
    IClock clock)
{
    /// <summary>Provider <c>anthropic</c>, auto-semeado pelo catálogo na inicialização do tenant.</summary>
    public const string AnthropicProviderId = "01ARZ3NDEKTSV4RRFFQ69G5FG2";

    /// <summary>
    /// Conta REAL do Chefe (fora da faixa simulada H1/H2). Guarda apenas a referência keychain;
    /// o <c>CreateAccount</c> não faz probe da credencial.
    /// </summary>
    public const string ChiefAccountId = "01ARZ3NDEKTSV4RRFFQ69G5FH3";

    /// <summary>Modelo de chat REAL do Chefe (fora da faixa simulada J1..J4).</summary>
    public const string ChiefModelId = "01ARZ3NDEKTSV4RRFFQ69G5FJ5";

    /// <summary>
    /// Nome passado verbatim para <c>claude --model &lt;nome&gt;</c>. É um alias que a CLI do Claude
    /// Code resolve para o Sonnet atual da assinatura. NÃO é usado como chave de API.
    /// </summary>
    public const string ChiefModelName = "claude-sonnet-4-5";

    /// <summary>Referência opaca ao segredo — nunca a credencial. Igual ao alias da conta CLI.</summary>
    public const string ChiefCredentialReference = "keychain://poseidon/chief-claude-primary";

    private readonly IProviderCatalogStore _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly IAgentCatalogStore _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IWorkflowCatalogStore _workflows =
        workflows ?? throw new ArgumentNullException(nameof(workflows));
    private readonly WorkflowTemplateSeeder _workflowSeeder =
        workflowSeeder ?? throw new ArgumentNullException(nameof(workflowSeeder));
    private readonly Harness.Persistence.Abstractions.Workflows.IWorkflowStore _workflowRuns =
        workflowRuns ?? throw new ArgumentNullException(nameof(workflowRuns));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Converge o catálogo de um tenant para o estado executável. Idempotente: reexecutar não
    /// duplica conta/modelo, não reativa o que já está ativo e não reata o que já está atado.
    /// </summary>
    public async Task<ChiefCliCatalogSeedResult> EnsureSeededAsync(
        string tenantId, string actorProfileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorProfileId);

        var (accountCreated, accountActivated) = await EnsureAccountAsync(tenantId, actorProfileId, cancellationToken);
        var (modelCreated, modelEnabled) = await EnsureModelAsync(tenantId, actorProfileId, cancellationToken);

        var projectPage = await _projects.ListAsync(tenantId, null, 200, cancellationToken);
        var agentsWired = await WireChiefAgentsAsync(tenantId, actorProfileId, projectPage, cancellationToken);
        var workflowsBound = await BindWorkflowsAsync(tenantId, actorProfileId, projectPage, cancellationToken);

        return new ChiefCliCatalogSeedResult(
            accountCreated, accountActivated, modelCreated, modelEnabled, agentsWired, workflowsBound);
    }

    private async Task<(bool Created, bool Activated)> EnsureAccountAsync(
        string tenantId, string actorProfileId, CancellationToken cancellationToken)
    {
        var created = false;
        var account = await _providers.GetAccountAsync(tenantId, ChiefAccountId, cancellationToken);
        if (account is null)
        {
            // O CreateAccount insere sempre 'disabled' e não faz probe da credencial: só armazena o
            // ref opaco. `plan=pro`/`authentication=oauth` descrevem a assinatura Claude Code (login
            // OAuth, não chave de API). `quotaWindow=none` sem limite: a assinatura não tem cota USD
            // controlada por este produto, então o roteamento não barra por custo estimado.
            await _providers.CreateAccountAsync(
                new ProviderAccountCreateCommand(
                    tenantId,
                    actorProfileId,
                    ChiefAccountId,
                    AnthropicProviderId,
                    "Chefe — Claude Code (assinatura CLI)",
                    ChiefCredentialReference,
                    QuotaLimitUsd: null,
                    _clock.UtcNow,
                    Identity: null,
                    Plan: "pro",
                    Authentication: "oauth",
                    QuotaWindow: "none",
                    QuotaResetsAt: null,
                    Capabilities: ["chat"]),
                cancellationToken);
            created = true;
            account = await _providers.GetAccountAsync(tenantId, ChiefAccountId, cancellationToken);
        }

        var activated = false;
        if (account is not null && !string.Equals(account.State, "active", StringComparison.Ordinal))
        {
            await _providers.UpdateAsync(
                new ProviderCatalogUpdateCommand(
                    tenantId, actorProfileId, "accounts", ChiefAccountId,
                    "{\"state\":\"active\"}", _clock.UtcNow),
                cancellationToken);
            activated = true;
        }

        return (created, activated);
    }

    private async Task<(bool Created, bool Enabled)> EnsureModelAsync(
        string tenantId, string actorProfileId, CancellationToken cancellationToken)
    {
        var created = false;
        var model = await _providers.GetModelAsync(tenantId, ChiefModelId, cancellationToken);
        if (model is null)
        {
            // O CreateModel insere sempre disabled; habilitamos por patch em seguida. Os quatro
            // esforços mapeados cobrem a seleção do roteamento; os custos são plausíveis para o
            // Sonnet, mas não travam o turno (a conta CLI não tem cota USD).
            await _providers.CreateModelAsync(
                new ProviderModelCreateCommand(
                    tenantId,
                    actorProfileId,
                    ChiefModelId,
                    AnthropicProviderId,
                    ChiefModelName,
                    "Claude Sonnet 4.5 (CLI)",
                    Capabilities: ["chat", "code"],
                    ContextWindow: 200_000,
                    CostPer1kInputUsd: 0.003m,
                    CostPer1kOutputUsd: 0.015m,
                    EffortMappings:
                    [
                        new EffortMappingRecord("low", "low"),
                        new EffortMappingRecord("medium", "medium"),
                        new EffortMappingRecord("high", "high"),
                        new EffortMappingRecord("max", "high"),
                    ],
                    _clock.UtcNow),
                cancellationToken);
            created = true;
            model = await _providers.GetModelAsync(tenantId, ChiefModelId, cancellationToken);
        }

        var enabled = false;
        if (model is not null && !model.Enabled)
        {
            await _providers.UpdateAsync(
                new ProviderCatalogUpdateCommand(
                    tenantId, actorProfileId, "models", ChiefModelId,
                    "{\"enabled\":true}", _clock.UtcNow),
                cancellationToken);
            enabled = true;
        }

        return (created, enabled);
    }

    private async Task<int> WireChiefAgentsAsync(
        string tenantId,
        string actorProfileId,
        IReadOnlyList<ProjectRecord> projectPage,
        CancellationToken cancellationToken)
    {
        var wired = 0;
        foreach (var project in projectPage)
        {
            var agent = await _agents.GetAgentAsync(tenantId, project.ChiefAgentId, cancellationToken);
            if (agent is null || string.Equals(agent.ModelId, ChiefModelId, StringComparison.Ordinal))
            {
                continue;
            }

            // A seleção só troca com o agente parado (idle/waiting); um chefe ocupado é deixado como
            // está e converge no próximo restart.
            if (agent.State is not ("idle" or "waiting"))
            {
                continue;
            }

            try
            {
                await _agents.UpdateSelectionAsync(
                    new AgentSelectionCommand(
                        tenantId,
                        project.ChiefAgentId,
                        actorProfileId,
                        ChiefAccountId,
                        ChiefModelId,
                        "medium",
                        [],
                        "Chefe apontado para a assinatura Claude Code CLI autenticada (GP-06).",
                        _clock.UtcNow),
                    cancellationToken);
                wired++;
            }
            catch (AgentSelectionConflictException)
            {
                // Estado mudou entre a leitura e a escrita: benigno, converge depois.
            }
            catch (AgentSelectionValidationException)
            {
            }
            catch (AgentSelectionNotFoundException)
            {
            }
        }

        return wired;
    }

    // RN-02: a garantia de "todo projeto tem workflow" vive no ProjectWorkflowConvergenceSeeder.
    // Aqui apenas delegamos, reusando a página já lida, para que o fecho GP-06 continue reportando
    // quantos workflows vinculou sem duplicar a regra de vínculo.
    private Task<int> BindWorkflowsAsync(
        string tenantId,
        string actorProfileId,
        IReadOnlyList<ProjectRecord> projectPage,
        CancellationToken cancellationToken) =>
        new ProjectWorkflowConvergenceSeeder(_projects, _workflows, _workflowSeeder, _workflowRuns, _clock)
            .EnsureBoundAsync(tenantId, actorProfileId, projectPage, cancellationToken);
}
