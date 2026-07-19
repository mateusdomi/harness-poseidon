namespace Harness.Persistence.Abstractions.WorkChain;

public interface IWorkBoardStore
{
    Task<BoardSolicitationRecord?> GetSolicitationAsync(
        string tenantId, string solicitationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardSolicitationRecord>> ListSolicitationsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<BoardSolicitationRecord> CreateSolicitationAsync(
        BoardSolicitationCreateCommand command, CancellationToken cancellationToken = default);
    Task<BoardSolicitationRecord> TransitionSolicitationAsync(
        BoardSolicitationTransitionCommand command, CancellationToken cancellationToken = default);

    Task<BoardDemandRecord?> GetDemandAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardDemandRecord>> ListDemandsAsync(
        string tenantId, string? projectId, string? solicitationId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<BoardDemandRecord> CreateDemandAsync(
        BoardDemandCreateCommand command, CancellationToken cancellationToken = default);

    Task<BoardTaskRecord?> GetTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardTaskRecord>> ListTasksAsync(
        string tenantId, string? projectId, string? demandId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<BoardTaskPageRecord> PageTasksAsync(
        string tenantId, BoardTaskPageQuery query,
        CancellationToken cancellationToken = default);
    Task<BoardTaskCreateResult> CreateTaskAsync(
        BoardTaskCreateCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> MoveTaskAsync(
        BoardTaskMoveCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> SetTaskPriorityAsync(
        BoardTaskPriorityCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> SetTaskArchivedAsync(
        BoardTaskArchiveCommand command, CancellationToken cancellationToken = default);

    Task<BoardInstructionRecord?> GetInstructionAsync(
        string tenantId, string instructionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardInstructionRecord>> ListInstructionsAsync(
        string tenantId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<BoardInstructionRecord> AppendInstructionAsync(
        BoardInstructionAppendCommand command, CancellationToken cancellationToken = default);

    Task<BoardAttemptRecord?> GetAttemptAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardAttemptRecord>> ListAttemptsAsync(
        string tenantId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<BoardAttemptEventRecord?> GetAttemptEventAsync(
        string tenantId, string eventId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardAttemptEventRecord>> ListAttemptEventsAsync(
        string tenantId, string? attemptId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
}

public sealed record BoardSolicitationRecord(
    string TenantId, string Id, string ProjectId, string AuthorProfileId, string Kind,
    string Title, string Body, string State, string? SupersedesId, DateTimeOffset CreatedAt,
    bool Internal);

public sealed record BoardDemandRecord(
    string TenantId, string Id, string ProjectId, string? SolicitationId, string Title,
    string Description, string State, string Priority, DateTimeOffset CreatedAt, bool Internal);

public sealed record BoardProgressRecord(decimal Executed, decimal Validated, decimal Approved);

public sealed record BoardTaskRecord(
    string TenantId, string Id, string ProjectId, string? DemandId, string Title, string State,
    string Priority, string? AssigneeAgentId, string? BlockedReason, int InstructionVersion,
    BoardProgressRecord Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? DueAt, DateTimeOffset? ArchivedAt, long Version, string InternalState,
    string BackingSolicitationId, string BackingDemandId);

public sealed record BoardTaskPageQuery(
    string? ProjectId, string? DemandId, string? Search, string? State, string? Priority,
    string? AssigneeAgentId, string Archive, DateTimeOffset? UpdatedSince, int Offset, int Limit);

public sealed record BoardTaskPageRecord(
    IReadOnlyList<BoardTaskRecord> Items, int Total);

public sealed record BoardInstructionRecord(
    string TenantId, string Id, string TaskId, int Version, string Body, string AuthorKind,
    string? AuthorId, DateTimeOffset CreatedAt);

public sealed record BoardAttemptRecord(
    string TenantId, string Id, string TaskId, int Number, string State, string AgentId,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, decimal CostUsd,
    long TokensInput, long TokensOutput, IReadOnlyList<string> CommitRefs, string? Summary,
    string? FailureReason);

public sealed record BoardAttemptEventRecord(
    string TenantId, string Id, string AttemptId, string Kind, string Content,
    DateTimeOffset OccurredAt);

public sealed record BoardSolicitationCreateCommand(
    string TenantId, string Id, string ProjectId, string AuthorProfileId, string Kind,
    string Title, string Body, string? SupersedesId, DateTimeOffset OccurredAt);

public sealed record BoardDemandCreateCommand(
    string TenantId, string Id, string ProjectId, string? SolicitationId,
    string BackingSolicitationId, string AuthorProfileId, string Title, string Description,
    string Priority, DateTimeOffset OccurredAt);

public sealed record BoardTaskCreateCommand(
    string TenantId, string Id, string ProjectId, string? DemandId, string BackingDemandId,
    string BackingSolicitationId, string AuthorProfileId, string Title, string Priority,
    string? AssigneeAgentId, DateTimeOffset? DueAt, string InstructionId,
    string InstructionBody, DateTimeOffset OccurredAt);

public sealed record BoardTaskCreateResult(BoardTaskRecord Task, BoardInstructionRecord Instruction);

public sealed record BoardSolicitationTransitionCommand(
    string TenantId, string SolicitationId, string State, DateTimeOffset OccurredAt);

public sealed record BoardTaskMoveCommand(
    string TenantId, string TaskId, string ToState, string? Note, string ChangedByKind,
    DateTimeOffset OccurredAt);

public sealed record BoardTaskPriorityCommand(
    string TenantId, string TaskId, string Priority, DateTimeOffset OccurredAt);

public sealed record BoardTaskArchiveCommand(
    string TenantId, string TaskId, bool Archived, string ChangedByKind,
    DateTimeOffset OccurredAt);

public sealed record BoardInstructionAppendCommand(
    string TenantId, string TaskId, string InstructionId, string Body, string AuthorKind,
    string? AuthorId, DateTimeOffset OccurredAt);

public sealed class WorkBoardReferenceNotFoundException(string reference) : Exception(reference)
{
    public string Reference { get; } = reference;
}

public sealed class WorkBoardInvalidStateException(string detail) : Exception(detail);
