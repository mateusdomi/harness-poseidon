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

    /// <summary>
    /// Publica um estado granular do turno em andamento (ex.: <c>reading_context</c>,
    /// <c>thinking</c>, <c>planning</c>, <c>delegating</c>) como evento
    /// <c>chief.turnStateChanged</c> em tempo real, carregando um heartbeat
    /// (<c>lastActivityAt</c>) para o front distinguir "trabalhando" de "travado".
    /// É observabilidade honesta: só reporta fases pelas quais o turno realmente
    /// passa, nunca uma resposta fabricada. Não altera o estado durável do mailbox.
    /// </summary>
    Task RecordActivityAsync(
        ChiefTurnActivityCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Comando de observabilidade do turno: estado granular + heartbeat, com metadados
/// opcionais de delegação (agente e início do trabalho) para o balão da conversa.
/// </summary>
public sealed record ChiefTurnActivityCommand(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    string State, DateTimeOffset LastActivityAt,
    string? AgentName = null, DateTimeOffset? ActivityStartedAt = null,
    string? Detail = null);

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

/// <summary>
/// A superfície que o Chefe DECLAROU para a demanda, propagada do turno até o planejamento. Cada
/// campo é tri-state: <c>true</c>/<c>false</c> são declarações do Chefe; nulo é "não declarei" e
/// mantém a inferência por texto. Espelha (sem acoplar módulos) a declaração do contrato de saída
/// do Chefe e as dicas de decomposição do planner.
/// </summary>
public sealed record ChiefDemandSurfaceDeclaration(
    bool? Frontend = null,
    bool? Backend = null,
    bool? ExternalCredential = null,
    bool? TechnicalUncertainty = null,
    bool? Decision = null);

public sealed record ChiefDemandSeed(
    string DemandId, string BackingSolicitationId, string Title, string Description,
    string RiskTier, IReadOnlyList<string> AcceptanceCriteria,
    string? Specialty = null, ChiefDemandSurfaceDeclaration? Surfaces = null)
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
