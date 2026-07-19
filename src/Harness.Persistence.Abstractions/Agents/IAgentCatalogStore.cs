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
}

public sealed record AgentDefinitionRecord(
    string Id,
    string Key,
    string Name,
    string Role,
    string? Specialty,
    string Description,
    string? DefaultModelId,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> ToolIds);

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
