using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgents(this IEndpointRouteBuilder endpoints)
    {
        var definitions = endpoints.MapGroup("/api/v1/agent-definitions").WithTags("agents");
        definitions.MapGet("/", ListDefinitionsAsync).Produces<AgentDefinitionPage>().ProducesProblem(400).ProducesProblem(401);
        definitions.MapGet("/{definitionId}", GetDefinitionAsync).Produces<AgentDefinitionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        var agents = endpoints.MapGroup("/api/v1/agents").WithTags("agents");
        agents.MapGet("/", ListAgentsAsync).Produces<AgentPage>().ProducesProblem(400).ProducesProblem(401);
        agents.MapGet("/{agentId}", GetAgentAsync).Produces<AgentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        var chief = endpoints.MapGroup("/api/v1/projects/{projectId}/chief").WithTags("agents");
        chief.MapPost("/pause", PauseChiefAsync).Produces<ProjectResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        chief.MapPost("/resume", ResumeChiefAsync).Produces<ProjectResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        chief.MapPost("/handoff", HandoffChiefAsync).Produces<AgentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        chief.MapPost("/drain", DrainChiefAsync).Produces<int>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static Task<IResult> PauseChiefAsync(string projectId, HttpRequest request,
        ILocalProfileStore profiles, IChiefOrchestratorStore store, IClock clock, CancellationToken token) =>
        SetChiefPauseAsync(projectId, true, request, profiles, store, clock, token);

    private static Task<IResult> ResumeChiefAsync(string projectId, HttpRequest request,
        ILocalProfileStore profiles, IChiefOrchestratorStore store, IClock clock, CancellationToken token) =>
        SetChiefPauseAsync(projectId, false, request, profiles, store, clock, token);

    private static async Task<IResult> SetChiefPauseAsync(string projectId, bool pause, HttpRequest request,
        ILocalProfileStore profiles, IChiefOrchestratorStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId("project");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var command = new ChiefProjectCommand(profile.TenantId, projectId, profile.Id, clock.UtcNow);
            var project = pause ? await store.PauseAsync(command, token) : await store.ResumeAsync(command, token);
            return Results.Ok(ProjectEndpoints.ToResponse(project));
        }
        catch (ChiefResourceNotFoundException e) { return NotFound(e.Resource); }
        catch (ChiefStateConflictException e) { return Problem(409, "chief_state_conflict", e.Message); }
    }

    private static async Task<IResult> HandoffChiefAsync(string projectId, HandoffChiefRequest input,
        HttpRequest request, ILocalProfileStore profiles, IChiefOrchestratorStore store,
        IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId("project");
        if (input.TargetDefinitionId is not null && !UlidValue.TryParse(input.TargetDefinitionId, out _)) return InvalidId("definition");
        if (input.TargetModelId is not null && !UlidValue.TryParse(input.TargetModelId, out _)) return InvalidId("model");
        if (string.IsNullOrWhiteSpace(input.Note)) return Problem(400, "invalid_handoff", "Handoff note is required.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow;
            var agent = await store.HandoffAsync(new(profile.TenantId, projectId,
                UlidValue.New(now).ToString(), profile.Id, input.TargetDefinitionId,
                input.TargetModelId, input.Note.Trim(), now), token);
            return Results.Ok(ToContract(agent));
        }
        catch (ChiefResourceNotFoundException e) { return NotFound(e.Resource); }
        catch (ChiefStateConflictException e) { return Problem(409, "chief_state_conflict", e.Message); }
    }

    private static async Task<IResult> DrainChiefAsync(string projectId, DrainChiefRequest input,
        HttpRequest request, ILocalProfileStore profiles, IChiefOrchestratorStore store,
        IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId("project");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var count = await store.DrainAsync(new(profile.TenantId, projectId, profile.Id,
                input.Note, clock.UtcNow), token);
            return Results.Ok(count);
        }
        catch (ChiefResourceNotFoundException e) { return NotFound(e.Resource); }
        catch (ChiefStateConflictException e) { return Problem(409, "chief_state_conflict", e.Message); }
    }

    private static async Task<IResult> ListDefinitionsAsync(
        string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        if (!TryPage(cursor, limit, out var size)) return InvalidCursor();
        var values = await store.ListDefinitionsAsync(cursor, size + 1, token);
        var more = values.Count > size;
        var items = values.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new AgentDefinitionPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetDefinitionAsync(
        string definitionId, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(definitionId, out _)) return InvalidId("definition");
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        var value = await store.GetDefinitionAsync(definitionId, token);
        return value is null ? NotFound("agent_definition") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> ListAgentsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, CancellationToken token)
    {
        if (projectId is not null && !UlidValue.TryParse(projectId, out _)) return InvalidId("project");
        if (!TryPage(cursor, limit, out var size)) return InvalidCursor();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var values = await store.ListAgentsAsync(profile.TenantId, projectId, cursor, size + 1, token);
        var more = values.Count > size;
        var items = values.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new AgentPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAgentAsync(
        string agentId, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(agentId, out _)) return InvalidId("agent");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var value = await store.GetAgentAsync(profile.TenantId, agentId, token);
        return value is null ? NotFound("agent") : Results.Ok(ToContract(value));
    }

    private static bool TryPage(string? cursor, int? limit, out int size)
    {
        size = limit ?? 50;
        return size is >= 1 and <= 200 && (cursor is null || UlidValue.TryParse(cursor, out _));
    }

    private static AgentDefinitionContract ToContract(AgentDefinitionRecord value) => new(
        value.Id, value.Key, value.Name, value.Role, value.Specialty, value.Description,
        value.DefaultModelId, value.SkillIds, value.ToolIds);

    private static AgentContract ToContract(AgentRecord value) => new(
        value.Id, value.DefinitionId, value.ProjectId, value.Name, value.State, value.CurrentTaskId,
        value.ModelId,
        value.Lease is null ? null : new AgentLeaseContract(value.Lease.FencingToken, value.Lease.ExpiresAt),
        new AgentMetricsContract(value.Metrics.TasksCompleted, value.Metrics.TokensInput,
            value.Metrics.TokensOutput, value.Metrics.CostUsd, value.Metrics.UptimeMs),
        value.LastHeartbeatAt);

    private static IResult InvalidCursor() => Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", $"{resource} ID must be a ULID.");
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The requested resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record AgentDefinitionContract(
    string Id, string Key, string Name, string Role, string? Specialty, string Description,
    string? DefaultModelId, IReadOnlyList<string> SkillIds, IReadOnlyList<string> ToolIds);
public sealed record AgentDefinitionPage(IReadOnlyList<AgentDefinitionContract> Items, string? NextCursor);
public sealed record AgentMetricsContract(long TasksCompleted, long TokensInput, long TokensOutput, decimal CostUsd, long UptimeMs);
public sealed record AgentLeaseContract(long FencingToken, DateTimeOffset ExpiresAt);
public sealed record AgentContract(
    string Id, string DefinitionId, string? ProjectId, string Name, string State,
    string? CurrentTaskId, string? ModelId, AgentLeaseContract? Lease,
    AgentMetricsContract Metrics, DateTimeOffset? LastHeartbeatAt);
public sealed record AgentPage(IReadOnlyList<AgentContract> Items, string? NextCursor);
public sealed record HandoffChiefRequest(string? TargetDefinitionId, string? TargetModelId, string Note);
public sealed record DrainChiefRequest(string? Note);
