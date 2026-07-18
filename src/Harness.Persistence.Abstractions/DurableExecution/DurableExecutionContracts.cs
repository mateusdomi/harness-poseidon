namespace Harness.Persistence.Abstractions.DurableExecution;

public sealed record DurableExecutionStartRequest(
    string TenantId,
    string ProjectId,
    string ExecutionId,
    string PayloadJson,
    DurableRetryPolicy RetryPolicy,
    DateTimeOffset AvailableAt,
    string IdempotencyKey);

public sealed record DurableExecutionSnapshot(
    string TenantId,
    string ProjectId,
    string ExecutionId,
    DurableExecutionState State,
    string PayloadJson,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset AvailableAt,
    string? ActiveAttemptId,
    string? ActiveOwner,
    long FencingToken,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? LastHeartbeatAt,
    string? LatestCheckpointKey,
    string? LatestCheckpointJson,
    string? LastError,
    long Version);

public sealed record DurableExecutionLease(
    string TenantId,
    string ProjectId,
    string ExecutionId,
    string AttemptId,
    int AttemptNumber,
    string Owner,
    long FencingToken,
    DateTimeOffset LeaseExpiresAt,
    string PayloadJson,
    string? LatestCheckpointKey,
    string? LatestCheckpointJson);

public enum DurableCommandStatus
{
    Applied,
    IdempotentReplay,
    NotFound,
    VersionConflict,
    InvalidState,
    LeaseRejected,
    IdempotencyConflict,
}

public sealed record DurableCommandResult(
    DurableCommandStatus Status,
    long? Version = null,
    DurableExecutionState? State = null)
{
    public bool Succeeded => Status is DurableCommandStatus.Applied or DurableCommandStatus.IdempotentReplay;
}

public sealed record DurableLeaseCommand(
    string TenantId,
    string ExecutionId,
    string AttemptId,
    string Owner,
    long FencingToken,
    DateTimeOffset OccurredAt,
    string IdempotencyKey);

public sealed record DurableCheckpointCommand(
    string TenantId,
    string ExecutionId,
    string AttemptId,
    string Owner,
    long FencingToken,
    string CheckpointKey,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    string IdempotencyKey);

public sealed record DurableFailureCommand(
    string TenantId,
    string ExecutionId,
    string AttemptId,
    string Owner,
    long FencingToken,
    string ErrorCode,
    string ErrorDetail,
    DateTimeOffset OccurredAt,
    string IdempotencyKey);

public sealed record DurableSignalCommand(
    string TenantId,
    string ExecutionId,
    string SignalName,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    string IdempotencyKey);

public sealed record DurableTimerCommand(
    string TenantId,
    string ExecutionId,
    string TimerId,
    DateTimeOffset DueAt,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    string IdempotencyKey);

public sealed record DurableReconciliationResult(
    int Requeued,
    int DeadLettered,
    IReadOnlyList<string> ExecutionIds);
