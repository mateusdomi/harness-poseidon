using System.Text.Json;
using Harness.Host.Profiles;
using Harness.Modules.Architecture.Application;
using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Architecture;

/// <summary>
/// Endpoints das áreas estendidas do Architecture Hub: descoberta de sistemas existentes (ARC-06),
/// insights &amp; racionalização (ARC-07, proposta e nunca ação), padrões &amp; decisões (ARC-08) e a
/// integração Delivery↔Architecture (ARC-10: consulta de reuso, baseline da entrega, AS-IS e a
/// comparação proposta × implementação no encerramento). Aditivos; nada quebra o existente.
/// </summary>
public static class ArchitectureHubEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapArchitectureHub(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/architecture").WithTags("architecture");

        // ARC-06 — descoberta
        group.MapPost("/discoveries", CreateDiscoveryAsync).Produces<DiscoveryContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/discoveries", ListDiscoveriesAsync).Produces<DiscoveryListContract>().ProducesProblem(401);
        group.MapGet("/discoveries/summary", GetDiscoverySummaryAsync).Produces<DiscoverySummaryListContract>().ProducesProblem(401);
        group.MapPost("/discoveries/{id}/status", SetDiscoveryStatusAsync).Produces<DiscoveryContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        // ARC-07 — insights & racionalização (read-only, derivado do modelo)
        group.MapGet("/insights", GetInsightsAsync).Produces<RationalizationReportContract>().ProducesProblem(401);

        // ARC-08 — padrões & decisões
        group.MapPost("/patterns", CreatePatternAsync).Produces<ArchitecturePatternContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/patterns", ListPatternsAsync).Produces<ArchitecturePatternListContract>().ProducesProblem(401);
        group.MapGet("/patterns/{id}", GetPatternAsync).Produces<ArchitecturePatternContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        // ARC-10 — integração Delivery↔Architecture
        group.MapGet("/portfolio/reuse", GetPortfolioReuseAsync).Produces<PortfolioReuseContract>().ProducesProblem(401);
        group.MapPost("/projects/{projectId}/baseline", CreateBaselineAsync).Produces<ArchitectureBaselineContract>(201).ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/baselines", ListBaselinesAsync).Produces<ArchitectureBaselineListContract>().ProducesProblem(401);
        group.MapGet("/baselines/{id}", GetBaselineAsync).Produces<ArchitectureBaselineContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/baselines/{id}/as-built", UpdateAsBuiltAsync).Produces<ArchitectureBaselineContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/baselines/{id}/comparison", GetBaselineComparisonAsync).Produces<BaselineComparisonContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/baselines/{id}/close", CloseBaselineAsync).Produces<BaselineComparisonContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    // ARC-06 -----------------------------------------------------------------------------------------

    private static async Task<IResult> CreateDiscoveryAsync(
        CreateDiscoveryRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureHubCommandService commands, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null) return Invalid("body", "A request body is required.");
        if (!ArchitectureHubKinds.IsDiscoverySource(body.SourceKind)) return Invalid("sourceKind", "Unknown discovery source.");
        if (!ArchitectureHubKinds.IsConfidence(body.Confidence)) return Invalid("confidence", "Confidence must be low/medium/high.");
        if (string.IsNullOrWhiteSpace(body.SubjectName)) return Invalid("subjectName", "Subject name is required.");
        if (string.IsNullOrWhiteSpace(body.Field)) return Invalid("field", "Field is required.");
        if (string.IsNullOrWhiteSpace(body.Evidence)) return Invalid("evidence", "Evidence is required for every discovery.");
        if (body.SystemId is not null && !Valid(body.SystemId)) return Invalid("systemId", "systemId must be a ULID.");

        var record = await commands.CreateDiscoveryAsync(
            profile.TenantId, Trim(body.ProjectId), Trim(body.SystemId), body.SubjectName!.Trim(),
            body.SourceKind!, body.Field!.Trim(), body.Value?.Trim() ?? string.Empty, body.Confidence!,
            body.Evidence!.Trim(), Clean(body.PendingQuestions), token);
        return Results.Created($"/api/v1/architecture/discoveries/{record.Id}", ToContract(record));
    }

    private static async Task<IResult> ListDiscoveriesAsync(
        string? projectId, string? systemId, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var page = await store.ListDiscoveriesAsync(
            profile.TenantId, Trim(projectId), Trim(systemId), Trim(cursor), Math.Clamp(limit ?? 200, 1, 200), token);
        var contracts = page.Select(ToContract).ToArray();
        var next = contracts.Length == Math.Clamp(limit ?? 200, 1, 200) ? page[^1].Id : null;
        return Results.Ok(new DiscoveryListContract(contracts.Length, next, contracts));
    }

    private static async Task<IResult> GetDiscoverySummaryAsync(
        string? projectId, string? systemId, HttpRequest request, ILocalProfileStore profiles,
        IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var all = new List<ArchitectureDiscoveryRecord>();
        string? after = null;
        for (var page = 0; page < 25; page++)
        {
            var batch = await store.ListDiscoveriesAsync(profile.TenantId, Trim(projectId), Trim(systemId), after, 200, token);
            all.AddRange(batch);
            if (batch.Count < 200) break;
            after = batch[^1].Id;
        }

        var facts = all
            .Select(d => new DiscoveryAggregator.DiscoveryFact(
                d.SystemId, d.SubjectName, d.SourceKind, d.Confidence, d.Status, d.PendingQuestions))
            .ToArray();
        return Results.Ok(DiscoveryAggregator.Summarize(facts));
    }

    private static async Task<IResult> SetDiscoveryStatusAsync(
        string id, SetDiscoveryStatusRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureHubCommandService commands, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("discovery");
        if (body is null || !ArchitectureHubKinds.IsDiscoveryStatus(body.Status))
            return Invalid("status", "Status must be open/confirmed/rejected.");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var updated = await commands.SetDiscoveryStatusAsync(profile.TenantId, id, body.Status!, token);
        return updated is null ? NotFound("discovery") : Results.Ok(ToContract(updated));
    }

    // ARC-07 -----------------------------------------------------------------------------------------

    private static async Task<IResult> GetInsightsAsync(
        string? projectId, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureReadModelService readModel, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var model = await readModel.BuildImplementedAsync(profile.TenantId, Trim(projectId), token);
        return Results.Ok(RationalizationAnalyzer.Analyze(model));
    }

    // ARC-08 -----------------------------------------------------------------------------------------

    private static async Task<IResult> CreatePatternAsync(
        CreatePatternRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureHubCommandService commands, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null) return Invalid("body", "A request body is required.");
        if (!ArchitectureHubKinds.IsPatternKind(body.Kind)) return Invalid("kind", "kind must be adr/pattern.");
        if (string.IsNullOrWhiteSpace(body.Title)) return Invalid("title", "Title is required.");
        var validStatus = body.Kind == ArchitectureHubKinds.Adr
            ? ArchitectureHubKinds.AdrStatus
            : ArchitectureHubKinds.PatternStatus;
        var status = string.IsNullOrWhiteSpace(body.Status)
            ? (body.Kind == ArchitectureHubKinds.Adr ? "proposed" : "recommended")
            : body.Status!.Trim();
        if (!validStatus.Contains(status)) return Invalid("status", $"Unknown status for {body.Kind}.");
        if (body.SupersedesId is not null && !Valid(body.SupersedesId)) return Invalid("supersedesId", "supersedesId must be a ULID.");

        var record = await commands.CreatePatternAsync(
            profile.TenantId, Trim(body.ProjectId), body.Kind!, body.Title!.Trim(), status,
            body.Context?.Trim() ?? string.Empty, body.Body?.Trim() ?? string.Empty, Trim(body.Problem),
            body.Consequences?.Trim() ?? string.Empty, Clean(body.Tags), Trim(body.SupersedesId),
            Trim(body.DocumentId), token);
        return Results.Created($"/api/v1/architecture/patterns/{record.Id}", ToContract(record));
    }

    private static async Task<IResult> ListPatternsAsync(
        string? projectId, string? kind, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (kind is not null && !ArchitectureHubKinds.IsPatternKind(kind)) return Invalid("kind", "kind must be adr/pattern.");
        var page = await store.ListPatternsAsync(
            profile.TenantId, Trim(projectId), Trim(kind), Trim(cursor), Math.Clamp(limit ?? 100, 1, 200), token);
        var contracts = page.Select(ToContract).ToArray();
        var next = contracts.Length == Math.Clamp(limit ?? 100, 1, 200) ? page[^1].Id : null;
        return Results.Ok(new ArchitecturePatternListContract(contracts.Length, next, contracts));
    }

    private static async Task<IResult> GetPatternAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("pattern");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var record = await store.GetPatternAsync(profile.TenantId, id, token);
        return record is null ? NotFound("pattern") : Results.Ok(ToContract(record));
    }

    // ARC-10 -----------------------------------------------------------------------------------------

    private static async Task<IResult> GetPortfolioReuseAsync(
        string? capability, string? domain, string? projectId, HttpRequest request,
        ILocalProfileStore profiles, ArchitectureReadModelService readModel, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        // Consulta o portfólio INTEIRO (todos os sistemas do tenant), não só o do projeto.
        var model = await readModel.BuildImplementedAsync(profile.TenantId, Trim(projectId), token);
        return Results.Ok(BaselineComparator.FindReuse(model, capability, domain));
    }

    private static async Task<IResult> CreateBaselineAsync(
        string projectId, CreateBaselineRequest? body, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureHubCommandService commands, CancellationToken token)
    {
        if (!Valid(projectId)) return InvalidId("project");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (body is null || string.IsNullOrWhiteSpace(body.Title)) return Invalid("title", "Title is required.");
        if (body.ProposalId is not null && !Valid(body.ProposalId)) return Invalid("proposalId", "proposalId must be a ULID.");
        var record = await commands.CreateBaselineAsync(
            profile.TenantId, projectId, body.Title!.Trim(), Trim(body.ProposalId), token);
        return Results.Created($"/api/v1/architecture/baselines/{record.Id}", ToContract(record));
    }

    private static async Task<IResult> ListBaselinesAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        IArchitectureStore store, CancellationToken token)
    {
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var page = await store.ListBaselinesAsync(
            profile.TenantId, Trim(projectId), Trim(cursor), Math.Clamp(limit ?? 100, 1, 200), token);
        return Results.Ok(new ArchitectureBaselineListContract(page.Count, page.Select(ToContract).ToArray()));
    }

    private static async Task<IResult> GetBaselineAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("baseline");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var record = await store.GetBaselineAsync(profile.TenantId, id, token);
        return record is null ? NotFound("baseline") : Results.Ok(ToContract(record));
    }

    private static async Task<IResult> UpdateAsBuiltAsync(
        string id, HttpRequest request, ILocalProfileStore profiles,
        ArchitectureHubCommandService commands, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("baseline");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var updated = await commands.UpdateAsBuiltAsync(profile.TenantId, id, token);
        return updated is null ? NotFound("baseline") : Results.Ok(ToContract(updated));
    }

    private static async Task<IResult> GetBaselineComparisonAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("baseline");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var record = await store.GetBaselineAsync(profile.TenantId, id, token);
        if (record is null) return NotFound("baseline");
        if (record.AsBuiltSnapshotJson is null)
            return Problem(409, "as_built_missing", "Record the production AS-IS before comparing.");
        return Results.Ok(Compare(record));
    }

    private static async Task<IResult> CloseBaselineAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IArchitectureStore store,
        ArchitectureHubCommandService commands, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("baseline");
        var profile = await Session(request, profiles, token);
        if (profile is null) return SessionRequired();
        var record = await store.GetBaselineAsync(profile.TenantId, id, token);
        if (record is null) return NotFound("baseline");
        if (record.AsBuiltSnapshotJson is null)
            return Problem(409, "as_built_missing", "Record the production AS-IS before closing.");
        await commands.CloseBaselineAsync(profile.TenantId, id, token);
        return Results.Ok(Compare(record));
    }

    // Helpers ----------------------------------------------------------------------------------------

    private static BaselineComparisonContract Compare(ArchitectureBaselineRecord record)
    {
        var baseline = JsonSerializer.Deserialize<ArchitectureModelSnapshot>(record.BaselineSnapshotJson, JsonOptions)
            ?? new ArchitectureModelSnapshot([], []);
        var asBuilt = JsonSerializer.Deserialize<ArchitectureModelSnapshot>(record.AsBuiltSnapshotJson!, JsonOptions)
            ?? new ArchitectureModelSnapshot([], []);
        return BaselineComparator.Compare(record.Id, baseline, asBuilt);
    }

    private static DiscoveryContract ToContract(ArchitectureDiscoveryRecord d) => new(
        d.Id, d.ProjectId, d.SystemId, d.SubjectName, d.SourceKind, d.Field, d.Value, d.Confidence,
        d.Evidence, d.PendingQuestions, d.Status, d.CreatedAt, d.UpdatedAt);

    private static ArchitecturePatternContract ToContract(ArchitecturePatternRecord p) => new(
        p.Id, p.ProjectId, p.Kind, p.Title, p.Status, p.Context, p.Body, p.Problem, p.Consequences,
        p.Tags, p.SupersedesId, p.DocumentId, p.CreatedAt, p.UpdatedAt);

    private static ArchitectureBaselineContract ToContract(ArchitectureBaselineRecord b)
    {
        var snapshot = JsonSerializer.Deserialize<ArchitectureModelSnapshot>(b.BaselineSnapshotJson, JsonOptions);
        return new ArchitectureBaselineContract(
            b.Id, b.ProjectId, b.Status, b.Title, b.ProposalId, snapshot?.Elements.Count ?? 0,
            b.AsBuiltSnapshotJson is not null, b.CreatedAt, b.UpdatedAt);
    }

    private static string[] Clean(IReadOnlyList<string>? values) => values is null
        ? []
        : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();

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

public sealed record CreateDiscoveryRequest(
    string? ProjectId, string? SystemId, string? SubjectName, string? SourceKind, string? Field,
    string? Value, string? Confidence, string? Evidence, IReadOnlyList<string>? PendingQuestions);

public sealed record SetDiscoveryStatusRequest(string? Status);

public sealed record CreatePatternRequest(
    string? ProjectId, string? Kind, string? Title, string? Status, string? Context, string? Body,
    string? Problem, string? Consequences, IReadOnlyList<string>? Tags, string? SupersedesId, string? DocumentId);

public sealed record CreateBaselineRequest(string? Title, string? ProposalId);
