using Harness.Persistence.Abstractions.Conversations;

namespace Harness.Persistence.Abstractions.Agents;

public interface IChiefTurnStore
{
    Task<ChiefTurnRecord> EnqueueAsync(ChiefTurnEnqueueCommand command, CancellationToken cancellationToken = default);
    Task<ChiefTurnLease> AcquireAsync(ChiefTurnAcquireCommand command, CancellationToken cancellationToken = default);
    Task<ChiefTurnLease?> AcquireNextAsync(
        string ownerId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task CompleteAsync(ChiefTurnCompleteCommand command, CancellationToken cancellationToken = default);
    Task FailAsync(ChiefTurnFailCommand command, CancellationToken cancellationToken = default);
    Task<ChiefTurnRecord?> GetAsync(string tenantId, string turnId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persiste a mensagem humana e registra o turno como BLOQUEADO por prontidão, sem
    /// enfileirar execução (ADR-019). Um turno recusado por falta de provider/modelo/workflow
    /// não é item de mailbox: a mensagem é durável, o bloqueio é auditável e nenhuma resposta
    /// é fabricada. Idempotente por mensagem: o retry devolve o mesmo bloqueio, sem duplicar
    /// mensagem, registro ou evento.
    /// </summary>
    Task<ChiefTurnBlockRecord> BlockAsync(
        ChiefTurnBlockCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Bloqueador tipado do turno: código estável e IDs relacionados, nunca texto livre.</summary>
public sealed record ChiefTurnBlockerRecord(string Code, IReadOnlyList<string> RelatedIds);

/// <summary>Próxima ação recomendada para desbloquear o turno.</summary>
public sealed record ChiefTurnNextActionRecord(string Code, string Route, string? ResourceId);

public sealed record ChiefTurnBlockRecord(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    string UserMessageId, string ReadinessState, string CorrelationId,
    IReadOnlyList<ChiefTurnBlockerRecord> Blockers,
    IReadOnlyList<ChiefTurnNextActionRecord> NextActions,
    DateTimeOffset CreatedAt);

public sealed record ChiefTurnBlockCommand(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    MessageRecord UserMessage, string ReadinessState, string CorrelationId,
    IReadOnlyList<ChiefTurnBlockerRecord> Blockers,
    IReadOnlyList<ChiefTurnNextActionRecord> NextActions,
    string IdempotencyKey, DateTimeOffset OccurredAt);

public sealed record ChiefTurnRecord(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    string UserMessageId, string State, int AttemptCount, string? SessionId,
    string? ResponseMessageId, string? LastErrorCode, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, ChiefInvocationSelection? Selection = null);

public sealed record ChiefInvocationSelection(
    string AccountId, string ModelId, string ModelName, string Effort,
    string ProviderEffortValue, IReadOnlyList<string> FallbackModelIds,
    string Source, string Reason, decimal? EstimatedCostUsd, decimal? QuotaRemainingUsd);

public sealed record ChiefTurnEnqueueCommand(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    string ChiefAgentId, MessageRecord UserMessage, string IdempotencyKey,
    DateTimeOffset OccurredAt, ChiefInvocationSelection? Selection = null);

public sealed record ChiefTurnAcquireCommand(
    string TenantId, string TurnId, string OwnerId, DateTimeOffset Now, TimeSpan LeaseDuration);

public sealed record ChiefTurnLease(
    ChiefTurnRecord Turn, string OwnerId, long FencingToken, DateTimeOffset ExpiresAt,
    string ChiefAgentId, string Instruction, string? SessionId);

public sealed record ChiefTurnCompleteCommand(
    ChiefTurnLease Lease, MessageRecord ChiefMessage, IReadOnlyList<string> Chunks,
    string SessionId, string StatusDigestJson, DateTimeOffset OccurredAt,
    IReadOnlyList<ChiefDemandSeed>? Demands = null);

public sealed record ChiefDemandSeed(
    string DemandId, string BackingSolicitationId, string Title, string Description,
    string RiskTier, IReadOnlyList<string> AcceptanceCriteria)
{
    private static readonly HashSet<string> RiskTiers =
        new(["low", "medium", "high", "critical"], StringComparer.Ordinal);

    public ChiefDemandSeed Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DemandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BackingSolicitationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(Description);
        ArgumentNullException.ThrowIfNull(AcceptanceCriteria);
        if (!RiskTiers.Contains(RiskTier))
        {
            throw new ArgumentException("The demand risk tier is not part of the closed set.", nameof(RiskTier));
        }

        var criteria = AcceptanceCriteria
            .Select(criterion => criterion.Trim())
            .Where(criterion => criterion.Length > 0)
            .ToArray();
        return criteria.Length == 0
            ? throw new ArgumentException("At least one acceptance criterion is required.", nameof(AcceptanceCriteria))
            : this with
            {
                Title = Title.Trim().Length > 500 ? Title.Trim()[..500] : Title.Trim(),
                Description = Description.Trim(),
                AcceptanceCriteria = criteria,
            };
    }
}

public sealed record ChiefTurnFailCommand(
    ChiefTurnLease Lease, string ErrorCode, DateTimeOffset OccurredAt, bool Retryable);

public sealed class ChiefTurnConflictException(string message) : Exception(message);
