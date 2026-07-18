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
    DateTimeOffset? LastHeartbeatAt);
