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

    /// <summary>
    /// Fase 0A1 (BR-001): cria o card de uma FATIA do plano. A chave lógica
    /// <c>(tenant, plan_id, plan_slice_key)</c> é única no banco, então a operação é idempotente por
    /// construção: uma segunda chamada — retry, entrega duplicada do evento ou dois consumidores
    /// concorrentes — devolve o card já existente com <c>Created=false</c> e não cria nada.
    /// </summary>
    Task<BoardPlanCardResult> CreatePlanCardAsync(
        BoardPlanCardCreateCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cards já materializados do plano, por fatia. É a MEDIDA de completude: quem valida o plano
    /// compara este conjunto com o conjunto previsto, em vez de confiar no marker.
    /// </summary>
    Task<IReadOnlyList<BoardPlanCardRecord>> ListPlanCardsAsync(
        string tenantId, string planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adota cards criados ANTES desta chave lógica existir, carimbando plano e fatia quando o
    /// título casa exatamente com a fatia prevista e o slot ainda está livre. Sem isso, um plano
    /// materializado no fluxo antigo pareceria vazio e seria recriado — duplicando o board.
    /// Devolve quantos cards foram adotados.
    /// </summary>
    Task<int> AdoptPlanCardsAsync(
        BoardPlanCardAdoptCommand command, CancellationToken cancellationToken = default);

    Task<BoardTaskRecord> MoveTaskAsync(
        BoardTaskMoveCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> SetTaskPriorityAsync(
        BoardTaskPriorityCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> SetTaskPlanningAsync(
        BoardTaskPlanningCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> SetTaskArchivedAsync(
        BoardTaskArchiveCommand command, CancellationToken cancellationToken = default);
    Task<BoardTaskRecord> DismissTaskAsync(
        BoardTaskDismissCommand command, CancellationToken cancellationToken = default);

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

    // PLAT-04: read-model row for the per-feature evaluation metrics and semantic stuck detection.
    // Joins each durable attempt to its task title (feature id is parsed from the title) and its
    // instruction content hash (to detect repeated/near-identical instructions). Ordered by
    // (task, attempt_number) so the stuck detector receives the attempt history in sequence.
    Task<IReadOnlyList<FeatureAttemptRow>> ListFeatureAttemptRowsAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default);
}

// PLAT-04: strictly-recorded attempt facts used by the measurement layer. Never fabricated —
// every field maps to a persisted work_attempts column (or the joined task title / instruction hash).
// Fase 5: AttemptId e ProducerAgentId expõem colunas que já existiam (id, producer_agent_id) para
// que o Evaluation Service agregue desempenho por agente e cruze com `model_invocations`.
public sealed record FeatureAttemptRow(
    string TaskId, string TaskTitle, int AttemptNumber, string State, string OperationalState,
    decimal CostUsd, long TokensInput, long TokensOutput, long? DurationMs,
    string? FailureReason, string? InstructionContentHash,
    string AttemptId = "", string ProducerAgentId = "");

public sealed record BoardSolicitationRecord(
    string TenantId, string Id, string ProjectId, string AuthorProfileId, string Kind,
    string Title, string Body, string State, string? SupersedesId, DateTimeOffset CreatedAt,
    bool Internal);

public sealed record BoardDemandRecord(
    string TenantId, string Id, string ProjectId, string? SolicitationId, string Title,
    string Description, string State, string Priority, DateTimeOffset CreatedAt, bool Internal,
    string? PhaseName = null);

public sealed record BoardProgressRecord(decimal Executed, decimal Validated, decimal Approved);

public sealed record BoardTaskRecord(
    string TenantId, string Id, string ProjectId, string? DemandId, string Title, string State,
    string Priority, string? AssigneeAgentId, string? BlockedReason, int InstructionVersion,
    BoardProgressRecord Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? DueAt, DateTimeOffset? ArchivedAt, long Version, string InternalState,
    string BackingSolicitationId, string BackingDemandId, string? PhaseName = null,
    string CardType = "agent_task");

public sealed record BoardTaskPageQuery(
    string? ProjectId, string? DemandId, string? Search, string? State, string? Priority,
    string? AssigneeAgentId, string Archive, DateTimeOffset? UpdatedSince, int Offset, int Limit,
    string? PhaseName = null);

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
    DateTimeOffset OccurredAt, string Severity = "info");

public sealed record BoardSolicitationCreateCommand(
    string TenantId, string Id, string ProjectId, string AuthorProfileId, string Kind,
    string Title, string Body, string? SupersedesId, DateTimeOffset OccurredAt);

public sealed record BoardDemandCreateCommand(
    string TenantId, string Id, string ProjectId, string? SolicitationId,
    string BackingSolicitationId, string AuthorProfileId, string Title, string Description,
    string Priority, DateTimeOffset OccurredAt, string? PhaseName = null);

public sealed record BoardTaskCreateCommand(
    string TenantId, string Id, string ProjectId, string? DemandId, string BackingDemandId,
    string BackingSolicitationId, string AuthorProfileId, string Title, string Priority,
    string? AssigneeAgentId, DateTimeOffset? DueAt, string InstructionId,
    string InstructionBody, DateTimeOffset OccurredAt, string? PhaseName = null,
    string CardType = "agent_task");

public sealed record BoardTaskCreateResult(BoardTaskRecord Task, BoardInstructionRecord Instruction);

/// <summary>Criação de card ancorada na fatia do plano que o originou (Fase 0A1).</summary>
public sealed record BoardPlanCardCreateCommand(
    BoardTaskCreateCommand Task, string PlanId, string PlanSliceKey);

/// <summary>
/// Resultado da criação idempotente. <see cref="Created"/> falso significa que a fatia JÁ estava
/// materializada — o card devolvido é o que já existia, com o mesmo id de sempre.
/// </summary>
public sealed record BoardPlanCardResult(BoardTaskRecord Task, bool Created);

/// <summary>Card materializado do plano, identificado pela fatia.</summary>
public sealed record BoardPlanCardRecord(
    string SliceKey, string TaskId, string Title, string State, string InternalState,
    DateTimeOffset CreatedAt);

/// <summary>Adoção de cards legados: cada fatia é casada pelo título exato previsto no plano.</summary>
public sealed record BoardPlanCardAdoptCommand(
    string TenantId, string PlanId, string DemandId, IReadOnlyList<BoardPlanSlice> Slices);

public sealed record BoardPlanSlice(string SliceKey, string Title);

public sealed record BoardSolicitationTransitionCommand(
    string TenantId, string SolicitationId, string State, DateTimeOffset OccurredAt);

public sealed record BoardTaskMoveCommand(
    string TenantId, string TaskId, string ToState, string? Note, string ChangedByKind,
    DateTimeOffset OccurredAt);

public sealed record BoardTaskPriorityCommand(
    string TenantId, string TaskId, string Priority, DateTimeOffset OccurredAt);

public sealed record BoardTaskPlanningCommand(
    string TenantId, string TaskId, string AssigneeAgentId, DateTimeOffset DueAt,
    DateTimeOffset OccurredAt);

public sealed record BoardTaskArchiveCommand(
    string TenantId, string TaskId, bool Archived, string ChangedByKind,
    DateTimeOffset OccurredAt);

/// <summary>
/// Encerra sem sucesso um card que deixou de representar trabalho necessário. Diferente de
/// arquivar uma entrega concluída, esta operação preserva <c>cancelled</c> como fato interno e
/// exige justificativa auditável.
/// </summary>
public sealed record BoardTaskDismissCommand(
    string TenantId, string TaskId, string Reason, string ChangedByKind,
    DateTimeOffset OccurredAt);

public sealed record BoardInstructionAppendCommand(
    string TenantId, string TaskId, string InstructionId, string Body, string AuthorKind,
    string? AuthorId, DateTimeOffset OccurredAt);

public sealed class WorkBoardReferenceNotFoundException(string reference) : Exception(reference)
{
    public string Reference { get; } = reference;
}

public sealed class WorkBoardInvalidStateException(string detail) : Exception(detail);
