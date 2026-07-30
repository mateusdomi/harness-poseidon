using Harness.Modules.Coordination.Application;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Security;
using Harness.SharedKernel.Time;

namespace Harness.Host.Governance;

public static class LearningCandidateEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapLearningCandidates(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/governance-runtime/learning-candidates")
            .WithTags("governance-learning");
        group.MapGet("/", ListAsync).Produces<LearningCandidatePageContract>().ProducesProblem(400).ProducesProblem(401);
        group.MapPost("/", CreateAsync).Produces<LearningCandidateMutationContract>(201).Produces<LearningCandidateMutationContract>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        group.MapGet("/metrics", MetricsAsync).Produces<LearningCandidateMetrics>();
        group.MapGet("/{candidateId}", GetAsync).Produces<LearningCandidateContract>().ProducesProblem(404);
        group.MapGet("/{candidateId}/evidence", EvidenceAsync).Produces<IReadOnlyList<LearningEvidenceRecord>>();
        group.MapGet("/{candidateId}/compare", CompareAsync).Produces<LearningCandidateComparisonContract>();
        group.MapGet("/{candidateId}/history", HistoryAsync).Produces<IReadOnlyList<LearningCandidateHistoryRecord>>();
        group.MapPost("/{candidateId}/review", ReviewAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/evaluation-request", EvaluationRequestAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/evaluations", EvaluationAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/shadow", ShadowAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/decision", DecisionAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/promotion", PromotionAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/rollback", RollbackAsync).Produces<LearningCandidateContract>();
        group.MapPost("/{candidateId}/deprecation", DeprecationAsync).Produces<LearningCandidateContract>();
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? organizationId, string? projectId, string? type, string? state, string? cursor, int? limit,
        HttpRequest request, ILocalProfileStore profiles, ILearningCandidateStore store, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        try
        {
            var page = await store.ListAsync(session.TenantId, organizationId, projectId,
                string.IsNullOrWhiteSpace(type) ? null : LearningCandidatePolicy.ParseType(type),
                string.IsNullOrWhiteSpace(state) ? null : LearningCandidatePolicy.ParseState(state),
                cursor, limit ?? 30, token);
            return Results.Ok(new LearningCandidatePageContract(page.Items.Select(ToContract).ToArray(), page.NextCursor, page.Total));
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }

    private static async Task<IResult> CreateAsync(
        LearningCandidateCreateRequest input, HttpRequest request, ILocalProfileStore profiles,
        IProjectStore projects, IAgentCatalogStore agents, ILearningCandidateStore store,
        GovernanceFeatureSettings features, IClock clock, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        if (!features.LearningCandidatesEnabled) return Disabled();
        var idempotency = Idempotency(request);
        if (idempotency is null) return Invalid("Idempotency-Key is required and must be at most 150 characters.");
        var project = await projects.GetAsync(session.TenantId, input.ProjectId, token);
        var actor = await agents.GetAgentAsync(session.TenantId, input.ActorAgentId, token);
        if (project is null || actor is null || actor.ProjectId != input.ProjectId) return NotFound("project_or_actor");
        try
        {
            RejectSecrets(input);
            var type = LearningCandidatePolicy.ParseType(input.Type);
            var fingerprint = LearningCandidatePolicy.ComputeFingerprint(type, input.Observation, input.Evidence, input.Payload);
            var command = new LearningCandidateCreateCommand(session.TenantId, project.OrganizationId,
                project.Id, UlidValue.New(clock.UtcNow).ToString(), type, fingerprint, input.Observation,
                input.Evidence, input.Payload, input.ActorAgentId, input.ActorProvider, input.ActorModel,
                input.BaselineVersion, input.ProposedVersion, $"learning:create:{idempotency}", Hash(input), clock.UtcNow);
            LearningCandidatePolicy.ValidateCreate(command);
            var result = await store.CreateAsync(command, token);
            var contract = new LearningCandidateMutationContract(ToContract(result.Candidate), result.Deduplicated);
            return result.Deduplicated ? Results.Ok(contract) : Results.Created(
                $"/api/v1/governance-runtime/learning-candidates/{result.Candidate.CandidateId}", contract);
        }
        catch (Exception exception) when (exception is ArgumentException or LearningCandidateConflictException)
        { return exception is LearningCandidateConflictException ? Conflict(exception.Message) : Invalid(exception.Message); }
    }

    private static Task<IResult> ReviewAsync(string candidateId, LearningTransitionRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId, LearningCandidateAction.RequestReview, input.ExpectedVersion,
            input.Note, null, null, null, null, null, input, request, profiles, store, features, clock, token);

    private static Task<IResult> EvaluationRequestAsync(string candidateId, LearningTransitionRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId, LearningCandidateAction.RequestEvaluation, input.ExpectedVersion,
            input.Note, null, null, null, null, null, input, request, profiles, store, features, clock, token);

    private static async Task<IResult> EvaluationAsync(string candidateId, LearningEvaluationRequest input, HttpRequest request,
        ILocalProfileStore profiles, IAgentCatalogStore agents, ILearningCandidateStore store,
        GovernanceFeatureSettings features, IClock clock, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var current = await store.GetAsync(session.TenantId, candidateId, token);
        var evaluator = await agents.GetAgentAsync(session.TenantId, input.EvaluatorAgentId, token);
        if (current is null || evaluator is null || evaluator.ProjectId != current.ProjectId) return NotFound("candidate_or_evaluator");
        return await TransitionResolvedAsync(current, LearningCandidateAction.CompleteEvaluation, input.ExpectedVersion,
            input.Note, input.EvaluatorAgentId, input.EvaluatorProvider, input.EvaluatorModel, input.Verdict, null,
            input, request, session, store, features, clock, token);
    }

    private static Task<IResult> ShadowAsync(string candidateId, LearningShadowRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId, LearningCandidateAction.StartShadow, input.ExpectedVersion,
            input.Note, null, null, null, null, input.Result, input, request, profiles, store, features, clock, token);

    private static Task<IResult> DecisionAsync(string candidateId, LearningDecisionRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId,
            input.Approved ? LearningCandidateAction.Approve : LearningCandidateAction.Reject, input.ExpectedVersion,
            input.Note, null, null, null, null, null, input, request, profiles, store, features, clock, token);

    private static Task<IResult> PromotionAsync(string candidateId, LearningTransitionRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId, LearningCandidateAction.Promote, input.ExpectedVersion,
            input.Note, null, null, null, null, null, input, request, profiles, store, features, clock, token);

    private static Task<IResult> RollbackAsync(string candidateId, LearningTransitionRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId, LearningCandidateAction.Rollback, input.ExpectedVersion,
            input.Note, null, null, null, null, null, input, request, profiles, store, features, clock, token);

    private static Task<IResult> DeprecationAsync(string candidateId, LearningTransitionRequest input, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock,
        CancellationToken token) => TransitionAsync(candidateId, LearningCandidateAction.Deprecate, input.ExpectedVersion,
            input.Note, null, null, null, null, null, input, request, profiles, store, features, clock, token);

    private static async Task<IResult> TransitionAsync<T>(string candidateId, LearningCandidateAction action,
        long expectedVersion, string? note, string? evaluatorId, string? evaluatorProvider, string? evaluatorModel,
        string? verdict, LearningShadowResult? shadow, T input, HttpRequest request, ILocalProfileStore profiles,
        ILearningCandidateStore store, GovernanceFeatureSettings features, IClock clock, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var current = await store.GetAsync(session.TenantId, candidateId, token);
        if (current is null) return NotFound("candidate");
        return await TransitionResolvedAsync(current, action, expectedVersion, note, evaluatorId,
            evaluatorProvider, evaluatorModel, verdict, shadow, input, request, session, store, features, clock, token);
    }

    private static async Task<IResult> TransitionResolvedAsync<T>(LearningCandidateRecord current,
        LearningCandidateAction action, long expectedVersion, string? note, string? evaluatorId,
        string? evaluatorProvider, string? evaluatorModel, string? verdict, LearningShadowResult? shadow,
        T input, HttpRequest request, LocalProfileRecord session, ILearningCandidateStore store,
        GovernanceFeatureSettings features, IClock clock, CancellationToken token)
    {
        if (!features.LearningCandidatesEnabled) return Disabled();
        var idempotency = Idempotency(request);
        if (idempotency is null) return Invalid("Idempotency-Key is required and must be at most 150 characters.");
        try
        {
            RejectSecrets(input);

            // B10/F16 — promoção a skill exige aprovação humana IDENTIFICADA. O ciclo já restringe
            // a ação ao admin; esta guarda acrescenta o que a fase pede e o papel não garante: um
            // aprovador nomeado no registro. Sem ele a política lança, e é assim que se garante que
            // não existe caminho de promoção silenciosa — nem por conveniência de teste.
            if (action == LearningCandidateAction.Promote)
            {
                LearningPromotionPolicy.RequireHumanApproval(session.Id);
            }

            var command = new LearningCandidateTransitionCommand(session.TenantId, current.CandidateId, action,
                expectedVersion, session.Id, session.Role == LocalProfileRole.Admin, note, evaluatorId,
                evaluatorProvider, evaluatorModel, verdict, shadow, $"learning:{action}:{idempotency}", Hash(input), clock.UtcNow);
            LearningCandidatePolicy.Next(current, command);
            return Results.Ok(ToContract(await store.TransitionAsync(command, token)));
        }
        catch (LearningCandidateAuthorizationException exception) { return Forbidden(exception.Message); }
        catch (LearningCandidateConflictException exception) { return Conflict(exception.Message); }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }

    private static async Task<IResult> GetAsync(string candidateId, HttpRequest request, ILocalProfileStore profiles,
        ILearningCandidateStore store, CancellationToken token)
    { var value = await ResolveAsync(candidateId, request, profiles, store, token); return value is null ? NotFound("candidate") : Results.Ok(ToContract(value)); }
    private static async Task<IResult> EvidenceAsync(string candidateId, HttpRequest request, ILocalProfileStore profiles,
        ILearningCandidateStore store, CancellationToken token)
    { var value = await ResolveAsync(candidateId, request, profiles, store, token); return value is null ? NotFound("candidate") : Results.Ok(value.Evidence); }
    private static async Task<IResult> CompareAsync(string candidateId, HttpRequest request, ILocalProfileStore profiles,
        ILearningCandidateStore store, CancellationToken token)
    {
        var value = await ResolveAsync(candidateId, request, profiles, store, token); return value is null ? NotFound("candidate") :
        Results.Ok(new LearningCandidateComparisonContract(value.BaselineVersion, value.ProposedVersion, value.Payload, value.ActiveVersion, value.PreviousVersion));
    }
    private static async Task<IResult> HistoryAsync(string candidateId, HttpRequest request, ILocalProfileStore profiles,
        ILearningCandidateStore store, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized();
        return await store.GetAsync(session.TenantId, candidateId, token) is null ? NotFound("candidate") : Results.Ok(await store.ListHistoryAsync(session.TenantId, candidateId, token));
    }
    private static async Task<IResult> MetricsAsync(string? organizationId, string? projectId, HttpRequest request,
        ILocalProfileStore profiles, ILearningCandidateStore store, CancellationToken token)
    { var session = await LocalProfileSession.ResolveAsync(request, profiles, token); return session is null ? Unauthorized() : Results.Ok(await store.GetMetricsAsync(session.TenantId, organizationId, projectId, token)); }
    private static async Task<LearningCandidateRecord?> ResolveAsync(string id, HttpRequest request, ILocalProfileStore profiles,
        ILearningCandidateStore store, CancellationToken token)
    { var session = await LocalProfileSession.ResolveAsync(request, profiles, token); return session is null ? null : await store.GetAsync(session.TenantId, id, token); }

    private static LearningCandidateContract ToContract(LearningCandidateRecord v) => new(v.OrganizationId, v.ProjectId, v.CandidateId,
        LearningCandidatePolicy.Type(v.Type), LearningCandidatePolicy.State(v.State), v.Fingerprint, v.Observation, v.Evidence, v.Payload,
        v.ActorAgentId, v.ActorProvider, v.ActorModel, v.BaselineVersion, v.ProposedVersion, v.EvaluatorAgentId, v.EvaluatorProvider,
        v.EvaluatorModel, v.EvaluationVerdict, v.ShadowResult, v.ReviewerProfileId, v.DecisionNote, v.ActiveVersion, v.PreviousVersion,
        v.CreatedAt, v.UpdatedAt, v.Version);
    private static string? Idempotency(HttpRequest request) => request.Headers.TryGetValue("Idempotency-Key", out var values) &&
        !string.IsNullOrWhiteSpace(values.ToString()) && values.ToString().Length <= 150 ? values.ToString() : null;
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions)))).ToLowerInvariant();
    private static void RejectSecrets<T>(T value) { if (SecretTextProtector.ContainsSecret(JsonSerializer.Serialize(value, JsonOptions))) throw new ArgumentException("Candidate payload contains a secret-like value; use an opaque reference."); }
    private static IResult Unauthorized() => Results.Problem(statusCode: 401, title: "local_session_required", detail: "A local profile session is required.");
    private static IResult Forbidden(string detail) => Results.Problem(statusCode: 403, title: "learning_action_forbidden", detail: detail);
    private static IResult Invalid(string detail) => Results.Problem(statusCode: 400, title: "invalid_learning_candidate", detail: detail);
    private static IResult Conflict(string detail) => Results.Problem(statusCode: 409, title: "learning_candidate_conflict", detail: detail);
    private static IResult NotFound(string resource) => Results.Problem(statusCode: 404, title: $"{resource}_not_found", detail: "The requested resource does not exist.");
    private static IResult Disabled() => Results.Problem(statusCode: 503, title: "learning_candidates_disabled", detail: "The learning pipeline is disabled by operational configuration.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningCandidateCreateRequest(string ProjectId, string Type, string Observation,
    IReadOnlyList<LearningEvidenceRecord> Evidence, LearningCandidatePayload Payload, string ActorAgentId,
    string ActorProvider, string? ActorModel, string BaselineVersion, string ProposedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningTransitionRequest(long ExpectedVersion, string? Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningEvaluationRequest(long ExpectedVersion, string EvaluatorAgentId, string EvaluatorProvider,
    string? EvaluatorModel, string Verdict, string? Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningShadowRequest(long ExpectedVersion, LearningShadowResult Result, string? Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningDecisionRequest(long ExpectedVersion, bool Approved, string? Note);
public sealed record LearningCandidateMutationContract(LearningCandidateContract Candidate, bool Deduplicated);
public sealed record LearningCandidatePageContract(IReadOnlyList<LearningCandidateContract> Items, string? NextCursor, int Total);
public sealed record LearningCandidateComparisonContract(string BaselineVersion, string ProposedVersion,
    LearningCandidatePayload ProposedPayload, string? ActiveVersion, string? PreviousVersion);
public sealed record LearningCandidateContract(string OrganizationId, string ProjectId, string CandidateId, string Type, string State,
    string Fingerprint, string Observation, IReadOnlyList<LearningEvidenceRecord> Evidence, LearningCandidatePayload Payload,
    string ActorAgentId, string ActorProvider, string? ActorModel, string BaselineVersion, string ProposedVersion,
    string? EvaluatorAgentId, string? EvaluatorProvider, string? EvaluatorModel, string? EvaluationVerdict,
    LearningShadowResult? ShadowResult, string? ReviewerProfileId, string? DecisionNote, string? ActiveVersion,
    string? PreviousVersion, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);
