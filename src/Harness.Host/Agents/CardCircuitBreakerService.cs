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
internal sealed class CardCircuitBreakerService(
    ICardCircuitBreakerStore store,
    int failureThreshold = CardCircuitBreakerPolicy.ConsecutiveFailureThreshold)
{
    private readonly ICardCircuitBreakerStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Limiar em vigor. Vem da configuração do operador; o default é o histórico.</summary>
    private readonly int _failureThreshold = failureThreshold;

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
        // Contagem de NÃO-PROGRESSO, derivada junto e independente da atribuição de culpa:
        // qualquer tentativa que terminou sem produzir token soma; qualquer produção zera.
        var noProgress = 0;
        DateTimeOffset? lastAttemptAt = null;
        foreach (var attempt in attempts.Where(item => horizon is null || item.OccurredAt > horizon))
        {
            noProgress = attempt.OutputTokens > 0 ? 0 : noProgress + 1;
            lastAttemptAt = attempt.OccurredAt;
            if (IsFailure(attempt))
            {
                snapshot = CardCircuitBreakerPolicy.RecordFailure(
                    snapshot, attempt.OccurredAt, attempt.FailureReason ?? attempt.State,
                    _failureThreshold);
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

        snapshot = snapshot with
        {
            ConsecutiveNoProgress = noProgress,
            LastAttemptAt = lastAttemptAt,
        };

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
                _failureThreshold,
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
    /// A pergunta que importa: <b>esta tentativa chegou a JULGAR o enunciado do card?</b>
    /// Quatro rodadas de whack-a-mole com substrings de infraestrutura (host_shutdown,
    /// cancelled, quota, e então account_model_unsupported e turn_failed) ensinaram que listar
    /// motivos um a um não fecha a classe — sempre aparece um quinto. A regra é invertida:
    ///
    /// - reprovação de REVIEW (<c>rejected</c> sem motivo de run): a revisão leu o trabalho e o
    ///   reprovou — julgamento aconteceu, conta sempre;
    /// - demais falhas (<c>failed</c>, <c>rejected</c>/<c>cancelled</c> com motivo de run): só
    ///   contam quando o modelo PRODUZIU saída (<c>OutputTokens &gt; 0</c>). Uma tentativa com
    ///   zero token morreu antes de julgar coisa alguma — conta sem cota, credencial
    ///   inalcançável, plano sem o modelo, contêiner que não abriu. Nada disso diz respeito ao
    ///   enunciado, e punir o card por culpa alheia mata trabalho saudável: só o
    ///   replanejamento reabre um circuito aberto por engano.
    ///
    /// Compensação conhecida: um executor que não expõe uso (o codex hoje) grava zero tokens
    /// mesmo quando trabalhou — suas falhas de RUN não abrem circuito, mas suas reprovações de
    /// review continuam contando pelo ramo acima, e o orçamento de rodadas segue de pé.
    /// </summary>
    private static bool IsFailure(CardAttemptOutcome attempt)
    {
        // PRIMEIRO: a tentativa chegou a existir de fato?
        //
        // Sem duração medida e sem token, ela não executou. É o cancelamento de DESPACHO — a
        // tentativa que perde a corrida pelo slot da conta e morre em zero milissegundo, sem
        // motivo, sem token, sem ter lido o enunciado. Observado em 03/08/2026: cards da prova
        // limpa acumulando `rejected` de duração vazia, cada um contando para abrir o circuito
        // de um trabalho que ninguém julgou — e o circuito só reabre por replanejamento da
        // Bruna, replanejamento que não conserta nada porque o enunciado nunca esteve errado.
        //
        // A duração é o que separa esse caso da reprovação de review, que também chega como
        // `rejected` sem motivo e também pode ter zero token do executor: a reprovação veio
        // DEPOIS de trabalho real, e trabalho real deixa duração.
        if (attempt.DurationMs is null && attempt.OutputTokens == 0)
        {
            return false;
        }

        // Reprovação de REVIEW chega como `rejected` sem motivo de run. Ali o julgamento
        // aconteceu — quem avaliou foi o crítico — e conta mesmo sem token do executor.
        if (string.Equals(attempt.State, "rejected", StringComparison.Ordinal) &&
            attempt.FailureReason is null)
        {
            return true;
        }

        var isRunFailure =
            string.Equals(attempt.State, "failed", StringComparison.Ordinal) ||
            string.Equals(attempt.State, "rejected", StringComparison.Ordinal) ||
            (string.Equals(attempt.State, "cancelled", StringComparison.Ordinal) &&
             !string.IsNullOrWhiteSpace(attempt.FailureReason));
        if (!isRunFailure || IsInfrastructureReason(attempt.FailureReason ?? string.Empty))
        {
            return false;
        }

        return attempt.OutputTokens > 0;
    }

    /// <summary>
    /// Motivos que descrevem a INFRAESTRUTURA, não o card.
    ///
    /// Desde que a expiração de lease passou a gravar o motivo, um reinício do Host deixou de ser
    /// um cancelamento anônimo — o que é bom para a auditoria e péssimo para o circuito, porque
    /// derrubar o Host durante uma tentativa passaria a contar como falha do card. Três reinícios
    /// abririam o circuito de um card perfeitamente saudável, e como só o replanejamento da Bruna
    /// reabre, o falso positivo PARA trabalho de verdade.
    ///
    /// A falha do executor (`run.failed`, `executor.exit_code_*`) continua contando: ali quem não
    /// entregou foi a tentativa, e repeti-la é repetir o fracasso.
    /// </summary>
    /// <summary>
    /// A falha descreve a INFRAESTRUTURA (host, conta, provedor), não o trabalho do card.
    ///
    /// Público porque é a mesma pergunta em três lugares — circuito do card, orçamento de rodadas
    /// e teto de replanejamento. Uma pergunta, uma resposta: uma tentativa morta por reinício do
    /// Host ou por conta sem cota nunca chegou a julgar o enunciado do card.
    ///
    /// F-06: a classificação é feita por igualdade com reason codes canônicos, não por
    /// <c>Contains</c> sobre texto livre. Substring casa com mensagem de erro legítima e
    /// transforma falha do trabalho em infraestrutura.
    /// </summary>
    public static bool IsInfrastructureFailure(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && IsInfrastructureReason(reason);

    private static bool IsInfrastructureReason(string reason)
    {
        // Reinício / parada do Host. Os prefixos `attempt.` são os reason codes canônicos
        // (F-06); os prefixos `run.` e `executor.` são aliases legados que ainda aparecem em
        // código e em testes — classificá-los por igualdade preserva a proteção sem reabrir
        // whack-a-mole de substring.
        if (IsAny(reason,
                "attempt.interrupted_by_host_shutdown",
                "attempt.orphaned_by_host_restart",
                "run.host_shutdown",
                "run.host_restart"))
        {
            return true;
        }

        // Falha da CONTA, não do card: cota esgotada, login exigido ou plano que não serve o
        // modelo dizem que o provedor não atendeu — o enunciado do card nunca chegou a ser
        // julgado. Observado no E2E de empréstimos: a mesma conta com cota estourada foi
        // reeleita três vezes, cada run morreu sem produzir um token, e o card saudável
        // escalou por culpa alheia.
        if (IsAny(reason,
                "run.quota_exhausted",
                "executor.quota_exhausted",
                "run.authentication_required",
                "executor.authentication_required",
                "run.account_model_unsupported",
                "executor.account_model_unsupported"))
        {
            return true;
        }

        // CANCELAMENTO é "nós paramos", não "o card é ruim". Uma parada do Host no meio de um
        // run chega aqui como `TaskCanceledException`/`OperationCanceledException` — o nome do
        // tipo sanitizado, sem nenhuma pista de que a causa foi infraestrutura. Observado na
        // prova limpa: um card de Arquitetura abriu o circuito com três falhas, DUAS delas
        // cancelamentos provocados por reinícios da própria sessão de auditoria. O trabalho
        // nunca chegou a ser julgado.
        if (IsAny(reason,
                "run.cancelled",
                "executor.cancelled",
                "TaskCanceledException",
                "OperationCanceledException"))
        {
            return true;
        }

        // RECUSA DE DESPACHO: o orquestrador não aceitou o run (perfil ocupado, claim em
        // conflito, adaptador ausente). A tentativa morre em milissegundos sem ler o enunciado.
        // A guarda de duração acima já a descartaria; a classificação entra aqui porque agora o
        // motivo é GRAVADO — e o handoff avisa que tornar a causa visível já introduziu, uma vez,
        // exatamente esta regressão: reinícios do Host passaram a abrir circuito de card
        // saudável. Escrever o motivo sem classificá-lo seria repetir o mesmo erro.
        if (reason.StartsWith("chief.dispatch_rejected", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Contenção de perfil local: outro processo segura o slot.
        return IsAny(reason, "profile.locked", "profile.concurrency_exhausted");
    }

    private static bool IsAny(string reason, params ReadOnlySpan<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(reason, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSuccess(string state) =>
        string.Equals(state, "completed", StringComparison.Ordinal) ||
        string.Equals(state, "merged", StringComparison.Ordinal) ||
        string.Equals(state, "approved", StringComparison.Ordinal);
}

/// <summary>Desfecho de uma tentativa, como o circuito do card precisa vê-lo.</summary>
public readonly record struct CardAttemptOutcome(
    string State,
    string? FailureReason,
    DateTimeOffset OccurredAt,
    /// <summary>
    /// Tokens de saída medidos da tentativa. Zero significa que o modelo nunca respondeu —
    /// o enunciado não chegou a ser julgado — e é o pivô da regra invertida do circuito.
    /// </summary>
    long OutputTokens = 0,
    /// <summary>
    /// Duração medida da tentativa. <see langword="null"/> significa que ela NÃO CHEGOU A
    /// EXECUTAR — é o caso da tentativa que perde a corrida pelo slot da conta e morre no
    /// despacho, sem motivo, sem token e sem ter lido o enunciado. Distingue-a da reprovação
    /// de review, que também chega como `rejected` sem motivo mas veio depois de trabalho
    /// real, e portanto tem duração.
    /// </summary>
    long? DurationMs = null);
