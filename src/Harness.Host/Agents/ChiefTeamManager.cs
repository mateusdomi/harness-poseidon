using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Persistence.Abstractions.Agents;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

/// <summary>O que aconteceu com uma intenção de equipe — para log, teste e resposta ao dono.</summary>
public sealed record ChiefTeamActionResult(
    string Action, string ReasonCode, string? PersonaKey, bool Created);

/// <summary>
/// O serviço por onde a chefe ADMINISTRA A PRÓPRIA EQUIPE.
///
/// Decisão de produto: o dono do Poseidon é o stakeholder que delega um projeto, não o gerente que
/// escolhe agentes nem o RH da fábrica. Quando chega uma demanda que nenhuma persona do catálogo
/// cobre, esperar aprovação humana para criar o especialista transforma o dono em gargalo de uma
/// fábrica que existe para não depender dele em decisão operacional. Criar persona é gestão do
/// Control Plane, e passa a ser dela.
///
/// O que NÃO muda: a chefe continua sem editar código, sem terminal, sem Git, sem escrever no
/// banco e sem conceder a si mesma capability nenhuma. Por isso a criação passa por AQUI e não
/// pelo CRUD administrativo — este serviço valida contra a policy, deduplica contra o catálogo,
/// carimba a procedência no store e deixa o rastro no ledger. O texto do modelo propõe; o sistema
/// decide.
/// </summary>
public sealed partial class ChiefTeamManager(
    IAgentCatalogStore catalog,
    IClock clock,
    ILogger<ChiefTeamManager> logger)
{
    private readonly IAgentCatalogStore _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<IReadOnlyList<ChiefTeamActionResult>> ApplyAsync(
        string tenantId,
        string projectId,
        string actorProfileId,
        IReadOnlyList<ChiefTeamAction> actions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return [];
        }

        var existing = await _catalog.ListDefinitionsForTenantAsync(
            tenantId, null, 200, false, cancellationToken);
        var results = new List<ChiefTeamActionResult>(actions.Count);
        foreach (var action in actions)
        {
            results.Add(action.Action switch
            {
                "create_persona" => await CreateAsync(
                    tenantId, projectId, actorProfileId, action, existing, cancellationToken),
                "observe_persona" => await LifecycleAsync(
                    tenantId, actorProfileId, action, "observation", existing, cancellationToken),
                "suspend_persona" => await LifecycleAsync(
                    tenantId, actorProfileId, action, "quarantined", existing, cancellationToken),
                "reactivate_persona" => await LifecycleAsync(
                    tenantId, actorProfileId, action, "active", existing, cancellationToken),
                "promote_persona" => await LifecycleAsync(
                    tenantId, actorProfileId, action, "reusable", existing, cancellationToken),
                _ => new ChiefTeamActionResult(action.Action, "team.unknown_action", null, false),
            });
        }

        return results;
    }

    private async Task<ChiefTeamActionResult> CreateAsync(
        string tenantId,
        string projectId,
        string actorProfileId,
        ChiefTeamAction action,
        IReadOnlyList<AgentDefinitionRecord> existing,
        CancellationToken cancellationToken)
    {
        var verdict = TeamActionPolicy.Evaluate(ToProposal(action.Persona));
        if (!verdict.Allowed || verdict.Persona is null)
        {
            LogTeamActionRefused(logger, action.Action, verdict.ReasonCode);
            return new ChiefTeamActionResult(action.Action, verdict.ReasonCode, null, false);
        }

        var persona = verdict.Persona;
        if (verdict.RemovedCapabilities.Count > 0)
        {
            // Reduzir em vez de recusar: negar a persona inteira por uma capability a mais
            // devolveria o trabalho ao generalista, que é o resultado pior. O que foi cortado
            // fica no log e no motivo registrado.
            LogCapabilitiesRemoved(
                logger, persona.Key, string.Join(", ", verdict.RemovedCapabilities));
        }

        // DEDUPLICAÇÃO antes de criar. Sem isto, cada demanda pareceria exigir um especialista
        // novo e o catálogo viraria uma lista de quase-duplicatas — o oposto de uma equipe.
        var covered = existing.FirstOrDefault(definition =>
            definition.Enabled &&
            definition.ArchivedAt is null &&
            TeamActionPolicy.IsCoveredBy(
                persona, definition.Key, definition.Specialty, definition.Description));
        if (covered is not null)
        {
            LogPersonaReused(logger, persona.Key, covered.Key);
            return new ChiefTeamActionResult(action.Action, "team.reused_existing", covered.Key, false);
        }

        var now = _clock.UtcNow;
        var content = new AgentDefinitionContent(
            persona.Key,
            persona.Name,
            // Persona criada pela chefe é sempre ESPECIALISTA: uma segunda chefe fabricada por
            // texto do modelo seria autoridade nascendo fora do Control Plane.
            "specialist",
            string.IsNullOrWhiteSpace(persona.Specialty) ? null : persona.Specialty,
            persona.Purpose,
            DefaultModelId: null,
            SkillIds: [],
            // Ferramenta que não existe no catálogo não pode ser concedida. Nascer sem tool é o
            // menor privilégio possível; ampliar é ato explícito do dono.
            ToolIds: [],
            Persona: persona.Purpose,
            Mission: persona.Purpose,
            OperatingPrinciples: persona.Responsibilities,
            Deliverables: [],
            QualityCriteria: [],
            CommunicationStyle: null,
            Limitations: persona.Constraints,
            Stacks: null,
            DefaultEffort: null,
            PreferredAccountId: null,
            FallbackModelIds: null,
            Team: null,
            ActorCritic: "actor",
            Risk: persona.RiskTiers.Contains("critical") || persona.RiskTiers.Contains("high")
                ? "high"
                : persona.RiskTiers.Contains("medium") ? "medium" : "low");

        try
        {
            var (definition, created) = await _catalog.CreateChiefDefinitionAsync(
                new ChiefDefinitionCreateCommand(
                    tenantId, actorProfileId, UlidValue.New(now).ToString(), content,
                    projectId, action.Reason, now),
                cancellationToken);
            if (created)
            {
                LogPersonaCreated(logger, definition.Key, projectId);
            }

            return new ChiefTeamActionResult(
                action.Action,
                created ? verdict.ReasonCode : "team.already_exists",
                definition.Key,
                created);
        }
        catch (AgentDefinitionAdminException exception)
        {
            // Referência inválida (modelo, tool, especialidade inexistente) é recusa legítima do
            // catálogo, não falha do turno: a chefe segue com quem já existe.
            LogTeamActionRefused(logger, action.Action, exception.GetType().Name);
            return new ChiefTeamActionResult(action.Action, "team.catalog_refused", persona.Key, false);
        }
    }

    private async Task<ChiefTeamActionResult> LifecycleAsync(
        string tenantId,
        string actorProfileId,
        ChiefTeamAction action,
        string lifecycleState,
        IReadOnlyList<AgentDefinitionRecord> existing,
        CancellationToken cancellationToken)
    {
        var key = action.PersonaKey?.Trim();
        var target = existing.FirstOrDefault(definition =>
            string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return new ChiefTeamActionResult(action.Action, "team.persona_not_found", key, false);
        }

        // A chefe reorganiza a equipe, não a hierarquia: rebaixar ou promover o próprio chefe
        // seria mexer na autoridade, e isso continua sendo do dono.
        if (string.Equals(target.Role, "chief", StringComparison.OrdinalIgnoreCase))
        {
            return new ChiefTeamActionResult(action.Action, "team.chief_is_not_managed", key, false);
        }

        try
        {
            _ = await _catalog.SetDefinitionLifecycleStateAsync(
                new AgentDefinitionLifecycleStateCommand(
                    tenantId, actorProfileId, target.Id, lifecycleState, action.Reason, _clock.UtcNow),
                cancellationToken);
            LogLifecycleChanged(logger, target.Key, lifecycleState);
            return new ChiefTeamActionResult(action.Action, $"team.{lifecycleState}", target.Key, false);
        }
        catch (AgentDefinitionAdminException exception)
        {
            LogTeamActionRefused(logger, action.Action, exception.GetType().Name);
            return new ChiefTeamActionResult(action.Action, "team.catalog_refused", target.Key, false);
        }
    }

    private static ProposedPersona? ToProposal(ChiefProposedPersona? persona) =>
        persona is null
            ? null
            : new ProposedPersona(
                persona.Key, persona.Name, persona.Purpose, persona.Specialty,
                persona.Responsibilities, persona.Constraints,
                persona.RequiredCapabilities, persona.RiskTiers);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: persona '{PersonaKey}' criada para o projeto {ProjectId}.")]
    private static partial void LogPersonaCreated(ILogger logger, string personaKey, string projectId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: persona '{Proposed}' NÃO criada — '{Existing}' já cobre a lacuna.")]
    private static partial void LogPersonaReused(ILogger logger, string proposed, string existing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: capabilities removidas da persona '{PersonaKey}' pela policy: {Removed}.")]
    private static partial void LogCapabilitiesRemoved(ILogger logger, string personaKey, string removed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: persona '{PersonaKey}' movida para '{LifecycleState}'.")]
    private static partial void LogLifecycleChanged(ILogger logger, string personaKey, string lifecycleState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: ação de equipe '{Action}' recusada: {ReasonCode}.")]
    private static partial void LogTeamActionRefused(ILogger logger, string action, string reasonCode);
}
