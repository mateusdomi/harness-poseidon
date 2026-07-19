using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
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
        definitions.MapPost("/", CreateDefinitionAsync).Produces<AgentDefinitionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        definitions.MapPatch("/{definitionId}", UpdateDefinitionAsync).Produces<AgentDefinitionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        definitions.MapPost("/{definitionId}/duplicate", DuplicateDefinitionAsync).Produces<AgentDefinitionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        definitions.MapPost("/{definitionId}/{action:regex(^(enable|disable|archive)$)}", SetDefinitionLifecycleAsync).Produces<AgentDefinitionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        definitions.MapDelete("/{definitionId}", DeleteDefinitionAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);

        var agents = endpoints.MapGroup("/api/v1/agents").WithTags("agents");
        agents.MapGet("/", ListAgentsAsync).Produces<AgentPage>().ProducesProblem(400).ProducesProblem(401);
        agents.MapGet("/{agentId}", GetAgentAsync).Produces<AgentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        agents.MapPatch("/{agentId}/selection", UpdateSelectionAsync).Produces<AgentContract>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        var organization = endpoints.MapGroup("/api/v1/projects").WithTags("agents");
        organization.MapGet("/{projectId}/agent-org-chart", GetOrgChartAsync)
            .Produces<AgentOrgChartContract>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);

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
        string? cursor, int? limit, bool? includeArchived, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        if (!TryPage(cursor, limit, out var size)) return InvalidCursor();
        var values = await store.ListDefinitionsForTenantAsync(profile.TenantId, cursor, size + 1, includeArchived == true, token);
        var more = values.Count > size;
        var items = values.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new AgentDefinitionPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetDefinitionAsync(
        string definitionId, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(definitionId, out _)) return InvalidId("definition");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var value = await store.GetDefinitionForTenantAsync(profile.TenantId, definitionId, token);
        return value is null ? NotFound("agent_definition") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> CreateDefinitionAsync(AgentDefinitionWriteRequest input, HttpRequest request, ILocalProfileStore profiles, IAgentCatalogStore store, IClock clock, CancellationToken token) { var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); var now = clock.UtcNow; var id = UlidValue.New(now).ToString(); try { var value = await store.CreateDefinitionAsync(new(profile.TenantId, profile.Id, id, ToContent(input), now), token); return Results.Created($"/api/v1/agent-definitions/{id}", ToContract(value)); } catch (AgentDefinitionAdminException e) { return Problem(400, "invalid_agent_definition", e.Message); } catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or Npgsql.PostgresException) { return Problem(409, "agent_definition_conflict", "Definition key already exists."); } }
    private static async Task<IResult> UpdateDefinitionAsync(string definitionId, AgentDefinitionWriteRequest input, HttpRequest request, ILocalProfileStore profiles, IAgentCatalogStore store, IClock clock, CancellationToken token) { if (!UlidValue.TryParse(definitionId, out _)) return InvalidId("definition"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); try { return Results.Ok(ToContract(await store.UpdateDefinitionAsync(new(profile.TenantId, profile.Id, definitionId, input.ExpectedVersion, ToContent(input), clock.UtcNow), token))); } catch (AgentDefinitionAdminException e) { return Problem(409, "agent_definition_conflict", e.Message); } }
    private static async Task<IResult> DuplicateDefinitionAsync(string definitionId, AgentDefinitionDuplicateRequest input, HttpRequest request, ILocalProfileStore profiles, IAgentCatalogStore store, IClock clock, CancellationToken token) { if (!UlidValue.TryParse(definitionId, out _)) return InvalidId("definition"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); var now = clock.UtcNow; var id = UlidValue.New(now).ToString(); try { var value = await store.DuplicateDefinitionAsync(new(profile.TenantId, profile.Id, definitionId, id, input.Key, input.Name, now), token); return Results.Created($"/api/v1/agent-definitions/{id}", ToContract(value)); } catch (AgentDefinitionAdminException e) { return Problem(409, "agent_definition_conflict", e.Message); } }
    private static async Task<IResult> SetDefinitionLifecycleAsync(string definitionId, string action, HttpRequest request, ILocalProfileStore profiles, IAgentCatalogStore store, IClock clock, CancellationToken token) { if (!UlidValue.TryParse(definitionId, out _)) return InvalidId("definition"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); try { return Results.Ok(ToContract(await store.SetDefinitionLifecycleAsync(new(profile.TenantId, profile.Id, definitionId, action, clock.UtcNow), token))); } catch (AgentDefinitionAdminException e) { return Problem(409, "agent_definition_conflict", e.Message); } }
    private static async Task<IResult> DeleteDefinitionAsync(string definitionId, HttpRequest request, ILocalProfileStore profiles, IAgentCatalogStore store, IClock clock, CancellationToken token) { if (!UlidValue.TryParse(definitionId, out _)) return InvalidId("definition"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); try { await store.DeleteDefinitionAsync(new(profile.TenantId, profile.Id, definitionId, clock.UtcNow), token); return Results.NoContent(); } catch (AgentDefinitionAdminException e) { return Problem(409, "agent_definition_conflict", e.Message); } }
    private static AgentDefinitionContent ToContent(AgentDefinitionWriteRequest value) => new(value.Key, value.Name, value.Role, value.Specialty, value.Description, value.DefaultModelId, value.SkillIds, value.ToolIds, value.Persona, value.Mission, value.OperatingPrinciples, value.Deliverables, value.QualityCriteria, value.CommunicationStyle, value.Limitations);

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

    private static async Task<IResult> UpdateSelectionAsync(
        string agentId, AgentSelectionRequest input, HttpRequest request, ILocalProfileStore profiles,
        IAgentCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(agentId, out _) || !UlidValue.TryParse(input.AccountId, out _) ||
            !UlidValue.TryParse(input.ModelId, out _) ||
            input.FallbackModelIds.Any(value => !UlidValue.TryParse(value, out _)))
            return InvalidId("selection_resource");
        if (string.IsNullOrWhiteSpace(input.Reason))
            return Problem(400, "invalid_agent_selection", "Selection reason is required.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var value = await store.UpdateSelectionAsync(new(profile.TenantId, agentId, profile.Id,
                input.AccountId, input.ModelId, input.Effort, input.FallbackModelIds,
                input.Reason.Trim(), clock.UtcNow), token);
            return Results.Ok(ToContract(value));
        }
        catch (AgentSelectionNotFoundException e) { return NotFound(e.Resource); }
        catch (AgentSelectionValidationException e) { return Problem(400, "invalid_agent_selection", e.Message); }
        catch (AgentSelectionConflictException e) { return Problem(409, "agent_selection_conflict", e.Message); }
    }

    private static async Task<IResult> GetOrgChartAsync(
        string projectId, HttpRequest request, ILocalProfileStore profiles,
        IProjectStore projects, IAgentCatalogStore agents, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId("project");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        if (project is null) return NotFound("project");

        var team = new List<AgentRecord>();
        string? cursor = null;
        do
        {
            var page = await agents.ListAgentsAsync(
                profile.TenantId, projectId, cursor, 200, token);
            team.AddRange(page);
            if (team.Count > 2_000)
                return Problem(409, "agent_org_chart_too_large", "Project team exceeds the organogram safety limit.");
            cursor = page.Count == 200 ? page[^1].Id : null;
        } while (cursor is not null);

        var definitions = new Dictionary<string, AgentDefinitionRecord>(StringComparer.Ordinal);
        foreach (var definitionId in team.Select(value => value.DefinitionId).Distinct(StringComparer.Ordinal))
        {
            var definition = await agents.GetDefinitionAsync(definitionId, token);
            if (definition is null)
                return Problem(409, "agent_definition_missing", "An agent references a missing definition.");
            definitions.Add(definitionId, definition);
        }

        var root = team.FirstOrDefault(value => value.Id == project.ChiefAgentId);
        var ordered = team
            .OrderBy(value => value.Id == project.ChiefAgentId ? 0 : 1)
            .ThenBy(value => definitions[value.DefinitionId].Role == "chief" ? 0 : 1)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var nodes = ordered.Select((agent, index) =>
        {
            var definition = definitions[agent.DefinitionId];
            var isRoot = root is not null && agent.Id == root.Id;
            return new AgentOrgChartNodeContract(
                agent.Id, agent.DefinitionId, isRoot || root is null ? null : root.Id,
                isRoot || root is null ? 0 : 1, index, agent.Name, definition.Role,
                definition.Specialty, agent.State, agent.CurrentTaskId,
                agent.ModelId ?? definition.DefaultModelId, definition.SkillIds,
                definition.ToolIds,
                new AgentMetricsContract(agent.Metrics.TasksCompleted, agent.Metrics.TokensInput,
                    agent.Metrics.TokensOutput, agent.Metrics.CostUsd, agent.Metrics.UptimeMs));
        }).ToArray();
        return Results.Ok(new AgentOrgChartContract(
            project.Id, root?.Id, nodes));
    }

    private static bool TryPage(string? cursor, int? limit, out int size)
    {
        size = limit ?? 50;
        return size is >= 1 and <= 200 && (cursor is null || UlidValue.TryParse(cursor, out _));
    }

    private static AgentDefinitionContract ToContract(AgentDefinitionRecord value) => new(
        value.Id, value.Key, value.Name, value.Role, value.Specialty, value.Description,
        value.DefaultModelId, value.SkillIds, value.ToolIds, value.Persona, value.Mission,
        value.OperatingPrinciples ?? [], value.Deliverables ?? [], value.QualityCriteria ?? [],
        value.CommunicationStyle, value.Limitations ?? [], value.Version, value.Enabled, value.ArchivedAt);

    private static AgentContract ToContract(AgentRecord value) => new(
        value.Id, value.DefinitionId, value.ProjectId, value.Name, value.State, value.CurrentTaskId,
        value.ModelId,
        value.Lease is null ? null : new AgentLeaseContract(value.Lease.FencingToken, value.Lease.ExpiresAt),
        new AgentMetricsContract(value.Metrics.TasksCompleted, value.Metrics.TokensInput,
            value.Metrics.TokensOutput, value.Metrics.CostUsd, value.Metrics.UptimeMs),
        value.LastHeartbeatAt, value.AccountId, value.Effort, value.ProviderEffortValue,
        value.FallbackModelIds ?? [], value.SelectionReason, value.SelectionUpdatedAt);

    private static IResult InvalidCursor() => Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", $"{resource} ID must be a ULID.");
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The requested resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record AgentDefinitionContract(
    string Id, string Key, string Name, string Role, string? Specialty, string Description,
    string? DefaultModelId, IReadOnlyList<string> SkillIds, IReadOnlyList<string> ToolIds,
    string? Persona, string? Mission, IReadOnlyList<string> OperatingPrinciples,
    IReadOnlyList<string> Deliverables, IReadOnlyList<string> QualityCriteria,
    string? CommunicationStyle, IReadOnlyList<string> Limitations, int Version, bool Enabled,
    DateTimeOffset? ArchivedAt);
