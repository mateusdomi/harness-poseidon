namespace Harness.Persistence.Abstractions.Tools;

public interface IToolCatalogStore
{
    Task<IReadOnlyList<SkillCatalogRecord>> ListSkillsAsync(string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<SkillCatalogRecord?> GetSkillAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolCatalogRecord>> ListToolsAsync(string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<ToolCatalogRecord?> GetToolAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PluginCatalogRecord>> ListPluginsAsync(string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<PluginCatalogRecord?> GetPluginAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpServerCatalogRecord>> ListMcpServersAsync(string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<McpServerCatalogRecord?> GetMcpServerAsync(string id, CancellationToken cancellationToken = default);
    Task<ComponentCatalogRecord> UpdateAsync(ComponentCatalogUpdateCommand command, CancellationToken cancellationToken = default);
}

public abstract record ComponentCatalogRecord(string Id, string State);
public sealed record SkillCatalogRecord(string Id, string Key, string Name, string Description, string Version, string ComponentState)
    : ComponentCatalogRecord(Id, ComponentState);
public sealed record ToolCatalogRecord(string Id, string Key, string Name, string Description, string Kind, string ComponentState)
    : ComponentCatalogRecord(Id, ComponentState);
public sealed record PluginCatalogRecord(string Id, string Key, string Name, string Version, string Description, string ComponentState, IReadOnlyList<string> ProvidesToolIds)
    : ComponentCatalogRecord(Id, ComponentState);
public sealed record McpServerCatalogRecord(string Id, string Name, string Transport, string Endpoint, string ComponentState, int ToolCount)
    : ComponentCatalogRecord(Id, ComponentState);

public sealed record ComponentCatalogUpdateCommand(
    string TenantId,
    string ActorProfileId,
    string Resource,
    string Id,
    string? State,
    string? Endpoint,
    DateTimeOffset OccurredAt);

public sealed class ToolCatalogNotFoundException(string resource) : Exception(resource)
{
    public string Resource { get; } = resource;
}

public sealed class ToolCatalogValidationException(string detail) : Exception(detail);
