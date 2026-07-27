namespace Harness.Persistence.Abstractions.DurableExecution;

/// <summary>
/// Checkpoint de execução do CARD. Ver a migration 0087 para o porquê de ele não pertencer à
/// tentativa: quando a cota esgota, continuar significa trocar de conta, e um estado amarrado ao
/// ator anterior tornava a continuação impossível justamente no caso que ela deveria cobrir.
/// </summary>
public interface IExecutionCheckpointStore
{
    /// <summary>
    /// Grava o checkpoint e o torna o disponível do card. Um checkpoint anterior ainda não
    /// consumido é marcado como consumido pelo novo — dois disponíveis ao mesmo tempo deixariam a
    /// retomada ambígua.
    /// </summary>
    Task<ExecutionCheckpointRecord> SaveAsync(
        ExecutionCheckpointSaveCommand command, CancellationToken cancellationToken = default);

    /// <summary>O checkpoint disponível do card, ou nulo quando não há o que retomar.</summary>
    Task<ExecutionCheckpointRecord?> GetAvailableAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca o checkpoint como consumido pela nova tentativa. Idempotente por tentativa: repetir
    /// o consumo não reabre nem duplica.
    /// </summary>
    Task<bool> ConsumeAsync(
        string tenantId, string checkpointId, string attemptId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExecutionCheckpointRecord>> ListForTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default);
}

public sealed record ExecutionCheckpointSaveCommand(
    string TenantId,
    string CheckpointId,
    string ProjectId,
    string TaskId,
    string ExecutionId,
    string SourceAttemptId,
    string SourceAccountAlias,
    string SourceRole,
    string Origin,
    string BranchName,
    string? SourceCommit,
    string RepositoryRoot,
    IReadOnlyList<string> ScopeClaims,
    IReadOnlyList<string> ChangedFiles,
    string? ProgressNote,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Evidence,
    long OriginFencingToken,
    DateTimeOffset OccurredAt);

public sealed record ExecutionCheckpointRecord(
    string TenantId,
    string CheckpointId,
    string ProjectId,
    string TaskId,
    string ExecutionId,
    string SourceAttemptId,
    string SourceAccountAlias,
    string SourceRole,
    string Origin,
    string BranchName,
    string? SourceCommit,
    string RepositoryRoot,
    IReadOnlyList<string> ScopeClaims,
    IReadOnlyList<string> ChangedFiles,
    string? ProgressNote,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Evidence,
    long OriginFencingToken,
    string? ConsumedByAttemptId,
    DateTimeOffset? ConsumedAt,
    DateTimeOffset CreatedAt);