public sealed record AgentDefinitionPage(IReadOnlyList<AgentDefinitionContract> Items, string? NextCursor);
public sealed record AgentMetricsContract(long TasksCompleted, long TokensInput, long TokensOutput, decimal CostUsd, long UptimeMs);
public sealed record AgentLeaseContract(long FencingToken, DateTimeOffset ExpiresAt);
public sealed record AgentContract(
    string Id, string DefinitionId, string? ProjectId, string Name, string State,
    string? CurrentTaskId, string? ModelId, AgentLeaseContract? Lease,
    AgentMetricsContract Metrics, DateTimeOffset? LastHeartbeatAt, string? AccountId,
    string? Effort, string? ProviderEffortValue, IReadOnlyList<string> FallbackModelIds,
    string? SelectionReason, DateTimeOffset? SelectionUpdatedAt);
public sealed record AgentPage(IReadOnlyList<AgentContract> Items, string? NextCursor);
public sealed record AgentOrgChartContract(
    string ProjectId, string? RootAgentId, IReadOnlyList<AgentOrgChartNodeContract> Nodes);
public sealed record AgentOrgChartNodeContract(
    string AgentId, string DefinitionId, string? ParentAgentId, int Level, int Order,
    string Name, string Role, string? Specialty, string State, string? CurrentTaskId,
    string? EffectiveModelId, IReadOnlyList<string> SkillIds, IReadOnlyList<string> ToolIds,
    AgentMetricsContract Metrics);
public sealed record HandoffChiefRequest(string? TargetDefinitionId, string? TargetModelId, string Note);
public sealed record DrainChiefRequest(string? Note);
public sealed record AgentSelectionRequest(
    string AccountId, string ModelId, string Effort, IReadOnlyList<string> FallbackModelIds,
    string Reason);
public sealed record AgentDefinitionWriteRequest(
    string Key, string Name, string Role, string? Specialty, string Description,
    string? DefaultModelId, IReadOnlyList<string> SkillIds, IReadOnlyList<string> ToolIds,
    string? Persona, string? Mission, IReadOnlyList<string> OperatingPrinciples,
    IReadOnlyList<string> Deliverables, IReadOnlyList<string> QualityCriteria,
    string? CommunicationStyle, IReadOnlyList<string> Limitations, int ExpectedVersion = 0);
public sealed record AgentDefinitionDuplicateRequest(string Key, string Name);
