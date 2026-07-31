namespace Harness.Persistence.Abstractions.WorkChain;

/// <summary>
/// Fase 0C1/0C2 (BR-003): a INTENÇÃO de merge, registrada antes de qualquer efeito Git.
///
/// Não existe transação distribuída entre Git e SQL, e fingir que existe é pior do que não ter
/// nenhuma: registra-se a intenção antes, de modo que todo desfecho — inclusive queda no meio —
/// seja reconhecível e reconciliável depois.
/// </summary>
public interface IMergeIntentStore
{
    /// <summary>
    /// Registra a intenção. Idempotente por tentativa: um retry reusa a mesma intenção em vez de
    /// abrir outra e arriscar dois merges para o mesmo trabalho.
    /// </summary>
    Task<MergeIntentRecord> RequestAsync(
        MergeIntentRequestCommand command, CancellationToken cancellationToken = default);

    Task<MergeIntentRecord?> GetAsync(
        string tenantId, string mergeIntentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adquire o direito de mergear NAQUELE repositório. O banco garante um único ativo por
    /// repositório — um semáforo em memória protegia um processo, isto protege o recurso.
    /// Devolve nulo quando outro dono vivo detém o merge.
    /// </summary>
    Task<MergeIntentRecord?> TryBeginAsync(
        MergeIntentBeginCommand command, CancellationToken cancellationToken = default);

    /// <summary>Registra o SHA resultante. Falso quando o fencing foi perdido.</summary>
    Task<bool> TryRecordMergedAsync(
        MergeIntentResultCommand command, CancellationToken cancellationToken = default);

    /// <summary>Marca que o estado factual do board acompanhou o efeito Git.</summary>
    Task<bool> TrySettleBoardAsync(
        string tenantId, string mergeIntentId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryFailAsync(
        MergeIntentFailCommand command, CancellationToken cancellationToken = default);

    /// <summary>Intenções que ainda não convergiram, para o reconciliador.</summary>
    Task<IReadOnlyList<MergeIntentRecord>> ListUnsettledAsync(
        int limit, CancellationToken cancellationToken = default);
}

public static class MergeIntentState
{
    public const string Pending = "pending";
    public const string Merging = "merging";
    public const string Merged = "merged";
    public const string Failed = "failed";
    public const string Aborted = "aborted";
}

public sealed record MergeIntentRecord(
    string TenantId,
    string MergeIntentId,
    string RepositoryId,
    string ProjectId,
    string CardId,
    string AttemptId,
    string SourceBranch,
    string TargetBranch,
    string? ExpectedSourceSha,
    string? ExpectedBaseSha,
    string State,
    string? OwnerId,
    long FencingToken,
    DateTimeOffset? LeaseExpiresAt,
    string? ResultSha,
    bool BoardSettled,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MergeIntentRequestCommand(
    string TenantId,
    string MergeIntentId,
    string RepositoryId,
    string ProjectId,
    string CardId,
    string AttemptId,
    string SourceBranch,
    string TargetBranch,
    string? ExpectedSourceSha,
    string? ExpectedBaseSha,
    DateTimeOffset OccurredAt);

public sealed record MergeIntentBeginCommand(
    string TenantId,
    string MergeIntentId,
    string OwnerId,
    DateTimeOffset Now,
    TimeSpan LeaseDuration);

public sealed record MergeIntentResultCommand(
    string TenantId,
    string MergeIntentId,
    string OwnerId,
    long FencingToken,
    string ResultSha,
    DateTimeOffset OccurredAt);

public sealed record MergeIntentFailCommand(
    string TenantId,
    string MergeIntentId,
    string OwnerId,
    long FencingToken,
    string ErrorCode,
    bool Aborted,
    DateTimeOffset OccurredAt);
