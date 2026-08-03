namespace Harness.Modules.Coordination.Application;

/// <summary>Estado do circuito de um card.</summary>
public enum CardCircuitState
{
    /// <summary>Fechado: o card pode ser despachado.</summary>
    Closed = 0,

    /// <summary>Aberto: o card não é mais despachado até a Bruna replanejar.</summary>
    Open = 1
}

/// <summary>
/// Estado durável do circuito de um card. <see cref="OpenedAt"/> registra QUANDO o circuito abriu,
/// mas não existe janela de reabertura por tempo — só o replanejamento fecha.
/// </summary>
public sealed record CardCircuitSnapshot(
    string CardId,
    CardCircuitState State,
    int ConsecutiveFailures,
    DateTimeOffset? OpenedAt = null,
    string? LastFailureReasonCode = null)
{
    public bool IsDispatchable => State == CardCircuitState.Closed;

    public static CardCircuitSnapshot Closed(string cardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        return new CardCircuitSnapshot(cardId.Trim(), CardCircuitState.Closed, 0);
    }
}

/// <summary>
/// Circuit breaker POR CARD (B4) — distinto do circuito por conta já existente no
/// <c>CapacityManager</c>, que responde por indisponibilidade do provedor.
///
/// A diferença de fundo é a causa: quando a mesma conta falha em vários cards, o problema é da
/// conta; quando vários agentes falham no MESMO card, o problema é do card — instrução ambígua,
/// escopo impossível, critério de aceite inatingível. Trocar de agente não conserta um enunciado
/// errado, e reenfileirar de novo só queima cota repetindo o mesmo fracasso.
///
/// Por isso a reabertura aqui NÃO é por cooldown. Tempo não corrige enunciado: um card que falhou
/// três vezes seguidas volta a ser despachável somente quando a Bruna o REPLANEJA — reescreve a
/// instrução, corta o escopo ou o decompõe. Sucesso em qualquer rodada zera a contagem, porque o
/// que interessa é a sequência de falhas, não o total histórico.
/// </summary>
public static class CardCircuitBreakerPolicy
{
    /// <summary>
    /// Três falhas consecutivas abrem o circuito do card. É o DEFAULT — o operador pode
    /// mudá-lo por configuração (<c>AgentRunSettings.CardCircuitFailureThreshold</c>), porque
    /// quantas falhas dizem "o enunciado está errado" depende de quanto a infraestrutura da vez
    /// está confiável, e isso o código não sabe.
    /// </summary>
    public const int ConsecutiveFailureThreshold = 3;

    public const string ReasonCircuitOpen = "card.circuit_open";

    /// <summary>Registra uma falha da rodada. Ao atingir o limiar, o circuito abre.</summary>
    public static CardCircuitSnapshot RecordFailure(
        CardCircuitSnapshot snapshot,
        DateTimeOffset now,
        string? reasonCode = null,
        int threshold = ConsecutiveFailureThreshold)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Limiar abaixo de 1 abriria o circuito antes da primeira falha — o card nasceria
        // condenado. Configuração inválida cai no default em vez de virar comportamento novo.
        if (threshold < 1)
        {
            threshold = ConsecutiveFailureThreshold;
        }

        // Circuito já aberto não conta de novo: ele não deveria ter sido despachado.
        if (snapshot.State == CardCircuitState.Open)
        {
            return snapshot with { LastFailureReasonCode = reasonCode ?? snapshot.LastFailureReasonCode };
        }

        var failures = snapshot.ConsecutiveFailures + 1;
        return failures >= threshold
            ? snapshot with
            {
                State = CardCircuitState.Open,
                ConsecutiveFailures = failures,
                OpenedAt = now,
                LastFailureReasonCode = reasonCode
            }
            : snapshot with { ConsecutiveFailures = failures, LastFailureReasonCode = reasonCode };
    }

    /// <summary>Sucesso zera a sequência: o que abre o circuito é a repetição, não o histórico.</summary>
    public static CardCircuitSnapshot RecordSuccess(CardCircuitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with
        {
            State = CardCircuitState.Closed,
            ConsecutiveFailures = 0,
            OpenedAt = null,
            LastFailureReasonCode = null
        };
    }

    /// <summary>
    /// Único caminho de reabertura: o replanejamento da Bruna. Nem tempo, nem troca de agente, nem
    /// nova tentativa manual fecham um circuito aberto.
    /// </summary>
    public static CardCircuitSnapshot Replan(CardCircuitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with
        {
            State = CardCircuitState.Closed,
            ConsecutiveFailures = 0,
            OpenedAt = null,
            LastFailureReasonCode = null
        };
    }

    /// <summary>
    /// Filtra o que pode ser despachado. Cards com circuito aberto saem da fila em silêncio para o
    /// despacho e ficam visíveis para o replanejamento — nunca são reenfileirados.
    /// </summary>
    public static IReadOnlyList<string> FilterDispatchable(
        IReadOnlyCollection<string> cardIds,
        IReadOnlyDictionary<string, CardCircuitSnapshot> circuits)
    {
        ArgumentNullException.ThrowIfNull(cardIds);
        ArgumentNullException.ThrowIfNull(circuits);

        return cardIds
            .Where(cardId =>
                !circuits.TryGetValue(cardId, out var circuit) || circuit.IsDispatchable)
            .ToArray();
    }
}
