namespace Harness.Persistence.Abstractions.Providers;

public interface IProviderCatalogStore
{
    Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<ProviderRecord?> GetProviderAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountRecord>> ListAccountsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<AccountRecord?> GetAccountAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelRecord>> ListModelsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<ModelRecord?> GetModelAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoutingPolicyRecord>> ListRoutingPoliciesAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<RoutingPolicyRecord?> GetRoutingPolicyAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetRecord>> ListBudgetsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<BudgetRecord?> GetBudgetAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<ProviderCatalogRecord> UpdateAsync(ProviderCatalogUpdateCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelRecord>> SyncAsync(ProviderCatalogSyncCommand command, CancellationToken cancellationToken = default);
}

public abstract record ProviderCatalogRecord(string Id);
public sealed record ProviderRecord(string Id, string Kind, string Name, string? BaseUrl, bool Enabled) : ProviderCatalogRecord(Id);
public sealed record AccountRecord(string Id, string ProviderId, string Label, string State, decimal? QuotaLimitUsd, decimal QuotaUsedUsd) : ProviderCatalogRecord(Id);
public sealed record ModelRecord(string Id, string ProviderId, string Name, string DisplayName, IReadOnlyList<string> Capabilities, int ContextWindow, decimal? CostPer1kInputUsd, decimal? CostPer1kOutputUsd, bool Enabled) : ProviderCatalogRecord(Id);
public sealed record RoutingRuleRecord(string? TaskKind, string PreferredModelId, IReadOnlyList<string> FallbackModelIds, decimal? MaxCostPerAttemptUsd);
public sealed record RoutingPolicyRecord(string Id, string? ProjectId, string Name, IReadOnlyList<RoutingRuleRecord> Rules, bool Active) : ProviderCatalogRecord(Id);
public sealed record BudgetRecord(string Id, string Scope, string? ScopeId, string Period, decimal LimitUsd, decimal SpentUsd, decimal AlertThresholdPct) : ProviderCatalogRecord(Id);

public sealed record ProviderCatalogUpdateCommand(string TenantId, string ActorProfileId, string Resource,
    string Id, string PatchJson, DateTimeOffset OccurredAt);
public sealed record ProviderCatalogSyncCommand(string TenantId, string ActorProfileId, string ProviderId, DateTimeOffset OccurredAt);
public sealed class ProviderCatalogNotFoundException(string resource) : Exception(resource) { public string Resource { get; } = resource; }
public sealed class ProviderCatalogValidationException(string detail) : Exception(detail);
