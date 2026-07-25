using Harness.Host.Profiles;
using Harness.Host.Workflows;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Domain;
using Harness.Modules.Projects.Application;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Projects;

public static class ProjectEndpoints
{
    private static readonly Action<ILogger, string, Exception?> WorkflowlessProjectCreated =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1012, nameof(WorkflowlessProjectCreated)),
            "RN-02: projeto {ProjectId} criado sem workflow — nenhum template recomendado publicável " +
            "disponível; a convergência de startup vinculará quando existir.");

    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/projects").WithTags("projects");
        group.MapGet("/", ListAsync).Produces<ProjectPage>().ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/{projectId}", GetAsync).Produces<ProjectResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/", CreateAsync).Produces<ProjectResponse>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{projectId}/logo", UploadLogoAsync).Produces<ProjectResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        group.MapGet("/{projectId}/logo-assets/{assetId}", GetLogoAsync).Produces(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPatch("/{projectId}", PatchAsync).Produces<ProjectResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        group.MapDelete("/{projectId}", DeleteAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapGet("/{projectId}/status-digest", GetStatusDigestAsync)
            .Produces<ProjectStatusDigestContract>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        return endpoints;
    }

    private static readonly HashSet<string> LogoExtensions =
        new([".png", ".jpg", ".jpeg"], StringComparer.OrdinalIgnoreCase);

    private static async Task<IResult> UploadLogoAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore store,
        SolicitationAttachmentStorage storage,
        IAuditEventStore audit,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var current = await store.GetAsync(profile.TenantId, projectId, token);
        if (current is null) return NotFound();
        if (!request.HasFormContentType)
            return Problem(400, "multipart_required", "The logo upload requires multipart/form-data.");
        var form = await request.ReadFormAsync(token);
        var file = form.Files.Count == 1 ? form.Files[0] : null;
        if (file is null)
            return Problem(400, "single_file_required", "Exactly one logo file is required.");
        if (file.Length > AttachmentIngestPolicy.MaximumSizeBytes)
            return Problem(400, "size_exceeded", $"The logo exceeds {AttachmentIngestPolicy.MaximumSizeBytes} bytes.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, token);
        var content = new ReadOnlyMemory<byte>(buffer.ToArray());
        var decision = AttachmentIngestPolicy.Evaluate(
            new AttachmentIngestRequest(file.FileName, file.ContentType, content));
        if (decision.Accepted && !LogoExtensions.Contains(Path.GetExtension(file.FileName)))
            decision = AttachmentIngestDecision.Deny("unsupported_type", "Project logos accept PNG or JPEG images.");
        if (!decision.Accepted) return Problem(400, decision.Code, decision.Detail);

        var occurredAt = clock.UtcNow;
        var assetId = UlidValue.New(occurredAt).ToString();
        await storage.SaveAsync(profile.TenantId, assetId, content, token);
        var logoUrl = $"/api/v1/projects/{projectId}/logo-assets/{assetId}";
        var patch = new UpdateProjectRequest
        {
            Brand = new ProjectBrandContract(
                logoUrl,
                current.Brand.PrimaryColor,
                current.Brand.SecondaryColor,
                current.Brand.Typography)
        };
        var updated = ProjectApplicationService.Patch(ToContract(current), patch, occurredAt);
        var result = await store.UpdateAsync(new(ToRecord(profile.TenantId, updated), current.Version), token);
        if (result.Status != ProjectMutationStatus.Applied)
            return result.Status == ProjectMutationStatus.VersionConflict
                ? Problem(409, "project_version_conflict", "The project changed concurrently.")
                : NotFound();
        await audit.AppendAsync(new(
            profile.TenantId,
            "user",
            profile.Id,
            "project.logoUpdated",
            "project",
            projectId,
            $"Project logo stored as managed asset {assetId}.",
            occurredAt), token);
        return Results.Ok(ToResponse(result.Project!));
    }

    private static async Task<IResult> GetLogoAsync(
        string projectId,
        string assetId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore store,
        SolicitationAttachmentStorage storage,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _) || !UlidValue.TryParse(assetId, out _))
            return Problem(400, "invalid_logo_asset_id", "Project and asset IDs must be ULIDs.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (await store.GetAsync(profile.TenantId, projectId, token) is null) return NotFound();
        var path = storage.Resolve(Path.Combine(profile.TenantId, assetId));
        if (!File.Exists(path)) return Problem(404, "logo_asset_not_found", "The logo asset does not exist.");
        var header = new byte[8];
        await using (var stream = File.OpenRead(path))
            _ = await stream.ReadAsync(header, token);
        var contentType = header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? "image/png"
            : header[0] == 0xFF && header[1] == 0xD8
                ? "image/jpeg"
                : null;
        return contentType is null
            ? Problem(404, "logo_asset_invalid", "The managed logo is not a supported image.")
            : Results.File(path, contentType);
    }

    private static async Task<IResult> ListAsync(string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IProjectStore store, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 50; if (size is < 1 or > 200 || (cursor is not null && !UlidValue.TryParse(cursor, out _))) return Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
        var records = await store.ListAsync(profile.TenantId, cursor, size + 1, token); var more = records.Count > size; var items = records.Take(size).Select(ToResponse).ToArray(); return Results.Ok(new ProjectPage(items, more ? items[^1].Id : null));
    }
    private static async Task<IResult> GetAsync(string projectId, HttpRequest request, ILocalProfileStore profiles, IProjectStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId(); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); var project = await store.GetAsync(profile.TenantId, projectId, token); return project is null ? NotFound() : Results.Ok(ToResponse(project));
    }
    private static async Task<IResult> CreateAsync(CreateProjectRequest request, HttpRequest http, ILocalProfileStore profiles, IProjectStore store, IWorkflowCatalogStore workflows, WorkflowTemplateSeeder seeder, IClock clock, ILoggerFactory loggers, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(http, profiles, token); if (profile is null) return SessionRequired();
        ArgumentNullException.ThrowIfNull(request);
        // RN-02: é impossível um projeto sem workflow — não há opt-out. Um ULID seleciona um template
        // específico (override); ausência OU string vazia caem no template recomendado publicado.
        // Resolvemos o template ANTES de criar o projeto para que um override inválido falhe rápido,
        // sem deixar um projeto órfão.
        if (request.WorkflowTemplateId is { Length: > 0 } && !UlidValue.TryParse(request.WorkflowTemplateId, out _))
            return Problem(400, "invalid_workflow_template_id", "Workflow template ID must be a ULID.");
        try
        {
            var template = request.WorkflowTemplateId is { Length: > 0 } overrideId
                ? await workflows.GetTemplateAsync(profile.TenantId, overrideId, token)
                : await ProjectWorkflowLinker.ResolveRecommendedAsync(workflows, seeder, profile.TenantId, token);
            if (request.WorkflowTemplateId is { Length: > 0 } && template is null)
                return Problem(404, "workflow_template_not_found", "The workflow template does not exist.");

            var now = clock.UtcNow;
            var value = ProjectApplicationService.Create(UlidValue.New(now).ToString(), UlidValue.New(now.AddTicks(1)).ToString(), profile.Id, request, now);
            var record = ToRecord(profile.TenantId, value);
            var result = await store.CreateAsync(new(profile.TenantId, record, now), token);
            if (result.Status != ProjectMutationStatus.Applied)
            {
                return result.Status switch
                {
                    ProjectMutationStatus.OrganizationNotFound => Problem(404, "organization_not_found", "The organization does not exist."),
                    ProjectMutationStatus.AlreadyExists => Conflict(),
                    _ => throw new InvalidOperationException($"Unexpected project create status {result.Status}.")
                };
            }

            if (template is not null)
            {
                var link = await ProjectWorkflowLinker.LinkAsync(workflows, profile.TenantId, value.Id, template, null, profile.Id, clock, token);
                if (link.Outcome is not (ProjectWorkflowLinker.LinkOutcome.Applied or ProjectWorkflowLinker.LinkOutcome.AlreadyExists))
                    return ProjectWorkflowLinker.ToProblem(link);
            }
            else
            {
                // RN-02: nenhum template recomendado publicável existe (nem após semear os canônicos).
                // Não quebramos a criação — log honesto; a convergência de startup vincula quando existir.
                WorkflowlessProjectCreated(loggers.CreateLogger("Harness.Host.Projects.ProjectEndpoints"), value.Id, null);
            }

            return Results.Created($"/api/v1/projects/{value.Id}", ToResponse(result.Project!));
        }
        catch (ArgumentException e) { return Problem(400, "invalid_project", e.Message); }
    }
    private static async Task<IResult> PatchAsync(string projectId, UpdateProjectRequest patch, HttpRequest request, ILocalProfileStore profiles, IProjectStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId(); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); var current = await store.GetAsync(profile.TenantId, projectId, token); if (current is null) return NotFound();
        try { var updated = ProjectApplicationService.Patch(ToContract(current), patch, clock.UtcNow); var result = await store.UpdateAsync(new(ToRecord(profile.TenantId, updated), current.Version), token); return result.Status switch { ProjectMutationStatus.Applied => Results.Ok(ToResponse(result.Project!)), ProjectMutationStatus.AlreadyExists => Conflict(), ProjectMutationStatus.NotFound => NotFound(), ProjectMutationStatus.VersionConflict => Problem(409, "project_version_conflict", "The project changed concurrently."), _ => throw new InvalidOperationException($"Unexpected project update status {result.Status}.") }; } catch (ArgumentException e) { return Problem(400, "invalid_project", e.Message); }
    }
    private static async Task<IResult> DeleteAsync(string projectId, HttpRequest request, ILocalProfileStore profiles, IProjectStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return InvalidId(); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired(); if (profile.Role != LocalProfileRole.Admin) return Problem(403, "admin_required", "Only a tenant admin can delete projects."); var current = await store.GetAsync(profile.TenantId, projectId, token); if (current is null) return NotFound(); var result = await store.DeleteAsync(profile.TenantId, projectId, current.Version, clock.UtcNow, token); return result.Status switch { ProjectMutationStatus.Applied => Results.NoContent(), ProjectMutationStatus.NotFound => NotFound(), ProjectMutationStatus.VersionConflict => Problem(409, "project_version_conflict", "The project changed concurrently."), _ => throw new InvalidOperationException($"Unexpected project delete status {result.Status}.") };
    }

    private static async Task<IResult> GetStatusDigestAsync(
        string projectId,
        int? activityLimit,
        HttpRequest request,
        ILocalProfileStore profiles,
        ICockpitDigestStore store,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return InvalidId();
        }

        var limit = activityLimit ?? 8;
        if (limit is < 1 or > 100)
        {
            return Problem(400, "invalid_activity_limit", "Activity limit must be between 1 and 100.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null)
        {
            return SessionRequired();
        }

        var digest = await store.ReadAsync(
            profile.TenantId,
            projectId,
            limit,
            cancellationToken);
        if (digest is null)
        {
            return NotFound();
        }

        var source = new ProjectStatusDigestSource(
            digest.ProjectId,
            digest.AsOf,
            new ProjectProgressContract(
                digest.Progress.Executed,
                digest.Progress.Validated,
                digest.Progress.Approved),
            new ProjectTaskCountsContract(
                digest.TaskCounts.Backlog,
                digest.TaskCounts.Ready,
                digest.TaskCounts.Development,
                digest.TaskCounts.Review,
                digest.TaskCounts.Corrections,
                digest.TaskCounts.TestsGates,
                digest.TaskCounts.Blocked,
                digest.TaskCounts.Done,
                digest.TaskCounts.Total),
            digest.PendingApprovals,
            digest.Workflow is null
                ? null
                : new ProjectWorkflowDigestContract(
                    digest.Workflow.RunId,
                    digest.Workflow.State,
                    digest.Workflow.PhaseName,
                    digest.Workflow.PhaseState,
                    digest.Workflow.PendingGates),
            digest.RecentActivity.Select(activity => new ProjectActivityContract(
                activity.Id,
                activity.Action,
                activity.Detail,
                activity.OccurredAt)).ToArray());
        return Results.Ok(ProjectStatusDigestService.Create(source));
    }

    private static ProjectContract ToContract(ProjectRecord p) => new(p.Id, p.OrganizationId, p.Name, p.Key, p.Description, p.State, p.Criticality, p.RepositoryUrl, p.RepositoryProvider, p.DefaultBranch, p.Technologies, new(p.Brand.LogoUrl, p.Brand.PrimaryColor, p.Brand.SecondaryColor, p.Brand.Typography), p.MemberProfileIds, p.ConfigVersion, p.ChiefAgentId, p.OperationMode, p.CreatedAt, p.LastActivityAt, p.Version) { Prototyping = new(p.Prototyping.Mode, p.Prototyping.Waiver is null ? null : new(p.Prototyping.Waiver.Reason, p.Prototyping.Waiver.GrantedAt)) };
    private static ProjectRecord ToRecord(string tenant, ProjectContract p) => new(tenant, p.Id, p.OrganizationId, p.Name, p.Key, p.Description, p.State, p.Criticality, p.RepositoryUrl, p.RepositoryProvider, p.DefaultBranch, p.Technologies, new(p.Brand.LogoUrl, p.Brand.PrimaryColor, p.Brand.SecondaryColor, p.Brand.Typography), p.MemberProfileIds, p.ConfigVersion, p.ChiefAgentId, p.OperationMode, p.CreatedAt, p.LastActivityAt, p.Version) { Prototyping = new(p.Prototyping.Mode, p.Prototyping.Waiver is null ? null : new(p.Prototyping.Waiver.Reason, p.Prototyping.Waiver.GrantedAt)) };
    internal static ProjectResponse ToResponse(ProjectRecord p) => new(p.Id, p.OrganizationId, p.Name, p.Key, p.Description, p.State, p.Criticality, p.RepositoryUrl, p.RepositoryProvider, p.DefaultBranch, p.Technologies, new(p.Brand.LogoUrl, p.Brand.PrimaryColor, p.Brand.SecondaryColor, p.Brand.Typography), p.MemberProfileIds, p.ConfigVersion, p.ChiefAgentId, p.OperationMode, new(p.Prototyping.Mode, p.Prototyping.Waiver is null ? null : new(p.Prototyping.Waiver.Reason, p.Prototyping.Waiver.GrantedAt)), p.CreatedAt, p.LastActivityAt);
    private static IResult InvalidId() => Problem(400, "invalid_project_id", "Project ID must be a ULID."); private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required."); private static IResult NotFound() => Problem(404, "project_not_found", "The project does not exist."); private static IResult Conflict() => Problem(409, "project_already_exists", "A project with this name or key already exists."); private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record ProjectResponse(string Id, string OrganizationId, string Name, string Key, string Description, string State, string Criticality, string? RepositoryUrl, string RepositoryProvider, string DefaultBranch, IReadOnlyList<string> Technologies, ProjectBrandContract Brand, IReadOnlyList<string> MemberProfileIds, long ConfigVersion, string ChiefAgentId, string OperationMode, PrototypingConfigContract Prototyping, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);
public sealed record ProjectPage(IReadOnlyList<ProjectResponse> Items, string? NextCursor);
