using Harness.Persistence.Abstractions.Coordination;

namespace Harness.Host.Agents;

/// <summary>
/// O estado que explica as decisões do laço da chefe — backoff de despacho, backoff de revisão e
/// contagem de não-progresso — com cache em memória e escrita através para a store durável.
///
/// A regra da Onda 0.6: <b>todo estado que explica uma decisão de loop sobrevive ao processo que
/// executa o loop.</b> Antes, um reinício do Host devolvia todos os cards ao despacho imediato e
/// zerava o freio de não-progresso — justamente depois de uma queda, que é quando a fábrica mais
/// tende a repetir trabalho (em 03/08/2026 foram catorze tentativas de zero token em 1h40, e o
/// contador que as teria parado morria a cada restart).
///
/// Falha de persistência NUNCA derruba o laço: o cache continua valendo para o processo atual e a
/// perda fica registrada — durabilidade degradada é melhor que laço parado, e as duas coisas
/// ficam visíveis.
/// </summary>
public sealed class ChiefLoopDurableState(
    IChiefLoopStateStore? store,
    ILogger? logger = null)
{
    public const string DispatchBackoffKind = "dispatch_backoff";
    public const string ReviewBackoffKind = "review_backoff";
    public const string NoProgressKind = "no_progress";

    private readonly Dictionary<string, DateTimeOffset> _dispatchBackoff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _reviewBackoff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _noProgressRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hydratedTenants = new(StringComparer.Ordinal);

    /// <summary>
    /// Hidrata o cache do tenant a partir da store, uma vez por processo. É o RECOVERY: o que o
    /// laço decidiu antes do reinício volta a valer antes da primeira decisão nova.
    /// </summary>
    public async Task HydrateAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (store is null || !_hydratedTenants.Add(tenantId))
        {
            return;
        }

        try
        {
            foreach (var entry in await store.LoadAsync(tenantId, cancellationToken))
            {
                switch (entry.Kind)
                {
                    case DispatchBackoffKind when entry.NotBefore is { } dispatch:
                        _dispatchBackoff[entry.EntryId] = dispatch;
                        break;
                    case ReviewBackoffKind when entry.NotBefore is { } review:
                        _reviewBackoff[entry.EntryId] = review;
                        break;
                    case NoProgressKind:
                        _noProgressRuns[entry.EntryId] = entry.Counter;
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem hidratação o laço opera como antes da Onda 0.6 — pior em durabilidade, nunca
            // parado. A degradação fica dita.
            LogHydrationFailed(logger, tenantId, exception);
        }
    }

    public bool TryGetDispatchBackoff(string taskId, out DateTimeOffset notBefore) =>
        _dispatchBackoff.TryGetValue(taskId, out notBefore);

    public bool TryGetReviewBackoff(string attemptId, out DateTimeOffset notBefore) =>
        _reviewBackoff.TryGetValue(attemptId, out notBefore);

    public int NoProgressRuns(string taskId) => _noProgressRuns.GetValueOrDefault(taskId);

    public async Task SetDispatchBackoffAsync(
        string tenantId, string taskId, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        _dispatchBackoff[taskId] = notBefore;
        await PersistAsync(
            tenantId, new ChiefLoopStateEntry(DispatchBackoffKind, taskId, notBefore, 0),
            cancellationToken);
    }

    public async Task SetReviewBackoffAsync(
        string tenantId, string attemptId, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        _reviewBackoff[attemptId] = notBefore;
        await PersistAsync(
            tenantId, new ChiefLoopStateEntry(ReviewBackoffKind, attemptId, notBefore, 0),
            cancellationToken);
    }

    public async Task ClearReviewBackoffAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken)
    {
        _reviewBackoff.Remove(attemptId);
        await RemoveAsync(tenantId, ReviewBackoffKind, attemptId, cancellationToken);
    }

    public async Task<int> IncrementNoProgressAsync(
        string tenantId, string taskId, CancellationToken cancellationToken)
    {
        var next = _noProgressRuns.GetValueOrDefault(taskId) + 1;
        _noProgressRuns[taskId] = next;
        await PersistAsync(
            tenantId, new ChiefLoopStateEntry(NoProgressKind, taskId, null, next), cancellationToken);
        return next;
    }

    public async Task ClearNoProgressAsync(
        string tenantId, string taskId, CancellationToken cancellationToken)
    {
        _noProgressRuns.Remove(taskId);
        await RemoveAsync(tenantId, NoProgressKind, taskId, cancellationToken);
    }

    private async Task PersistAsync(
        string tenantId, ChiefLoopStateEntry entry, CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            await store.UpsertAsync(tenantId, entry, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPersistFailed(logger, entry.Kind, entry.EntryId, exception);
        }
    }

    private async Task RemoveAsync(
        string tenantId, string kind, string entryId, CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            await store.RemoveAsync(tenantId, kind, entryId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPersistFailed(logger, kind, entryId, exception);
        }
    }

    private static void LogHydrationFailed(ILogger? logger, string tenantId, Exception exception)
    {
        if (logger is not null)
        {
            HydrationFailed(logger, tenantId, exception);
        }
    }

    private static void LogPersistFailed(
        ILogger? logger, string kind, string entryId, Exception exception)
    {
        if (logger is not null)
        {
            PersistFailed(logger, kind, entryId, exception);
        }
    }

    private static readonly Action<ILogger, string, Exception?> HydrationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(HydrationFailed)),
            "O estado durável do laço não pôde ser hidratado para o tenant {TenantId}; o laço " +
            "segue com estado vazio, como antes da persistência — durabilidade degradada, dita.");

    private static readonly Action<ILogger, string, string, Exception?> PersistFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2, nameof(PersistFailed)),
            "Falha ao persistir estado do laço ({Kind}:{EntryId}); o cache do processo continua " +
            "valendo e a durabilidade desta entrada foi perdida.");
}
