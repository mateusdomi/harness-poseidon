namespace Harness.Persistence.Abstractions.Governance;

public enum GovernanceReceiptState
{
    Selected,
    Delivered,
    Completed,
    Failed,
}

public static class GovernanceReceiptLifecycle
{
    public static bool CanTransition(GovernanceReceiptState current, GovernanceReceiptState next) =>
        (current, next) switch
        {
            (GovernanceReceiptState.Selected, GovernanceReceiptState.Delivered or GovernanceReceiptState.Completed or GovernanceReceiptState.Failed) => true,
            (GovernanceReceiptState.Delivered, GovernanceReceiptState.Completed or GovernanceReceiptState.Failed) => true,
            (GovernanceReceiptState.Failed, GovernanceReceiptState.Delivered) => true,
            _ => false,
        };
}

public enum GovernanceMetricKind
{
    Selected,
    Delivered,
    OpenedByTool,
    RuleTriggered,
    ViolationDetected,
    ItemTruncated,
    GateResult,
    PatchApplied,
    PatchRejected,
    EvaluatorVerdict,
}

public sealed record GovernanceReceiptDocumentRecord(
    string DocumentId,
    string Checksum,
    string SelectionReason,
    string LoadPolicy,
    int EstimatedTokens);

public sealed record GovernanceTurnReceiptRecord(
    string TenantId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string TurnId,
    string AgentId,
    string ManifestVersion,
    IReadOnlyList<GovernanceReceiptDocumentRecord> Documents,
    int EstimatedTokens,
    int? ActualPromptTokens,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Conflicts,
    int CacheHits,
    string Provider,
    string? Model,
    DateTimeOffset Timestamp,
    string BundleChecksum,
    GovernanceReceiptState State,
    string? GateResult,
    long Version);

public sealed record GovernanceTurnReceiptCreateCommand(
    string TenantId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string TurnId,
    string AgentId,
    string ManifestVersion,
    IReadOnlyList<GovernanceReceiptDocumentRecord> Documents,
    int EstimatedTokens,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Conflicts,
    int CacheHits,
    string Provider,
    string? Model,
    DateTimeOffset Timestamp,
    string BundleChecksum);

public sealed record GovernanceTurnReceiptCompleteCommand(
    string TenantId,
    string TurnId,
    long ExpectedVersion,
    int? ActualPromptTokens,
    GovernanceReceiptState State,
    string? GateResult,
    DateTimeOffset OccurredAt);

public sealed record GovernanceMetricAppendCommand(
    string TenantId,
    string ProjectId,
    string TurnId,
    string EventId,
    GovernanceMetricKind Kind,
    string? DocumentId,
    string? RuleId,
    string? DetailCode,
    int? TokenCount,
    DateTimeOffset OccurredAt);

public sealed record GovernanceMetricRecord(
    string TenantId,
    string ProjectId,
    string TurnId,
    string EventId,
    GovernanceMetricKind Kind,
    string? DocumentId,
    string? RuleId,
    string? DetailCode,
    int? TokenCount,
    DateTimeOffset OccurredAt);

public interface IGovernanceRuntimeStore
{
    Task<GovernanceTurnReceiptRecord> CreateReceiptAsync(
        GovernanceTurnReceiptCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<GovernanceTurnReceiptRecord> CompleteReceiptAsync(
        GovernanceTurnReceiptCompleteCommand command,
        CancellationToken cancellationToken = default);

    Task<GovernanceTurnReceiptRecord?> GetReceiptAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernanceTurnReceiptRecord>> ListReceiptsAsync(
        string tenantId,
        string? projectId,
        string? afterTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AppendMetricAsync(
        GovernanceMetricAppendCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernanceMetricRecord>> ListMetricsAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default);
}

public sealed class GovernanceRuntimeConflictException(string message) : Exception(message);
