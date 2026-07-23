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
}

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
    string? ActorCritic = null, string? Risk = null, string? Owner = null);

public sealed record AgentDefinitionContent(
    string Key, string Name, string Role, string? Specialty, string Description,
    string? DefaultModelId, IReadOnlyList<string> SkillIds, IReadOnlyList<string> ToolIds,
    string? Persona, string? Mission, IReadOnlyList<string> OperatingPrinciples,
    IReadOnlyList<string> Deliverables, IReadOnlyList<string> QualityCriteria,
    string? CommunicationStyle, IReadOnlyList<string> Limitations,
    IReadOnlyList<string>? Stacks = null, string? DefaultEffort = null,
    string? PreferredAccountId = null, IReadOnlyList<string>? FallbackModelIds = null,
    string? Team = null, string? ActorCritic = null, string? Risk = null);
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

public sealed class AgentSelectionNotFoundException(string resource) : Exception(resource)
{
    public string Resource { get; } = resource;
}
public sealed class AgentSelectionValidationException(string detail) : Exception(detail);
public sealed class AgentSelectionConflictException(string detail) : Exception(detail);
