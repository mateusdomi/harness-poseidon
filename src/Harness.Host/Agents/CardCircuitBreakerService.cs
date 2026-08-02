using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Coordination;

namespace Harness.Host.Agents;

/// <summary>
/// Compõe a política pura do circuito por card com o estado durável.
///
/// A contagem de falhas é DERIVADA do histórico de tentativas do card, não incrementada em cada
/// ponto de falha. Duas razões: derivar é idempotente — reprocessar o mesmo histórico dá o mesmo
/// resultado, e um reinício no meio de uma rodada não perde nem duplica contagem; e não exige um
/// gancho em cada lugar que pode falhar, que é justamente onde um gancho seria esquecido.
///
/// O replanejamento é a exceção: ele não está no histórico de tentativas, é um ato da Bruna, e por
/// isso vive no estado durável e prevalece sobre as falhas anteriores a ele.
/// </summary>
internal sealed class CardCircuitBreakerService(ICardCircuitBreakerStore store)
{
    private readonly ICardCircuitBreakerStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Recalcula o circuito a partir das tentativas e persiste. As tentativas devem vir em ordem
    /// cronológica; <paramref name="failedStates"/> define o que conta como falha.
    /// </summary>
    public async Task<CardCircuitSnapshot> SynchronizeAsync(
        string tenantId,
        string projectId,
        string taskId,
        IReadOnlyList<CardAttemptOutcome> attempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var stored = await _store.GetAsync(tenantId, taskId, cancellationToken);

        // Falha anterior ao replanejamento não conta: o card que a Bruna reescreveu é outro card
        // do ponto de vista do enunciado, mesmo mantendo o id.
        var horizon = stored?.ReplannedAt;
        var snapshot = CardCircuitSnapshot.Closed(taskId);
        var lastFailureAt = default(DateTimeOffset?);
        string? lastReason = null;
        foreach (var attempt in attempts.Where(item => horizon is null || item.OccurredAt > horizon))
        {
            if (IsFailure(attempt))
            {
                snapshot = CardCircuitBreakerPolicy.RecordFailure(
                    snapshot, attempt.OccurredAt, attempt.FailureReason ?? attempt.State);
                lastFailureAt = attempt.OccurredAt;
                lastReason = attempt.FailureReason ?? attempt.State;
            }
            else if (IsSuccess(attempt.State))
            {
                snapshot = CardCircuitBreakerPolicy.RecordSuccess(snapshot);
                lastFailureAt = null;
                lastReason = null;
            }
        }

        var persisted = stored?.ConsecutiveFailures ?? 0;
        if (persisted == snapshot.ConsecutiveFailures &&
            (stored?.IsOpen ?? false) == (snapshot.State == CardCircuitState.Open))
        {
            return snapshot;
        }

        // A sequência derivada é MENOR que a persistida quando houve sucesso: zera e sai.
        if (snapshot.ConsecutiveFailures < persisted)
        {
            await _store.RecordSuccessAsync(
                tenantId, projectId, taskId,
                lastFailureAt ?? DateTimeOffset.UtcNow, cancellationToken);
            return snapshot;
        }

        // Uma chamada por falha ainda não contabilizada, sempre com o limiar real: assim a
        // contagem gravada é idêntica à derivada e a abertura acontece na falha certa — em vez de
        // um atalho que gravaria "1" para uma sequência de três e reescreveria o mesmo estado a
        // cada ciclo do laço.
        for (var pending = persisted; pending < snapshot.ConsecutiveFailures; pending++)
        {
            await _store.RecordFailureAsync(
                tenantId, projectId, taskId,
                lastFailureAt ?? DateTimeOffset.UtcNow,
                CardCircuitBreakerPolicy.ConsecutiveFailureThreshold,
                lastReason,
                cancellationToken);
        }

        return snapshot;
    }

    /// <summary>Cards com circuito aberto: saem do despacho e entram na fila de replanejamento.</summary>
    public async Task<IReadOnlySet<string>> ListOpenCardsAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        var open = await _store.ListOpenAsync(tenantId, projectId, cancellationToken);
        return open.Select(record => record.TaskId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Fecha o circuito por replanejamento — o único caminho de reabertura.</summary>
    public Task ReplanAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        _store.ReplanAsync(tenantId, projectId, taskId, occurredAt, note, cancellationToken);

    /// <summary>
    /// Rodada perdida do ponto de vista do CARD.
    ///
    /// `cancelled` sozinho não conta: é o mesmo estado operacional de um reinício do Host ou de um
    /// cancelamento do operador, e punir o card por uma ação de infraestrutura abriria o circuito
    /// de cards saudáveis — o circuito só reabre por replanejamento, então um falso positivo aqui
    /// PARA o trabalho de verdade.
    ///
    /// Um MOTIVO gravado é o que separa as duas coisas: ele só existe quando o run realmente
    /// falhou. Sem essa distinção, nove falhas consecutivas de execução no mesmo card chegavam ao
    /// circuito como cancelamentos anônimos e ele não contava nenhuma.
    /// </summary>
    private static bool IsFailure(CardAttemptOutcome attempt) =>
        string.Equals(attempt.State, "failed", StringComparison.Ordinal) ||
        string.Equals(attempt.State, "rejected", StringComparison.Ordinal) ||
        (string.Equals(attempt.State, "cancelled", StringComparison.Ordinal) &&
         !string.IsNullOrWhiteSpace(attempt.FailureReason));

    private static bool IsSuccess(string state) =>
        string.Equals(state, "completed", StringComparison.Ordinal) ||
        string.Equals(state, "merged", StringComparison.Ordinal) ||
        string.Equals(state, "approved", StringComparison.Ordinal);
}

/// <summary>Desfecho de uma tentativa, como o circuito do card precisa vê-lo.</summary>
public readonly record struct CardAttemptOutcome(
    string State,
    string? FailureReason,
    DateTimeOffset OccurredAt);
