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
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
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
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        var analyzed = V3UnderstandAnalyzer.Analyze(current, input, clock.UtcNow);
        store.WriteProject(analyzed.State);
        var refreshed = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
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
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
        if (!V3AuthorizationPolicy.IsAuthorized(input.Response))
        {
            return Problem(400, "authorization_not_recognized", "A autorização precisa ser explícita.");
        }

        var deadline = input.Deadline ?? context.Deadline;
        var repository = FirstNonBlank(input.Repository, context.State?.Repository);
        if (deadline is null || string.IsNullOrWhiteSpace(repository))
        {
            return Problem(409, "project_not_ready_to_authorize", "Deadline and repository are required before BUILD authorization.");
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
        store.WriteProject(state);
        var refreshed = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
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
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
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
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, store, token);
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

        var buildExecution = V3BuildRuntimeStore.ForConfiguration(configuration)
            .LatestCompletedBuildForProject(projectId);
        var mission = V3MissionCompiler.CompileValidationMission(
            context,
            buildExecution,
            V3ValidationExecutorPreview.Select(accounts.List(), buildExecution?.ExecutorAccountId),
            clock.UtcNow);
        store.WriteMission(mission);
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
                value.StoragePath,
                value.Sha256,
                value.State)));
        }

        var coverage = await V3SourceCoverageAnalyzer.AnalyzeAsync(
            artifactRefs, attachmentStorage, token);
        var facts = V3RequirementFactsExtractor.Extract(coverage);
        var documentRefs = (await documents.ListDocumentsAsync(profile.TenantId, project.Id, null, 200, token))
            .Select(value => new V3DocumentReference(
                value.Id,
                value.Title,
                value.Kind,
                value.State,
                value.CurrentVersion,
                value.Classifications))
            .ToArray();
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
        var openQuestions = V3OpenQuestionPolicy.RequiredQuestions(
            project.Id,
            state?.Deadline ?? facts.Deadline ?? project.TargetDeadline,
            state?.Repository,
            facts,
            coverage);
        var lifecycleState = state?.LifecycleState ??
            (openQuestions.Count == 0 ? "READY_TO_START" : "AWAITING_INPUT");

        return new V3ProjectContextResponse(
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
            state?.Repository ?? project.RepositoryUrl,
            "host-api-and-frontend; database container only when stack requires it",
            "existing notification channels; no new notification system",
            capacity,
            v3Readiness.Items,
            lifecycleState);
    }
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
            var path = storage.Resolve(artifact.PathReference);
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
        var combined = string.Join("\n\n", texts.Select(item => item.Text));
        var deadline = ExtractDeadline(texts);
        var authentication = ContainsAny(combined, "autenticação própria", "sem sso")
            ? "Autenticação própria, sem SSO"
            : null;
        var productNotification = ContainsAny(combined, "notificações", "digest", "caixa", "e-mail", "email")
            ? "Notificações do produto por e-mail/digest conforme requisitos"
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
        var repository = FirstNonBlank(input.Repository, state.Repository);
        var questions = V3OpenQuestionPolicy.RequiredQuestions(
            context.ProjectId, deadline, repository, context.SourceFacts, context.PrimaryRequirementsCoverage);
        var nextState = !sourceComplete
            ? "UNDERSTANDING"
            : questions.Count == 0
                ? "READY_TO_START"
                : "AWAITING_INPUT";
        var updated = state with
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
        var frontend = text.Contains("react", StringComparison.Ordinal) || artifacts.Any(artifact =>
            string.Equals(artifact.Role, "provided_frontend", StringComparison.OrdinalIgnoreCase))
            ? "React + TypeScript + Vite"
            : "Frontend conforme requisitos";
        var backend = text.Contains(".net", StringComparison.Ordinal) || text.Contains("dotnet", StringComparison.Ordinal)
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
        RequiredQuestions(project.Id, state?.Deadline ?? project.TargetDeadline, state?.Repository, state?.SourceFacts, state?.PrimaryRequirementsCoverage ?? []);

    public static IReadOnlyList<V3OpenQuestion> RequiredQuestions(
        string projectId,
        DateTimeOffset? deadline,
        string? repository,
        V3RequirementSourceFacts? facts = null,
        IReadOnlyList<V3SourceCoverage>? sourceCoverage = null)
    {
        var questions = new List<V3OpenQuestion>();
        var primarySources = sourceCoverage ?? [];
        if (primarySources.Any(source => !source.Complete))
        {
            return questions;
        }

        if (deadline is null && facts?.Deadline is null)
        {
            questions.Add(new V3OpenQuestion("deadline", "Qual o prazo final do projeto?", "required_before_authorization"));
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            questions.Add(new V3OpenQuestion("repository", "Qual repositório deve receber o código?", "required_before_authorization"));
        }

        return questions;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public static class V3AuthorizationPolicy
{
    private static readonly string[] Phrases =
    [
        "sim",
        "pode iniciar",
        "autorizado",
        "comece",
        "inicie",
        "pode começar",
        "vamos iniciar",
    ];

    public static bool IsAuthorized(string? response) =>
        !string.IsNullOrWhiteSpace(response) &&
        Phrases.Any(phrase => response.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}

public static class V3ExecutorPreview
{
    public static V3RecommendedExecutor Select(IReadOnlyList<AgentAccountContract> accounts)
    {
        var candidates = accounts
            .Where(account =>
                account.State == AgentAccountState.Available &&
                account.AllowedRoles.Any(role =>
                    string.Equals(role, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)) &&
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
    public static IReadOnlyList<V3KnowledgeReference> Select(V3EffectiveStackContract stack)
    {
        var refs = new List<V3KnowledgeReference>
        {
            Ref("docs/product/definition-of-done.md", "Definition of Done do produto entregue"),
            Ref("docs/product/baseline.md", "Baseline técnico do produto entregue"),
            Ref("governance/rules/git.md", "Git e integração"),
            Ref("governance/rules/secrets.md", "Segredos"),
            Ref("governance/rules/testing.md", "Testes e gates"),
        };
        if (stack.Frontend.Contains("React", StringComparison.OrdinalIgnoreCase) ||
            !stack.Frontend.Contains("conforme", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add(Ref("docs/product/frontend-standards.md", "Frontend do produto entregue"));
        }

        if (stack.Backend.Contains(".NET", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add(Ref("docs/product/backend-standards.md", "Backend .NET do produto entregue"));
        }

        if (stack.Database.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add(Ref("docs/product/oracle-data-standards.md", "Dados e banco Oracle"));
        }
        else if (!stack.Database.Contains("conforme", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add(Ref("docs/product/data-standards.md", "Dados e banco do produto entregue"));
        }

        refs.Add(Ref("docs/product/qa-standards.md", "QA do produto entregue"));
        return refs;
    }

    public static IReadOnlyList<V3KnowledgeReference> SelectForValidation(V3EffectiveStackContract stack)
    {
        var refs = Select(stack).ToList();
        refs.Add(Ref("docs/product/qa-standards.md#8-toolchain-local-de-navegador", "Toolchain E2E local e browser-first"));
        refs.Add(Ref("~/Downloads/checklist-auto-auditoria-ia.md", "Checklist genérico de qualidade para autoauditoria final"));
        return [.. refs.DistinctBy(item => item.Path)];
    }

    private static V3KnowledgeReference Ref(string path, string reason) => new(path, reason);
}

public static class V3MissionCompiler
{
    public static V3BuildMissionRecord CompileBuildMission(
        V3ProjectContextResponse context,
        V3RecommendedExecutor recommended,
        string? overrideExecutor,
        DateTimeOffset now)
    {
        var missionId = UlidValue.New(now).ToString();
        var knowledge = V3KnowledgeSelector.Select(context.EffectiveStack);
        var executor = string.IsNullOrWhiteSpace(overrideExecutor)
            ? recommended
            : new V3RecommendedExecutor(overrideExecutor.Trim(), "OVERRIDDEN", "Human/operator override.");
        var text = ComposeMissionText(context, knowledge, executor);
        return new V3BuildMissionRecord(
            missionId,
            context.ProjectId,
            "BUILD",
            1,
            now,
            "Bruna",
            "write-capable project executor",
            text,
            context.Artifacts,
            knowledge,
            context.EffectiveStack,
            context.Deadline,
            context.Repository,
            "COMPILED",
            executor)
        {
            PrimaryRequirementsCoverage = context.PrimaryRequirementsCoverage,
        };
    }

    public static V3BuildMissionRecord CompileValidationMission(
        V3ProjectContextResponse context,
        V3BuildExecutionRecord? buildExecution,
        V3RecommendedExecutor executor,
        DateTimeOffset now)
    {
        var missionId = UlidValue.New(now).ToString();
        var knowledge = V3KnowledgeSelector.SelectForValidation(context.EffectiveStack);
        var text = ComposeValidationMissionText(context, knowledge, executor, buildExecution);
        return new V3BuildMissionRecord(
            missionId,
            context.ProjectId,
            "VALIDATE",
            1,
            now,
            "Bruna",
            "write-capable project executor with browser validation capability",
            text,
            context.Artifacts,
            knowledge,
            context.EffectiveStack,
            context.Deadline,
            context.Repository,
            "COMPILED",
            executor)
        {
            PrimaryRequirementsCoverage = context.PrimaryRequirementsCoverage,
        };
    }

    private static string ComposeMissionText(
        V3ProjectContextResponse context,
        IReadOnlyList<V3KnowledgeReference> knowledge,
        V3RecommendedExecutor executor)
    {
        var state = context.State;
        var lines = new List<string>
        {
            $"# BUILD MISSION — {context.ProjectName}",
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
        };
        lines.AddRange((state?.Requirements ?? context.Requirements).Select(item => $"- {item}"));
        lines.Add("");
        lines.Add("## ORIGINAL ARTIFACTS");
        if (context.Artifacts.Count == 0 && context.Documents.Count == 0)
        {
            lines.Add("- Nenhum artefato externo associado até a compilação; use a intenção original persistida.");
        }
        else
        {
            lines.AddRange(context.Artifacts.Select(artifact =>
                $"- attachment:{artifact.ArtifactId} — {artifact.Name} ({artifact.ContentType}, role={artifact.Role}, sha256={artifact.Sha256})"));
            lines.AddRange(context.Documents.Select(document =>
                $"- document:{document.DocumentId} — {document.Title} ({document.Kind}, state={document.State}, version={document.CurrentVersion})"));
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
        lines.AddRange(knowledge.Select(item => $"- Aplicar conhecimento relevante: {item.Path} — {item.Reason}"));
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
            $"- attachment:{artifact.ArtifactId} — {artifact.Name} ({artifact.ContentType}, role={artifact.Role}, sha256={artifact.Sha256})"));
        lines.AddRange(context.Documents.Select(document =>
            $"- document:{document.DocumentId} — {document.Title} ({document.Kind}, state={document.State}, version={document.CurrentVersion})"));
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
            "- Use ~/Downloads/checklist-auto-auditoria-ia.md como checklist genérico obrigatório.",
            "- Não transforme o checklist em 244 tarefas ou 244 chamadas LLM.",
            "- Classifique internamente cada item aplicável como PASS, FIXED, N/A ou FAIL.",
            "- FAIL aplicável final precisa ser ZERO.",
            "",
            "## KNOWLEDGE SOURCES",
        ]);
        lines.AddRange(knowledge.Select(item => $"- {item.Path} — {item.Reason}"));
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
    string CurrentLifecycleState);

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
}

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
    string State);

public sealed record V3DocumentReference(
    string DocumentId,
    string Title,
    string Kind,
    string State,
    int CurrentVersion,
    IReadOnlyList<string> Classifications);

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
    string? Database,
    string? DatabaseProvenance,
    string? ItrcRules,
    string? ItrcRulesProvenance,
    int AcceptanceCriteriaCount)
{
    public static V3RequirementSourceFacts Empty { get; } =
        new(null, null, null, null, null, null, null, null, null, null, 0);
}

public sealed record V3EffectiveStackContract(
    string Frontend,
    string Backend,
    string Database,
    string Architecture,
    string Testing,
    IReadOnlyList<string> Provenance);

public sealed record V3KnowledgeReference(string Path, string Reason);

public sealed record V3RecommendedExecutor(string? AccountAlias, string Status, string Reason);

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
    IReadOnlyList<V3SourceCoverage> PrimaryRequirementsCoverage)
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
            value.PrimaryRequirementsCoverage);
}

public sealed record V3MissionPageResponse(IReadOnlyList<V3BuildMissionResponse> Items);
