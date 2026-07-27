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

            // RETOMADA. Um turno interrompido no meio da invocação (queda do Host, lease vencida)
            // deixa o recibo em `delivered`; a nova tentativa refaz o caminho e entrega o MESMO
            // bundle outra vez. Sem esta repetição idempotente, a transição era recusada como
            // "stale", a tentativa morria em conflito e o turno esgotava as retentativas sem nunca
            // sair do lugar — a mensagem do usuário ficava sem resposta para sempre. A repetição é
            // registrada como uma nova versão do recibo: o ledger mostra as duas entregas, e o que
            // é idempotente é o direito de repetir, não o registro do fato.
            (GovernanceReceiptState.Delivered, GovernanceReceiptState.Delivered) => true,
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

public sealed record ContextSnapshotSourceRecord(
    string SourceId,
    string Kind,
    string CitationReference);

public sealed record ContextSnapshotCreateCommand(
    string TenantId,
    string SnapshotId,
    string ProjectId,
    string WorkTaskId,
    string ExecutionId,
    string ManifestVersion,
    IReadOnlyList<string> BundleManifestIds,
    IReadOnlyList<ContextSnapshotSourceRecord> Sources,
    string AssembledContextHash,
    int TokenCount,
    DateTimeOffset CreatedAt);

public sealed record ContextSnapshotRecord(
    string TenantId,
    string SnapshotId,
    string ProjectId,
    string WorkTaskId,
    string ExecutionId,
    string ManifestVersion,
    IReadOnlyList<string> BundleManifestIds,
    IReadOnlyList<ContextSnapshotSourceRecord> Sources,
    string AssembledContextHash,
    int TokenCount,
    DateTimeOffset CreatedAt);

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

    Task<ContextSnapshotRecord> CreateContextSnapshotAsync(
        ContextSnapshotCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<ContextSnapshotRecord?> GetContextSnapshotAsync(
        string tenantId,
        string snapshotId,
        CancellationToken cancellationToken = default);
}

public sealed class GovernanceRuntimeConflictException(string message) : Exception(message);
