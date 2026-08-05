namespace Harness.Persistence.Abstractions.Attention;

/// <summary>
/// A store do SLA de atenção humana (Human Attention Loop). Um registro nasce SOMENTE de um ASK
/// genuíno — CLOSED resolve sozinho, INFER registra premissa, DEFER registra lacuna; nenhum dos
/// três interrompe uma pessoa. Criação é idempotente por <c>CorrelationId</c>: o mesmo card
/// escalado re-observado a cada ciclo do loop não duplica pedido nem reinicia lembretes.
/// </summary>
public interface IHumanAttentionStore
{
    /// <summary>Cria, ou devolve o existente com a mesma correlação (idempotência).</summary>
    Task<HumanAttentionRecord> CreateAsync(
        HumanAttentionCreateCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HumanAttentionRecord>> ListOpenAsync(
        string tenantId, string? projectId = null, CancellationToken cancellationToken = default);

    Task<HumanAttentionRecord?> GetByCorrelationAsync(
        string tenantId, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>Registra o efeito de um passo de notificação decidido pela política.</summary>
    Task RecordEscalationAsync(
        string tenantId, string id, string status, int reminderCount, DateTimeOffset? nextActionAt,
        string channelStatus, CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(
        string tenantId, string id, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task AnswerAsync(
        string tenantId, string id, string answer, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task SupersedeAsync(
        string tenantId, string id, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

public sealed record HumanAttentionRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string? ConversationId,
    string? CardId,
    string? GraphNodeId,
    string Question,
    string Reason,
    string Severity,
    string BlockingScope,
    string Status,
    string SourceAgent,
    string CorrelationId,
    int ReminderCount,
    string ChannelStatus,
    string? Answer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? NextActionAt);

public sealed record HumanAttentionCreateCommand(
    string TenantId,
    string Id,
    string ProjectId,
    string Question,
    string Reason,
    string Severity,
    string BlockingScope,
    string SourceAgent,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string? ConversationId = null,
    string? CardId = null,
    string? GraphNodeId = null);
