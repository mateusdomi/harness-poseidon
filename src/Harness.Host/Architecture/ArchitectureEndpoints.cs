using Harness.Host.Profiles;
using Harness.Modules.Architecture.Application;
using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Architecture;

/// <summary>
/// Architecture Hub (Solutions Architect). Mantém um MODELO estruturado (elementos + relacionamentos),
/// nunca imagens; diagramas são VIEWS. ARC-01 modelo + dependências + views; ARC-02 mapa corporativo;
/// ARC-03 Sistema 360; ARC-05 edição humana da arquitetura proposta (diff, lock, impacto, aplicar,
/// rollback) com o modelo PROPOSTO e o VIGENTE separados. Endpoints aditivos; nada quebra o existente.
/// </summary>
public static class ArchitectureEndpoints
{
    public static IEndpointRouteBuilder MapArchitecture(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/architecture").WithTags("architecture");

        // ARC-01 — modelo
        group.MapPost("/elements", CreateElementAsync).Produces<ArchitectureElementContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/elements", ListElementsAsync).Produces<ArchitectureElementPageContract>().ProducesProblem(401);
        group.MapGet("/elements/{id}", GetElementAsync).Produces<ArchitectureElementContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/elements/{id}/dependents", GetDependentsAsync).Produces<ArchitectureDependencyQueryContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/elements/{id}/history", GetHistoryAsync).Produces<ArchitectureHistoryContract>().ProducesProblem(400).ProducesProblem(401);
        group.MapPost("/elements/{id}/lock", SetLockAsync).Produces<ArchitectureElementContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/elements/{id}/rollback", RollbackElementAsync).Produces<ArchitectureElementContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/relationships", CreateRelationshipAsync).Produces<ArchitectureRelationshipContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/relationships", ListRelationshipsAsync).Produces<ArchitectureRelationshipListContract>().ProducesProblem(401);
        group.MapPost("/views", CreateViewAsync).Produces<ArchitectureViewContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/views", ListViewsAsync).Produces<ArchitectureViewListContract>().ProducesProblem(401);
        group.MapGet("/views/{id}", GetViewAsync).Produces<ArchitectureViewContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        // ARC-02 — mapa corporativo de sistemas
        group.MapPost("/systems/{id}/metadata", UpsertSystemMetadataAsync).Produces<SystemCatalogEntryContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/systems", ListSystemsAsync).Produces<SystemCatalogContract>().ProducesProblem(401);
        group.MapGet("/systems/{id}/overview", GetSystem360Async).Produces<System360Contract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/maps/domains", GetDomainMapAsync).Produces<DomainMapContract>().ProducesProblem(401);
        group.MapGet("/maps/capabilities", GetCapabilityMapAsync).Produces<CapabilityMapContract>().ProducesProblem(401);
        group.MapGet("/maps/integration", GetIntegrationGraphAsync).Produces<IntegrationGraphContract>().ProducesProblem(401);
        group.MapGet("/maps/heatmap", GetHeatmapAsync).Produces<SystemHeatmapContract>().ProducesProblem(401);

        // ARC-05 — edição humana da arquitetura proposta
        group.MapPost("/proposals", CreateProposalAsync).Produces<ArchitectureProposalContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/proposals", ListProposalsAsync).Produces<ArchitectureProposalListContract>().ProducesProblem(401);
        group.MapGet("/proposals/{id}", GetProposalAsync).Produces<ArchitectureProposalContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/proposals/{id}/diff", GetDiffAsync).Produces<ArchitectureDiffContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/proposals/{id}/impact", GetImpactAsync).Produces<ArchitectureImpactContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/proposals/{id}/apply", ApplyProposalAsync).Produces<ArchitectureApplyResultContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        return endpoints;
    }

    // ARC-01 ---------------------------------------------------------------------------------------

    private static async Task<IResult> CreateElementAsync(
        CreateElementRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null) return Invalid("body", "A request body is required.");
        if (!ArchitectureKinds.IsElementKind(body.Kind)) return Invalid("kind", "Unknown element kind.");
        if (string.IsNullOrWhiteSpace(body.Name)) return Invalid("name", "Name is required.");

        var record = await commands.CreateElementAsync(
            profile.TenantId, Trim(body.ProjectId), body.Kind!, body.Name!.Trim(),
            body.Description ?? string.Empty, body.Properties ?? new Dictionary<string, string>(), token);
        return Results.Created(
            $"/api/v1/architecture/elements/{record.Id}",
            ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(record)));
    }

    private static async Task<IResult> ListElementsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var page = await store.ListElementsAsync(
            profile.TenantId, Trim(projectId), ArchitectureKinds.Implemented, Trim(cursor),
            Math.Clamp(limit ?? 200, 1, 200), token);
        var contracts = page.Select(e => ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(e))).ToArray();
        var next = contracts.Length == Math.Clamp(limit ?? 200, 1, 200) ? page[^1].Id : null;
        return Results.Ok(new ArchitectureElementPageContract(contracts.Length, next, contracts));
    }

    private static async Task<IResult> GetElementAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("element");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var record = await store.GetElementAsync(profile.TenantId, id, token);
        return record is null
            ? NotFound("element")
            : Results.Ok(ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(record)));
    }

    private static async Task<IResult> GetDependentsAsync(
        string id, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, IArchitectureStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("element");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (await store.GetElementAsync(profile.TenantId, id, token) is null) return NotFound("element");
        var model = await readModel.BuildImplementedAsync(profile.TenantId, null, token);
        return Results.Ok(DependencyAnalyzer.WhoDependsOn(model, id));
    }

    private static async Task<IResult> GetHistoryAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("element");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var history = await store.ListHistoryAsync(profile.TenantId, id, 200, token);
        var entries = history
            .Select(h => new ArchitectureHistoryEntryContract(
                h.Id, h.EntityType, h.EntityId, h.Version, h.ChangeKind, h.Actor, h.Justification, h.OccurredAt))
            .ToArray();
        return Results.Ok(new ArchitectureHistoryContract(id, entries));
    }

    private static async Task<IResult> SetLockAsync(
        string id, LockRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("element");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var updated = await commands.SetLockAsync(profile.TenantId, id, body?.Locked ?? true, token);
        return updated is null
            ? NotFound("element")
            : Results.Ok(ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(updated)));
    }

    private static async Task<IResult> RollbackElementAsync(
        string id, RollbackRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("element");
        if (body is null || body.ToVersion <= 0) return Invalid("toVersion", "A positive target version is required.");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var outcome = await commands.RollbackElementAsync(
            profile.TenantId, id, body.ToVersion, profile.DisplayName, body.Justification, token);
        return outcome.Status == "not_found"
            ? NotFound("version")
            : Results.Ok(ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(outcome.Element!)));
    }

    private static async Task<IResult> CreateRelationshipAsync(
        CreateRelationshipRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null) return Invalid("body", "A request body is required.");
        if (!Valid(body.SourceId) || !Valid(body.TargetId)) return Invalid("endpoint", "source and target must be ULIDs.");
        if (!ArchitectureKinds.IsRelationshipKind(body.Kind)) return Invalid("kind", "Unknown relationship kind.");

        var record = await commands.CreateRelationshipAsync(
            profile.TenantId, Trim(body.ProjectId), body.SourceId, body.TargetId, body.Kind!,
            body.Properties ?? new Dictionary<string, string>(), token);
        return Results.Created(
            $"/api/v1/architecture/relationships/{record.Id}",
            ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(record)));
    }

    private static async Task<IResult> ListRelationshipsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var page = await store.ListRelationshipsAsync(
            profile.TenantId, Trim(projectId), ArchitectureKinds.Implemented, Trim(cursor),
            Math.Clamp(limit ?? 200, 1, 200), token);
        var contracts = page.Select(r => ArchitectureMapper.ToContract(ArchitectureReadModelService.ToInput(r))).ToArray();
        return Results.Ok(new ArchitectureRelationshipListContract(contracts.Length, contracts));
    }

    private static async Task<IResult> CreateViewAsync(
        CreateViewRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, ArchitectureReadModelService readModel, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null || string.IsNullOrWhiteSpace(body.Name)) return Invalid("name", "Name is required.");
        var notation = string.IsNullOrWhiteSpace(body.Notation) ? "c4" : body.Notation!.Trim();

        var record = await commands.CreateViewAsync(
            profile.TenantId, Trim(body.ProjectId), body.Name!.Trim(), body.Description ?? string.Empty,
            notation, body.ElementIds ?? [], body.RelationshipIds ?? [], body.FilterKinds ?? [],
            body.FilterTags ?? [], token);
        var model = await readModel.BuildImplementedAsync(profile.TenantId, record.ProjectId, token);
        var resolved = ArchitectureViewResolver.Resolve(model, ToSpec(record));
        return Results.Created($"/api/v1/architecture/views/{record.Id}", resolved);
    }

    private static async Task<IResult> ListViewsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var page = await store.ListViewsAsync(
            profile.TenantId, Trim(projectId), Trim(cursor), Math.Clamp(limit ?? 200, 1, 200), token);
        var summaries = page
            .Select(v => new ArchitectureViewSummaryContract(v.Id, v.ProjectId, v.Name, v.Notation, v.ElementIds.Count))
            .ToArray();
        return Results.Ok(new ArchitectureViewListContract(summaries.Length, summaries));
    }

    private static async Task<IResult> GetViewAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        ArchitectureReadModelService readModel, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("view");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var view = await store.GetViewAsync(profile.TenantId, id, token);
        if (view is null) return NotFound("view");
        var model = await readModel.BuildImplementedAsync(profile.TenantId, view.ProjectId, token);
        return Results.Ok(ArchitectureViewResolver.Resolve(model, ToSpec(view)));
    }

    // ARC-02 / ARC-03 ------------------------------------------------------------------------------

    private static async Task<IResult> UpsertSystemMetadataAsync(
        string id, UpsertSystemMetadataRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, IArchitectureStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("element");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null) return Invalid("body", "A request body is required.");
        var element = await store.GetElementAsync(profile.TenantId, id, token);
        if (element is null || element.Kind != ArchitectureKinds.SystemKind) return NotFound("system");

        var now = DateTimeOffset.UtcNow;
        var record = new ArchitectureSystemMetadataRecord(
            profile.TenantId, id, element.ProjectId, Trim(body.Domain), body.Capabilities ?? [],
            Trim(body.Owner), string.IsNullOrWhiteSpace(body.Criticality) ? "medium" : body.Criticality!.Trim(),
            body.TechStack ?? [], Trim(body.LifecycleStatus), body.CostMonthlyUsd, Math.Max(0, body.IncidentCount ?? 0),
            body.BusFactor, Trim(body.DuplicateOfId), Trim(body.RiskLevel), Trim(body.Sla), Trim(body.BackupPolicy),
            Trim(body.DrPolicy), body.LastIncidentAt, body.Pii ?? false, body.Sensitive ?? false, Trim(body.Retention),
            body.DataClasses ?? [],
            (body.Documents ?? []).Select(d => new ArchitectureDocumentLinkRecord(
                d.Id, d.Title, d.Kind, string.IsNullOrWhiteSpace(d.State) ? "current" : d.State!)).ToArray(),
            Math.Max(0, body.AdrCount ?? 0), body.Risks ?? [], body.LastReviewAt, Trim(body.ReviewConfidence), now);
        await commands.UpsertSystemMetadataAsync(record, token);
        return Results.Ok(SystemsMapProjector.CatalogEntry(
            ArchitectureReadModelService.ToInput(element), ArchitectureReadModelService.ToInput(record)));
    }

    private static async Task<IResult> ListSystemsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var model = await readModel.BuildImplementedAsync(profile.TenantId, Trim(projectId), token);
        var metadata = model.Systems.ToDictionary(s => s.ElementId, StringComparer.Ordinal);
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var after = Trim(cursor);
        var systems = SystemsMapProjector.Systems(model)
            .Where(s => after is null || string.CompareOrdinal(s.Id, after) > 0)
            .ToArray();
        var pageItems = systems.Take(pageSize).ToArray();
        var entries = pageItems
            .Select(s => SystemsMapProjector.CatalogEntry(s, metadata.GetValueOrDefault(s.Id)))
            .ToArray();
        var next = systems.Length > pageSize ? pageItems[^1].Id : null;
        return Results.Ok(new SystemCatalogContract(entries.Length, next, entries));
    }

    private static async Task<IResult> GetSystem360Async(
        string id, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("system");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var model = await readModel.BuildImplementedAsync(profile.TenantId, null, token);
        var overview = System360Projector.Project(model, id);
        return overview is null ? NotFound("system") : Results.Ok(overview);
    }

    private static Task<IResult> GetDomainMapAsync(
        string? projectId, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token) =>
        MapAsync(request, profiles, readModel, projectId, SystemsMapProjector.DomainMap, token);

    private static Task<IResult> GetCapabilityMapAsync(
        string? projectId, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token) =>
        MapAsync(request, profiles, readModel, projectId, SystemsMapProjector.CapabilityMap, token);

    private static Task<IResult> GetIntegrationGraphAsync(
        string? projectId, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token) =>
        MapAsync(request, profiles, readModel, projectId, SystemsMapProjector.IntegrationGraph, token);

    private static Task<IResult> GetHeatmapAsync(
        string? projectId, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token) =>
        MapAsync(request, profiles, readModel, projectId, SystemsMapProjector.Heatmap, token);

    private static async Task<IResult> MapAsync<T>(
        HttpRequest request, ILocalProfileStore profiles, ArchitectureReadModelService readModel,
        string? projectId, Func<ArchitectureModelInput, T> project, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var model = await readModel.BuildImplementedAsync(profile.TenantId, Trim(projectId), token);
        return Results.Ok(project(model));
    }

    // ARC-05 ---------------------------------------------------------------------------------------

    private static async Task<IResult> CreateProposalAsync(
        CreateProposalRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null || string.IsNullOrWhiteSpace(body.Title)) return Invalid("title", "Title is required.");
        var elements = body.Elements ?? [];
        var relationships = body.Relationships ?? [];
        if (elements.Any(e => !ArchitectureKinds.IsChangeKind(e.ChangeKind)) ||
            relationships.Any(r => !ArchitectureKinds.IsChangeKind(r.ChangeKind)))
        {
            return Invalid("changeKind", "changeKind must be add/modify/remove.");
        }

        var proposal = await commands.CreateProposalAsync(
            profile.TenantId, Trim(body.ProjectId), body.Title!.Trim(),
            elements.Select(e => new ProposedElementInput(
                e.ChangeKind!, Trim(e.CounterpartId), e.Kind, e.Name, e.Description, e.Properties)).ToArray(),
            relationships.Select(r => new ProposedRelationshipInput(
                r.ChangeKind!, Trim(r.CounterpartId), r.SourceId, r.TargetId, r.Kind, r.Properties)).ToArray(),
            token);
        return Results.Created($"/api/v1/architecture/proposals/{proposal.Id}", ToContract(proposal));
    }

    private static async Task<IResult> ListProposalsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var page = await store.ListProposalsAsync(
            profile.TenantId, Trim(projectId), Trim(cursor), Math.Clamp(limit ?? 100, 1, 200), token);
        return Results.Ok(new ArchitectureProposalListContract(page.Count, page.Select(ToContract).ToArray()));
    }

    private static async Task<IResult> GetProposalAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("proposal");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var proposal = await store.GetProposalAsync(profile.TenantId, id, token);
        return proposal is null ? NotFound("proposal") : Results.Ok(ToContract(proposal));
    }

    private static async Task<IResult> GetDiffAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        ArchitectureReadModelService readModel, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("proposal");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var proposal = await store.GetProposalAsync(profile.TenantId, id, token);
        if (proposal is null) return NotFound("proposal");
        var model = await readModel.BuildProposalAsync(profile.TenantId, proposal.ProjectId, id, token);
        return Results.Ok(ArchitectureProposalPlanner.Diff(model, id));
    }

    private static async Task<IResult> GetImpactAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        ArchitectureReadModelService readModel, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("proposal");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var proposal = await store.GetProposalAsync(profile.TenantId, id, token);
        if (proposal is null) return NotFound("proposal");
        var model = await readModel.BuildProposalAsync(profile.TenantId, proposal.ProjectId, id, token);
        return Results.Ok(ArchitectureProposalPlanner.Impact(model, id));
    }

    private static async Task<IResult> ApplyProposalAsync(
        string id, ApplyProposalRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureCommandService commands, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("proposal");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var outcome = await commands.ApplyAsync(profile.TenantId, id, body?.Justification, profile.DisplayName, token);
        return outcome.Status switch
        {
            "applied" => Results.Ok(outcome.Result),
            "justification_required" => Results.Json(outcome.Result, statusCode: 422),
            "conflict" => Problem(409, "proposal_not_open", $"The proposal is '{outcome.ConflictStatus}'."),
            _ => NotFound("proposal"),
        };
    }

    // Helpers --------------------------------------------------------------------------------------

    private static ArchViewSpec ToSpec(ArchitectureViewRecord view) => new(
        view.Id, view.ProjectId, view.Name, view.Description, view.Notation, view.ElementIds,
        view.RelationshipIds, view.FilterKinds, view.FilterTags);

    private static ArchitectureProposalContract ToContract(ArchitectureProposalRecord p) => new(
        p.Id, p.ProjectId, p.Title, p.Status, p.Justification, p.CreatedAt, p.AppliedAt);

    private static Task<LocalProfileRecord?> Session(
        HttpRequest request, ILocalProfileStore profiles, CancellationToken token) =>
        LocalProfileSession.ResolveAsync(request, profiles, token);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool Valid(string? id) => id is not null && UlidValue.TryParse(id, out _);
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", "ID must be a ULID.");
    private static IResult Invalid(string resource, string detail) => Problem(400, $"invalid_{resource}", detail);
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

