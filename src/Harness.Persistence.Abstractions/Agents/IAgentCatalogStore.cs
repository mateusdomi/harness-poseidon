namespace Harness.Persistence.Abstractions.Agents;

public interface IAgentCatalogStore
{
    Task<AgentDefinitionRecord?> GetDefinitionAsync(
        string definitionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsAsync(
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<AgentRecord?> GetAgentAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(
        string tenantId,
        string? projectId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<AgentRecord> UpdateSelectionAsync(
        AgentSelectionCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Garante uma instância visível e executável de uma definição dentro do projeto. A chave
    /// lógica é (tenant, projeto, definição): retries e duas criações concorrentes convergem para
    /// a mesma pessoa, em vez de deixarem uma definição nominal ou duplicarem o organograma.
    /// </summary>
    Task<(AgentRecord Agent, bool Created)> EnsureProjectAgentAsync(
        ProjectAgentEnsureCommand command,
        CancellationToken cancellationToken = default);

    Task<AgentDefinitionRecord?> GetDefinitionForTenantAsync(string tenantId, string definitionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsForTenantAsync(string tenantId, string? afterId, int limit, bool includeArchived, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentDefinitionVersionRecord>> ListDefinitionVersionsAsync(
        string tenantId, string definitionId, int? beforeVersion, int limit,
        CancellationToken cancellationToken = default);
    Task<AgentDefinitionRecord> CreateDefinitionAsync(AgentDefinitionCreateCommand command, CancellationToken cancellationToken = default);
    Task<AgentDefinitionRecord> UpdateDefinitionAsync(AgentDefinitionUpdateCommand command, CancellationToken cancellationToken = default);
    Task<AgentDefinitionRecord> DuplicateDefinitionAsync(AgentDefinitionDuplicateCommand command, CancellationToken cancellationToken = default);
    Task<AgentDefinitionRecord> SetDefinitionLifecycleAsync(AgentDefinitionLifecycleCommand command, CancellationToken cancellationToken = default);
    Task DeleteDefinitionAsync(AgentDefinitionDeleteCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Semeia, de forma idempotente, as definições canônicas built-in (tenant_id IS NULL) com o
    /// conteúdo completo das personas e o proprietário (owner). Cada linha só é preenchida uma vez
    /// (guarda em owner IS NULL): reexecutar não duplica nem sobrescreve. Retorna o número de
    /// definições efetivamente semeadas nesta chamada.
    /// </summary>
    Task<int> EnsureBuiltInDefinitionsAsync(
        IReadOnlyList<BuiltInAgentDefinitionSeed> definitions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Caminho DEDICADO da criação de persona pela chefe. Existe separado do CRUD administrativo
    /// de propósito: aqui a procedência é carimbada pelo próprio store (`origin='chief'`, escopo do
    /// projeto que motivou a criação, motivo obrigatório) e não pode ser forjada pelo chamador.
    /// Dar à chefe o endpoint genérico seria dar-lhe também o poder de se declarar humana.
    ///
    /// Idempotente por (tenant, agent_key): repetir a mesma intenção devolve a definição existente
    /// em vez de criar uma segunda — é o que impede duas demandas simultâneas de gerarem personas
    /// gêmeas.
    /// </summary>
    Task<(AgentDefinitionRecord Definition, bool Created)> CreateChiefDefinitionAsync(
        ChiefDefinitionCreateCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Move a persona no ciclo de confiança (observação, quarentena, reutilizável, global...). É a
    /// alavanca que permite à chefe rebaixar quem entrega mal e promover quem entrega bem sem
    /// depender do dono — e ao dono intervir a qualquer momento.
    /// </summary>
    Task<AgentDefinitionRecord> SetDefinitionLifecycleStateAsync(
        AgentDefinitionLifecycleStateCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Criação de persona pela chefe. O conteúdo é o mesmo de qualquer definição; o que este comando
/// acrescenta é a PROCEDÊNCIA: por que a persona nasceu e a que projeto ela pertence.
/// </summary>
public sealed record ChiefDefinitionCreateCommand(
    string TenantId,
    string ActorProfileId,
    string Id,
    AgentDefinitionContent Content,
    string ScopeProjectId,
    string CreationReason,
    DateTimeOffset OccurredAt);

public sealed record AgentDefinitionLifecycleStateCommand(
    string TenantId,
    string ActorProfileId,
    string Id,
    string LifecycleState,
    string Reason,
    DateTimeOffset OccurredAt);

/// <summary>
/// Conteúdo canônico de uma definição built-in: o id estável da linha semeada na migração
/// inicial, o proprietário (owner, p.ex. "system") e o conteúdo completo da persona.
/// </summary>
public sealed record BuiltInAgentDefinitionSeed(
    string Id, string Owner, AgentDefinitionContent Content);

public sealed record AgentDefinitionRecord(
    string Id,
    string Key,
    string Name,
    string Role,
    string? Specialty,
    string Description,
    string? DefaultModelId,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> ToolIds,
    string? Persona = null, string? Mission = null,
    IReadOnlyList<string>? OperatingPrinciples = null, IReadOnlyList<string>? Deliverables = null,
    IReadOnlyList<string>? QualityCriteria = null, string? CommunicationStyle = null,
    IReadOnlyList<string>? Limitations = null, int Version = 1, bool Enabled = true,
    DateTimeOffset? ArchivedAt = null, IReadOnlyList<string>? Stacks = null,
    string? DefaultEffort = null, string? PreferredAccountId = null,
    IReadOnlyList<string>? FallbackModelIds = null, string? Team = null,
    string? ActorCritic = null, string? Risk = null, string? Owner = null,

    /// <summary>Quem criou a persona: `human`, `chief` ou `system`. Auditoria depende disto.</summary>
    string Origin = "human",

    /// <summary>
    /// Estágio de confiança: `project_scoped`, `active`, `reusable`, `global`, `observation`,
    /// `quarantined` ou `disabled`. Persona criada pela chefe nasce presa ao projeto que a
    /// motivou; ampliar alcance é promoção auditada, não estado inicial.
    /// </summary>
    string LifecycleState = "active",

    /// <summary>Projeto que motivou a criação, quando a persona é específica de um.</summary>
    string? ScopeProjectId = null,

    /// <summary>Por que a persona existe — a lacuna concreta que ela veio cobrir.</summary>
    string? CreationReason = null,

    /// <summary>Caminhos que a persona pode tocar (B8/F17). Vazio = herda o escopo do papel.</summary>
    IReadOnlyList<string>? AllowedScopes = null,

    /// <summary>
    /// Caminhos que ela NUNCA toca. A ausência é a omissão que deixa governança, segredo e o
    /// próprio quadro abertos por esquecimento — e omissão não é decisão.
    /// </summary>
    IReadOnlyList<string>? DeniedScopes = null,

    /// <summary>
    /// QUANDO acionar esta persona (Fase 2A.3). Sem isto, escolher especialista é adivinhação por
    /// semelhança de nome, e o card de banco de dados cai no back-end genérico porque "parece
    /// parecido".
    /// </summary>
    IReadOnlyList<string>? ActivationCriteria = null,

    /// <summary>
    /// Quando NÃO acionar — a fronteira negativa (B11). A ausência é a omissão que faz uma persona
    /// ser chamada para tudo e, por isso, não servir para nada.
    /// </summary>
    IReadOnlyList<string>? NonActivationCriteria = null);

public sealed record AgentDefinitionContent(
    string Key, string Name, string Role, string? Specialty, string Description,
    string? DefaultModelId, IReadOnlyList<string> SkillIds, IReadOnlyList<string> ToolIds,
    string? Persona, string? Mission, IReadOnlyList<string> OperatingPrinciples,
    IReadOnlyList<string> Deliverables, IReadOnlyList<string> QualityCriteria,
    string? CommunicationStyle, IReadOnlyList<string> Limitations,
    IReadOnlyList<string>? Stacks = null, string? DefaultEffort = null,
    string? PreferredAccountId = null, IReadOnlyList<string>? FallbackModelIds = null,
    string? Team = null, string? ActorCritic = null, string? Risk = null,
    // B8/F17 — o que a persona pode e não pode TOCAR. `Stacks` diz tecnologia e `Limitations` é
    // texto livre; nenhum dos dois é escopo de caminho, e o lint de persona julga caminho.
    IReadOnlyList<string>? AllowedScopes = null, IReadOnlyList<string>? DeniedScopes = null,
    // Fase 2A.3 — QUANDO acionar e quando NÃO acionar. Sem os dois, a escolha de especialista é
    // semelhança de nome, e a persona sem fronteira negativa é chamada para tudo.
    IReadOnlyList<string>? ActivationCriteria = null,
    IReadOnlyList<string>? NonActivationCriteria = null);
public sealed record AgentDefinitionVersionRecord(
    string Id, string DefinitionId, int Version, AgentDefinitionContent Snapshot,
    string ActorProfileId, DateTimeOffset CreatedAt);
public sealed record AgentDefinitionCreateCommand(string TenantId, string ActorProfileId, string Id, AgentDefinitionContent Content, DateTimeOffset OccurredAt);
public sealed record AgentDefinitionUpdateCommand(string TenantId, string ActorProfileId, string Id, int ExpectedVersion, AgentDefinitionContent Content, DateTimeOffset OccurredAt);
public sealed record AgentDefinitionDuplicateCommand(string TenantId, string ActorProfileId, string SourceId, string Id, string Key, string Name, DateTimeOffset OccurredAt);
public sealed record AgentDefinitionLifecycleCommand(string TenantId, string ActorProfileId, string Id, string Action, DateTimeOffset OccurredAt);
public sealed record AgentDefinitionDeleteCommand(string TenantId, string ActorProfileId, string Id, DateTimeOffset OccurredAt);
public class AgentDefinitionAdminException(string detail) : Exception(detail);

/// <summary>
/// CAT-05: uma definição referenciou um item de catálogo (modelo, ferramenta, skill, equipe ou
/// especialidade) que não existe (ou está desabilitado). Em vez de falhar de forma silenciosa e
/// genérica, carregamos os dados ACIONÁVEIS: QUAL catálogo, QUAL referência faltou e a ROTA para
/// criá-lo/gerenciá-lo — consistente com o padrão do catálogo de team/specialty (CAT-04). A API
/// materializa esses campos num problema tipado para que o cliente possa "criar quando não
/// encontrar", nunca uma mensagem opaca. É uma especialização de
/// <see cref="AgentDefinitionAdminException"/>: quem só distingue "definição inválida" continua
/// tratando-a como antes; quem quer a resposta acionável captura o tipo específico primeiro.
/// </summary>
public sealed class AgentDefinitionCatalogMissingException(string catalog, string reference, string createRoute)
    : AgentDefinitionAdminException($"Referenced {catalog} '{reference}' was not found in the catalog.")
{
    /// <summary>O catálogo faltante: "model", "tool", "skill", "team" ou "specialty".</summary>
    public string Catalog { get; } = catalog;

    /// <summary>A referência exata (id ULID ou nome) que a definição citou e não existe.</summary>
    public string Reference { get; } = reference;

    /// <summary>A rota (coleção) onde o item pode ser criado/gerenciado, p.ex. "/api/v1/skills".</summary>
    public string CreateRoute { get; } = createRoute;
}

public sealed record AgentMetricsRecord(
    long TasksCompleted,
    long TokensInput,
    long TokensOutput,
    decimal CostUsd,
    long UptimeMs);

public sealed record AgentLeaseRecord(long FencingToken, DateTimeOffset ExpiresAt);

public sealed record AgentRecord(
    string TenantId,
    string Id,
    string DefinitionId,
    string? ProjectId,
    string Name,
    string State,
    string? CurrentTaskId,
    string? ModelId,
    AgentLeaseRecord? Lease,
    AgentMetricsRecord Metrics,
    DateTimeOffset? LastHeartbeatAt,
    string? AccountId = null,
    string? Effort = null,
    string? ProviderEffortValue = null,
    IReadOnlyList<string>? FallbackModelIds = null,
    string? SelectionReason = null,
    DateTimeOffset? SelectionUpdatedAt = null);

public sealed record AgentSelectionCommand(
    string TenantId, string AgentId, string ActorProfileId, string AccountId, string ModelId,
    string Effort, IReadOnlyList<string> FallbackModelIds, string Reason, DateTimeOffset OccurredAt);

public sealed record ProjectAgentEnsureCommand(
    string TenantId,
    string ActorProfileId,
    string AgentId,
    string ProjectId,
    string DefinitionId,
    string Name,
    string AccountId,
    string ModelId,
    string Effort,
    string ProviderEffortValue,
    IReadOnlyList<string> FallbackModelIds,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed class AgentSelectionNotFoundException(string resource) : Exception(resource)
{
    public string Resource { get; } = resource;
}
public sealed class AgentSelectionValidationException(string detail) : Exception(detail);
public sealed class AgentSelectionConflictException(string detail) : Exception(detail);
