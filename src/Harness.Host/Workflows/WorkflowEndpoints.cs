using System.Text.Json;
using Harness.Host.Profiles;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowCatalog(this IEndpointRouteBuilder endpoints)
    {
        var templates = endpoints.MapGroup("/api/v1/workflow-templates").WithTags("workflow-templates");
        templates.MapGet("/", ListTemplatesAsync).Produces<WorkflowTemplatePage>().ProducesProblem(400).ProducesProblem(401);
        templates.MapGet("/{id}", GetTemplateAsync).Produces<WorkflowTemplateContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        templates.MapPost("/", CreateTemplateAsync).Produces<WorkflowTemplateContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        templates.MapPost("/{id}/drafts", CreateDraftAsync).Produces<WorkflowVersionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        templates.MapPost("/{id}/versions", PublishVersionAsync).Produces<WorkflowVersionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        templates.MapPost("/{id}/archive", ArchiveTemplateAsync).Produces<WorkflowTemplateContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        templates.MapDelete("/{id}", DeleteTemplateAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        templates.MapPost("/{id}/duplicate", DuplicateTemplateAsync).Produces<WorkflowTemplateContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        var versions = endpoints.MapGroup("/api/v1/workflow-versions").WithTags("workflow-versions");
        versions.MapGet("/", ListVersionsAsync).Produces<WorkflowVersionPage>().ProducesProblem(400).ProducesProblem(401);
        versions.MapGet("/{id}", GetVersionAsync).Produces<WorkflowVersionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        versions.MapPatch("/{id}", UpdateDraftAsync).Produces<WorkflowVersionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        versions.MapPost("/{id}/publish", PublishDraftAsync).Produces<WorkflowVersionContract>().ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        versions.MapPost("/{id}/archive", ArchiveVersionAsync).Produces<WorkflowVersionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        versions.MapDelete("/{id}", DeleteDraftVersionAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        versions.MapPost("/{id}/duplicate", DuplicateVersionAsync).Produces<WorkflowVersionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        var workflows = endpoints.MapGroup("/api/v1/workflows").WithTags("workflows");
        workflows.MapGet("/", ListWorkflowsAsync).Produces<WorkflowPage>().ProducesProblem(400).ProducesProblem(401);
        workflows.MapGet("/{id}", GetWorkflowAsync).Produces<WorkflowContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        workflows.MapPost("/", CreateWorkflowAsync).Produces<WorkflowContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        workflows.MapPost("/{id}/operation-mode", SetOperationModeAsync).Produces<WorkflowContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        endpoints.MapPost("/api/v1/projects/{id}/workflow", LinkProjectWorkflowAsync)
            .WithTags("projects").Produces<WorkflowContract>(201).ProducesProblem(400)
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        // Fase 9: catálogo GLOBAL dos templates de documento/card do playbook (§7) — estrutura
        // semeada por migração, somente leitura em runtime.
        endpoints.MapGroup("/api/v1/workflow-document-templates").WithTags("workflows")
            .MapGet("/", async (
                Harness.Persistence.Abstractions.Workflows.IWorkflowDocumentTemplateStore store,
                CancellationToken token) =>
            {
                var items = await store.ListAsync(token);
                return Results.Ok(new WorkflowDocumentTemplatePage(
                    items.Select(item => new WorkflowDocumentTemplateContract(
                        item.Code, item.Name, item.Phase, item.TargetCardType,
                        item.RequiredFieldsJson)).ToArray()));
            }).Produces<WorkflowDocumentTemplatePage>();

        var runs = endpoints.MapGroup("/api/v1/workflow-runs").WithTags("workflow-runs");
        runs.MapGet("/", ListRunsAsync).Produces<WorkflowRunPage>().ProducesProblem(400).ProducesProblem(401);
        runs.MapGet("/{id}", GetRunAsync).Produces<WorkflowRunContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        runs.MapPost("/", CreateRunAsync).Produces<WorkflowRunContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        runs.MapPost("/{id}/transitions", TransitionRunAsync).Produces<WorkflowRunContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        runs.MapPost("/{id}/objectives", AdvanceObjectiveAsync).Produces<WorkflowRunContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        runs.MapPost("/{id}/gates", EvaluateGateAsync).Produces<WorkflowRunContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        runs.MapPost("/{id}/phases/{phaseKey}/completion", CompletePhaseAsync).Produces<WorkflowRunContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        var phases = endpoints.MapGroup("/api/v1/phases").WithTags("phases");
        phases.MapGet("/", ListPhasesAsync).Produces<PhasePage>().ProducesProblem(400).ProducesProblem(401);
        phases.MapGet("/{id}", GetPhaseAsync).Produces<PhaseContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        var gates = endpoints.MapGroup("/api/v1/gates").WithTags("gates");
        gates.MapGet("/", ListGatesAsync).Produces<GatePage>().ProducesProblem(400).ProducesProblem(401);
        gates.MapGet("/{id}", GetGateAsync).Produces<GateContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListTemplatesAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { var invalid = Page(cursor, limit); if (invalid is not null) return invalid; var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var size = limit ?? 50; var rows = await store.ListTemplatesAsync(profile.TenantId, cursor, size + 1, token); return Paged(rows, size, ToContract, (x) => x.Id, (items, next) => new WorkflowTemplatePage(items, next)); }
    private static async Task<IResult> GetTemplateAsync(string id, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var row = await store.GetTemplateAsync(profile.TenantId, id, token); return row is null ? NotFound("workflow_template") : Results.Ok(ToContract(row)); }
    private static async Task<IResult> CreateTemplateAsync(CreateWorkflowTemplateRequest input, HttpRequest request, ILocalProfileStore profiles, IWorkflowStore authority, IWorkflowCatalogStore catalog, IClock clock, CancellationToken token)
    {
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var now = clock.UtcNow;
            if (input.Phases is null || input.Phases.Count == 0)
            {
                var draft = WorkflowCatalogApplicationService.CreateDraftTemplate(
                    UlidValue.New(now).ToString(), input, now);
                var created = await catalog.CreateTemplateAsync(new(profile.TenantId,
                    draft.TemplateId, draft.Name, draft.Description, profile.Id, now), token);
                return Results.Created($"/api/v1/workflow-templates/{created.Id}", ToContract(created));
            }

            var creation = WorkflowCatalogApplicationService.CreateTemplate(UlidValue.New(now).ToString(), UlidValue.New(now.AddTicks(1)).ToString(), input, now);
            var phases = creation.Phases.Select(p => new WorkflowPhaseCreateInput(p.Id, p.Key, p.Name, p.Order, p.Objectives.Select(o => new WorkflowObjectiveCreateInput(o.Id, o.Key, o.Name, o.Kind, o.Weight)).ToArray(), p.Gates.Select(g => new WorkflowGateCreateInput(g.Id, g.ObjectiveId, g.Key, g.Name, g.MinimumRequiredState, g.RequiredObjectiveIds)).ToArray())).ToArray();
            await authority.CreatePublishedDefinitionAsync(new(profile.TenantId, creation.TemplateId, creation.Name, creation.VersionId, 1, WorkflowDefinitionContentHash.Compute(phases), phases, $"api:workflow-template:{creation.TemplateId}", now, creation.Description), token);
            var row = await catalog.GetTemplateAsync(profile.TenantId, creation.TemplateId, token) ?? throw new InvalidOperationException("Created template was not readable.");
            return Results.Created($"/api/v1/workflow-templates/{row.Id}", ToContract(row));
        }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_template", e.Message); }
        catch (WorkflowTemplateAlreadyExistsException) { return Problem(409, "workflow_template_conflict", "The workflow template already exists."); }
        catch (Exception e) when (e.GetType().Name == "IdempotencyConflictException") { return Problem(409, "workflow_template_conflict", e.Message); }
    }

    private static async Task<IResult> ListVersionsAsync(string? templateId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { var invalid = Page(cursor, limit, templateId); if (invalid is not null) return invalid; var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var size = limit ?? 100; var rows = await store.ListVersionsAsync(profile.TenantId, templateId, cursor, size + 1, token); return Paged(rows, size, ToContract, x => x.Id, (items, next) => new WorkflowVersionPage(items, next)); }
    private static async Task<IResult> GetVersionAsync(string id, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var row = await store.GetVersionAsync(profile.TenantId, id, token); return row is null ? NotFound("workflow_version") : Results.Ok(ToContract(row)); }

    private static async Task<IResult> CreateDraftAsync(string id, WorkflowDraftRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var template = await store.GetTemplateAsync(profile.TenantId, id, token);
        if (template is null) return NotFound("workflow_template");
        try
        {
            WorkflowVersionCatalogRecord? current = null;
            if (template.CurrentVersionId is not null)
                current = await store.GetVersionAsync(profile.TenantId, template.CurrentVersionId, token);
            var effective = new WorkflowDraftRequest(
                input.Phases ?? current?.Phases,
                input.GatesByPhase ?? current?.GatesByPhase,
                input.PhaseConfigs ?? (current is null ? null : JsonSerializer.Deserialize<Dictionary<string, WorkflowPhaseConfigContract>>(current.PhaseConfigsJson)),
                input.DefaultOperationMode ?? current?.DefaultOperationMode,
                input.Transitions ?? (current is null ? null : JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<string>>>(current.TransitionsJson)),
                input.Changelog ?? current?.Changelog);
            var now = clock.UtcNow; var versionId = UlidValue.New(now).ToString();
            var value = WorkflowCatalogApplicationService.CreateDraftVersion(id, versionId, effective, now);
            var row = await store.CreateDraftAsync(new(profile.TenantId, id, versionId,
                ToPersistence(value.Hierarchy.Phases), JsonSerializer.Serialize(value.PhaseConfigs),
                value.DefaultOperationMode, JsonSerializer.Serialize(value.Transitions),
                value.Hierarchy.Changelog, now), token);
            return Results.Created($"/api/v1/workflow-versions/{versionId}", ToContract(row));
        }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_draft", e.Message); }
    }

    private static async Task<IResult> UpdateDraftAsync(string id, WorkflowDraftRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var current = await store.GetVersionAsync(profile.TenantId, id, token);
        if (current is null) return NotFound("workflow_version");
        try
        {
            var effective = MergeDraft(input, current); var now = clock.UtcNow;
            var value = WorkflowCatalogApplicationService.CreateDraftVersion(
                current.TemplateId, id, effective, now);
            var row = await store.UpdateDraftAsync(new(profile.TenantId, id,
                ToPersistence(value.Hierarchy.Phases), JsonSerializer.Serialize(value.PhaseConfigs),
                value.DefaultOperationMode, JsonSerializer.Serialize(value.Transitions),
                value.Hierarchy.Changelog, now), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_draft", e.Message); }
    }

    private static async Task<IResult> PublishDraftAsync(string id, PublishWorkflowDraftRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var current = await store.GetVersionAsync(profile.TenantId, id, token);
        if (current is null) return NotFound("workflow_version");
        try
        {
            var now = clock.UtcNow;
            _ = WorkflowCatalogApplicationService.CreateVersion(current.TemplateId,
                UlidValue.New(now).ToString(), new PublishWorkflowVersionRequest(
                    current.Phases, current.GatesByPhase,
                    JsonSerializer.Deserialize<Dictionary<string, WorkflowPhaseConfigContract>>(current.PhaseConfigsJson),
                    current.DefaultOperationMode,
                    JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<string>>>(current.TransitionsJson),
                    input.Changelog ?? current.Changelog), now);
            var row = await store.PublishDraftAsync(new(profile.TenantId, id,
                input.Changelog ?? current.Changelog, now), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
        catch (ArgumentException e) { return Problem(422, "workflow_validation_failed", e.Message); }
    }

    private static WorkflowDraftRequest MergeDraft(WorkflowDraftRequest input,
        WorkflowVersionCatalogRecord current) => new(
            input.Phases ?? current.Phases,
            input.GatesByPhase ?? current.GatesByPhase,
            input.PhaseConfigs ?? JsonSerializer.Deserialize<Dictionary<string, WorkflowPhaseConfigContract>>(current.PhaseConfigsJson),
            input.DefaultOperationMode ?? current.DefaultOperationMode,
            input.Transitions ?? JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<string>>>(current.TransitionsJson),
            input.Changelog ?? current.Changelog);

    private static async Task<IResult> PublishVersionAsync(string id, PublishWorkflowVersionRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        if (await store.GetTemplateAsync(profile.TenantId, id, token) is null) return NotFound("workflow_template");
        try
        {
            var now = clock.UtcNow; var versionId = UlidValue.New(now).ToString();
            var value = WorkflowCatalogApplicationService.CreateVersion(id, versionId, input, now);
            var phases = ToPersistence(value.Hierarchy.Phases);
            var row = await store.PublishVersionAsync(new(profile.TenantId, id, versionId, phases,
                JsonSerializer.Serialize(value.PhaseConfigs), value.DefaultOperationMode,
                JsonSerializer.Serialize(value.Transitions), value.Hierarchy.Changelog, now), token);
            return Results.Created($"/api/v1/workflow-versions/{versionId}", ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_version", e.Message); }
    }

    private static async Task<IResult> ArchiveTemplateAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var row = await store.ArchiveTemplateAsync(new(profile.TenantId, id, profile.Id,
                clock.UtcNow), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
    }

    private static async Task<IResult> ArchiveVersionAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var row = await store.ArchiveVersionAsync(new(profile.TenantId, id, profile.Id,
                clock.UtcNow), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
    }

    private static async Task<IResult> DeleteDraftVersionAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            await store.DeleteDraftVersionAsync(new(profile.TenantId, id, profile.Id, clock.UtcNow), token);
            return Results.NoContent();
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
    }

    private static async Task<IResult> DeleteTemplateAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            await store.DeleteTemplateAsync(new(profile.TenantId, id, profile.Id, clock.UtcNow), token);
            return Results.NoContent();
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
    }

    private static async Task<IResult> DuplicateVersionAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var source = await store.GetVersionAsync(profile.TenantId, id, token);
        if (source is null) return NotFound("workflow_version");
        try
        {
            var now = clock.UtcNow; var versionId = UlidValue.New(now).ToString();
            var value = WorkflowCatalogApplicationService.CreateDraftVersion(source.TemplateId,
                versionId, CopyAsDraft(source), now);
            var row = await store.CreateDraftAsync(new(profile.TenantId, source.TemplateId,
                versionId, ToPersistence(value.Hierarchy.Phases),
                JsonSerializer.Serialize(value.PhaseConfigs), value.DefaultOperationMode,
                JsonSerializer.Serialize(value.Transitions), null, now), token);
            return Results.Created($"/api/v1/workflow-versions/{versionId}", ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_draft", e.Message); }
    }

    private static async Task<IResult> DuplicateTemplateAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var source = await store.GetTemplateAsync(profile.TenantId, id, token);
        if (source is null) return NotFound("workflow_template");
        try
        {
            var now = clock.UtcNow; var templateId = UlidValue.New(now).ToString();
            WorkflowVersionDraftCreateCommand? draftCommand = null;
            if (source.CurrentVersionId is not null)
            {
                var current = await store.GetVersionAsync(profile.TenantId, source.CurrentVersionId, token)
                    ?? throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
                var versionId = UlidValue.New(now.AddTicks(1)).ToString();
                var draft = WorkflowCatalogApplicationService.CreateDraftVersion(templateId,
                    versionId, CopyAsDraft(current), now);
                draftCommand = new(profile.TenantId, templateId, versionId,
                    ToPersistence(draft.Hierarchy.Phases), JsonSerializer.Serialize(draft.PhaseConfigs),
                    draft.DefaultOperationMode, JsonSerializer.Serialize(draft.Transitions), null, now);
            }
            const string suffix = " (cópia)";
            var baseName = source.Name.Length > 200 - suffix.Length
                ? source.Name[..(200 - suffix.Length)]
                : source.Name;
            var row = await store.DuplicateTemplateAsync(new(profile.TenantId, source.Id,
                source.CurrentVersionId, templateId, baseName + suffix, source.Description,
                profile.Id, draftCommand, now), token);
            return Results.Created($"/api/v1/workflow-templates/{templateId}", ToContract(row));
        }
        catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); }
        catch (WorkflowCatalogLifecycleException e) { return Problem(409, "workflow_lifecycle_conflict", e.Message); }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_template", e.Message); }
    }

    private static WorkflowDraftRequest CopyAsDraft(WorkflowVersionCatalogRecord source) => new(
        source.Phases, source.GatesByPhase,
        JsonSerializer.Deserialize<Dictionary<string, WorkflowPhaseConfigContract>>(source.PhaseConfigsJson),
        source.DefaultOperationMode,
        JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<string>>>(source.TransitionsJson),
        null);

    private static async Task<IResult> ListWorkflowsAsync(string? projectId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { var invalid = Page(cursor, limit, projectId); if (invalid is not null) return invalid; var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var size = limit ?? 100; var rows = await store.ListBindingsAsync(profile.TenantId, projectId, cursor, size + 1, token); return Paged(rows, size, ToContract, x => x.Id, (items, next) => new WorkflowPage(items, next)); }
    private static async Task<IResult> GetWorkflowAsync(string id, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var row = await store.GetBindingAsync(profile.TenantId, id, token); return row is null ? NotFound("workflow") : Results.Ok(ToContract(row)); }
    private static async Task<IResult> CreateWorkflowAsync(CreateWorkflowRequest input, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock, CancellationToken token)
    { var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); try { var value = WorkflowCatalogApplicationService.CreateBinding(input); var version = value.VersionId ?? (await store.GetTemplateAsync(profile.TenantId, value.TemplateId, token))?.CurrentVersionId ?? throw new WorkflowCatalogReferenceNotFoundException("workflow_version"); var now = clock.UtcNow; var id = UlidValue.New(now).ToString(); var row = await store.CreateBindingAsync(new(profile.TenantId, id, value.ProjectId, value.TemplateId, version, value.OperationMode, value.PauseGates, profile.Id, UlidValue.New(now.AddTicks(1)).ToString(), value.RiskAcceptanceNote, now), token); return Results.Created($"/api/v1/workflows/{id}", ToContract(row)); } catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); } catch (WorkflowBindingAlreadyExistsException) { return Problem(409, "workflow_already_exists", "The project already has a workflow."); } catch (ArgumentException e) { return Problem(400, "invalid_workflow", e.Message); } }

    private static async Task<IResult> LinkProjectWorkflowAsync(string id,
        LinkWorkflowTemplateRequest input, HttpRequest request, ILocalProfileStore profiles,
        IWorkflowCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!Valid(id) || !Valid(input.TemplateId) ||
            (input.VersionId is not null && !Valid(input.VersionId))) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var template = await store.GetTemplateAsync(profile.TenantId, input.TemplateId, token);
        if (template is null) return NotFound("workflow_template");
        var result = await ProjectWorkflowLinker.LinkAsync(store, profile.TenantId, id, template,
            input.VersionId, profile.Id, clock, token);
        return result.Outcome == ProjectWorkflowLinker.LinkOutcome.Applied
            ? Results.Created($"/api/v1/workflows/{result.Binding!.Id}", ToContract(result.Binding))
            : ProjectWorkflowLinker.ToProblem(result);
    }

    private static async Task<IResult> SetOperationModeAsync(string id, SetWorkflowOperationModeRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var current = await store.GetBindingAsync(profile.TenantId, id, token); if (current is null) return NotFound("workflow");
        try
        {
            var value = WorkflowCatalogApplicationService.SetOperationMode(id, current.ProjectId,
                current.TemplateId, current.ActiveVersionId, input); var now = clock.UtcNow;
            var row = await store.SetOperationModeAsync(new(profile.TenantId, id, value.OperationMode,
                value.PauseGates, UlidValue.New(now).ToString(), profile.Id,
                value.RiskAcceptanceNote, now), token); return Results.Ok(ToContract(row));
        }
        catch (ArgumentException e) { return Problem(400, "invalid_operation_mode", e.Message); }
    }

    private static async Task<IResult> ListRunsAsync(string? workflowId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { var invalid = Page(cursor, limit, workflowId); if (invalid is not null) return invalid; var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var size = limit ?? 100; var rows = await store.ListRunsAsync(profile.TenantId, workflowId, cursor, size + 1, token); return Paged(rows, size, ToContract, x => x.Id, (items, next) => new WorkflowRunPage(items, next)); }
    private static async Task<IResult> GetRunAsync(string id, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var row = await store.GetRunAsync(profile.TenantId, id, token); return row is null ? NotFound("workflow_run") : Results.Ok(ToContract(row)); }
    private static async Task<IResult> CreateRunAsync(CreateWorkflowRunRequest input, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore catalog, IWorkflowStore authority, IClock clock, CancellationToken token)
    { var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); try { var workflowId = WorkflowCatalogApplicationService.WorkflowId(input); var workflow = await catalog.GetBindingAsync(profile.TenantId, workflowId, token) ?? throw new WorkflowCatalogReferenceNotFoundException("workflow"); var now = clock.UtcNow; var runId = UlidValue.New(now).ToString(); var created = await authority.CreateRunAsync(new(profile.TenantId, workflow.ProjectId, workflow.ActiveVersionId, runId, $"api:workflow-run:{runId}", now, workflowId), token); var started = await authority.TransitionRunAsync(new(profile.TenantId, runId, WorkflowRunTransition.Start, created.RunVersion, $"api:workflow-run:start:{runId}", now.AddTicks(1)), token); if (started.Status != WorkflowRunMutationStatus.Applied) throw new InvalidOperationException($"Workflow run start failed: {started.Status}."); var row = await catalog.GetRunAsync(profile.TenantId, runId, token) ?? throw new InvalidOperationException("Created run was not readable."); return Results.Created($"/api/v1/workflow-runs/{runId}", ToContract(row)); } catch (WorkflowCatalogReferenceNotFoundException e) { return NotFound(e.Reference); } catch (ArgumentException e) { return Problem(400, "invalid_workflow_run", e.Message); } catch (InvalidOperationException e) { return Problem(409, "workflow_run_conflict", e.Message); } }

    private static async Task<IResult> TransitionRunAsync(string id, TransitionWorkflowRunRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore catalog,
        IWorkflowStore authority, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var transition = WorkflowCatalogApplicationService.RunTransition(input) switch
            {
                "pause" => WorkflowRunTransition.Pause,
                "resume" => WorkflowRunTransition.Resume,
                "cancel" => WorkflowRunTransition.Cancel,
                _ => throw new InvalidOperationException()
            };
            var current = await authority.ReadRunAsync(profile.TenantId, id, token); if (current is null) return NotFound("workflow_run");
            var result = await authority.TransitionRunAsync(new(profile.TenantId, id, transition,
                current.Version, $"api:workflow-run:{input.Transition}:{UlidValue.New(clock.UtcNow)}", clock.UtcNow), token);
            return await MutationResultAsync(result, profile.TenantId, id, catalog, token);
        }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_transition", e.Message); }
    }

    private static async Task<IResult> AdvanceObjectiveAsync(string id, AdvanceWorkflowObjectiveRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore catalog,
        IWorkflowStore authority, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var value = WorkflowCatalogApplicationService.AdvanceObjective(input);
            var current = await authority.ReadRunAsync(profile.TenantId, id, token); if (current is null) return NotFound("workflow_run"); var now = clock.UtcNow;
            var result = await authority.AdvanceObjectiveAsync(new(profile.TenantId, id, value.PhaseKey,
                value.ObjectiveKey, value.TargetState, current.Version, $"api:workflow-objective:{UlidValue.New(now)}", now), token);
            return await MutationResultAsync(result, profile.TenantId, id, catalog, token);
        }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_objective", e.Message); }
    }

    private static async Task<IResult> EvaluateGateAsync(string id, EvaluateWorkflowGateRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore catalog,
        IWorkflowStore authority, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var value = WorkflowCatalogApplicationService.EvaluateGate(input);
            var current = await authority.ReadRunAsync(profile.TenantId, id, token); if (current is null) return NotFound("workflow_run"); var now = clock.UtcNow;
            var result = await authority.EvaluateGateAsync(new(profile.TenantId, id, value.PhaseKey,
                value.GateKey, value.Passed, current.Version, $"api:workflow-gate:{UlidValue.New(now)}", now,
                profile.Id, value.Note), token);
            return await MutationResultAsync(result, profile.TenantId, id, catalog, token);
        }
        catch (ArgumentException e) { return Problem(400, "invalid_workflow_gate", e.Message); }
    }

    private static async Task<IResult> CompletePhaseAsync(string id, string phaseKey,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore catalog,
        IWorkflowStore authority, IClock clock, CancellationToken token)
    {
        if (!Valid(id) || string.IsNullOrWhiteSpace(phaseKey)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var current = await authority.ReadRunAsync(profile.TenantId, id, token); if (current is null) return NotFound("workflow_run"); var now = clock.UtcNow;
        var result = await authority.CompletePhaseAsync(new(profile.TenantId, id, phaseKey,
            current.Version, $"api:workflow-phase:{UlidValue.New(now)}", now), token);
        return await MutationResultAsync(result, profile.TenantId, id, catalog, token);
    }

    private static async Task<IResult> MutationResultAsync(WorkflowRunMutationReceipt result,
        string tenantId, string runId, IWorkflowCatalogStore catalog, CancellationToken token)
    {
        if (result.Status == WorkflowRunMutationStatus.NotFound) return NotFound("workflow_run");
        if (result.Status is not (WorkflowRunMutationStatus.Applied or WorkflowRunMutationStatus.IdempotentReplay))
            return Problem(409, "workflow_mutation_conflict", $"Workflow command was rejected: {result.Status}.");
        var row = await catalog.GetRunAsync(tenantId, runId, token) ?? throw new InvalidOperationException("Mutated run was not readable.");
        return Results.Ok(ToContract(row));
    }

    private static async Task<IResult> ListPhasesAsync(string? runId, string? cursor, int? limit,
        HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store,
        IWorkflowStore authority, CancellationToken token)
    {
        var invalid = Page(cursor, limit, runId);
        if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token);
        if (profile is null) return Unauthorized();
        var size = limit ?? 100;
        var rows = await store.ListPhasesAsync(
            profile.TenantId, runId, cursor, size + 1, token);
        var run = runId is null
            ? null
            : await authority.ReadRunAggregateAsync(profile.TenantId, runId, token);
        return Paged(
            rows,
            size,
            row => ToContract(row, run),
            row => row.Id,
            (items, next) => new PhasePage(items, next));
    }

    private static async Task<IResult> GetPhaseAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkflowCatalogStore store, IWorkflowStore authority,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token);
        if (profile is null) return Unauthorized();
        var row = await store.GetPhaseAsync(profile.TenantId, id, token);
        if (row is null) return NotFound("phase");
        var run = await authority.ReadRunAggregateAsync(profile.TenantId, row.RunId, token);
        return Results.Ok(ToContract(row, run));
    }
    private static async Task<IResult> ListGatesAsync(string? runId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { var invalid = Page(cursor, limit, runId); if (invalid is not null) return invalid; var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var size = limit ?? 100; var rows = await store.ListGatesAsync(profile.TenantId, runId, cursor, size + 1, token); return Paged(rows, size, ToContract, x => x.Id, (items, next) => new GatePage(items, next)); }
    private static async Task<IResult> GetGateAsync(string id, HttpRequest request, ILocalProfileStore profiles, IWorkflowCatalogStore store, CancellationToken token)
    { if (!Valid(id)) return InvalidId(); var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized(); var row = await store.GetGateAsync(profile.TenantId, id, token); return row is null ? NotFound("gate") : Results.Ok(ToContract(row)); }

    private static IResult Paged<TRecord, TContract, TPage>(IReadOnlyList<TRecord> rows, int size, Func<TRecord, TContract> map, Func<TRecord, string> id, Func<IReadOnlyList<TContract>, string?, TPage> page)
    { var more = rows.Count > size; var selected = rows.Take(size).ToArray(); var items = selected.Select(map).ToArray(); return Results.Ok(page(items, more ? id(selected[^1]) : null)); }
    private static IResult? Page(string? cursor, int? limit, params string?[] filters) => ((cursor is not null && !Valid(cursor)) || limit is < 1 or > 200 || filters.Any(x => x is not null && !Valid(x))) ? Problem(400, "invalid_cursor", "Filter, cursor, or limit is invalid.") : null;
    private static Task<LocalProfileRecord?> Session(HttpRequest request, ILocalProfileStore profiles, CancellationToken token) => LocalProfileSession.ResolveAsync(request, profiles, token);
    private static bool Valid(string id) => UlidValue.TryParse(id, out _); private static IResult InvalidId() => Problem(400, "invalid_id", "ID must be a ULID."); private static IResult Unauthorized() => Problem(401, "local_session_required", "A local profile session is required."); private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist."); private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
    private static WorkflowTemplateContract ToContract(WorkflowTemplateCatalogRecord x) => new(x.Id, x.Name, x.Description, x.CurrentVersionId, x.State, x.ArchivedAt, x.CreatedAt);
    private static WorkflowVersionContract ToContract(WorkflowVersionCatalogRecord x) => new(x.Id, x.TemplateId, x.Version, x.Phases, x.GatesByPhase,
        JsonSerializer.Deserialize<Dictionary<string, WorkflowPhaseConfigContract>>(x.PhaseConfigsJson) ?? [],
        x.DefaultOperationMode, JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<string>>>(x.TransitionsJson) ?? [], x.Changelog, x.State, x.PublishedAt, x.ArchivedAt);
    private static WorkflowContract ToContract(WorkflowBindingCatalogRecord x) => new(x.Id, x.ProjectId, x.TemplateId, x.ActiveVersionId, x.OperationMode, x.SemiautonomousPauseGates, x.RiskAcceptances.Select(a => new WorkflowRiskAcceptanceContract(a.Mode, a.AcceptedByProfileId, a.Note, a.AcceptedAt)).ToArray(), x.CreatedAt);
    private static WorkflowRunContract ToContract(WorkflowRunCatalogRecord x) => new(x.Id, x.WorkflowId, x.VersionId, x.State, x.StartedAt, x.FinishedAt);
    private static PhaseContract ToContract(
        WorkflowPhaseCatalogRecord row,
        WorkflowRunAggregateSnapshot? run = null)
    {
        var phase = run?.Phases.FirstOrDefault(item => item.PhaseRunId == row.Id);
        if (phase is null)
        {
            return new(
                row.Id,
                row.RunId,
                row.Name,
                row.Order,
                row.State,
                row.StartedAt,
                row.FinishedAt,
                new(
                    0,
                    0,
                    0m,
                    "workflow_run_unavailable",
                    row.FinishedAt ?? row.StartedAt,
                    new(0, 0),
                    new(0, 0),
                    new(0, 0)),
                []);
        }

        var documentObjectives = phase.Objectives
            .Where(objective => objective.Kind == "document")
            .ToArray();
        var taskObjectives = phase.Objectives
            .Where(objective => objective.Kind != "document" && objective.Kind != "gate")
            .ToArray();
        var taskProgress = Breakdown(taskObjectives);
        var documentProgress = Breakdown(documentObjectives);
        var gateProgress = new PhaseProgressBreakdownContract(
            phase.Gates.Count(gate => gate.State == "passed"),
            phase.Gates.Count);
        var total = taskProgress.Total + documentProgress.Total + gateProgress.Total;
        var completed =
            taskProgress.Completed + documentProgress.Completed + gateProgress.Completed;
        var percent = total == 0
            ? row.State is "completed" or "skipped" ? 100m : 0m
            : Math.Round(completed * 100m / total, 2, MidpointRounding.AwayFromZero);
        var updatedAt = phase.Objectives
            .Select(objective => (DateTimeOffset?)objective.UpdatedAt)
            .Concat(phase.Gates.Select(gate => gate.EvaluatedAt))
            .Append(row.FinishedAt)
            .Append(row.StartedAt)
            .Where(value => value.HasValue)
            .Max();
        var deliverables = documentObjectives
            .Select(objective => new PhaseDeliverableContract(
                objective.Name,
                DeliverableStatus(objective.State, row.State)))
            .ToArray();

        return new(
            row.Id,
            row.RunId,
            row.Name,
            row.Order,
            row.State,
            row.StartedAt,
            row.FinishedAt,
            new(
                completed,
                total,
                percent,
                "workflow_run_objectives_and_gates",
                updatedAt,
                taskProgress,
                documentProgress,
                gateProgress),
            deliverables);
    }

    private static PhaseProgressBreakdownContract Breakdown(
        WorkflowObjectiveRunSnapshot[] objectives) => new(
        objectives.Count(objective => objective.State == "approved"),
        objectives.Length);

    private static string DeliverableStatus(string objectiveState, string phaseState) =>
        objectiveState switch
        {
            "approved" => "approved",
            "validated" => "inReview",
            "executed" => "inProduction",
            _ when phaseState == "pending" => "planned",
            _ => "notStarted",
        };
    private static GateContract ToContract(WorkflowGateCatalogRecord x) => new(x.Id, x.PhaseId, x.RunId, x.Name, x.State, x.RequiresApproval, x.DecidedByProfileId, x.DecidedAt, x.Note);
    private static WorkflowPhaseCreateInput[] ToPersistence(IReadOnlyList<WorkflowApiPhaseCreation> phases) =>
        phases.Select(p => new WorkflowPhaseCreateInput(p.Id, p.Key, p.Name, p.Order,
            p.Objectives.Select(o => new WorkflowObjectiveCreateInput(o.Id, o.Key, o.Name, o.Kind, o.Weight)).ToArray(),
            p.Gates.Select(g => new WorkflowGateCreateInput(g.Id, g.ObjectiveId, g.Key, g.Name,
                g.MinimumRequiredState, g.RequiredObjectiveIds)).ToArray())).ToArray();
}

public sealed record WorkflowTemplatePage(IReadOnlyList<WorkflowTemplateContract> Items, string? NextCursor);
public sealed record WorkflowVersionPage(IReadOnlyList<WorkflowVersionContract> Items, string? NextCursor);
public sealed record WorkflowPage(IReadOnlyList<WorkflowContract> Items, string? NextCursor);
public sealed record WorkflowRunPage(IReadOnlyList<WorkflowRunContract> Items, string? NextCursor);
public sealed record PhasePage(IReadOnlyList<PhaseContract> Items, string? NextCursor);
public sealed record GatePage(IReadOnlyList<GateContract> Items, string? NextCursor);

public sealed record WorkflowDocumentTemplateContract(
    string Code, string Name, string Phase, string TargetCardType, string RequiredFieldsJson);

public sealed record WorkflowDocumentTemplatePage(
    IReadOnlyList<WorkflowDocumentTemplateContract> Items);