// Request DTOs -------------------------------------------------------------------------------------

public sealed record CreateElementRequest(
    string? ProjectId, string? Kind, string? Name, string? Description,
    IReadOnlyDictionary<string, string>? Properties);

public sealed record CreateRelationshipRequest(
    string? ProjectId, string SourceId, string TargetId, string? Kind,
    IReadOnlyDictionary<string, string>? Properties);

public sealed record CreateViewRequest(
    string? ProjectId, string? Name, string? Description, string? Notation,
    IReadOnlyList<string>? ElementIds, IReadOnlyList<string>? RelationshipIds,
    IReadOnlyList<string>? FilterKinds, IReadOnlyList<string>? FilterTags);

public sealed record LockRequest(bool Locked);
public sealed record RollbackRequest(int ToVersion, string? Justification);

public sealed record SystemDocumentInput(string Id, string Title, string Kind, string? State);

public sealed record UpsertSystemMetadataRequest(
    string? Domain, IReadOnlyList<string>? Capabilities, string? Owner, string? Criticality,
    IReadOnlyList<string>? TechStack, string? LifecycleStatus, decimal? CostMonthlyUsd, int? IncidentCount,
    int? BusFactor, string? DuplicateOfId, string? RiskLevel, string? Sla, string? BackupPolicy,
    string? DrPolicy, DateTimeOffset? LastIncidentAt, bool? Pii, bool? Sensitive, string? Retention,
    IReadOnlyList<string>? DataClasses, IReadOnlyList<SystemDocumentInput>? Documents, int? AdrCount,
    IReadOnlyList<string>? Risks, DateTimeOffset? LastReviewAt, string? ReviewConfidence);

public sealed record ProposedElementRequest(
    string? ChangeKind, string? CounterpartId, string? Kind, string? Name, string? Description,
    IReadOnlyDictionary<string, string>? Properties);

public sealed record ProposedRelationshipRequest(
    string? ChangeKind, string? CounterpartId, string? SourceId, string? TargetId, string? Kind,
    IReadOnlyDictionary<string, string>? Properties);

public sealed record CreateProposalRequest(
    string? ProjectId, string? Title, IReadOnlyList<ProposedElementRequest>? Elements,
    IReadOnlyList<ProposedRelationshipRequest>? Relationships);

public sealed record ApplyProposalRequest(string? Justification);
