using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Persistence.Abstractions.Governance;

public enum LearningCandidateType
{
    Rule,
    Skill,
    PersonaRefinement,
    WorkflowRefinement,
    ToolRoutingRecommendation,
    DocumentationCorrection,
    ProviderModelRoutingRecommendation,
}

public enum LearningCandidateState
{
    Candidate,
    InReview,
    AwaitingEvaluation,
    Evaluated,
    Shadow,
    Approved,
    Rejected,
    Promoted,
    RolledBack,
    Deprecated,
}

public enum LearningCandidateAction
{
    RequestReview,
    RequestEvaluation,
    CompleteEvaluation,
    StartShadow,
    Approve,
    Reject,
    Promote,
    Rollback,
    Deprecate,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningCandidatePayload(
    string Title,
    string? Statement,
    string? Instructions,
    string? PersonaId,
    string? WorkflowId,
    string? ToolId,
    string? DocumentId,
    string? ProviderId,
    string? ModelId,
    string? Refinement,
    string? Recommendation,
    string? Correction);

public sealed record LearningEvidenceRecord(
    string Kind,
    string Reference,
    string Checksum,
    string Summary);

public sealed record LearningShadowResult(
    int SampleSize,
    decimal FirstPassSuccessDelta,
    decimal RepeatedErrorRateDelta,
    int TokenImpact,
    decimal CostPerAcceptedTaskDelta,
    int Regressions,
    string EvidenceReference);

public sealed record LearningCandidateRecord(
    string TenantId,
    string OrganizationId,
    string ProjectId,
    string CandidateId,
    LearningCandidateType Type,
    LearningCandidateState State,
    string Fingerprint,
    string Observation,
    IReadOnlyList<LearningEvidenceRecord> Evidence,
    LearningCandidatePayload Payload,
    string ActorAgentId,
    string ActorProvider,
    string? ActorModel,
    string BaselineVersion,
    string ProposedVersion,
    string? EvaluatorAgentId,
    string? EvaluatorProvider,
    string? EvaluatorModel,
    string? EvaluationVerdict,
    LearningShadowResult? ShadowResult,
    string? ReviewerProfileId,
    string? DecisionNote,
    string? ActiveVersion,
    string? PreviousVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record LearningCandidateCreateCommand(
    string TenantId,
    string OrganizationId,
    string ProjectId,
    string CandidateId,
    LearningCandidateType Type,
    string Fingerprint,
    string Observation,
    IReadOnlyList<LearningEvidenceRecord> Evidence,
    LearningCandidatePayload Payload,
    string ActorAgentId,
    string ActorProvider,
    string? ActorModel,
    string BaselineVersion,
    string ProposedVersion,
    string IdempotencyKey,
    string PayloadHash,
    DateTimeOffset OccurredAt);

public sealed record LearningCandidateTransitionCommand(
    string TenantId,
    string CandidateId,
    LearningCandidateAction Action,
    long ExpectedVersion,
    string ActorProfileId,
    bool ActorIsAdmin,
    string? Note,
    string? EvaluatorAgentId,
    string? EvaluatorProvider,
    string? EvaluatorModel,
    string? EvaluationVerdict,
    LearningShadowResult? ShadowResult,
    string IdempotencyKey,
    string PayloadHash,
    DateTimeOffset OccurredAt);

public sealed record LearningCandidatePage(
    IReadOnlyList<LearningCandidateRecord> Items,
    string? NextCursor,
    int Total);

public sealed record LearningCandidateHistoryRecord(
    string EventId,
    string CandidateId,
    LearningCandidateState FromState,
    LearningCandidateState ToState,
    LearningCandidateAction? Action,
    string ActorId,
    string? Note,
    DateTimeOffset OccurredAt,
    long CandidateVersion);

public sealed record LearningCandidateMetrics(
    int Created,
    int Deduplicated,
    int Rejected,
    int Approved,
    int Promoted,
    int RolledBack,
    decimal AverageFirstPassSuccessDelta,
    decimal AverageRepeatedErrorRateDelta,
    long TokenImpact,
    decimal AverageCostPerAcceptedTaskDelta,
    int RegressionsAfterPromotion);

public interface ILearningCandidateStore
{
    Task<(LearningCandidateRecord Candidate, bool Deduplicated)> CreateAsync(
        LearningCandidateCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<LearningCandidateRecord> TransitionAsync(
        LearningCandidateTransitionCommand command,
        CancellationToken cancellationToken = default);

    Task<LearningCandidateRecord?> GetAsync(
        string tenantId,
        string candidateId,
        CancellationToken cancellationToken = default);

    Task<LearningCandidatePage> ListAsync(
        string tenantId,
        string? organizationId,
        string? projectId,
        LearningCandidateType? type,
        LearningCandidateState? state,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LearningCandidateHistoryRecord>> ListHistoryAsync(
        string tenantId,
        string candidateId,
        CancellationToken cancellationToken = default);

    Task<LearningCandidateMetrics> GetMetricsAsync(
        string tenantId,
        string? organizationId,
        string? projectId,
        CancellationToken cancellationToken = default);
}

public sealed class LearningCandidateConflictException(string message) : Exception(message);

public sealed class LearningCandidateAuthorizationException(string message) : Exception(message);

public static class LearningCandidatePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void ValidateCreate(LearningCandidateCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Required(command.TenantId, nameof(command.TenantId)); Required(command.OrganizationId, nameof(command.OrganizationId));
        Required(command.ProjectId, nameof(command.ProjectId)); Required(command.CandidateId, nameof(command.CandidateId));
        Required(command.Observation, nameof(command.Observation)); Required(command.ActorAgentId, nameof(command.ActorAgentId));
        Required(command.ActorProvider, nameof(command.ActorProvider)); Required(command.BaselineVersion, nameof(command.BaselineVersion));
        Required(command.ProposedVersion, nameof(command.ProposedVersion)); Required(command.IdempotencyKey, nameof(command.IdempotencyKey));
        Required(command.PayloadHash, nameof(command.PayloadHash));
        if (command.Evidence.Count == 0) throw new ArgumentException("At least one evidence item is required.", nameof(command));
        foreach (var evidence in command.Evidence)
        {
            Required(evidence.Kind, nameof(evidence.Kind)); Required(evidence.Reference, nameof(evidence.Reference));
            Required(evidence.Checksum, nameof(evidence.Checksum)); Required(evidence.Summary, nameof(evidence.Summary));
        }
        ValidatePayload(command.Type, command.Payload);
        if (!string.Equals(ComputeFingerprint(command.Type, command.Observation, command.Evidence, command.Payload), command.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Candidate fingerprint is not canonical.", nameof(command));
    }

    public static void ValidatePayload(LearningCandidateType type, LearningCandidatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload); Required(payload.Title, nameof(payload.Title));
        var populated = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(payload.Statement)] = payload.Statement,
            [nameof(payload.Instructions)] = payload.Instructions,
            [nameof(payload.PersonaId)] = payload.PersonaId,
            [nameof(payload.WorkflowId)] = payload.WorkflowId,
            [nameof(payload.ToolId)] = payload.ToolId,
            [nameof(payload.DocumentId)] = payload.DocumentId,
            [nameof(payload.ProviderId)] = payload.ProviderId,
            [nameof(payload.ModelId)] = payload.ModelId,
            [nameof(payload.Refinement)] = payload.Refinement,
            [nameof(payload.Recommendation)] = payload.Recommendation,
            [nameof(payload.Correction)] = payload.Correction
        };
        var allowed = type switch
        {
            LearningCandidateType.Rule => Set(nameof(payload.Statement)),
            LearningCandidateType.Skill => Set(nameof(payload.Instructions)),
            LearningCandidateType.PersonaRefinement => Set(nameof(payload.PersonaId), nameof(payload.Refinement)),
            LearningCandidateType.WorkflowRefinement => Set(nameof(payload.WorkflowId), nameof(payload.Refinement)),
            LearningCandidateType.ToolRoutingRecommendation => Set(nameof(payload.ToolId), nameof(payload.Recommendation)),
            LearningCandidateType.DocumentationCorrection => Set(nameof(payload.DocumentId), nameof(payload.Correction)),
            LearningCandidateType.ProviderModelRoutingRecommendation => Set(nameof(payload.ProviderId), nameof(payload.ModelId), nameof(payload.Recommendation)),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        foreach (var required in allowed) Required(populated[required], required);
        var unexpected = populated.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && !allowed.Contains(pair.Key)).Select(pair => pair.Key).ToArray();
        if (unexpected.Length > 0) throw new ArgumentException($"Payload fields are not valid for {type}: {string.Join(',', unexpected)}.");
    }

    public static string ComputeFingerprint(LearningCandidateType type, string observation, IReadOnlyList<LearningEvidenceRecord> evidence, LearningCandidatePayload payload)
    {
        var canonical = JsonSerializer.Serialize(new { type = Type(type), observation = Normalize(observation), evidence = evidence.OrderBy(item => item.Checksum, StringComparer.Ordinal).Select(item => new { item.Kind, item.Reference, item.Checksum }).ToArray(), payload }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string Type(LearningCandidateType value) => value switch
    {
        LearningCandidateType.Rule => "rule",
        LearningCandidateType.Skill => "skill",
        LearningCandidateType.PersonaRefinement => "persona_refinement",
        LearningCandidateType.WorkflowRefinement => "workflow_refinement",
        LearningCandidateType.ToolRoutingRecommendation => "tool_routing_recommendation",
        LearningCandidateType.DocumentationCorrection => "documentation_correction",
        LearningCandidateType.ProviderModelRoutingRecommendation => "provider_model_routing_recommendation",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    public static LearningCandidateType ParseType(string value) => value switch
    {
        "rule" => LearningCandidateType.Rule,
        "skill" => LearningCandidateType.Skill,
        "persona_refinement" => LearningCandidateType.PersonaRefinement,
        "workflow_refinement" => LearningCandidateType.WorkflowRefinement,
        "tool_routing_recommendation" => LearningCandidateType.ToolRoutingRecommendation,
        "documentation_correction" => LearningCandidateType.DocumentationCorrection,
        "provider_model_routing_recommendation" => LearningCandidateType.ProviderModelRoutingRecommendation,
        _ => throw new ArgumentException("Unknown learning candidate type.", nameof(value))
    };
    public static string State(LearningCandidateState value) => value switch
    {
        LearningCandidateState.Candidate => "candidate",
        LearningCandidateState.InReview => "in_review",
        LearningCandidateState.AwaitingEvaluation => "awaiting_evaluation",
        LearningCandidateState.Evaluated => "evaluated",
        LearningCandidateState.Shadow => "shadow",
        LearningCandidateState.Approved => "approved",
        LearningCandidateState.Rejected => "rejected",
        LearningCandidateState.Promoted => "promoted",
        LearningCandidateState.RolledBack => "rolled_back",
        LearningCandidateState.Deprecated => "deprecated",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    public static LearningCandidateState ParseState(string value) => value switch
    {
        "candidate" => LearningCandidateState.Candidate,
        "in_review" => LearningCandidateState.InReview,
        "awaiting_evaluation" => LearningCandidateState.AwaitingEvaluation,
        "evaluated" => LearningCandidateState.Evaluated,
        "shadow" => LearningCandidateState.Shadow,
        "approved" => LearningCandidateState.Approved,
        "rejected" => LearningCandidateState.Rejected,
        "promoted" => LearningCandidateState.Promoted,
        "rolled_back" => LearningCandidateState.RolledBack,
        "deprecated" => LearningCandidateState.Deprecated,
        _ => throw new ArgumentException("Unknown learning candidate state.", nameof(value))
    };

    public static LearningCandidateState Next(
        LearningCandidateRecord current,
        LearningCandidateTransitionCommand command)
    {
        if (current.Version != command.ExpectedVersion)
            throw new LearningCandidateConflictException("The candidate version is stale.");
        var next = (current.State, command.Action) switch
        {
            (LearningCandidateState.Candidate, LearningCandidateAction.RequestReview) => LearningCandidateState.InReview,
            (LearningCandidateState.InReview, LearningCandidateAction.RequestEvaluation) => LearningCandidateState.AwaitingEvaluation,
            (LearningCandidateState.AwaitingEvaluation, LearningCandidateAction.CompleteEvaluation) => LearningCandidateState.Evaluated,
            (LearningCandidateState.Evaluated, LearningCandidateAction.StartShadow) => LearningCandidateState.Shadow,
            (LearningCandidateState.Shadow, LearningCandidateAction.Approve) => LearningCandidateState.Approved,
            (LearningCandidateState.Approved, LearningCandidateAction.Promote) => LearningCandidateState.Promoted,
            (LearningCandidateState.Promoted, LearningCandidateAction.Rollback) => LearningCandidateState.RolledBack,
            (LearningCandidateState.Promoted or LearningCandidateState.RolledBack, LearningCandidateAction.Deprecate) => LearningCandidateState.Deprecated,
            (LearningCandidateState.Candidate or LearningCandidateState.InReview or
             LearningCandidateState.AwaitingEvaluation or LearningCandidateState.Evaluated or LearningCandidateState.Shadow,
             LearningCandidateAction.Reject) => LearningCandidateState.Rejected,
            _ => throw new LearningCandidateConflictException(
                $"Transition {command.Action} is invalid from {current.State}.")
        };
        if (command.Action is LearningCandidateAction.Approve or LearningCandidateAction.Promote or
            LearningCandidateAction.Rollback or LearningCandidateAction.Deprecate && !command.ActorIsAdmin)
            throw new LearningCandidateAuthorizationException("This transition requires an administrator.");
        if (command.Action == LearningCandidateAction.CompleteEvaluation &&
            (string.IsNullOrWhiteSpace(command.EvaluatorAgentId) ||
             string.IsNullOrWhiteSpace(command.EvaluatorProvider) ||
             !string.Equals(command.EvaluationVerdict, "pass", StringComparison.Ordinal) ||
             string.Equals(current.ActorAgentId, command.EvaluatorAgentId, StringComparison.Ordinal) ||
             (string.Equals(current.ActorProvider, command.EvaluatorProvider, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(current.ActorModel, command.EvaluatorModel, StringComparison.OrdinalIgnoreCase))))
            throw new LearningCandidateAuthorizationException("A passing independent evaluation is required.");
        if (command.Action == LearningCandidateAction.StartShadow &&
            (current.EvaluationVerdict != "pass" || command.ShadowResult is null ||
             command.ShadowResult.SampleSize < 1 || string.IsNullOrWhiteSpace(command.ShadowResult.EvidenceReference)))
            throw new LearningCandidateConflictException("A valid shadow result requires a passing evaluation.");
        if (command.Action is LearningCandidateAction.Reject or LearningCandidateAction.Rollback or
            LearningCandidateAction.Deprecate && string.IsNullOrWhiteSpace(command.Note))
            throw new ArgumentException("A decision note is required.", nameof(command));
        return next;
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
    private static string Normalize(string value) => string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static void Required(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name); }
}
