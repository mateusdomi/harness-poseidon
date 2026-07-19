using System.Text.Json;
using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Providers;

public static class ProviderEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapProviders(this IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/v1/providers").WithTags("providers");
        providers.MapGet("/", ListProvidersAsync).Produces<ProviderPage>().ProducesProblem(400).ProducesProblem(401);
        providers.MapGet("/{id}", GetProviderAsync).Produces<ProviderContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        providers.MapPatch("/{id}", PatchProviderAsync).Produces<ProviderContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        providers.MapPost("/{id}/sync", SyncProviderAsync).Produces<IReadOnlyList<ModelContract>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var accounts = endpoints.MapGroup("/api/v1/accounts").WithTags("providers");
        accounts.MapGet("/", ListAccountsAsync).Produces<AccountPage>().ProducesProblem(400).ProducesProblem(401);
        accounts.MapPost("/", CreateAccountAsync).Produces<AccountContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        accounts.MapGet("/{id}", GetAccountAsync).Produces<AccountContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        accounts.MapPatch("/{id}", PatchAccountAsync).Produces<AccountContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        accounts.MapDelete("/{id}", DeleteAccountAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        var models = endpoints.MapGroup("/api/v1/models").WithTags("providers");
        models.MapGet("/", ListModelsAsync).Produces<ModelPage>().ProducesProblem(400).ProducesProblem(401);
        models.MapGet("/{id}", GetModelAsync).Produces<ModelContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        models.MapPatch("/{id}", PatchModelAsync).Produces<ModelContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var routing = endpoints.MapGroup("/api/v1/routing-policies").WithTags("providers");
        routing.MapGet("/", ListRoutingAsync).Produces<RoutingPolicyPage>().ProducesProblem(400).ProducesProblem(401);
        routing.MapGet("/{id}", GetRoutingAsync).Produces<RoutingPolicyContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        routing.MapPatch("/{id}", PatchRoutingAsync).Produces<RoutingPolicyContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var budgets = endpoints.MapGroup("/api/v1/budgets").WithTags("providers");
        budgets.MapGet("/", ListBudgetsAsync).Produces<BudgetPage>().ProducesProblem(400).ProducesProblem(401);
        budgets.MapGet("/{id}", GetBudgetAsync).Produces<BudgetContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        budgets.MapPatch("/{id}", PatchBudgetAsync).Produces<BudgetContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListProvidersAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token)
    { var session = await SessionAsync(cursor, limit, request, profiles, token); if (session.Error is not null) return session.Error; var rows = await store.ListProvidersAsync(session.Profile!.TenantId, cursor, session.Size + 1, token); var items = rows.Take(session.Size).Select(ToContract).ToArray(); return Results.Ok(new ProviderPage(items, rows.Count > session.Size ? items[^1].Id : null)); }
    private static async Task<IResult> ListAccountsAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token)
    { var session = await SessionAsync(cursor, limit, request, profiles, token); if (session.Error is not null) return session.Error; var rows = await store.ListAccountsAsync(session.Profile!.TenantId, cursor, session.Size + 1, token); var items = rows.Take(session.Size).Select(ToContract).ToArray(); return Results.Ok(new AccountPage(items, rows.Count > session.Size ? items[^1].Id : null)); }
    private static async Task<IResult> ListModelsAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token)
    { var session = await SessionAsync(cursor, limit, request, profiles, token); if (session.Error is not null) return session.Error; var rows = await store.ListModelsAsync(session.Profile!.TenantId, cursor, session.Size + 1, token); var items = rows.Take(session.Size).Select(ToContract).ToArray(); return Results.Ok(new ModelPage(items, rows.Count > session.Size ? items[^1].Id : null)); }
    private static async Task<IResult> ListRoutingAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token)
    { var session = await SessionAsync(cursor, limit, request, profiles, token); if (session.Error is not null) return session.Error; var rows = await store.ListRoutingPoliciesAsync(session.Profile!.TenantId, cursor, session.Size + 1, token); var items = rows.Take(session.Size).Select(ToContract).ToArray(); return Results.Ok(new RoutingPolicyPage(items, rows.Count > session.Size ? items[^1].Id : null)); }
    private static async Task<IResult> ListBudgetsAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token)
    { var session = await SessionAsync(cursor, limit, request, profiles, token); if (session.Error is not null) return session.Error; var rows = await store.ListBudgetsAsync(session.Profile!.TenantId, cursor, session.Size + 1, token); var items = rows.Take(session.Size).Select(ToContract).ToArray(); return Results.Ok(new BudgetPage(items, rows.Count > session.Size ? items[^1].Id : null)); }

    private static Task<IResult> GetProviderAsync(string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token) => GetAsync("providers", id, request, profiles, store, token);
    private static Task<IResult> GetAccountAsync(string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token) => GetAsync("accounts", id, request, profiles, store, token);
    private static Task<IResult> GetModelAsync(string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token) => GetAsync("models", id, request, profiles, store, token);
    private static Task<IResult> GetRoutingAsync(string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token) => GetAsync("routing-policies", id, request, profiles, store, token);
    private static Task<IResult> GetBudgetAsync(string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token) => GetAsync("budgets", id, request, profiles, store, token);
    private static async Task<IResult> GetAsync(string resource, string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId(); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        ProviderCatalogRecord? value = resource switch { "providers" => await store.GetProviderAsync(profile.TenantId, id, token), "accounts" => await store.GetAccountAsync(profile.TenantId, id, token), "models" => await store.GetModelAsync(profile.TenantId, id, token), "routing-policies" => await store.GetRoutingPolicyAsync(profile.TenantId, id, token), _ => await store.GetBudgetAsync(profile.TenantId, id, token) };
        return value is null ? NotFound(resource) : Results.Ok(ToContract(value));
    }

    private static Task<IResult> PatchProviderAsync(string id, ProviderPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("providers", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchAccountAsync(string id, AccountPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("accounts", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchModelAsync(string id, ModelPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("models", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchRoutingAsync(string id, RoutingPolicyPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("routing-policies", id, input, request, profiles, store, clock, token);
    private static Task<IResult> PatchBudgetAsync(string id, BudgetPatchRequest input, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token) => PatchAsync("budgets", id, input, request, profiles, store, clock, token);
    private static async Task<IResult> PatchAsync(string resource, string id, object input, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId(); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        try { var value = await store.UpdateAsync(new(profile.TenantId, profile.Id, resource, id, JsonSerializer.Serialize(input, JsonOptions), clock.UtcNow), token); return Results.Ok(ToContract(value)); }
        catch (ProviderCatalogNotFoundException e) { return NotFound(e.Resource); }
        catch (ProviderCatalogValidationException e) { return Problem(400, "invalid_provider_update", e.Message); }
        catch (JsonException e) { return Problem(400, "invalid_provider_update", e.Message); }
    }

    private static async Task<IResult> SyncProviderAsync(string id, HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId(); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        try { var models = await store.SyncAsync(new(profile.TenantId, profile.Id, id, clock.UtcNow), token); return Results.Ok(models.Select(ToContract).ToArray()); }
        catch (ProviderCatalogNotFoundException e) { return NotFound(e.Resource); }
    }

    private static async Task<IResult> CreateAccountAsync(CreateProviderAccountRequest input,
        HttpRequest request, ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(input.ProviderId, out _)) return InvalidId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow; var id = UlidValue.New(now).ToString();
            var value = await store.CreateAccountAsync(new(profile.TenantId, profile.Id, id,
                input.ProviderId, input.Label, input.CredentialReference, input.QuotaLimitUsd, now,
                input.Identity, input.Plan, input.Authentication, input.QuotaWindow,
                input.QuotaResetsAt, input.Capabilities), token);
            return Results.Created($"/api/v1/accounts/{id}", ToContract(value));
        }
        catch (ProviderCatalogNotFoundException e) { return NotFound(e.Resource); }
        catch (ProviderCatalogValidationException e) { return Problem(400, "invalid_provider_account", e.Message); }
    }

    private static async Task<IResult> DeleteAccountAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IProviderCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            await store.DeleteAccountAsync(new(profile.TenantId, profile.Id, id, clock.UtcNow), token);
            return Results.NoContent();
        }
        catch (ProviderCatalogNotFoundException e) { return NotFound(e.Resource); }
        catch (ProviderCatalogLifecycleException e) { return Problem(409, "provider_account_in_use", e.Message); }
    }

    private static object ToContract(ProviderCatalogRecord value) => value switch { ProviderRecord x => ToContract(x), AccountRecord x => ToContract(x), ModelRecord x => ToContract(x), RoutingPolicyRecord x => ToContract(x), BudgetRecord x => ToContract(x), _ => throw new InvalidOperationException() };
    private static ProviderContract ToContract(ProviderRecord x) => new(x.Id, x.Kind, x.Name, x.BaseUrl, x.Enabled);
    private static AccountContract ToContract(AccountRecord x) => new(
        x.Id, x.ProviderId, x.Label, x.State, x.QuotaLimitUsd, x.QuotaUsedUsd, x.Identity,
        x.Plan, x.Authentication, x.Health, x.QuotaWindow, x.QuotaResetsAt, x.Capabilities ?? []);
    private static ModelContract ToContract(ModelRecord x) => new(x.Id, x.ProviderId, x.Name, x.DisplayName, x.Capabilities, x.ContextWindow, x.CostPer1kInputUsd, x.CostPer1kOutputUsd, x.Enabled);
    private static RoutingPolicyContract ToContract(RoutingPolicyRecord x) => new(x.Id, x.ProjectId, x.Name, x.Rules.Select(r => new RoutingRuleContract(r.TaskKind, r.PreferredModelId, r.FallbackModelIds, r.MaxCostPerAttemptUsd)).ToArray(), x.Active);
    private static BudgetContract ToContract(BudgetRecord x) => new(x.Id, x.Scope, x.ScopeId, x.Period, x.LimitUsd, x.SpentUsd, x.AlertThresholdPct);
    private static async Task<(Harness.Persistence.Abstractions.Identity.LocalProfileRecord? Profile, int Size, IResult? Error)> SessionAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, CancellationToken token)
    { var size = limit ?? 50; if (size is < 1 or > 200 || cursor is not null && !UlidValue.TryParse(cursor, out _)) return (null, size, Problem(400, "invalid_cursor", "Cursor or limit is invalid.")); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); return profile is null ? (null, size, SessionRequired()) : (profile, size, null); }
    private static IResult InvalidId() => Problem(400, "invalid_provider_resource_id", "Resource ID must be a ULID.");
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult NotFound(string resource) => Problem(404, "provider_resource_not_found", $"The {resource} resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record ProviderPatchRequest(string? Name, string? BaseUrl, bool? Enabled);
public sealed record AccountPatchRequest(
    string? Label, string? State, decimal? QuotaLimitUsd, string? Identity = null,
    string? Plan = null, string? Authentication = null, string? Health = null,
    string? QuotaWindow = null, DateTimeOffset? QuotaResetsAt = null,
    IReadOnlyList<string>? Capabilities = null);
public sealed record CreateProviderAccountRequest(
    string ProviderId, string Label, string CredentialReference, decimal? QuotaLimitUsd = null,
    string? Identity = null, string Plan = "unknown", string Authentication = "apiKey",
    string QuotaWindow = "monthly", DateTimeOffset? QuotaResetsAt = null,
    IReadOnlyList<string>? Capabilities = null);
public sealed record ModelPatchRequest(string? DisplayName, bool? Enabled);
public sealed record RoutingPolicyPatchRequest(string? Name, IReadOnlyList<RoutingRuleContract>? Rules, bool? Active);
public sealed record BudgetPatchRequest(decimal? LimitUsd, decimal? AlertThresholdPct);
public sealed record ProviderContract(string Id, string Kind, string Name, string? BaseUrl, bool Enabled);
public sealed record AccountContract(
    string Id, string ProviderId, string Label, string State, decimal? QuotaLimitUsd,
    decimal QuotaUsedUsd, string? Identity, string Plan, string Authentication, string Health,
    string QuotaWindow, DateTimeOffset? QuotaResetsAt, IReadOnlyList<string> Capabilities);
public sealed record ModelContract(string Id, string ProviderId, string Name, string DisplayName, IReadOnlyList<string> Capabilities, int ContextWindow, decimal? CostPer1kInputUsd, decimal? CostPer1kOutputUsd, bool Enabled);
public sealed record RoutingRuleContract(string? TaskKind, string PreferredModelId, IReadOnlyList<string> FallbackModelIds, decimal? MaxCostPerAttemptUsd);
public sealed record RoutingPolicyContract(string Id, string? ProjectId, string Name, IReadOnlyList<RoutingRuleContract> Rules, bool Active);
public sealed record BudgetContract(string Id, string Scope, string? ScopeId, string Period, decimal LimitUsd, decimal SpentUsd, decimal AlertThresholdPct);
public sealed record ProviderPage(IReadOnlyList<ProviderContract> Items, string? NextCursor);
public sealed record AccountPage(IReadOnlyList<AccountContract> Items, string? NextCursor);
public sealed record ModelPage(IReadOnlyList<ModelContract> Items, string? NextCursor);
public sealed record RoutingPolicyPage(IReadOnlyList<RoutingPolicyContract> Items, string? NextCursor);
public sealed record BudgetPage(IReadOnlyList<BudgetContract> Items, string? NextCursor);
