namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Fatos puros de um card já coletados do board para a detecção de saúde do backlog — sem IO.
/// <see cref="BoardState"/> é a coluna visível ('backlog','ready','development','review',
/// 'corrections','testsGates','blocked','done'); <see cref="InternalState"/> é o estado interno da
/// cadeia de trabalho ('ready','running','awaiting_review','completed'); <see cref="Archived"/>
/// exclui cards fora do fluxo; <see cref="UpdatedAt"/> é o último instante em que o card se moveu
/// (transição de board ou início de tentativa). Nenhum valor é fabricado: cada campo mapeia uma
/// coluna persistida de <c>work_tasks</c>.
/// </summary>
public sealed record BacklogCardFacts(
    string TaskId, string Title, string BoardState, string InternalState, bool Archived,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Um card sinalizado como PRESO (stuck) pela detecção de saúde do backlog. <see cref="ReasonCode"/>
/// é tipado; <see cref="StuckForMinutes"/> é o tempo sem progresso desde <c>UpdatedAt</c>. Somente
/// leitura — a detecção NÃO resolve nada, apenas expõe o sinal para o Chefe/PO.
/// </summary>
public sealed record StuckCard(
    string TaskId, string Title, string ReasonCode, string BoardState, string InternalState,
    long StuckForMinutes);

/// <summary>
/// RN-04 — SAÚDE DO BACKLOG. Avaliador PURO e determinístico (sem IO, sem relógio interno, sem
/// aleatoriedade) que detecta cards presos: em trabalho ativo além de um limite de tempo sem
/// progresso. Espelha a forma do <c>CardReadinessEvaluator</c> (função pura, códigos tipados).
///
/// Um card é "preso" quando NÃO está arquivado, está em trabalho ativo — board_state de execução
/// (<c>development/review/corrections/testsGates</c>) OU uma tentativa em curso
/// (<c>internal_state == running</c>) — e não se moveu há mais tempo que o limite. A detecção é
/// conservadora: nunca sinaliza um card recém-movido, um card em coluna passiva
/// (<c>backlog/ready/done</c>) nem um card arquivado. Não auto-resolve; apenas expõe o motivo.
/// </summary>
public static class BacklogHealthEvaluator
{
    public const string StuckRunning = "backlog.stuck.running";
    public const string StuckInProgress = "backlog.stuck.in_progress";

    /// <summary>Colunas de board que representam trabalho ativo e, portanto, devem progredir.</summary>
    private static readonly HashSet<string> ActiveBoardStates = new(
        ["development", "review", "corrections", "testsGates"], StringComparer.Ordinal);

    public static IReadOnlyList<StuckCard> Evaluate(
        IEnumerable<BacklogCardFacts> cards, DateTimeOffset now, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var stuck = new List<StuckCard>();
        foreach (var card in cards)
        {
            ArgumentNullException.ThrowIfNull(card);
            if (card.Archived)
            {
                continue;
            }

            var running = string.Equals(card.InternalState, "running", StringComparison.OrdinalIgnoreCase);
            var inProgressColumn = ActiveBoardStates.Contains(card.BoardState);
            if (!running && !inProgressColumn)
            {
                continue;
            }

            var elapsed = now - card.UpdatedAt;
            if (elapsed <= threshold)
            {
                continue;
            }

            // Uma tentativa em curso presa é o sinal mais forte (agente pode ter morrido sem
            // liberar); a coluna de trabalho parada é o sinal mais amplo. O motivo é tipado.
            var reason = running ? StuckRunning : StuckInProgress;
            stuck.Add(new StuckCard(
                card.TaskId, card.Title, reason, card.BoardState, card.InternalState,
                (long)Math.Floor(elapsed.TotalMinutes)));
        }

        // Mais presos primeiro: o Chefe/PO vê o pior caso no topo.
        return [.. stuck.OrderByDescending(card => card.StuckForMinutes)
            .ThenBy(card => card.TaskId, StringComparer.Ordinal)];
    }
}

/// <summary>
/// Fatos persistidos usados para reconciliar a projeção visual do quadro com a
/// máquina de execução. <see cref="AttemptStates"/> contém somente estados
/// gravados; ausência de tentativa nunca é interpretada como sucesso.
/// </summary>
public sealed record BoardReconciliationFacts(
    string TaskId,
    string BoardState,
    string InternalState,
    bool Archived,
    IReadOnlyList<string> AttemptStates);

public sealed record BoardReconciliationDecision(
    string TaskId,
    string Kind,
    string ReasonCode,
    string? TargetState);

/// <summary>
/// Reconciliador puro do quadro. Só propõe movimento quando a projeção correta
/// é consequência inequívoca do estado interno persistido; inconsistências que
/// exigem interpretação humana são retornadas como alerta.
/// </summary>
public static class BoardStateReconciliationEvaluator
{
    public const string Correction = "correction";
    public const string Attention = "attention";
    public const string CompletedProjectionMismatch = "board.completed_projection_mismatch";
    public const string ReviewProjectionMismatch = "board.review_projection_mismatch";
    public const string RunningProjectionMismatch = "board.running_projection_mismatch";
    public const string DevelopmentWithoutActiveAttempt =
        "board.development_without_active_attempt";
    public const string RunningWithoutActiveAttempt = "board.running_without_active_attempt";
    public const string TerminalAttemptWithoutTaskUpdate =
        "board.terminal_attempt_without_task_update";

    public static BoardReconciliationDecision? Evaluate(BoardReconciliationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.AttemptStates);
        if (facts.Archived)
        {
            return null;
        }

        var hasRunningAttempt = facts.AttemptStates.Any(
            state => string.Equals(state, "running", StringComparison.Ordinal));
        var hasTerminalAttempt = facts.AttemptStates.Any(
            state => string.Equals(state, "approved", StringComparison.Ordinal) ||
                     string.Equals(state, "rejected", StringComparison.Ordinal));

        if (string.Equals(facts.InternalState, "completed", StringComparison.Ordinal) &&
            !string.Equals(facts.BoardState, "done", StringComparison.Ordinal))
        {
            return new(facts.TaskId, Correction, CompletedProjectionMismatch, "done");
        }

        // Bloqueio é transversal: nunca apagamos o estado de origem por
        // inferência. A única exceção acima é conclusão já persistida.
        if (string.Equals(facts.BoardState, "blocked", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(facts.InternalState, "awaiting_review", StringComparison.Ordinal) &&
            !string.Equals(facts.BoardState, "review", StringComparison.Ordinal))
        {
            return new(facts.TaskId, Correction, ReviewProjectionMismatch, "review");
        }

        if (string.Equals(facts.InternalState, "running", StringComparison.Ordinal))
        {
            if (!hasRunningAttempt)
            {
                return new(facts.TaskId, Attention, RunningWithoutActiveAttempt, null);
            }

            if (!string.Equals(facts.BoardState, "development", StringComparison.Ordinal))
            {
                return new(facts.TaskId, Correction, RunningProjectionMismatch, "development");
            }
        }

        if (string.Equals(facts.BoardState, "development", StringComparison.Ordinal) &&
            !hasRunningAttempt)
        {
            return new(facts.TaskId, Attention, DevelopmentWithoutActiveAttempt, null);
        }

        if (hasTerminalAttempt &&
            !string.Equals(facts.InternalState, "completed", StringComparison.Ordinal) &&
            !string.Equals(facts.InternalState, "ready", StringComparison.Ordinal) &&
            !string.Equals(facts.InternalState, "approved", StringComparison.Ordinal) &&
            !string.Equals(facts.InternalState, "merged", StringComparison.Ordinal))
        {
            return new(facts.TaskId, Attention, TerminalAttemptWithoutTaskUpdate, null);
        }

        return null;
    }
}
