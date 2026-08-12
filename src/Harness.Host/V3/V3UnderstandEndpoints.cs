using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Harness.Host.Profiles;
using Harness.Host.WorkBoard;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Mvc;

namespace Harness.Host.V3;

public static class V3UnderstandEndpoints
{
    public static IEndpointRouteBuilder MapV3Understand(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/v3/projects/{projectId}").WithTags("v3-understand");
        group.MapGet("/context", GetContextAsync)
            .Produces<V3ProjectContextResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapPost("/understand", AnalyzeAsync)
            .Produces<V3ProjectContextResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapPost("/authorize", AuthorizeAsync)
            .Produces<V3ProjectContextResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/missions/build/compile", CompileBuildMissionAsync)
            .Produces<V3BuildMissionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/missions/validate/compile", CompileValidationMissionAsync)
            .Produces<V3BuildMissionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/missions/platform-maintenance/compile", CompilePlatformMaintenanceMissionAsync)
            .Produces<V3BuildMissionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapGet("/missions", ListMissionsAsync)
            .Produces<V3MissionPageResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        endpoints.MapGet("/api/v1/v3/missions/{missionId}", GetMissionAsync)
            .WithTags("v3-understand")
            .Produces<V3BuildMissionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);

        return endpoints;
    }

    private static async Task<IResult> GetContextAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks,
            V3UnderstandStore.ForConfiguration(configuration), token);
        return Results.Ok(context);
    }

    private static async Task<IResult> AnalyzeAsync(
        string projectId,
        V3UnderstandAnalyzeRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var store = V3UnderstandStore.ForConfiguration(configuration);
        var current = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        var analyzed = V3UnderstandAnalyzer.Analyze(current, input, clock.UtcNow);
        store.WriteProject(analyzed.State);
        var refreshed = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        return Results.Ok(refreshed);
    }

    private static async Task<IResult> AuthorizeAsync(
        string projectId,
        V3AuthorizeBuildRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var store = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        if (!V3AuthorizationPolicy.IsAuthorized(input.Response))
        {
            return Problem(400, "authorization_not_recognized", "A autorização precisa ser explícita.");
        }

        var deadline = input.Deadline ?? context.Deadline;
        var repository = FirstNonBlank(input.Repository, context.Repository);
        if (string.IsNullOrWhiteSpace(repository))
        {
            return Problem(409, "project_not_ready_to_authorize", "Repository could not be resolved.");
        }

        if (context.PrimaryRequirementsCoverage.Any(source => !source.Complete))
        {
            return Problem(409, "primary_requirements_incomplete", "BUILD authorization requires 100% coverage of primary requirement sources.");
        }

        var state = (context.State ?? V3ProjectUnderstandState.Create(resolved.Project!.Id, clock.UtcNow)) with
        {
            Deadline = deadline,
            Repository = repository,
            AuthorizedAt = clock.UtcNow,
            StartedAt = clock.UtcNow,
            LifecycleState = "BUILDING",
            Status = "AUTHORIZED",
            UpdatedAt = clock.UtcNow,
        };
        EnsureLocalRepository(repository);
        store.WriteProject(state);
        var refreshed = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        return Results.Ok(refreshed);
    }

    private static async Task<IResult> CompileBuildMissionAsync(
        string projectId,
        V3CompileBuildMissionRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var store = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        if (context.State?.AuthorizedAt is null)
        {
            return Problem(409, "build_not_authorized", "BUILD mission compilation requires explicit authorization.");
        }

        if (context.PrimaryRequirementsCoverage.Any(source => !source.Complete))
        {
            return Problem(
                409,
                "primary_requirements_incomplete",
                "BUILD mission compilation requires 100% coverage of primary requirement sources.");
        }

        var mission = V3MissionCompiler.CompileBuildMission(
            context,
            V3ExecutorPreview.Select(accounts.List()),
            input.RecommendedExecutorAlias,
            clock.UtcNow);
        store.WriteMission(mission);
        return Results.Ok(V3BuildMissionResponse.From(mission));
    }

    private static async Task<IResult> CompileValidationMissionAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var store = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        var buildRuntimeStore = V3BuildRuntimeStore.ForConfiguration(configuration);
        var buildExecution = buildRuntimeStore.LatestCompletedBuildForProject(projectId);
        if (!string.Equals(context.CurrentLifecycleState, "VALIDATING", StringComparison.OrdinalIgnoreCase) &&
            IsRetryableValidationRuntimePause(context.State, buildExecution, buildRuntimeStore.LatestForProject(projectId), clock.UtcNow))
        {
            store.WriteProject(context.State! with
            {
                LifecycleState = "VALIDATING",
                Status = "VALIDATION_RETRY_READY",
                UpdatedAt = clock.UtcNow,
            });
            context = await V3ProjectContextBuilder.BuildAsync(
                resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
                attachmentStorage, readiness, accounts, channelLinks, store, token);
        }

        if (!string.Equals(context.CurrentLifecycleState, "VALIDATING", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(409, "validation_not_reached", "VALIDATION mission compilation requires lifecycle VALIDATING.");
        }

        if (context.PrimaryRequirementsCoverage.Any(source => !source.Complete))
        {
            return Problem(
                409,
                "primary_requirements_incomplete",
                "VALIDATION mission compilation requires 100% coverage of primary requirement sources.");
        }

        var mission = V3MissionCompiler.CompileValidationMission(
            context,
            buildExecution,
            V3ValidationExecutorPreview.Select(accounts.List(), buildExecution?.ExecutorAccountId),
            clock.UtcNow);
        store.WriteMission(mission);
        return Results.Ok(V3BuildMissionResponse.From(mission));
    }

    private static bool IsRetryableValidationRuntimePause(
        V3ProjectUnderstandState? state,
        V3BuildExecutionRecord? completedBuild,
        V3BuildExecutionRecord? latestExecution,
        DateTimeOffset now)
    {
        if (state is null || completedBuild is null || latestExecution is null) return false;
        if (!string.Equals(latestExecution.MissionType, "VALIDATE", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(latestExecution.ProjectId, state.ProjectId, StringComparison.Ordinal)) return false;
        if (latestExecution.ValidationReport?.HasBlockingFailures == true) return false;
        if (latestExecution.StartedAt < (completedBuild.CompletedAt ?? completedBuild.LastActivityAt)) return false;
        if (!string.Equals(latestExecution.Status, "STALLED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(latestExecution.Status, "PAUSED_PROVIDER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var retryableStatus =
            string.Equals(state.Status, "RECOVERY_STALLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.Status, "STALLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.Status, "PROVIDER_TRANSPORT_TRANSIENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(latestExecution.QuotaState, "PROVIDER_TRANSPORT_TRANSIENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(latestExecution.LastFailureCode, "startup_recovery_requires_explicit_resume", StringComparison.OrdinalIgnoreCase);
        var noFunctionalValidationEvidence =
            latestExecution.ValidationReport is null &&
            latestExecution.HumanBlocker is null &&
            string.IsNullOrWhiteSpace(latestExecution.LastOutput) &&
            latestExecution.CommitDelta == 0;
        return retryableStatus && noFunctionalValidationEvidence && latestExecution.StartedAt <= now;
    }

    private static async Task<IResult> CompilePlatformMaintenanceMissionAsync(
        string projectId,
        V3CompilePlatformMaintenanceMissionRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IConfiguration configuration,
        [FromServices] AgentAccountRegistry accounts,
        IClock clock,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        if (string.IsNullOrWhiteSpace(input.Issue))
        {
            return Problem(400, "issue_required", "Platform maintenance requires a concrete issue.");
        }

        var repository = string.IsNullOrWhiteSpace(input.Repository)
            ? FindRepositoryRoot(Directory.GetCurrentDirectory())
            : Path.GetFullPath(input.Repository.Trim());
        if (string.IsNullOrWhiteSpace(repository) || !Directory.Exists(repository))
        {
            return Problem(409, "repository_unreachable", "Platform maintenance requires a reachable Poseidon repository.");
        }

        var mission = V3MissionCompiler.CompilePlatformMaintenanceMission(
            resolved.Project!.Id,
            resolved.Project.Name,
            repository,
            input,
            V3ExecutorPreview.Select(accounts.List(), AgentRoles.PlatformMaintainer),
            clock.UtcNow);
        V3UnderstandStore.ForConfiguration(configuration).WriteMission(mission);
        return Results.Ok(V3BuildMissionResponse.From(mission));
    }

    private static async Task<IResult> ListMissionsAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IConfiguration configuration,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var store = V3UnderstandStore.ForConfiguration(configuration);
        return Results.Ok(new V3MissionPageResponse(
            store.ListMissions(resolved.Project!.Id).Select(V3BuildMissionResponse.From).ToArray()));
    }

    private static async Task<IResult> GetMissionAsync(
        string missionId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConfiguration configuration,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(missionId, out _)) return Problem(400, "invalid_mission_id", "Mission ID must be a ULID.");
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var mission = V3UnderstandStore.ForConfiguration(configuration).ReadMission(missionId);
        return mission is null ? Problem(404, "mission_not_found", "The requested resource does not exist.") : Results.Ok(V3BuildMissionResponse.From(mission));
    }

    private static async Task<(IResult? Result, LocalProfileRecord? Profile, ProjectRecord? Project)> ResolveAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return (Problem(400, "invalid_project_id", "Project ID must be a ULID."), null, null);
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return (Problem(401, "local_session_required", "A local profile session is required."), null, null);
        }

        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        return project is null
            ? (Problem(404, "project_not_found", "The requested resource does not exist."), profile, null)
            : (null, profile, project);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FindRepositoryRoot(string start)
    {
        var current = Path.GetFullPath(start);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        return Path.GetFullPath(start);
    }

    private static void EnsureLocalRepository(string repository)
    {
        if (repository.Contains("://", StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetFullPath(repository));
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed class V3UnderstandStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    public V3UnderstandStore(string dataDir)
    {
        _root = Path.Combine(dataDir, "v3");
    }

    public static V3UnderstandStore ForConfiguration(IConfiguration configuration)
    {
        var dataDir = configuration["Harness:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".harness-poseidon");
        }

        return new V3UnderstandStore(dataDir);
    }

    public V3ProjectUnderstandState? ReadProject(string projectId)
    {
        var path = ProjectPath(projectId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<V3ProjectUnderstandState>(File.ReadAllText(path), Json)
            : null;
    }

    public void WriteProject(V3ProjectUnderstandState state)
    {
        Write(ProjectPath(state.ProjectId), state);
    }

    public void WriteMission(V3BuildMissionRecord mission)
    {
        Write(MissionPath(mission.MissionId), mission);
    }

    public V3BuildMissionRecord? ReadMission(string missionId)
    {
        var path = MissionPath(missionId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<V3BuildMissionRecord>(File.ReadAllText(path), Json)
            : null;
    }

    public IReadOnlyList<V3BuildMissionRecord> ListMissions(string projectId)
    {
        var dir = MissionsDirectory();
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "*.json")
            .Select(path => JsonSerializer.Deserialize<V3BuildMissionRecord>(File.ReadAllText(path), Json))
            .Where(mission => mission is not null && string.Equals(mission.ProjectId, projectId, StringComparison.Ordinal))
            .Select(mission => mission!)
            .OrderByDescending(mission => mission.CreatedAt)];
    }

    private string ProjectPath(string projectId) => Path.Combine(_root, "projects", projectId, "understand.json");

    private string MissionPath(string missionId) => Path.Combine(MissionsDirectory(), $"{missionId}.json");

    private string MissionsDirectory() => Path.Combine(_root, "missions");

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

public static class V3ProjectContextBuilder
{
    public static async Task<V3ProjectContextResponse> BuildAsync(
        LocalProfileRecord profile,
        ProjectRecord project,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        SolicitationAttachmentStorage attachmentStorage,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        V3UnderstandStore store,
        CancellationToken token)
    {
        var state = store.ReadProject(project.Id);
        var solicitations = await board.ListSolicitationsAsync(profile.TenantId, project.Id, null, 200, token);
        var artifactRefs = new List<V3ArtifactReference>();
        foreach (var solicitation in solicitations)
        {
            var values = await attachments.ListAsync(profile.TenantId, solicitation.Id, token);
            artifactRefs.AddRange(values.Select(value => new V3ArtifactReference(
                value.Id,
                value.FileName,
                value.ContentType,
                value.Role,
                "solicitation_attachment",
                attachmentStorage.Resolve(value.StoragePath),
                value.Sha256,
                value.State)));
        }

        var documentRows = await documents.ListDocumentsAsync(profile.TenantId, project.Id, null, 200, token);
        var documentRefs = new List<V3DocumentReference>(documentRows.Count);
        foreach (var value in documentRows)
        {
            var latest = await ResolveCurrentVersionAsync(documents, profile.TenantId, value, token);
            var readablePath = ResolveDocumentReadablePath(documentContent, latest);
            var sha256 = latest?.ContentHash.ToUpperInvariant();
            var sourceRole = DocumentSourceRole(value);
            var reference = new V3DocumentReference(
                value.Id,
                value.Title,
                value.Kind,
                value.State,
                value.CurrentVersion,
                value.Classifications)
            {
                CatalogPath = latest?.CatalogPath,
                Sha256 = sha256,
                ReadablePath = readablePath,
                SourceRole = sourceRole,
            };
            documentRefs.Add(reference);
            if (readablePath is not null && sourceRole is not null)
            {
                artifactRefs.Add(new V3ArtifactReference(
                    $"document:{value.Id}",
                    value.Title,
                    DocumentContentType(value),
                    sourceRole,
                    "document_catalog",
                    readablePath,
                    sha256 ?? "UNKNOWN",
                    value.State)
                {
                    ReadablePath = readablePath,
                });
            }
        }

        var coverage = await V3SourceCoverageAnalyzer.AnalyzeAsync(
            artifactRefs, attachmentStorage, token);
        var facts = V3RequirementFactsExtractor.Extract(coverage);
        var prototypeRefs = (await prototypes.ListPrototypesAsync(profile.TenantId, project.Id, null, 200, token))
            .Select(value => new V3PrototypeReference(value.Id, value.Name, value.State, value.SourceDocumentId))
            .ToArray();
        var snapshot = await readiness.EvaluateAsync(profile.TenantId, project, profileReady: true, token);
        var effective = V3StackResolver.Resolve(project, state, artifactRefs, facts);
        var links = await channelLinks.ListAsync(profile.TenantId, token);
        var v3Readiness = V3Readiness.For(
            project,
            snapshot,
            accounts.List(),
            state,
            artifactRefs.Count,
            effective,
            links.Count > 0);
        var capacity = V3Capacity.From(accounts.List(), DateTimeOffset.UtcNow);
        var repository = FirstNonBlank(
            state?.Repository,
            project.RepositoryUrl,
            DefaultLocalRepository(profile.TenantId, project.Id));
        var openQuestions = V3OpenQuestionPolicy.RequiredQuestions(
            project.Id,
            state?.Deadline ?? facts.Deadline ?? project.TargetDeadline,
            repository,
            facts,
            coverage,
            state);
        var lifecycleState = state?.LifecycleState ??
            (openQuestions.Count == 0 ? "READY_TO_START" : "AWAITING_INPUT");

        var response = new V3ProjectContextResponse(
            project.Id,
            project.Name,
            project.Description,
            artifactRefs,
            documentRefs,
            prototypeRefs,
            state,
            state?.OriginalIntent ?? project.Description,
            state?.ProjectSummary,
            state?.Requirements ?? [],
            state?.AcceptanceCriteria ?? [],
            state?.Decisions ?? [],
            state?.Assumptions ?? [],
            coverage,
            facts,
            openQuestions,
            effective,
            state?.Deadline ?? facts.Deadline ?? project.TargetDeadline,
            repository,
            "host-api-and-frontend; database container only when stack requires it",
            "existing notification channels; no new notification system",
            capacity,
            v3Readiness.Items,
            lifecycleState);
        return response with
        {
            Brand = new V3ProjectBrandContext(
                project.Brand.LogoUrl,
                project.Brand.PrimaryColor,
                project.Brand.SecondaryColor,
                project.Brand.Typography),
            SolutionStrategy = state?.SolutionStrategy ?? V3SolutionStrategyBuilder.Build(response, state),
        };
    }

    private static async Task<DocumentVersionCatalogRecord?> ResolveCurrentVersionAsync(
        IDocumentCatalogStore documents,
        string tenantId,
        DocumentCatalogRecord document,
        CancellationToken token)
    {
        var versions = await documents.ListVersionsAsync(tenantId, document.Id, null, 200, token);
        return versions.FirstOrDefault(version => version.Version == document.CurrentVersion) ??
            versions.OrderByDescending(version => version.Version).FirstOrDefault();
    }

    private static string? ResolveDocumentReadablePath(
        IDocumentContentCatalog content,
        DocumentVersionCatalogRecord? version)
    {
        if (version is null)
        {
            return null;
        }

        try
        {
            return content.ResolveReadPath(version.CatalogPath, version.ContentHash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static string? DocumentSourceRole(DocumentCatalogRecord document)
    {
        var tokens = $"{document.Title} {document.Kind} {string.Join(' ', document.Classifications)}"
            .ToLowerInvariant();
        if (tokens.Contains("prototype", StringComparison.Ordinal) ||
            tokens.Contains("protótipo", StringComparison.Ordinal) ||
            tokens.Contains("referência", StringComparison.Ordinal) ||
            tokens.Contains("reference", StringComparison.Ordinal) ||
            string.Equals(document.Kind, "design", StringComparison.OrdinalIgnoreCase))
        {
            return "design_reference";
        }

        if (string.Equals(document.Kind, "prd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Kind, "spec", StringComparison.OrdinalIgnoreCase) ||
            tokens.Contains("requirement", StringComparison.Ordinal) ||
            tokens.Contains("requis", StringComparison.Ordinal) ||
            tokens.Contains("especifica", StringComparison.Ordinal) ||
            tokens.Contains("source", StringComparison.Ordinal) ||
            tokens.Contains("fonte", StringComparison.Ordinal) ||
            tokens.Contains("primary", StringComparison.Ordinal))
        {
            return "requirements_source";
        }

        return null;
    }

    private static string DocumentContentType(DocumentCatalogRecord document) =>
        string.Equals(document.Kind, "design", StringComparison.OrdinalIgnoreCase)
            ? "text/markdown; role=design-reference"
            : "text/markdown";

    private static string DefaultLocalRepository(string tenantId, string projectId)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness-poseidon",
            "repositories",
            tenantId);
        return Path.Combine(root, projectId);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public static class V3SourceCoverageAnalyzer
{
    private const int MaxPrimaryRequirementCharacters = 180_000;

    public static async Task<IReadOnlyList<V3SourceCoverage>> AnalyzeAsync(
        IReadOnlyList<V3ArtifactReference> artifacts,
        SolicitationAttachmentStorage storage,
        CancellationToken token)
    {
        var values = new List<V3SourceCoverage>();
        foreach (var artifact in artifacts.Where(IsPrimaryRequirement))
        {
            var coverage = await AnalyzeOneAsync(artifact, storage, token);
            values.Add(coverage);
        }

        return values;
    }

    private static async Task<V3SourceCoverage> AnalyzeOneAsync(
        V3ArtifactReference artifact,
        SolicitationAttachmentStorage storage,
        CancellationToken token)
    {
        string text;
        try
        {
            var path = Path.IsPathRooted(artifact.PathReference)
                ? artifact.PathReference
                : storage.Resolve(artifact.PathReference);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxPrimaryRequirementCharacters)
            {
                return Empty(artifact, "primary requirement source is missing or exceeds safe full-read budget");
            }

            var bytes = await File.ReadAllBytesAsync(path, token);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                return Empty(artifact, "primary requirement source is binary and cannot be fully consumed as text");
            }

            text = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Empty(artifact, "primary requirement source could not be read");
        }

        var sections = AttachmentSectionizer.Split(text);
        return new V3SourceCoverage(
            artifact.ArtifactId,
            artifact.Name,
            artifact.Role,
            sections.Count,
            sections.Count,
            100,
            true,
            "full-text-read",
            text.Length,
            text);
    }

    private static V3SourceCoverage Empty(V3ArtifactReference artifact, string provider) =>
        new(artifact.ArtifactId, artifact.Name, artifact.Role, 0, 0, 0, false, provider, 0, null);

    private static bool IsPrimaryRequirement(V3ArtifactReference artifact) =>
        string.Equals(artifact.Role, "requirements_source", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artifact.Role, "requirements", StringComparison.OrdinalIgnoreCase) ||
        artifact.Name.Contains("requis", StringComparison.OrdinalIgnoreCase) ||
        artifact.Name.Contains("especifica", StringComparison.OrdinalIgnoreCase) ||
        artifact.Name.Contains("spec", StringComparison.OrdinalIgnoreCase);
}

public static partial class V3RequirementFactsExtractor
{
    private static readonly Regex DatePattern = new(
        @"(?i)(?:prazo(?:-alvo)?|deadline)[^\n\r]{0,80}?(\d{1,2})/(\d{1,2})/(\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static V3RequirementSourceFacts Extract(IReadOnlyList<V3SourceCoverage> coverages)
    {
        var texts = coverages
            .Where(coverage => coverage.Complete && !string.IsNullOrWhiteSpace(coverage.ContentPreview))
            .Select(coverage => (coverage.ArtifactId, Text: coverage.ContentPreview!))
            .ToArray();
        var combined = StripExplicitNonScopeSections(string.Join("\n\n", texts.Select(item => item.Text)));
        var deadline = ExtractDeadline(texts);
        var authentication = ContainsAny(combined, "autenticação própria", "sem sso")
            ? "Autenticação própria, sem SSO"
            : null;
        var productNotification = ContainsAny(combined, "notificações", "digest", "caixa", "e-mail", "email")
            ? "Notificações do produto por e-mail/digest conforme requisitos"
            : null;
        var frontend = ContainsAny(combined, "react")
            ? ContainsAny(combined, "typescript", "type script", "ts")
                ? "React + TypeScript"
                : "React"
            : null;
        var backend = ContainsAny(combined, ".net 8", "dotnet 8")
            ? ".NET 8"
            : ContainsAny(combined, ".net", "dotnet")
                ? ".NET"
                : null;
        var database = ContainsAny(combined, "oracle")
            ? "Oracle"
            : ContainsAny(combined, "postgres", "postgresql")
                ? "PostgreSQL"
                : ContainsAny(combined, "sql server", "sqlserver")
                    ? "SQL Server"
                    : null;
        var itrc = combined.Contains("ITRC", StringComparison.OrdinalIgnoreCase)
            ? "Regras de ITRC definidas na fonte primária"
            : null;
        var acceptance = Regex.Count(combined, @"(?m)^\s*-\s+\*\*T\d{1,2}\*\*");

        return new V3RequirementSourceFacts(
            deadline?.Value,
            deadline?.Provenance,
            authentication,
            authentication is null ? null : "PRIMARY_REQUIREMENTS",
            productNotification,
            productNotification is null ? null : "PRIMARY_REQUIREMENTS",
            frontend,
            frontend is null ? null : "PRIMARY_REQUIREMENTS",
            backend,
            backend is null ? null : "PRIMARY_REQUIREMENTS",
            database,
            database is null ? null : "PRIMARY_REQUIREMENTS",
            itrc,
            itrc is null ? null : "PRIMARY_REQUIREMENTS",
            acceptance);
    }

    private static (DateTimeOffset Value, string Provenance)? ExtractDeadline(
        IReadOnlyList<(string ArtifactId, string Text)> texts)
    {
        foreach (var (artifactId, text) in texts)
        {
            var match = DatePattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var day = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var year = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            return (new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero),
                $"PRIMARY_REQUIREMENTS:{artifactId}:deadline");
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string StripExplicitNonScopeSections(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        var skippingList = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (ContainsAny(trimmed, "não escopo", "nao escopo", "fora de escopo", "non-scope"))
            {
                skippingList = true;
                continue;
            }

            if (skippingList)
            {
                if (trimmed.Length == 0 ||
                    trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    continue;
                }

                skippingList = false;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }
}

public static class V3UnderstandAnalyzer
{
    public static V3UnderstandAnalysis Analyze(
        V3ProjectContextResponse context,
        V3UnderstandAnalyzeRequest input,
        DateTimeOffset now)
    {
        var state = context.State ?? V3ProjectUnderstandState.Create(context.ProjectId, now);
        var intent = FirstNonBlank(input.OriginalIntent, state.OriginalIntent, context.OriginalIntent);
        var summary = FirstNonBlank(input.BrunaSummary, state.ProjectSummary, Summarize(context.ProjectName, intent));
        var capabilities = input.CoreCapabilities.Count > 0
            ? input.CoreCapabilities
            : state.Requirements.Count > 0
                ? state.Requirements
                : InferCapabilities(intent, context.Artifacts, context.Documents);
        var assumptions = state.Assumptions.Concat(input.Assumptions)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var criteria = input.AcceptanceCriteria.Count > 0
            ? input.AcceptanceCriteria
            : state.AcceptanceCriteria.Count > 0
                ? state.AcceptanceCriteria
                : capabilities.Select(capability => $"A funcionalidade '{capability}' funciona pelo fluxo real do produto.").ToArray();
        var sourceComplete = context.PrimaryRequirementsCoverage.All(source => source.Complete);
        var deadline = input.Deadline ?? state.Deadline ?? context.SourceFacts.Deadline ?? context.Deadline;
        var repository = FirstNonBlank(input.Repository, state.Repository, context.Repository);
        var baseUpdate = state with
        {
            OriginalIntent = intent,
            ProjectSummary = summary,
            ProductGoal = FirstNonBlank(input.ProductGoal, state.ProductGoal, summary),
            PrimaryUsers = input.PrimaryUsers.Count > 0 ? input.PrimaryUsers : state.PrimaryUsers,
            Requirements = [.. capabilities],
            AcceptanceCriteria = [.. criteria],
            ImportantConstraints = input.ImportantConstraints.Count > 0 ? input.ImportantConstraints : state.ImportantConstraints,
            ProvidedFrontend = context.Artifacts.Any(artifact => string.Equals(artifact.Role, "provided_frontend", StringComparison.OrdinalIgnoreCase)),
            DesignReferences = [.. context.Artifacts.Where(artifact =>
                string.Equals(artifact.Role, "design_reference", StringComparison.OrdinalIgnoreCase)).Select(artifact => artifact.Name)],
            Assumptions = assumptions,
            Deadline = deadline,
            Repository = repository,
            EffectiveStack = context.EffectiveStack,
            PrimaryRequirementsCoverage = context.PrimaryRequirementsCoverage,
            SourceFacts = context.SourceFacts,
            UpdatedAt = now,
        };
        var strategy = V3SolutionStrategyBuilder.Build(context, baseUpdate);
        var stateWithStrategy = baseUpdate with { SolutionStrategy = strategy };
        var questions = V3OpenQuestionPolicy.RequiredQuestions(
            context.ProjectId, deadline, repository, context.SourceFacts, context.PrimaryRequirementsCoverage, stateWithStrategy);
        var nextState = !sourceComplete
            ? "UNDERSTANDING"
            : questions.Count == 0
                ? "READY_TO_START"
                : "AWAITING_INPUT";
        var updated = stateWithStrategy with
        {
            LifecycleState = nextState,
            Status = sourceComplete ? "UNDERSTOOD" : "READING_PRIMARY_REQUIREMENTS",
            UpdatedAt = now,
        };
        return new V3UnderstandAnalysis(updated, questions);
    }

    private static string Summarize(string projectName, string? intent) =>
        string.IsNullOrWhiteSpace(intent)
            ? $"Construir e validar o produto {projectName} conforme os materiais do projeto."
            : intent.Length <= 420
                ? intent
                : intent[..420].TrimEnd() + "…";

    private static List<string> InferCapabilities(
        string? intent,
        IReadOnlyList<V3ArtifactReference> artifacts,
        IReadOnlyList<V3DocumentReference> documents)
    {
        var text = $"{intent} {string.Join(' ', artifacts.Select(artifact => artifact.Name))} {string.Join(' ', documents.Select(document => document.Title))}".ToLowerInvariant();
        var values = new List<string>();
        if (text.Contains("risco", StringComparison.Ordinal)) values.Add("Gestão de riscos corporativos");
        if (text.Contains("aprova", StringComparison.Ordinal)) values.Add("Fluxo de aprovação por perfis");
        if (text.Contains("auditoria", StringComparison.Ordinal)) values.Add("Trilha de auditoria");
        if (text.Contains("dashboard", StringComparison.Ordinal) || text.Contains("painel", StringComparison.Ordinal)) values.Add("Painéis e indicadores operacionais");
        if (text.Contains("importa", StringComparison.Ordinal) || text.Contains("xlsx", StringComparison.Ordinal) || text.Contains("csv", StringComparison.Ordinal)) values.Add("Importação e validação de dados");
        if (values.Count == 0) values.Add("Fluxo principal descrito nos requisitos originais");
        return values;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public static class V3StackResolver
{
    public static V3EffectiveStackContract Resolve(
        ProjectRecord project,
        V3ProjectUnderstandState? state,
        IReadOnlyList<V3ArtifactReference> artifacts,
        V3RequirementSourceFacts facts)
    {
        if (state?.EffectiveStack is { } existing &&
            (facts.Database is null ||
             !existing.Database.Contains("conforme", StringComparison.OrdinalIgnoreCase)))
        {
            return existing;
        }

        var text = $"{project.Description} {string.Join(' ', project.Technologies)} {string.Join(' ', artifacts.Select(artifact => artifact.Name))}".ToLowerInvariant();
        var frontend = !string.IsNullOrWhiteSpace(facts.Frontend)
            ? facts.Frontend
            : text.Contains("react", StringComparison.Ordinal) || artifacts.Any(artifact =>
            string.Equals(artifact.Role, "provided_frontend", StringComparison.OrdinalIgnoreCase))
            ? "React + TypeScript + Vite"
            : "Frontend conforme requisitos";
        var backend = !string.IsNullOrWhiteSpace(facts.Backend)
            ? facts.Backend
            : text.Contains(".net", StringComparison.Ordinal) || text.Contains("dotnet", StringComparison.Ordinal)
            ? ".NET"
            : "Backend conforme baseline Poseidon";
        var database = !string.IsNullOrWhiteSpace(facts.Database)
            ? facts.Database
            : text.Contains("oracle", StringComparison.Ordinal)
            ? "Oracle"
            : text.Contains("postgres", StringComparison.Ordinal)
                ? "PostgreSQL"
                : "Database conforme requisitos";
        return new V3EffectiveStackContract(
            frontend,
            backend,
            database,
            "Clean Architecture / modular full-stack",
            "Playwright + unit/integration tests",
            [
                "restrição explícita do usuário/documento",
                "política organizacional",
                "baseline Poseidon",
                "inferência de artefatos e descrição",
            ]);
    }
}

public static class V3OpenQuestionPolicy
{
    public static IReadOnlyList<V3OpenQuestion> RequiredQuestions(ProjectRecord project, V3ProjectUnderstandState? state) =>
        RequiredQuestions(project.Id, state?.Deadline ?? project.TargetDeadline, state?.Repository ?? project.RepositoryUrl, state?.SourceFacts, state?.PrimaryRequirementsCoverage ?? [], state);

    public static IReadOnlyList<V3OpenQuestion> RequiredQuestions(
        string projectId,
        DateTimeOffset? deadline,
        string? repository,
        V3RequirementSourceFacts? facts = null,
        IReadOnlyList<V3SourceCoverage>? sourceCoverage = null,
        V3ProjectUnderstandState? state = null)
    {
        var questions = new List<V3OpenQuestion>();
        var primarySources = sourceCoverage ?? [];
        if (primarySources.Any(source => !source.Complete))
        {
            return questions;
        }

        questions.AddRange(PendingHumanDecisions(state));
        questions.AddRange(PendingArchitectureDecisions(state));

        return questions;
    }

    private static IEnumerable<V3OpenQuestion> PendingHumanDecisions(V3ProjectUnderstandState? state)
    {
        if (state?.Decisions is not { Count: > 0 } decisions)
        {
            yield break;
        }

        var index = 1;
        foreach (var decision in decisions)
        {
            if (!IsPendingHumanDecision(decision))
            {
                continue;
            }

            yield return new V3OpenQuestion(
                $"human-decision-{index++}",
                decision,
                "chief.pending_human_decision");
        }
    }

    private static bool IsPendingHumanDecision(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("PENDENTE", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("decisão humana necessária", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("decisão humana obrigatória", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("aguardando decisão", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("falta decisão", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("preciso que você", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("preciso da sua decisão", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<V3OpenQuestion> PendingArchitectureDecisions(V3ProjectUnderstandState? state)
    {
        var decisions = state?.SolutionStrategy?.HumanDecisionsRequired;
        if (decisions is not { Count: > 0 } ||
            state?.SolutionStrategy?.ArchitectureApprovalRequired != true)
        {
            yield break;
        }

        var index = 1;
        foreach (var decision in decisions.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            yield return new V3OpenQuestion(
                $"architecture-decision-{index++}",
                decision,
                "solution_strategy.architecture_approval_required");
        }
    }
}

public static class V3SolutionStrategyBuilder
{
    public static V3SolutionStrategy Build(V3ProjectContextResponse context, V3ProjectUnderstandState? state)
    {
        var text = Normalize(StripExplicitNonScopeSections(string.Join("\n",
            context.OriginalIntent,
            context.ProjectSummary,
            state?.ProjectSummary,
            state?.ProductGoal,
            string.Join('\n', state?.Requirements ?? context.Requirements),
            string.Join('\n', state?.AcceptanceCriteria ?? context.AcceptanceCriteria),
            string.Join('\n', context.PrimaryRequirementsCoverage.Select(source => source.ContentPreview)),
            string.Join(' ', context.Artifacts.Select(artifact => artifact.Name)))));
        var integrationPoints = DetectIntegrationPoints(text);
        var hasFrontend = HasAny(text, "frontend", "interface", "tela", "dashboard", "mobile", "react") ||
            context.Artifacts.Any(artifact => string.Equals(artifact.Role, "provided_frontend", StringComparison.OrdinalIgnoreCase));
        var hasDatabase = !context.EffectiveStack.Database.Contains("conforme requisitos", StringComparison.OrdinalIgnoreCase) ||
            HasAny(text, "persist", "banco", "database", "oracle", "postgres", "sql server");
        var hasAuth = HasAny(text, "login", "autentica", "sso", "perfil", "permiss", "rbac", "diretório corporativo", "diretorio corporativo");
        var hasAsync = HasAny(text, "assíncrono", "assincrono", "fila", "queue", "mensageria", "worker", "processamento em lote", "batch");
        var hasHighAvailability = HasAny(text, "alta disponibilidade", "disaster recovery", "99,9", "99.9", "24x7") ||
            HasTerm(text, "ha") ||
            HasTerm(text, "dr") ||
            HasTerm(text, "sla");
        var hasVolume = HasAny(text, "volume", "milhões", "milhoes", "alto volume", "100 mil", "concorrente", "throughput");
        var hasLegacy = HasAny(text, "legado", "modernizar", "compatibilidade", "sem alterar regras", "contrato existente");
        var hasCriticalMath = HasAny(text, "cálculo", "calculo", "fórmula", "formula", "tabela de referência", "tabela de referencia", "arredondamento");
        var materialDecision = DetectMaterialArchitectureDecisions(text);
        var score =
            integrationPoints.Length * 2 +
            (hasAsync ? 2 : 0) +
            (hasHighAvailability ? 2 : 0) +
            (hasVolume ? 1 : 0) +
            (hasLegacy ? 1 : 0) +
            (hasCriticalMath ? 1 : 0);
        var complexity = score >= 5 ? "COMPLEX" : score >= 2 ? "MODERATE" : "SIMPLE";
        var components = new List<string>();
        if (hasFrontend) components.Add("Frontend web");
        if (!context.EffectiveStack.Backend.Contains("conforme", StringComparison.OrdinalIgnoreCase) || hasFrontend || hasDatabase)
        {
            components.Add("API/backend");
        }

        if (hasDatabase) components.Add("Persistência relacional");
        if (hasAuth) components.Add("Autenticação/autorização");
        if (hasAsync) components.Add("Processamento assíncrono");
        components.AddRange(integrationPoints.Select(point => $"Integração: {point}"));
        if (components.Count == 0) components.Add("Fluxo principal do produto");

        var risks = new List<string>();
        if (integrationPoints.Length > 0) risks.Add("Contratos, autenticação, timeouts e falhas de integrações externas precisam ser explicitados e tratados.");
        if (hasAsync) risks.Add("Processamento assíncrono exige idempotência, rastreabilidade e retomada segura.");
        if (hasHighAvailability) risks.Add("Requisitos de disponibilidade/DR impactam infraestrutura e operação.");
        if (hasCriticalMath) risks.Add("Regras matemáticas críticas exigem testes determinísticos com casos de borda e tabela de referência.");
        if (hasLegacy) risks.Add("Modernização de legado exige compatibilidade de contrato e preservação de regras de negócio.");

        var decisions = materialDecision;
        return new V3SolutionStrategy(
            complexity,
            complexity switch
            {
                "COMPLEX" => "Mapear componentes, fronteiras, integrações, dados, segurança, falhas e operação antes de orientar a BUILD; ainda dentro do UNDERSTAND.",
                "MODERATE" => "Definir estratégia técnica proporcional, com foco em integração real, persistência e riscos específicos.",
                _ => "Implementar como produto simples e direto, usando o baseline sem arquitetura cerimonial.",
            },
            components.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            integrationPoints,
            hasDatabase ? "Persistência real com migrations; constraints e índices guiados por consultas/regras do domínio." : "Persistência somente se exigida pelo requisito ou pelo baseline da modalidade.",
            hasAuth ? ["Autorização server-side; frontend apenas reflete permissões."] : [],
            BuildOperationalConsiderations(hasAsync, hasHighAvailability, integrationPoints.Length > 0),
            risks,
            BuildTradeoffs(complexity, hasLegacy, integrationPoints.Length > 0),
            decisions,
            decisions.Count > 0);
    }

    private static string[] DetectIntegrationPoints(string text)
    {
        var values = new List<string>();
        if (HasTerm(text, "erp")) values.Add("ERP");
        if (HasAny(text, "api de terceiro", "terceiro", "fornecedor externo", "serviço externo", "servico externo")) values.Add("API externa/terceiro");
        if (HasTerm(text, "sso") ||
            HasTerm(text, "oidc") ||
            HasTerm(text, "saml") ||
            HasTerm(text, "ldap") ||
            HasAny(text, "diretório corporativo", "diretorio corporativo", "active directory"))
        {
            values.Add("Identidade corporativa");
        }

        if (HasAny(text, "email", "e-mail", "whatsapp", "telegram") || HasTerm(text, "sms"))
        {
            values.Add("Canal de comunicação");
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<string> DetectMaterialArchitectureDecisions(string text)
    {
        var values = new List<string>();
        if (HasAny(text, "cloud vs on-prem", "cloud ou on-prem", "nuvem ou on-prem", "a definir infraestrutura"))
        {
            values.Add("Decidir a topologia de infraestrutura (cloud/on-prem) antes da BUILD porque afeta implantação, segurança e operação.");
        }

        if (HasAny(text, "fornecedor a definir", "api externa a definir", "erp a definir", "protocolo a definir"))
        {
            values.Add("Definir fornecedor/protocolo de integração externa antes da BUILD porque o contrato externo é material.");
        }

        if (HasAny(text, "estratégia de alta disponibilidade a definir", "sla a definir", "dr a definir"))
        {
            values.Add("Definir requisitos de disponibilidade/DR antes da BUILD porque impactam arquitetura e custo.");
        }

        return values;
    }

    private static List<string> BuildOperationalConsiderations(bool hasAsync, bool hasHighAvailability, bool hasIntegration)
    {
        var values = new List<string>
        {
            "Descobrir ambiente, scripts, portas, runtimes e estado do repositório antes de instalar ou alterar dependências globais.",
        };
        if (hasIntegration) values.Add("Health checks e logs devem separar falha interna de falha de integração externa.");
        if (hasAsync) values.Add("Operações assíncronas devem expor status, retry controlado e erro recuperável.");
        if (hasHighAvailability) values.Add("Operabilidade precisa cobrir disponibilidade, recuperação e observabilidade proporcional ao SLA.");
        return values;
    }

    private static List<string> BuildTradeoffs(string complexity, bool hasLegacy, bool hasIntegration)
    {
        var values = new List<string>();
        if (complexity == "SIMPLE")
        {
            values.Add("Evitar camadas, documentos e infraestrutura que não reduzam risco real do projeto.");
        }

        if (hasLegacy) values.Add("Preservar contrato/regras legadas tem precedência sobre reescrita técnica estética.");
        if (hasIntegration) values.Add("Preferir integração simples e observável antes de otimizações distribuídas.");
        return values;
    }

    private static string Normalize(string value) => value.ToLowerInvariant();

    private static bool HasAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool HasTerm(string text, string value) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StripExplicitNonScopeSections(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        var skippingList = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (HasAny(trimmed, "não escopo", "nao escopo", "fora de escopo", "non-scope"))
            {
                skippingList = true;
                continue;
            }

            if (skippingList)
            {
                if (trimmed.Length == 0 ||
                    trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    continue;
                }

                skippingList = false;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }
}

public static partial class V3NaturalUserDecision
{
    [GeneratedRegex(@"\b(\d{1,2})/(\d{1,2})/(\d{4})\b")]
    private static partial Regex DateRegex();

    public static DateTimeOffset? ExtractDeadline(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = DateRegex().Match(text);
        if (!match.Success) return null;
        var day = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
    }
}

public static class V3AuthorizationPolicy
{
    private static readonly Regex NegativePattern = new(
        @"(?i)\b(n[aã]o|nao)\s+(pode\s+)?(inicie|iniciar|comece|começar|comecar)|\bn[aã]o\s+inicie\b|\bn[aã]o\s+comece\b|\bainda\s+n[aã]o\b|\bsem\s+autorizar\b|\bn[aã]o\s+autoriza",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuestionPattern = new(
        @"(?i)\?\s*$|\bpode\s+me\s+dizer\b|\bj[aá]\s+pode\s+iniciar\b|\best[aá]\s+pronto\s+para\s+iniciar\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PositivePattern = new(
        @"(?i)^\s*(sim|autorizado|autorizo|pode\s+iniciar|pode\s+come[cç]ar|comece|inicie|vamos\s+iniciar|vamos\s+come[cç]ar)\s*[.!]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsAuthorized(string? response) =>
        !string.IsNullOrWhiteSpace(response) &&
        !NegativePattern.IsMatch(response) &&
        !QuestionPattern.IsMatch(response) &&
        PositivePattern.IsMatch(response);
}

public static class V3ExecutorPreview
{
    public static V3RecommendedExecutor Select(
        IReadOnlyList<AgentAccountContract> accounts,
        string requiredRole = AgentRoles.ProjectExecutor)
    {
        var fallbackToLayerRoles = string.Equals(requiredRole, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase);
        var candidates = accounts
            .Where(account =>
                account.State == AgentAccountState.Available &&
                (account.AllowedRoles.Contains(requiredRole, StringComparer.OrdinalIgnoreCase) ||
                 (fallbackToLayerRoles && account.AllowedRoles.Any(role =>
                    string.Equals(role, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)))) &&
                ExecutorCatalog.Find(account.ExecutorId)?.Capabilities.Capabilities.Contains("code", StringComparer.OrdinalIgnoreCase) == true)
            .OrderByDescending(account => account.Priority)
            .ThenBy(account => account.ActiveAttempts)
            .ThenBy(account => account.Alias, StringComparer.Ordinal)
            .FirstOrDefault();
        return candidates is null
            ? new V3RecommendedExecutor(null, "BLOCKED", "No authenticated quota-available write-capable account is available.")
            : new V3RecommendedExecutor(candidates.Alias, "AVAILABLE", "AVAILABLE + WRITE_CAPABLE + role compatible.");
    }
}

public static class V3ValidationExecutorPreview
{
    public static V3RecommendedExecutor Select(
        IReadOnlyList<AgentAccountContract> accounts,
        string? previousBuildExecutor)
    {
        var selected = V3BuildExecutorSelector.Select(accounts, previousBuildExecutor);
        return selected.Account is null
            ? new V3RecommendedExecutor(null, "BLOCKED", selected.Reason)
            : new V3RecommendedExecutor(selected.Account.Alias, "AVAILABLE", selected.Reason);
    }
}

public static class V3KnowledgeSelector
{
    public static IReadOnlyList<V3KnowledgeReference> Select(
        V3EffectiveStackContract stack,
        V3SolutionStrategy? strategy = null,
        IReadOnlyList<V3ArtifactReference>? artifacts = null)
    {
        var hasFrontend = HasFrontend(stack, strategy);
        var hasBackend = HasBackend(stack, strategy, hasFrontend);
        var hasDatabase = HasDatabase(stack, strategy);
        var hasAuth = HasAuth(strategy);
        var hasProvidedArtifacts = artifacts?.Any(artifact =>
            string.Equals(artifact.Role, "provided_frontend", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(artifact.Role, "design_reference", StringComparison.OrdinalIgnoreCase)) == true;
        var isComplex = string.Equals(strategy?.Complexity, "COMPLEX", StringComparison.OrdinalIgnoreCase);
        var hasIntegration = strategy?.IntegrationPoints.Count > 0;
        var refs = new List<V3KnowledgeReference>
        {
            Ref("docs/product/definition-of-done.md", "Definition of Done do produto entregue"),
            Ref("docs/product/baseline.md", "Baseline técnico do produto entregue"),
        };
        if (hasAuth || isComplex || hasIntegration)
        {
            refs.Add(Ref("docs/product/security-and-operability.md", "Segurança e operabilidade do produto entregue"));
        }

        if (hasFrontend)
        {
            refs.Add(Ref("docs/product/frontend-standards.md", "Frontend do produto entregue"));
            refs.Add(Ref("docs/product/full-stack-integration.md", "Integração real frontend/API/persistência"));
        }

        if (hasBackend)
        {
            refs.Add(Ref("docs/product/backend-standards.md", "Backend .NET do produto entregue"));
        }

        if (hasDatabase)
        {
            refs.Add(Ref("docs/product/data-standards.md", "Dados e banco do produto entregue"));
        }

        if (stack.Database.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add(Ref("docs/product/oracle-data-standards.md", "Dados e banco Oracle"));
        }

        if (hasProvidedArtifacts)
        {
            refs.Add(Ref("docs/product/provided-artifacts.md", "Política para artefatos fornecidos pelo usuário"));
        }

        if (hasAuth)
        {
            refs.Add(Ref("docs/product/authentication-standards.md", "Autenticação e autorização do produto entregue"));
        }

        if (isComplex || hasIntegration || hasFrontend)
        {
            refs.Add(Ref("docs/product/qa-standards.md", "QA proporcional do produto entregue"));
        }

        return [.. refs.DistinctBy(item => item.Path)];
    }

    public static IReadOnlyList<V3KnowledgeReference> SelectForValidation(
        V3EffectiveStackContract stack,
        V3SolutionStrategy? strategy = null,
        IReadOnlyList<V3ArtifactReference>? artifacts = null)
    {
        var refs = Select(stack, strategy, artifacts).ToList();
        if (refs.All(item => item.Path != "docs/product/qa-standards.md"))
        {
            refs.Add(Ref("docs/product/qa-standards.md", "QA do produto entregue"));
        }

        refs.Add(Ref("tools/e2e/doctor.sh", "Doctor determinístico da toolchain E2E local"));
        refs.Add(Ref("docs/product/checklist-auto-auditoria-ia.md", "Checklist genérico de qualidade para autoauditoria final"));
        return [.. refs.DistinctBy(item => item.Path)];
    }

    private static bool HasFrontend(V3EffectiveStackContract stack, V3SolutionStrategy? strategy) =>
        !stack.Frontend.Contains("conforme", StringComparison.OrdinalIgnoreCase) ||
        strategy?.KeyComponents.Any(component => component.Contains("Frontend", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasBackend(V3EffectiveStackContract stack, V3SolutionStrategy? strategy, bool hasFrontend) =>
        stack.Backend.Contains(".NET", StringComparison.OrdinalIgnoreCase) ||
        !stack.Backend.Contains("conforme", StringComparison.OrdinalIgnoreCase) ||
        hasFrontend ||
        strategy?.KeyComponents.Any(component => component.Contains("API", StringComparison.OrdinalIgnoreCase) ||
            component.Contains("backend", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasDatabase(V3EffectiveStackContract stack, V3SolutionStrategy? strategy) =>
        !stack.Database.Contains("conforme", StringComparison.OrdinalIgnoreCase) ||
        strategy?.KeyComponents.Any(component => component.Contains("Persistência", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasAuth(V3SolutionStrategy? strategy) =>
        strategy?.KeyComponents.Any(component => component.Contains("Autenticação", StringComparison.OrdinalIgnoreCase)) == true ||
        strategy?.SecurityConsiderations.Count > 0;

    private static V3KnowledgeReference Ref(string path, string reason) => new(path, reason);
}

public sealed record V3MaterializedMissionContext(
    V3ProjectContextResponse Context,
    IReadOnlyList<V3KnowledgeReference> Knowledge,
    string? ContextDirectory);

public static class V3MissionContextMaterializer
{
    public static V3MaterializedMissionContext Materialize(
        V3ProjectContextResponse context,
        string missionId,
        IReadOnlyList<V3KnowledgeReference> knowledge)
    {
        if (string.IsNullOrWhiteSpace(context.Repository) ||
            context.Repository.Contains("://", StringComparison.Ordinal))
        {
            return new V3MaterializedMissionContext(context, knowledge, null);
        }

        var repository = Path.GetFullPath(context.Repository);
        var contextDirectory = Path.Combine(repository, ".poseidon", "context", missionId);
        Directory.CreateDirectory(contextDirectory);
        Directory.CreateDirectory(Path.Combine(contextDirectory, "artifacts"));
        Directory.CreateDirectory(Path.Combine(contextDirectory, "documents"));
        Directory.CreateDirectory(Path.Combine(contextDirectory, "knowledge"));

        var artifacts = context.Artifacts.Select((artifact, index) =>
        {
            var readable = TryCopyFile(
                artifact.PathReference,
                Path.Combine(contextDirectory, "artifacts", $"{index + 1:00}-{SafeFileName(artifact.Name)}"));
            return artifact with { ReadablePath = readable };
        }).ToArray();

        var materializedKnowledge = knowledge.Select((reference, index) =>
        {
            var source = ResolveKnowledgePath(reference.Path);
            var extension = Path.GetExtension(source ?? reference.Path);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".md";
            var readable = source is null
                ? null
                : TryCopyFile(
                    source,
                    Path.Combine(contextDirectory, "knowledge", $"{index + 1:00}-{SafeFileName(Path.GetFileNameWithoutExtension(reference.Path))}{extension}"));
            return reference with { ReadablePath = readable };
        }).ToArray();

        var documents = context.Documents.Select((document, index) =>
        {
            var extension = Path.GetExtension(document.CatalogPath ?? document.Title);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".md";
            var baseName = Path.GetFileNameWithoutExtension(document.Title);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = document.Title;
            var readable = document.ReadablePath is null
                ? null
                : TryCopyFile(
                    document.ReadablePath,
                    Path.Combine(contextDirectory, "documents", $"{index + 1:00}-{SafeFileName(baseName)}{extension}"));
            return document with { ReadablePath = readable ?? document.ReadablePath };
        }).ToArray();

        var materializedContext = context with { Artifacts = artifacts, Documents = documents };
        WriteIndex(contextDirectory, materializedContext, artifacts, documents, materializedKnowledge);
        return new V3MaterializedMissionContext(
            materializedContext,
            materializedKnowledge,
            contextDirectory);
    }

    private static string? TryCopyFile(string sourcePath, string destinationPath)
    {
        try
        {
            var source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source))
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(source, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveKnowledgePath(string path)
    {
        var clean = path.Split('#', 2)[0];
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, clean.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(Directory.GetCurrentDirectory(), clean.Replace('/', Path.DirectorySeparatorChar)),
            Path.GetFullPath(clean),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void WriteIndex(
        string contextDirectory,
        V3ProjectContextResponse context,
        IReadOnlyList<V3ArtifactReference> artifacts,
        IReadOnlyList<V3DocumentReference> documents,
        IReadOnlyList<V3KnowledgeReference> knowledge)
    {
        var lines = new List<string>
        {
            "# Poseidon Mission Context",
            "",
            $"ProjectId: {context.ProjectId}",
            $"Project: {context.ProjectName}",
            $"GeneratedAt: {DateTimeOffset.UtcNow:O}",
            "",
            "## Artifacts",
        };
        lines.AddRange(artifacts.Select(artifact =>
            $"- {artifact.Role} | {artifact.Name} | sha256={artifact.Sha256} | path={artifact.ReadablePath ?? "UNAVAILABLE"}"));
        lines.Add("");
        lines.Add("## Documents");
        lines.AddRange(documents.Select(document =>
            $"- {document.SourceRole ?? "catalog"} | {document.Title} | version={document.CurrentVersion} | sha256={document.Sha256 ?? "UNKNOWN"} | path={document.ReadablePath ?? "UNAVAILABLE"}"));
        lines.Add("");
        lines.Add("## Knowledge");
        lines.AddRange(knowledge.Select(item =>
            $"- {item.Path} | {item.Reason} | path={item.ReadablePath ?? "UNAVAILABLE"}"));
        File.WriteAllText(Path.Combine(contextDirectory, "INDEX.md"), string.Join(Environment.NewLine, lines));
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var value = new string(name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(value) ? "file" : value;
    }
}

public static class V3MissionCompiler
{
    public const string BuildMissionContractVersion = "v3.1";

    private static readonly JsonSerializerOptions MissionPlanJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static V3BuildMissionRecord CompileBuildMission(
        V3ProjectContextResponse context,
        V3RecommendedExecutor recommended,
        string? overrideExecutor,
        DateTimeOffset now)
    {
        var missionId = UlidValue.New(now).ToString();
        var knowledge = V3KnowledgeSelector.Select(context.EffectiveStack, context.SolutionStrategy ?? context.State?.SolutionStrategy, context.Artifacts);
        var executor = string.IsNullOrWhiteSpace(overrideExecutor)
            ? recommended
            : new V3RecommendedExecutor(overrideExecutor.Trim(), "OVERRIDDEN", "Human/operator override.");
        var package = V3MissionContextMaterializer.Materialize(context, missionId, knowledge);
        var plan = BuildStructuredMissionPlan(package.Context, package.Knowledge, executor);
        var text = ComposeMissionText(package.Context, package.Knowledge, executor, plan);
        return new V3BuildMissionRecord(
            missionId,
            context.ProjectId,
            "BUILD",
            1,
            now,
            "Bruna",
            "write-capable project executor",
            text,
            package.Context.Artifacts,
            package.Knowledge,
            context.EffectiveStack,
            context.Deadline,
            context.Repository,
            "COMPILED",
            executor)
        {
            PrimaryRequirementsCoverage = context.PrimaryRequirementsCoverage,
            MissionContractVersion = BuildMissionContractVersion,
            StructuredMissionPlanJson = JsonSerializer.Serialize(plan, MissionPlanJson),
        };
    }

    public static V3BuildMissionRecord CompileValidationMission(
        V3ProjectContextResponse context,
        V3BuildExecutionRecord? buildExecution,
        V3RecommendedExecutor executor,
        DateTimeOffset now)
    {
        var missionId = UlidValue.New(now).ToString();
        var knowledge = V3KnowledgeSelector.SelectForValidation(context.EffectiveStack, context.SolutionStrategy ?? context.State?.SolutionStrategy, context.Artifacts);
        var package = V3MissionContextMaterializer.Materialize(context, missionId, knowledge);
        var text = ComposeValidationMissionText(package.Context, package.Knowledge, executor, buildExecution);
        return new V3BuildMissionRecord(
            missionId,
            context.ProjectId,
            "VALIDATE",
            1,
            now,
            "Bruna",
            "write-capable project executor with browser validation capability",
            text,
            package.Context.Artifacts,
            package.Knowledge,
            context.EffectiveStack,
            context.Deadline,
            context.Repository,
            "COMPILED",
            executor)
        {
            PrimaryRequirementsCoverage = context.PrimaryRequirementsCoverage,
            MissionContractVersion = "v3.validation.2",
        };
    }

    public static V3BuildMissionRecord CompilePlatformMaintenanceMission(
        string projectId,
        string projectName,
        string repository,
        V3CompilePlatformMaintenanceMissionRequest request,
        V3RecommendedExecutor executor,
        DateTimeOffset now)
    {
        var missionId = UlidValue.New(now).ToString();
        var selectedExecutor = string.IsNullOrWhiteSpace(request.RecommendedExecutorAlias)
            ? executor
            : new V3RecommendedExecutor(request.RecommendedExecutorAlias.Trim(), "OVERRIDDEN", "Human/operator override.");
        var text = ComposePlatformMaintenanceMissionText(projectName, repository, request, selectedExecutor);
        return new V3BuildMissionRecord(
            missionId,
            projectId,
            "PLATFORM_MAINTENANCE",
            1,
            now,
            "Bruna",
            AgentRoles.PlatformMaintainer,
            text,
            [],
            [],
            new V3EffectiveStackContract(
                "React/TypeScript/Vite",
                ".NET",
                "SQLite/PostgreSQL",
                "Poseidon V3 control plane",
                "xUnit/Vitest/Playwright",
                ["PLATFORM_MAINTENANCE_CONTEXT"]),
            null,
            repository,
            "COMPILED",
            selectedExecutor)
        {
            MissionContractVersion = "v3.platform-maintenance.1",
        };
    }

    private static string ComposeMissionText(
        V3ProjectContextResponse context,
        IReadOnlyList<V3KnowledgeReference> knowledge,
        V3RecommendedExecutor executor,
        V3StructuredMissionPlan plan)
    {
        var state = context.State;
        var nonScope = NonScopeItems(state?.ImportantConstraints);
        var lines = new List<string>
        {
            $"# BUILD MISSION — {context.ProjectName}",
            "",
            $"MissionContractVersion: {plan.ContractVersion}",
            "",
            "## EXECUTION BRIEF",
            $"WHO: {plan.Brief.Who}",
            $"WHAT: {plan.Brief.What}",
            $"CONTEXT: {plan.Brief.Context}",
            $"FLOW: {plan.Brief.Flow}",
            $"PROOF: {plan.Brief.Proof}",
            "",
            "## CANONICAL MISSION PLAN",
        };
        foreach (var dimension in plan.Dimensions)
        {
            lines.Add("");
            lines.Add($"### {dimension.Number} {dimension.Title}");
            lines.Add($"Source: {dimension.Source}");
            lines.AddRange(dimension.Items.Count == 0 ? ["- N/A"] : dimension.Items.Select(item => $"- {item}"));
        }

        lines.AddRange([
            "",
            "## PROJECT OBJECTIVE",
            state?.ProductGoal ?? context.ProjectSummary ?? context.OriginalIntent ?? $"Construir {context.ProjectName}.",
            "",
            "## BUSINESS CONTEXT",
            context.ProjectSummary ?? context.OriginalIntent ?? "Use os requisitos e artefatos originais como fonte de verdade.",
            "",
            "## ORIGINAL REQUIREMENTS",
            "- Fonte primária: documentos e anexos originais associados ao projeto.",
            "- Não substitua os originais por este resumo; consulte os artefatos quando houver dúvida.",
            $"- PrimaryRequirementsCoverage: {PrimaryCoveragePercent(context.PrimaryRequirementsCoverage)}%.",
            "- Antes de implementar, leia integralmente os artefatos originais referenciados quando forem fontes primárias de requisitos.",
        ]);
        lines.AddRange((state?.Requirements ?? context.Requirements).Select(item => $"- {item}"));
        lines.Add("");
        lines.Add("## NON-SCOPE");
        lines.AddRange(nonScope.Length == 0
            ? ["- Nenhuma exclusão adicional além das fontes originais e decisões explícitas do usuário."]
            : nonScope.Select(item => $"- {item}"));
        lines.Add("");
        lines.Add("## ORIGINAL ARTIFACTS");
        if (context.Artifacts.Count == 0 && context.Documents.Count == 0)
        {
            lines.Add("- Nenhum artefato externo associado até a compilação; use a intenção original persistida.");
        }
        else
        {
            lines.AddRange(context.Artifacts.Select(artifact =>
                $"- {artifact.Role}: {artifact.Name} ({artifact.ContentType}, sha256={artifact.Sha256}) — path: {artifact.ReadablePath ?? "UNAVAILABLE"}"));
            lines.AddRange(context.Documents.Select(document =>
                $"- document:{document.DocumentId} — {document.Title} ({document.Kind}, state={document.State}, version={document.CurrentVersion}, sha256={document.Sha256 ?? "UNKNOWN"}) — path: {document.ReadablePath ?? "UNAVAILABLE"}"));
        }

        if (HasBranding(context.Brand))
        {
            lines.AddRange([
                "",
                "## BRANDING / PROVIDED VISUAL IDENTITY",
                "- A marca declarada no cadastro do projeto é contexto explícito do usuário.",
                "- Preserve essa identidade visual quando houver frontend/UI aplicável.",
                $"- Logo: {context.Brand?.LogoUrl ?? "not provided"}",
                $"- Primary color: {context.Brand?.PrimaryColor ?? "not provided"}",
                $"- Secondary color: {context.Brand?.SecondaryColor ?? "not provided"}",
                $"- Typography: {context.Brand?.Typography ?? "not provided"}",
            ]);
        }

        lines.AddRange([
            "",
            "## EFFECTIVE STACK",
            $"- Frontend: {context.EffectiveStack.Frontend}",
            $"- Backend: {context.EffectiveStack.Backend}",
            $"- Database: {context.EffectiveStack.Database}",
            $"- Architecture: {context.EffectiveStack.Architecture}",
            $"- Testing: {context.EffectiveStack.Testing}",
            "",
            "## SOLUTION STRATEGY",
            $"- Complexity: {state?.SolutionStrategy?.Complexity ?? "SIMPLE"}",
            $"- Architecture summary: {state?.SolutionStrategy?.ArchitectureSummary ?? "Implementar de forma proporcional ao risco, seguindo o baseline do produto."}",
            $"- Architecture approval required: {(state?.SolutionStrategy?.ArchitectureApprovalRequired == true ? "YES" : "NO")}",
        ]);
        if (state?.SolutionStrategy is { } strategy)
        {
            AddMissionList(lines, "Key components", strategy.KeyComponents);
            AddMissionList(lines, "Integration points", strategy.IntegrationPoints);
            if (!string.IsNullOrWhiteSpace(strategy.DataStrategy))
            {
                lines.Add($"- Data strategy: {strategy.DataStrategy}");
            }

            AddMissionList(lines, "Security considerations", strategy.SecurityConsiderations);
            AddMissionList(lines, "Operational considerations", strategy.OperationalConsiderations);
            AddMissionList(lines, "Technical risks", strategy.TechnicalRisks);
            AddMissionList(lines, "Important tradeoffs", strategy.ImportantTradeoffs);
            AddMissionList(lines, "Human decisions required", strategy.HumanDecisionsRequired);
        }

        lines.AddRange([
            "",
            "## ARCHITECTURAL CONSTRAINTS",
            "- Preserve o fluxo V3: UNDERSTAND → BUILD → VALIDATE → HUMAN ACCEPTANCE.",
            "- Use uma única missão ampla e um executor persistente por projeto; não recrie workflows intermediários ou loops de revisão obrigatórios.",
            "- O executor pode assumir competências de arquitetura, backend, frontend, dados e QA conforme necessário.",
            "- Use decisões técnicas reversíveis sem pedir autorização humana.",
            "- Nunca coloque segredos em Git, logs, prompts ou evidências.",
            "",
            "## PROVIDED FRONTEND POLICY",
            state?.ProvidedFrontend == true
                ? "- Há frontend/protótipo fornecido: preservar e evoluir; não reconstruir arbitrariamente."
                : "- Se houver frontend existente no repositório, preservar e evoluir; não substituir sem motivo técnico.",
            "",
            "## DATABASE/RUNTIME",
            $"- Runtime esperado: {context.RuntimeEnvironment}.",
            "- API e frontend rodam no host; banco pode usar Docker somente quando necessário.",
            "- Ambiente deve ser simples, estável e documentado para homologação local.",
            "",
            "## USERS/ROLES",
        ]);
        if (state?.PrimaryUsers.Count > 0)
        {
            lines.AddRange(state.PrimaryUsers.Select(user => $"- {user}"));
        }
        else
        {
            lines.Add("- Identificar perfis a partir dos requisitos originais e implementar RBAC quando solicitado.");
        }

        lines.Add("");
        lines.Add("## CORE BUSINESS RULES");
        lines.AddRange((state?.Requirements ?? context.Requirements).Select(item => $"- {item}"));
        lines.Add("");
        lines.Add("## ACCEPTANCE CRITERIA");
        lines.AddRange((state?.AcceptanceCriteria ?? context.AcceptanceCriteria).Select(item => $"- {item}"));
        lines.Add("");
        lines.Add("## QUALITY EXPECTATIONS");
        lines.AddRange(knowledge.Select(item => $"- Aplicar conhecimento relevante: {item.Path} — {item.Reason} — path: {item.ReadablePath ?? "UNAVAILABLE"}"));
        lines.AddRange([
            "",
            "## REPOSITORY",
            $"- {context.Repository}",
            "",
            "## HOW TO RUN",
            "- Ao implementar, descubra e registre comandos reais de start/stop/status/build/test do produto.",
            "- Não dependa de mocks no caminho crítico.",
            "",
            "## AUTONOMY CONTRACT",
            "- Trabalhe autonomamente até a missão terminar.",
            "- Não pare após checkpoints intermediários.",
            "- Não peça autorização entre funcionalidades.",
            "- Consulte os artefatos originais quando houver dúvida.",
            "- Execute o sistema real, corrija problemas encontrados e reteste.",
            "- Faça commits/checkpoints úteis.",
            "- Preserve trabalho existente; não use reset/clean destrutivo.",
            "- Não faça push sem autorização explícita.",
            "- Só pare quando a Definition of Done estiver satisfeita ou existir blocker genuinamente humano.",
            "- Se completar apenas uma parte, CONTINUE.",
            "",
            "## DEFINITION OF DONE",
            "- Produto compila.",
            "- Frontend, API e database integrados quando aplicável.",
            "- Fluxo principal executável.",
            "- Testes próprios relevantes verdes.",
            "- Nenhuma funcionalidade deliberadamente mockada no caminho crítico.",
            "- Commits feitos.",
            "- Relatório final claro com o que foi implementado e como rodar.",
            "",
            "## MACHINE-READABLE EXIT CONTRACT",
            "- Checkpoints intermediários podem conter POSEIDON_PROGRESS_CHECKPOINT, mas isso NÃO encerra a missão.",
            "- Somente finalize com POSEIDON_MISSION_COMPLETE quando a Definition of Done estiver satisfeita.",
            "- Se existir blocker genuinamente humano, finalize com POSEIDON_HUMAN_BLOCKER seguido da descrição objetiva.",
            "- Não aguarde autorização depois de checkpoints; continue autonomamente.",
            "",
            "## FINAL REPORT FORMAT",
            "- Status geral.",
            "- URLs/comandos de execução.",
            "- Funcionalidades implementadas.",
            "- Testes executados.",
            "- Commits.",
            "- Blockers reais, se houver.",
            "",
            "## RECOMMENDED EXECUTOR",
            $"- {executor.AccountAlias ?? "BLOCKED"} — {executor.Reason}",
        ]);
        return string.Join(Environment.NewLine, lines);
    }

    private static V3StructuredMissionPlan BuildStructuredMissionPlan(
        V3ProjectContextResponse context,
        IReadOnlyList<V3KnowledgeReference> knowledge,
        V3RecommendedExecutor executor)
    {
        var state = context.State;
        var requirements = state?.Requirements.Count > 0 ? state.Requirements : context.Requirements;
        var criteria = state?.AcceptanceCriteria.Count > 0 ? state.AcceptanceCriteria : context.AcceptanceCriteria;
        var nonScope = NonScopeItems(state?.ImportantConstraints);
        var hasFrontend = state?.ProvidedFrontend == true ||
            context.Artifacts.Any(artifact =>
                artifact.Role.Contains("frontend", StringComparison.OrdinalIgnoreCase) ||
                artifact.Role.Contains("prototype", StringComparison.OrdinalIgnoreCase));
        var complexity = state?.SolutionStrategy?.Complexity ?? "SIMPLE";
        var flow = string.Equals(complexity, "COMPLEX", StringComparison.OrdinalIgnoreCase)
            ? "inspect sources → map domain/integration contracts → implement vertical slices → integrate → self-verify → fix/retest"
            : "inspect sources → implement integrated vertical solution → build/test → fix/retest";

        return new V3StructuredMissionPlan(
            BuildMissionContractVersion,
            new V3ExecutionBrief(
                executor.AccountAlias ?? "eligible write-capable Project Executor selected by Poseidon",
                state?.ProductGoal ?? context.ProjectSummary ?? context.OriginalIntent ?? $"Deliver {context.ProjectName}.",
                $"Primary requirements coverage {PrimaryCoveragePercent(context.PrimaryRequirementsCoverage)}%; {context.Artifacts.Count} artifact(s); {knowledge.Count} knowledge source(s).",
                flow,
                "Acceptance criteria + quality gates + Definition of Done + POSEIDON_MISSION_COMPLETE."),
            [
                Dimension(1, "ROLE", "Runtime", "Senior autonomous software engineer; one persistent executor, not a swarm."),
                Dimension(2, "MISSION", "Bruna", state?.ProductGoal ?? context.ProjectSummary ?? context.OriginalIntent ?? $"Deliver executable integrated product for {context.ProjectName}."),
                Dimension(3, "BUSINESS CONTEXT", "Bruna/Context", context.ProjectSummary ?? context.OriginalIntent ?? "Use original sources as canonical business context."),
                Dimension(4, "CURRENT STATE", "Runtime/Context", [
                    context.Artifacts.Count > 0
                        ? "Provided artifacts are materialized in the mission context and must be inspected."
                        : "No external artifact was materialized; inspect repository state before changing files.",
                    $"Repository: {context.Repository ?? "local repository managed by Poseidon"}.",
                ]),
                Dimension(5, "SOURCES OF TRUTH", "Runtime/Context", [
                    $"PrimaryRequirementsCoverage: {PrimaryCoveragePercent(context.PrimaryRequirementsCoverage)}%",
                    .. context.Artifacts.Select(artifact => $"{artifact.Role}: {artifact.Name} — path={artifact.ReadablePath ?? "UNAVAILABLE"}"),
                    .. context.Documents.Select(document => $"{document.SourceRole ?? "document"}: {document.Title} — path={document.ReadablePath ?? "UNAVAILABLE"}"),
                    .. knowledge.Select(item => $"Knowledge: {item.Path} — path={item.ReadablePath ?? "UNAVAILABLE"}"),
                ]),
                Dimension(6, "SCOPE", "Bruna", requirements.Count == 0 ? ["All explicit scope in original sources."] : requirements),
                Dimension(7, "NON-SCOPE", "Bruna", nonScope.Length == 0 ? ["No additional exclusion beyond original sources unless explicitly declared by the user."] : nonScope),
                Dimension(8, "FUNCTIONAL REQUIREMENTS", "Bruna", requirements.Count == 0 ? ["Derive faithfully from primary requirements before implementing."] : requirements),
                Dimension(9, "NON-FUNCTIONAL REQUIREMENTS", "Bruna/Governance", [
                    $"Responsive/UI required: {(hasFrontend ? "YES" : "according to explicit UI scope")}",
                    "Security, operability, accessibility and performance expectations come from selected product knowledge and baseline unless explicit in sources.",
                ]),
                Dimension(10, "ARCHITECTURAL CONSTRAINTS", "Bruna/Runtime", [
                    $"Frontend: {context.EffectiveStack.Frontend}",
                    $"Backend: {context.EffectiveStack.Backend}",
                    $"Database: {context.EffectiveStack.Database}",
                    $"Architecture: {context.EffectiveStack.Architecture}",
                    $"SolutionStrategy complexity: {complexity}",
                    $"Architecture approval required: {(state?.SolutionStrategy?.ArchitectureApprovalRequired == true ? "YES" : "NO")}",
                ]),
                Dimension(11, "TOOLS & PERMISSIONS", "Runtime", [
                    "Workspace write allowed inside product repository.",
                    "Shell/build/test/browser allowed as required by mission.",
                    "Git local commits expected; push is not allowed unless explicitly authorized.",
                    "Secrets must not be written to Git, logs or prompts.",
                ]),
                Dimension(12, "EXECUTION STRATEGY", "Bruna", flow),
                Dimension(13, "WORKFLOW GRAPH", "Bruna", BuildWorkflowGraph(complexity, hasFrontend)),
                Dimension(14, "VALIDATION LOOPS", "Runtime/Governance", "During BUILD: implement → build → test → analyze → fix → retest. Final ValidationMission is separate."),
                Dimension(15, "QUALITY GATES", "Runtime/Governance", [
                    "Build gate applicable to stack.",
                    "Relevant tests gate.",
                    "Integration/runtime smoke gate.",
                    "Critical requirements and acceptance criteria gate.",
                    $"Acceptance criteria count: {criteria.Count}.",
                ]),
                Dimension(16, "DECISION POLICY", "Governance", [
                    "Executor decides reversible technical choices.",
                    "Executor blocks only for irreversible business decisions, external credentials, unsafe destructive actions, or unresolved material architecture decisions.",
                ]),
                Dimension(17, "STATE & RECOVERY", "Runtime", [
                    "MissionId assigned by Poseidon.",
                    $"Repository: {context.Repository ?? "managed local repository"}",
                    "Runtime tracks InitialHead/CurrentHead, commits, continue behavior, quota failover and crash recovery.",
                ]),
                Dimension(18, "DELIVERABLES", "Bruna/Governance", [
                    "Executable integrated product.",
                    "Source code, schema/migrations when applicable, minimal operational documentation, relevant tests and useful commits.",
                ]),
                Dimension(19, "DEFINITION OF DONE", "Governance", [
                    "Product builds.",
                    "Frontend/API/database integrated when applicable.",
                    "Critical path is real, not mocked.",
                    "Relevant tests pass.",
                    "Final marker only when complete.",
                ]),
                Dimension(20, "COMMUNICATION POLICY", "Runtime/Governance", [
                    "No approval requests between features.",
                    "Use meaningful checkpoints only.",
                    "POSEIDON_PROGRESS_CHECKPOINT does not end mission.",
                    "POSEIDON_MISSION_COMPLETE ends BUILD only when DoD is satisfied.",
                ]),
            ]);
    }

    private static IReadOnlyList<string> BuildWorkflowGraph(string complexity, bool hasFrontend) =>
        string.Equals(complexity, "COMPLEX", StringComparison.OrdinalIgnoreCase)
            ? [
                "sources → domain/integration contract map",
                "domain/integration contract map → backend/API/persistence",
                hasFrontend ? "backend/API/persistence → frontend integration" : "backend/API/persistence → runtime smoke",
                "implementation branches → self-verification loop",
                "self-verification loop → final report/complete marker",
            ]
            : [
                "sources → inspect repository",
                hasFrontend ? "inspect repository → implement frontend/API/database vertical flow" : "inspect repository → implement API/domain/persistence flow",
                "implementation → build/test",
                "build/test → fix/retest",
                "fix/retest → final report/complete marker",
            ];

    private static V3MissionPlanDimension Dimension(int number, string title, string source, string item) =>
        new(number, title, source, [item]);

    private static V3MissionPlanDimension Dimension(int number, string title, string source, IReadOnlyList<string> items) =>
        new(number, title, source, items);

    private static bool HasBranding(V3ProjectBrandContext? brand) =>
        brand is not null &&
        (!string.IsNullOrWhiteSpace(brand.LogoUrl) ||
         !string.IsNullOrWhiteSpace(brand.PrimaryColor) ||
         !string.IsNullOrWhiteSpace(brand.SecondaryColor) ||
         !string.IsNullOrWhiteSpace(brand.Typography));

    private static string[] NonScopeItems(IReadOnlyList<string>? constraints)
    {
        if (constraints is null || constraints.Count == 0)
        {
            return [];
        }

        return constraints
            .Select(value => value.Trim())
            .Select(value => StripNonScopePrefix(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string StripNonScopePrefix(string value)
    {
        const string ForaDeEscopo = "Fora de escopo:";
        const string NaoEscopo = "Não escopo:";
        const string NaoEscopoAscii = "Nao escopo:";
        if (value.StartsWith(ForaDeEscopo, StringComparison.OrdinalIgnoreCase))
        {
            return value[ForaDeEscopo.Length..].Trim();
        }

        if (value.StartsWith(NaoEscopo, StringComparison.OrdinalIgnoreCase))
        {
            return value[NaoEscopo.Length..].Trim();
        }

        if (value.StartsWith(NaoEscopoAscii, StringComparison.OrdinalIgnoreCase))
        {
            return value[NaoEscopoAscii.Length..].Trim();
        }

        return string.Empty;
    }

    private static void AddMissionList(List<string> lines, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        lines.Add($"- {label}:");
        lines.AddRange(values.Select(value => $"  - {value}"));
    }

    private static string ComposeValidationMissionText(
        V3ProjectContextResponse context,
        IReadOnlyList<V3KnowledgeReference> knowledge,
        V3RecommendedExecutor executor,
        V3BuildExecutionRecord? buildExecution)
    {
        var state = context.State;
        var lines = new List<string>
        {
            $"# VALIDATION MISSION — {context.ProjectName}",
            "",
            "## VALIDATION OBJECTIVE",
            "Validar tecnicamente o produto entregue contra os requisitos originais, corrigir bugs encontrados e deixar o projeto pronto para homologação humana.",
            "",
            "## ORIGINAL REQUIREMENTS AND ARTIFACTS",
            "- Leia integralmente os artefatos originais referenciados como fontes primárias antes de validar.",
            $"- PrimaryRequirementsCoverage: {PrimaryCoveragePercent(context.PrimaryRequirementsCoverage)}%.",
        };
        lines.AddRange(context.Artifacts.Select(artifact =>
            $"- {artifact.Role}: {artifact.Name} ({artifact.ContentType}, sha256={artifact.Sha256}) — path: {artifact.ReadablePath ?? "UNAVAILABLE"}"));
        lines.AddRange(context.Documents.Select(document =>
            $"- document:{document.DocumentId} — {document.Title} ({document.Kind}, state={document.State}, version={document.CurrentVersion}, sha256={document.Sha256 ?? "UNKNOWN"}) — path: {document.ReadablePath ?? "UNAVAILABLE"}"));
        lines.AddRange([
            "",
            "## PRODUCT CONTEXT",
            state?.ProductGoal ?? context.ProjectSummary ?? context.OriginalIntent ?? $"Validar {context.ProjectName}.",
            "",
            "## ACCEPTANCE CRITERIA",
        ]);
        lines.AddRange((state?.AcceptanceCriteria ?? context.AcceptanceCriteria).Select(item => $"- {item}"));
        lines.AddRange([
            "",
            "## EFFECTIVE STACK",
            $"- Frontend: {context.EffectiveStack.Frontend}",
            $"- Backend: {context.EffectiveStack.Backend}",
            $"- Database: {context.EffectiveStack.Database}",
            $"- Testing: {context.EffectiveStack.Testing}",
            "",
            "## REPOSITORY AND BUILD RESULT",
            $"- Repository: {context.Repository}",
            $"- Current HEAD before validation: {buildExecution?.CurrentHead ?? "unknown"}",
            $"- BuildExecutionId: {buildExecution?.MissionExecutionId ?? "not recorded"}",
            $"- Build executor: {buildExecution?.ExecutorAccountId ?? "unknown"}",
            "",
            "## RUNTIME",
            "- Use scripts operacionais do produto quando existirem: scripts/dev-up.sh, scripts/dev-down.sh, scripts/dev-status.sh, scripts/dev-access.sh.",
            "- API e frontend rodam no host; banco pode usar Docker somente quando necessário.",
            "- Não crie sandbox ou containers para API/frontend.",
            "",
            "## BROWSER-FIRST POLICY",
            "- Se uma funcionalidade é usada pelo cliente pela interface, valide por navegador real.",
            "- Prova funcional esperada: Browser → Frontend → API real → Banco real → resultado visível novamente no Browser.",
            "- API e banco podem ser usados para diagnóstico, mas não substituem prova funcional pelo frontend.",
            "- Execute ./poseidon tools e2e ou o doctor equivalente antes de alegar falta de navegador.",
            "",
            "## QUALITY CHECKLIST",
            "- Use o checklist materializado nesta missão como checklist genérico obrigatório.",
            "- Não transforme o checklist em 244 tarefas ou 244 chamadas LLM.",
            "- Classifique internamente cada item aplicável como PASS, FIXED, N/A ou FAIL.",
            "- FAIL aplicável final precisa ser ZERO.",
            "",
            "## KNOWLEDGE SOURCES",
        ]);
        lines.AddRange(knowledge.Select(item => $"- {item.Path} — {item.Reason} — path: {item.ReadablePath ?? "UNAVAILABLE"}"));
        lines.AddRange([
            "",
            "## AUTONOMY CONTRACT",
            "- Teste como usuário real, encontre problemas, corrija, reteste e faça regressão.",
            "- Não reporte cada bug ao humano.",
            "- Não peça autorização entre correções técnicas reversíveis.",
            "- Faça commits úteis das correções.",
            "- Continue autonomamente até satisfazer a conclusão de validação ou encontrar blocker genuinamente humano.",
            "",
            "## VALIDATION REPORT SUMMARY REQUIRED",
            "- RequirementsChecked: <n>",
            "- RequirementsPassed: <n>",
            "- RequirementsFailed: <n>",
            "- ChecklistTotal: <n>",
            "- ChecklistPass: <n>",
            "- ChecklistFixed: <n>",
            "- ChecklistNA: <n>",
            "- ChecklistFail: <n>",
            "- BrowserTestsPassed: <n>",
            "- BrowserTestsFailed: <n>",
            "- BrowserTestsSkipped: <n>",
            "- BugsFound: <n>",
            "- BugsFixed: <n>",
            "- BugsRemaining: <n>",
            "",
            "## STRUCTURED VALIDATION MANIFEST REQUIRED",
            "- Antes do marcador final, publique um bloco `POSEIDON_VALIDATION_MANIFEST` com JSON válido.",
            "- O JSON precisa conter: checklistVersion, checklistSha256, missionId, executionId, requirements[], checklist[], browserRuns[], handoffReadiness.",
            "- requirements[] precisa ter um item por critério/requisito verificado, com requirementId, status, evidenceReference e notes.",
            "- checklist[] precisa ter um item por check do checklist usado, com checkId, status, evidenceType, evidenceReference, notes e executedAt.",
            "- Status permitidos: PASS, FIXED, N_A, FAIL.",
            "- Todo N_A precisa de razão em notes.",
            "- Todo PASS/FIXED precisa de evidenceReference.",
            "- Projeto com UI precisa de browserRuns[] com execução real de browser, exitCode, baseUrl, viewports, testFiles, passed/failed/skipped, consoleErrors e networkErrors.",
            "- Projeto com UI precisa de handoffReadiness com applicationUrl, runtimeReachable, healthPass, cleanAcceptanceEnvironment, accessInformationCaptured e testCredentialsCapturedWhenApplicable.",
            "- Não inclua senhas de produção. Credenciais locais de teste podem ser referenciadas como TEST_ONLY, mas não envie segredo externo.",
            "- Exemplo mínimo de cabeçalho:",
            "```text",
            "POSEIDON_VALIDATION_MANIFEST",
            "{ \"checklistVersion\": \"checklist-auto-auditoria-ia\", \"checklistSha256\": \"...\", \"missionId\": \"...\", \"executionId\": \"...\", \"requirements\": [], \"checklist\": [], \"browserRuns\": [], \"handoffReadiness\": null }",
            "```",
            "",
            "## MACHINE-READABLE EXIT CONTRACT",
            "- Checkpoints intermediários podem conter POSEIDON_PROGRESS_CHECKPOINT, mas isso NÃO encerra a missão.",
            "- POSEIDON_MISSION_COMPLETE NÃO conclui uma validação.",
            "- Somente finalize com POSEIDON_VALIDATION_COMPLETE quando requisitos obrigatórios tiverem sido considerados, browser/E2E tiver sido executado quando aplicável, checklist concluído, FAIL final = 0, regressão realizada e nenhum blocker conhecido existir.",
            "- Se existir blocker genuinamente humano, finalize com POSEIDON_HUMAN_BLOCKER seguido da descrição objetiva.",
            "",
            "## RECOMMENDED EXECUTOR",
            $"- {executor.AccountAlias ?? "BLOCKED"} — {executor.Reason}",
        ]);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ComposePlatformMaintenanceMissionText(
        string projectName,
        string repository,
        V3CompilePlatformMaintenanceMissionRequest request,
        V3RecommendedExecutor executor)
    {
        var lines = new List<string>
        {
            $"# PLATFORM MAINTENANCE MISSION — {projectName}",
            "",
            "## OBJECTIVE",
            "Corrigir um defeito ou lacuna operacional do próprio Poseidon, preservando a arquitetura V3 congelada.",
            "",
            "## USER REPORTED ISSUE",
            request.Issue.Trim(),
            "",
            "## SAFE DIAGNOSTIC CONTEXT",
            string.IsNullOrWhiteSpace(request.Diagnostics) ? "- Nenhum diagnóstico estruturado informado." : request.Diagnostics.Trim(),
            "",
            "## REPRODUCTION",
            string.IsNullOrWhiteSpace(request.Reproduction) ? "- Reproduza pelo caminho mais seguro e local possível antes de alterar código." : request.Reproduction.Trim(),
            "",
            "## REPOSITORY",
            $"- {repository}",
            "",
            "## BOUNDARIES",
            "- Trabalhe somente no repositório Poseidon.",
            "- Não altere main.",
            "- Não use force push.",
            "- Não apague dados, evidências ou histórico.",
            "- Não exponha segredos.",
            "- Não altere Prisma ou Indicadores.",
            "- Não reintroduza Council, cards V1, micro-cards, workflow de 9 fases ou ProductE2ERunner.",
            "- Preserve trabalho preexistente no worktree.",
            "",
            "## EXPECTED WORKFLOW",
            "- Inspecione estado Git.",
            "- Reproduza ou prove a causa determinística do problema.",
            "- Faça o menor fix coerente.",
            "- Rode testes focados.",
            "- Rode build/verify proporcional ao risco.",
            "- Faça commit local coerente.",
            "- Relate evidência, arquivos alterados, testes e qualquer blocker real.",
            "",
            "## DEFINITION OF DONE",
            "- Causa explicada.",
            "- Fix implementado.",
            "- Teste/regressão adicionado ou atualizado quando aplicável.",
            "- Gates relevantes verdes.",
            "- Nenhum segredo exposto.",
            "- Nenhuma fronteira proibida violada.",
            "",
            "## MACHINE-READABLE EXIT CONTRACT",
            "- Checkpoints intermediários podem conter POSEIDON_PROGRESS_CHECKPOINT, mas isso NÃO encerra a missão.",
            "- Somente finalize com POSEIDON_MISSION_COMPLETE quando a manutenção estiver concluída.",
            "- Se existir blocker genuinamente humano, finalize com POSEIDON_HUMAN_BLOCKER seguido da descrição objetiva.",
            "",
            "## RECOMMENDED EXECUTOR",
            $"- {executor.AccountAlias ?? "BLOCKED"} — {executor.Reason}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static int PrimaryCoveragePercent(IReadOnlyList<V3SourceCoverage> coverage) =>
        coverage.Count == 0 ? 100 : (int)Math.Round(coverage.Average(item => item.CoveragePercent));
}

public sealed record V3UnderstandAnalyzeRequest
{
    public string? OriginalIntent { get; init; }
    public string? BrunaSummary { get; init; }
    public string? ProductGoal { get; init; }
    public IReadOnlyList<string> PrimaryUsers { get; init; } = [];
    public IReadOnlyList<string> CoreCapabilities { get; init; } = [];
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];
    public IReadOnlyList<string> ImportantConstraints { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public DateTimeOffset? Deadline { get; init; }
    public string? Repository { get; init; }
}

public sealed record V3AuthorizeBuildRequest(string Response, DateTimeOffset? Deadline = null, string? Repository = null);

public sealed record V3CompileBuildMissionRequest(string? RecommendedExecutorAlias = null);

public sealed record V3CompilePlatformMaintenanceMissionRequest(
    string Issue,
    string? Diagnostics = null,
    string? Reproduction = null,
    string? Repository = null,
    string? RecommendedExecutorAlias = null);

public sealed record V3ProjectContextResponse(
    string ProjectId,
    string ProjectName,
    string? OriginalIntent,
    IReadOnlyList<V3ArtifactReference> Artifacts,
    IReadOnlyList<V3DocumentReference> Documents,
    IReadOnlyList<V3PrototypeReference> Prototypes,
    V3ProjectUnderstandState? State,
    string? ProductGoal,
    string? ProjectSummary,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<V3SourceCoverage> PrimaryRequirementsCoverage,
    V3RequirementSourceFacts SourceFacts,
    IReadOnlyList<V3OpenQuestion> OpenQuestions,
    V3EffectiveStackContract EffectiveStack,
    DateTimeOffset? Deadline,
    string? Repository,
    string RuntimeEnvironment,
    string NotificationChannel,
    V3ExecutionCapacityResponse ExecutionCapacity,
    IReadOnlyList<V3ReadinessItem> Readiness,
    string CurrentLifecycleState)
{
    public V3ProjectBrandContext? Brand { get; init; }
    public V3SolutionStrategy? SolutionStrategy { get; init; }
}

public sealed record V3ProjectBrandContext(
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Typography);

public sealed record V3ProjectUnderstandState(
    string ProjectId,
    string Status,
    string LifecycleState,
    string? OriginalIntent,
    string? ProjectSummary,
    string? ProductGoal,
    IReadOnlyList<string> PrimaryUsers,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> ImportantConstraints,
    bool ProvidedFrontend,
    IReadOnlyList<string> DesignReferences,
    V3EffectiveStackContract? EffectiveStack,
    DateTimeOffset? Deadline,
    string? Repository,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static V3ProjectUnderstandState Create(string projectId, DateTimeOffset now) =>
        new(projectId, "DRAFT", "UNDERSTANDING", null, null, null, [], [], [], [], [], [],
            false, [], null, null, null, null, null, now, now);

    public IReadOnlyList<V3SourceCoverage> PrimaryRequirementsCoverage { get; init; } = [];
    public V3RequirementSourceFacts? SourceFacts { get; init; }
    public V3SolutionStrategy? SolutionStrategy { get; init; }
}

public sealed record V3SolutionStrategy(
    string Complexity,
    string ArchitectureSummary,
    IReadOnlyList<string> KeyComponents,
    IReadOnlyList<string> IntegrationPoints,
    string DataStrategy,
    IReadOnlyList<string> SecurityConsiderations,
    IReadOnlyList<string> OperationalConsiderations,
    IReadOnlyList<string> TechnicalRisks,
    IReadOnlyList<string> ImportantTradeoffs,
    IReadOnlyList<string> HumanDecisionsRequired,
    bool ArchitectureApprovalRequired);

public sealed record V3UnderstandAnalysis(
    V3ProjectUnderstandState State,
    IReadOnlyList<V3OpenQuestion> OpenQuestions);

public sealed record V3ArtifactReference(
    string ArtifactId,
    string Name,
    string ContentType,
    string Role,
    string Source,
    string PathReference,
    string Sha256,
    string State)
{
    public string? ReadablePath { get; init; }
}

public sealed record V3DocumentReference(
    string DocumentId,
    string Title,
    string Kind,
    string State,
    int CurrentVersion,
    IReadOnlyList<string> Classifications)
{
    public string? CatalogPath { get; init; }
    public string? Sha256 { get; init; }
    public string? ReadablePath { get; init; }
    public string? SourceRole { get; init; }
}

public sealed record V3PrototypeReference(string PrototypeId, string Name, string State, string? SourceDocumentId);

public sealed record V3OpenQuestion(string QuestionId, string Question, string Reason);

public sealed record V3SourceCoverage(
    string ArtifactId,
    string Name,
    string Role,
    int TotalSections,
    int ConsumedSections,
    int CoveragePercent,
    bool Complete,
    string EvidenceProvider,
    int CharacterCount,
    [property: JsonIgnore] string? ContentPreview);

public sealed record V3RequirementSourceFacts(
    DateTimeOffset? Deadline,
    string? DeadlineProvenance,
    string? Authentication,
    string? AuthenticationProvenance,
    string? ProductNotification,
    string? ProductNotificationProvenance,
    string? Frontend,
    string? FrontendProvenance,
    string? Backend,
    string? BackendProvenance,
    string? Database,
    string? DatabaseProvenance,
    string? ItrcRules,
    string? ItrcRulesProvenance,
    int AcceptanceCriteriaCount)
{
    public static V3RequirementSourceFacts Empty { get; } =
        new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, 0);
}

public sealed record V3EffectiveStackContract(
    string Frontend,
    string Backend,
    string Database,
    string Architecture,
    string Testing,
    IReadOnlyList<string> Provenance);

public sealed record V3KnowledgeReference(string Path, string Reason)
{
    public string? ReadablePath { get; init; }
}

public sealed record V3RecommendedExecutor(string? AccountAlias, string Status, string Reason);

public sealed record V3ExecutionBrief(string Who, string What, string Context, string Flow, string Proof);

public sealed record V3MissionPlanDimension(
    int Number,
    string Title,
    string Source,
    IReadOnlyList<string> Items);

public sealed record V3StructuredMissionPlan(
    string ContractVersion,
    V3ExecutionBrief Brief,
    IReadOnlyList<V3MissionPlanDimension> Dimensions);

public sealed record V3BuildMissionRecord(
    string MissionId,
    string ProjectId,
    string MissionType,
    int Version,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string TargetExecutorCapability,
    string MissionText,
    IReadOnlyList<V3ArtifactReference> ArtifactReferences,
    IReadOnlyList<V3KnowledgeReference> KnowledgeReferences,
    V3EffectiveStackContract EffectiveStack,
    DateTimeOffset? Deadline,
    string? Repository,
    string Status,
    V3RecommendedExecutor RecommendedExecutor)
{
    public IReadOnlyList<V3SourceCoverage> PrimaryRequirementsCoverage { get; init; } = [];
    public string MissionContractVersion { get; init; } = "v3.0";
    public string? StructuredMissionPlanJson { get; init; }
}

public sealed record V3BuildMissionResponse(
    string MissionId,
    string ProjectId,
    string MissionType,
    int Version,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string TargetExecutorCapability,
    string MissionText,
    int ApproximateCharacters,
    IReadOnlyList<V3ArtifactReference> ArtifactReferences,
    IReadOnlyList<V3KnowledgeReference> KnowledgeReferences,
    V3EffectiveStackContract EffectiveStack,
    DateTimeOffset? Deadline,
    string? Repository,
    string Status,
    V3RecommendedExecutor RecommendedExecutor,
    IReadOnlyList<V3SourceCoverage> PrimaryRequirementsCoverage,
    string MissionContractVersion,
    string? StructuredMissionPlanJson)
{
    public static V3BuildMissionResponse From(V3BuildMissionRecord value) =>
        new(
            value.MissionId,
            value.ProjectId,
            value.MissionType,
            value.Version,
            value.CreatedAt,
            value.CreatedBy,
            value.TargetExecutorCapability,
            value.MissionText,
            value.MissionText.Length,
            value.ArtifactReferences,
            value.KnowledgeReferences,
            value.EffectiveStack,
            value.Deadline,
            value.Repository,
            value.Status,
            value.RecommendedExecutor,
            value.PrimaryRequirementsCoverage,
            value.MissionContractVersion,
            value.StructuredMissionPlanJson);
}

public sealed record V3MissionPageResponse(IReadOnlyList<V3BuildMissionResponse> Items);
