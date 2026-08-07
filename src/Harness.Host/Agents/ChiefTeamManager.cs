using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Tools;
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
    ITeamSpecialtyCatalogStore specialties,
    IToolCatalogStore tools,
    IClock clock,
    ILogger<ChiefTeamManager> logger,
    AgentAccountRegistry? accounts = null)
{
    private readonly IAgentCatalogStore _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ITeamSpecialtyCatalogStore _specialties = specialties ?? throw new ArgumentNullException(nameof(specialties));
    private readonly IToolCatalogStore _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    // Opcional: só existe quando AgentRuns está habilitado (é onde as contas são carregadas). Sem
    // ele a seleção de rota cai para o comportamento anterior — qualquer executável serve —, em
    // vez de derrubar a criação de persona onde AgentRuns nunca foi ligado.
    private readonly AgentAccountRegistry? _accounts = accounts;
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

        var projectAgents = await _catalog.ListAgentsAsync(
            tenantId, projectId, null, 500, cancellationToken);
        var route = SelectRoute(projectAgents, persona);
        if (route is null)
        {
            LogTeamActionRefused(logger, action.Action, "team.no_executable_route");
            return new ChiefTeamActionResult(
                action.Action, "team.no_executable_route", persona.Key, false);
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
            var ensured = await EnsureProjectAgentAsync(
                tenantId, projectId, actorProfileId, covered, route, action.Reason,
                cancellationToken);
            LogPersonaReused(logger, persona.Key, covered.Key);
            return new ChiefTeamActionResult(
                action.Action,
                ensured.Created ? "team.reused_existing_added_to_project" : "team.reused_existing",
                covered.Key,
                ensured.Created);
        }

        var now = _clock.UtcNow;
        var specialty = await EnsureSpecialtyAsync(
            tenantId, actorProfileId, persona, now, cancellationToken);
        var enabledTools = await _tools.ListToolsAsync(null, 200, cancellationToken);
        var enabledSkills = await _tools.ListSkillsAsync(null, 200, cancellationToken);
        var toolIds = SelectTools(persona, enabledTools);
        var skillIds = SelectSkills(persona, enabledSkills);
        if (toolIds.Length == 0)
        {
            LogTeamActionRefused(logger, action.Action, "team.no_safe_tool");
            return new ChiefTeamActionResult(action.Action, "team.no_safe_tool", persona.Key, false);
        }

        var (allowedScopes, deniedScopes) = SelectScopes(persona);

        var content = new AgentDefinitionContent(
            persona.Key,
            persona.Name,
            // Persona criada pela chefe é sempre ESPECIALISTA: uma segunda chefe fabricada por
            // texto do modelo seria autoridade nascendo fora do Control Plane.
            "specialist",
            specialty.Name,
            persona.Purpose,
            DefaultModelId: route.ModelId,
            SkillIds: skillIds,
            ToolIds: toolIds,
            Persona: persona.Purpose,
            Mission: persona.Purpose,
            OperatingPrinciples: persona.Responsibilities,
            Deliverables: persona.Responsibilities,
            QualityCriteria: persona.Constraints,
            CommunicationStyle: "Comunique progresso, decisões, riscos e evidências de forma objetiva para a Diretora de Engenharia.",
            Limitations: persona.Constraints,
            Stacks: persona.RequiredCapabilities,
            DefaultEffort: route.Effort,
            PreferredAccountId: route.AccountId,
            FallbackModelIds: route.FallbackModelIds ?? [],
            Team: null,
            ActorCritic: "actor",
            Risk: persona.RiskTiers.Contains("critical") || persona.RiskTiers.Contains("high")
                ? "high"
                : persona.RiskTiers.Contains("medium") ? "medium" : "low",
            AllowedScopes: allowedScopes,
            DeniedScopes: deniedScopes,
            ActivationCriteria: [persona.Purpose, .. persona.Responsibilities],
            NonActivationCriteria: persona.Constraints);

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

            var ensured = await EnsureProjectAgentAsync(
                tenantId, projectId, actorProfileId, definition, route, action.Reason,
                cancellationToken);

            return new ChiefTeamActionResult(
                action.Action,
                created || ensured.Created ? verdict.ReasonCode : "team.already_exists",
                definition.Key,
                created || ensured.Created);
        }
        catch (Exception exception) when (exception is AgentDefinitionAdminException or
            AgentSelectionNotFoundException or AgentSelectionValidationException or
            AgentSelectionConflictException or TeamSpecialtyCatalogValidationException or
            TeamSpecialtyCatalogConflictException)
        {
            // Referência inválida (modelo, tool, especialidade inexistente) é recusa legítima do
            // catálogo, não falha do turno: a chefe segue com quem já existe.
            LogTeamActionRefused(logger, action.Action, exception.GetType().Name);
            return new ChiefTeamActionResult(action.Action, "team.catalog_refused", persona.Key, false);
        }
    }

    private async Task<SpecialtyRecord> EnsureSpecialtyAsync(
        string tenantId,
        string actorProfileId,
        ProposedPersona persona,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = await _specialties.ListSpecialtiesAsync(
            tenantId, null, null, 500, cancellationToken);
        var existing = rows.FirstOrDefault(value =>
            string.Equals(value.Key, persona.Key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Name, persona.Specialty, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            var created = await _specialties.CreateSpecialtyAsync(
                new SpecialtyCreateCommand(
                    tenantId, actorProfileId, UlidValue.New(now).ToString(), persona.Key,
                    persona.Specialty, persona.Purpose, null, now),
                cancellationToken);
            LogSpecialtyCreated(logger, persona.Key, created.Name);
            return created;
        }
        catch (TeamSpecialtyCatalogConflictException)
        {
            // Duas conversas podem detectar a mesma lacuna ao mesmo tempo. O catálogo garante a
            // unicidade; a segunda converge para a entrada que venceu a corrida.
            rows = await _specialties.ListSpecialtiesAsync(
                tenantId, null, null, 500, cancellationToken);
            return rows.First(value =>
                string.Equals(value.Key, persona.Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Name, persona.Specialty, StringComparison.OrdinalIgnoreCase));
        }
    }

    private Task<(AgentRecord Agent, bool Created)> EnsureProjectAgentAsync(
        string tenantId,
        string projectId,
        string actorProfileId,
        AgentDefinitionRecord definition,
        AgentRecord route,
        string reason,
        CancellationToken cancellationToken) =>
        _catalog.EnsureProjectAgentAsync(
            new ProjectAgentEnsureCommand(
                tenantId, actorProfileId, UlidValue.New(_clock.UtcNow).ToString(), projectId,
                definition.Id, definition.Name, route.AccountId!, route.ModelId!, route.Effort!,
                route.ProviderEffortValue!, route.FallbackModelIds ?? [], reason, _clock.UtcNow),
            cancellationToken);

    private static bool HasExecutableRoute(AgentRecord agent) =>
        !string.IsNullOrWhiteSpace(agent.AccountId) &&
        !string.IsNullOrWhiteSpace(agent.ModelId) &&
        !string.IsNullOrWhiteSpace(agent.Effort) &&
        !string.IsNullOrWhiteSpace(agent.ProviderEffortValue) &&
        agent.State is not ("retired" or "failed" or "disabled");

    /// <summary>
    /// A rota (conta/modelo/effort) que a persona nova herda ao nascer.
    ///
    /// Achado da recuperação de throughput da Fase 5 (2026-08): esta seleção nunca olhava o papel —
    /// só pegava o primeiro agente executável do projeto, ordenado por estado e depois por Id. Como
    /// os primeiros agentes de cada projeto (fases 1-4: arquiteto, PO) rodam sob a conta do Chief,
    /// TODA persona de código nova herdava a mesma conta por acidente de ordenação, mesmo com outras
    /// contas write-capable e com quota disponíveis para o papel certo. A fleet nominal de N contas
    /// virava fleet efetiva de 1 por bug de roteamento, não só por falta de cota real — e a conta do
    /// Chief ficava no caminho de virar executora de FEAT, violando a prioridade dela (coordenação
    /// antes de execução).
    ///
    /// Agora prefere, entre os agentes executáveis, aquele cuja CONTA declara o papel que esta
    /// persona vai exercer (<c>AgentAccountRegistry.AllowedRoles</c>) — e só cai no comportamento
    /// antigo (primeiro executável, por estado e Id) quando nenhuma conta com o papel certo existe,
    /// para não deixar a persona sem rota nenhuma.
    /// </summary>
    private AgentRecord? SelectRoute(IReadOnlyList<AgentRecord> projectAgents, ProposedPersona persona)
    {
        var role = InferAccountRole(persona);
        var (route, matchedRole) = SelectRouteCore(
            projectAgents,
            role,
            agent => _accounts is not null &&
                agent.AccountId is { Length: > 0 } accountId &&
                _accounts.Get(accountId)?.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase) == true);

        // _accounts nulo não é "nenhuma conta tinha o papel certo" — é "não dá para saber"
        // (AgentRuns desligado, registro de contas nunca carregado). Só vale a pena avisar quando
        // a checagem realmente rodou e não achou nada.
        if (route is not null && !matchedRole && _accounts is not null)
        {
            LogRouteWithoutMatchingRole(logger, persona.Key, role, route.AccountId ?? "?");
        }

        return route;
    }

    /// <summary>
    /// O núcleo puro da seleção: role-aware quando <paramref name="accountAllowsRole"/> acha
    /// alguém, senão cai no comportamento anterior (mais disponível, por estado e Id) — nenhuma
    /// persona fica sem rota só porque nenhuma conta do projeto declara o papel certo. Extraído
    /// para testar sem precisar de <see cref="AgentAccountRegistry"/> nem de DI: o predicado
    /// abstrai a fonte de verdade de "quais papéis esta conta aceita".
    /// </summary>
    internal static (AgentRecord? Route, bool MatchedRole) SelectRouteCore(
        IReadOnlyList<AgentRecord> projectAgents,
        string role,
        Func<AgentRecord, bool> accountAllowsRole)
    {
        var executable = projectAgents.Where(HasExecutableRoute).ToArray();
        var byRole = executable
            .Where(accountAllowsRole)
            .OrderByDescending(agent => agent.State is "active" or "idle" or "waiting")
            .ThenBy(agent => agent.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (byRole is not null)
        {
            return (byRole, true);
        }

        // Nenhuma conta do projeto declara o papel certo — cair para a rota mais disponível é
        // melhor do que recusar a persona inteira (team.no_executable_route).
        var fallback = executable
            .OrderByDescending(agent => agent.State is "active" or "idle" or "waiting")
            .ThenBy(agent => agent.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return (fallback, false);
    }

    /// <summary>
    /// O vocabulário de papel de CONTA (<see cref="AgentRoles"/>) só distingue frontend, backend,
    /// critic e chief-orchestrator — mais grosso que os escopos de <see cref="SelectScopes"/>
    /// (devops, dados, genérico). Toda persona que não é claramente de frontend cai em backend, que
    /// é o papel que cobre infra/dados/genérico neste vocabulário.
    /// </summary>
    internal static string InferAccountRole(ProposedPersona persona) =>
        IsFrontendPersona(PersonaText(persona)) ? AgentRoles.FrontendSpecialist : AgentRoles.BackendSpecialist;

    private static bool IsFrontendPersona(string text) =>
        text.Contains("frontend", StringComparison.Ordinal) ||
        text.Contains("mobile", StringComparison.Ordinal) ||
        text.Contains("acessibilidade", StringComparison.Ordinal) ||
        text.Contains("interface", StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chief: persona {PersonaKey} nasceu sem conta do papel {Role} disponível no " +
            "projeto; herdou a conta {AccountId} por fallback.")]
    private static partial void LogRouteWithoutMatchingRole(
        ILogger logger, string personaKey, string role, string accountId);

    private static string[] SelectTools(
        ProposedPersona persona,
        IReadOnlyList<ToolCatalogRecord> tools)
    {
        var text = PersonaText(persona);
        var keys = new HashSet<string>(StringComparer.Ordinal) { "filesystem" };
        if (text.Contains("repo", StringComparison.Ordinal) ||
            text.Contains("código", StringComparison.Ordinal) ||
            text.Contains("code", StringComparison.Ordinal) ||
            text.Contains("implement", StringComparison.Ordinal) ||
            text.Contains("teste", StringComparison.Ordinal))
        {
            keys.Add("git");
            keys.Add("shell");
        }

        if (text.Contains("document", StringComparison.Ordinal) ||
            text.Contains("relatório", StringComparison.Ordinal) ||
            text.Contains("artefato", StringComparison.Ordinal))
            keys.Add("artifact.render");
        if (text.Contains("pesquis", StringComparison.Ordinal) ||
            text.Contains("research", StringComparison.Ordinal) ||
            text.Contains("web", StringComparison.Ordinal))
            keys.Add("web.search");

        return tools
            .Where(tool => string.Equals(tool.ComponentState, "enabled", StringComparison.Ordinal) &&
                keys.Contains(tool.Key))
            .Select(tool => tool.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] SelectSkills(
        ProposedPersona persona,
        IReadOnlyList<SkillCatalogRecord> skills)
    {
        var text = PersonaText(persona);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (text.Contains("requis", StringComparison.Ordinal) || text.Contains("produto", StringComparison.Ordinal)) keys.Add("requirements");
        if (text.Contains("arquitet", StringComparison.Ordinal) || text.Contains("integra", StringComparison.Ordinal)) keys.Add("architecture");
        if (text.Contains("teste", StringComparison.Ordinal) || text.Contains("qualidade", StringComparison.Ordinal) || text.Contains("segurança", StringComparison.Ordinal)) keys.Add("testing-review");
        if (text.Contains("document", StringComparison.Ordinal) || text.Contains("relatório", StringComparison.Ordinal)) keys.Add("technical-writing");
        return skills
            .Where(skill => string.Equals(skill.ComponentState, "enabled", StringComparison.Ordinal) &&
                keys.Contains(skill.Key))
            .Select(skill => skill.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static (IReadOnlyList<string> Allowed, IReadOnlyList<string> Denied) SelectScopes(
        ProposedPersona persona)
    {
        var text = PersonaText(persona);
        IReadOnlyList<string> allowed;
        if (IsFrontendPersona(text))
            allowed = ["frontend/**", "tests/**", "docs/**"];
        else if (text.Contains("devops", StringComparison.Ordinal) ||
            text.Contains("sre", StringComparison.Ordinal) ||
            text.Contains("infraestrutur", StringComparison.Ordinal))
            allowed = ["infra/**", "tools/**", "tests/**", "docs/**"];
        else if (text.Contains("banco", StringComparison.Ordinal) ||
            text.Contains("database", StringComparison.Ordinal) ||
            text.Contains("oracle", StringComparison.Ordinal) ||
            text.Contains("dados", StringComparison.Ordinal))
            allowed = ["src/Harness.Persistence.Sqlite/**", "src/Harness.Persistence.Postgres/**", "tests/**", "docs/**"];
        else if (persona.RequiredCapabilities.All(capability =>
            capability.Contains("read", StringComparison.OrdinalIgnoreCase) ||
            capability.Contains("doc", StringComparison.OrdinalIgnoreCase) ||
            capability.Contains("research", StringComparison.OrdinalIgnoreCase)))
            allowed = ["docs/**"];
        else
            allowed = ["src/**", "tests/**", "docs/**"];

        return (allowed, ["governance/**", ".git/**", ".harness/**"]);
    }

    private static string PersonaText(ProposedPersona persona) =>
        string.Join(' ',
            [persona.Key, persona.Name, persona.Specialty, persona.Purpose,
             .. persona.Responsibilities, .. persona.RequiredCapabilities])
        .ToLowerInvariant();

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: especialidade '{Specialty}' criada no catálogo para a persona '{PersonaKey}'.")]
    private static partial void LogSpecialtyCreated(ILogger logger, string personaKey, string specialty);
}
