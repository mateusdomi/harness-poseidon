namespace Harness.Persistence.Abstractions.Coordination;

/// <summary>
/// Estado durável do circuito de um card. Ver a migration 0097 para o porquê de a reabertura NÃO
/// ser por tempo: quando vários agentes falham no mesmo card, o defeito está no enunciado do card,
/// e nenhum cooldown reescreve enunciado.
/// </summary>
public sealed record CardCircuitRecord(
    string TenantId,
    string TaskId,
    string ProjectId,
    string State,
    int ConsecutiveFailures,
    string? LastFailureReasonCode,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? ReplannedAt,
    string? ReplanNote,
    DateTimeOffset UpdatedAt)
{
    public const string ClosedState = "closed";
    public const string OpenState = "open";

    public bool IsOpen => string.Equals(State, OpenState, StringComparison.Ordinal);
}

public interface ICardCircuitBreakerStore
{
    /// <summary>O circuito do card, ou nulo quando ele nunca falhou.</summary>
    Task<CardCircuitRecord?> GetAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra uma falha da rodada e devolve o estado resultante. Ao atingir
    /// <paramref name="consecutiveFailureThreshold"/>, o circuito abre. Falha em circuito já aberto
    /// não infla a contagem — ele não deveria ter sido despachado.
    ///
    /// O limiar é PARÂMETRO, e não constante daqui, para não existir uma segunda fonte da verdade:
    /// quem o define é a política de coordenação; a persistência só o aplica atomicamente.
    /// </summary>
    Task<CardCircuitRecord> RecordFailureAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        int consecutiveFailureThreshold,
        string? reasonCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sucesso zera a sequência: o que abre o circuito é a repetição, não o histórico.</summary>
    Task<CardCircuitRecord> RecordSuccessAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Único caminho de reabertura: o replanejamento da Bruna. A nota fica registrada para a
    /// auditoria distinguir um card consertado de um card apenas destravado.
    /// </summary>
    Task<CardCircuitRecord> ReplanAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        string? replanNote = null,
        CancellationToken cancellationToken = default);

    /// <summary>Circuitos abertos do projeto — a fila de replanejamento da Bruna.</summary>
    Task<IReadOnlyList<CardCircuitRecord>> ListOpenAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default);
}
