using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

public sealed record PlanMaterializationReconciliationOptions(
    TimeSpan PollInterval,
    TimeSpan RetryAfter,
    int PageSize,
    string Owner)
{
    public void Validate()
    {
        if (PollInterval < TimeSpan.FromSeconds(10) || PollInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (RetryAfter < TimeSpan.FromSeconds(10) || RetryAfter > TimeSpan.FromHours(6))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryAfter));
        }

        if (PageSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Owner);
    }
}

public sealed record PlanMaterializationReconciliationResult(
    int Scanned,
    int Recovered,
    int Repaired,
    int Blocked);

/// <summary>
/// Fase 0A1: a segunda garantia. A outbox entrega o comando pelo menos uma vez, mas um comando pode
/// morrer no dead letter, um consumidor pode cair no meio e o board pode divergir do registro por
/// escrita externa. Este reconciliador varre os compromissos e CONVERGE — ele não se limita a
/// relatar o problema:
///
/// * <c>pending</c> nunca consumido (evento perdido) → executa;
/// * <c>processing</c> abandonado por queda do dono → reexecuta sob novo dono;
/// * <c>failed</c> transitório → tenta de novo até o teto de tentativas;
/// * <c>completed</c> cuja realidade no board divergiu (marker com card faltando, card removido)
///   → reabre e recria apenas o que falta;
/// * falha terminal (dependência que não resolve, fatia duplicada) → NÃO insiste: permanece
///   visível em <c>failed</c> com o código, porque repetir não corrigiria nada.
/// </summary>
public sealed class PlanMaterializationReconciliationBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, string, string, Exception?> RecordFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2114, nameof(RecordFailure)),
            "Plan materialization reconciliation of demand {DemandId} failed with {ExceptionType}.");

    private static readonly Action<ILogger, string, Exception?> CycleFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2113, nameof(CycleFailure)),
            "Plan materialization reconciliation cycle failed with {ExceptionType}.");

    private readonly IPlanMaterializationStore _jobs;
    private readonly PlanMaterializationService _materialization;
    private readonly IClock _clock;
    private readonly PlanMaterializationReconciliationOptions _options;
    private readonly PlanMaterializationOptions _materializationOptions;
    private readonly ILogger<PlanMaterializationReconciliationBackgroundService> _logger;

    public PlanMaterializationReconciliationBackgroundService(
        IPlanMaterializationStore jobs,
        PlanMaterializationService materialization,
        IClock clock,
        PlanMaterializationReconciliationOptions options,
        PlanMaterializationOptions materializationOptions,
        ILogger<PlanMaterializationReconciliationBackgroundService> logger)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _materializationOptions = materializationOptions
            ?? throw new ArgumentNullException(nameof(materializationOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
        _materializationOptions.Validate();
    }

    public async Task<PlanMaterializationReconciliationResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var maximumAttempts = _materializationOptions.MaximumAttempts;
        var scanned = 0;
        var recovered = 0;
        var repaired = 0;
        var blocked = 0;
        PlanMaterializationCursor? cursor = null;
        while (true)
        {
            var page = await _jobs.ListForReconciliationAsync(
                cursor, _options.PageSize, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var record in page)
            {
                scanned++;
                cursor = new PlanMaterializationCursor(record.TenantId, record.DemandId);

                // UM registro problemático não pode parar a varredura. Sem este isolamento, um
                // único plano corrompido derrubaria o ciclo inteiro e TODOS os demais
                // compromissos ficariam sem reconciliação — o oposto do que este serviço existe
                // para garantir. O erro segue visível no compromisso e no log.
                try
                {
                    var action = await ClassifyAsync(record, maximumAttempts, cancellationToken);
                    if (action == ReconciliationAction.Blocked)
                    {
                        blocked++;
                        continue;
                    }

                    if (action == ReconciliationAction.None)
                    {
                        continue;
                    }

                    var outcome = await _materialization.RunAsync(
                        record.TenantId, record.DemandId, _options.Owner, cancellationToken);
                    if (outcome.Result is PlanMaterializationResult.Materialized)
                    {
                        if (action == ReconciliationAction.Repair)
                        {
                            repaired++;
                        }
                        else
                        {
                            recovered++;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    blocked++;
                    RecordFailure(_logger, record.DemandId, exception.GetType().Name, exception);
                }
            }

            if (page.Count < _options.PageSize)
            {
                break;
            }
        }

        return new PlanMaterializationReconciliationResult(scanned, recovered, repaired, blocked);
    }

    private enum ReconciliationAction
    {
        None,
        Recover,
        Repair,
        Blocked,
    }

    private async Task<ReconciliationAction> ClassifyAsync(
        PlanMaterializationRecord record, int maximumAttempts, CancellationToken cancellationToken)
    {
        if (string.Equals(record.Status, PlanMaterializationStatus.Completed, StringComparison.Ordinal))
        {
            // Só reabre um compromisso concluído depois de COMPROVAR a divergência no board.
            return await _materialization.IsSettledAsync(record, cancellationToken)
                ? ReconciliationAction.None
                : ReconciliationAction.Repair;
        }

        if (record.LastError is not null &&
            PlanMaterializationService.TerminalErrorCodes.Contains(record.LastError))
        {
            return ReconciliationAction.Blocked;
        }

        if (record.AttemptCount >= maximumAttempts)
        {
            return ReconciliationAction.Blocked;
        }

        if (string.Equals(record.Status, PlanMaterializationStatus.Processing, StringComparison.Ordinal) &&
            record.UpdatedAt > _clock.UtcNow - _options.RetryAfter)
        {
            // Um dono vivo está trabalhando: interferir só duplicaria esforço.
            return ReconciliationAction.None;
        }

        return ReconciliationAction.Recover;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                CycleFailure(_logger, exception.GetType().Name, exception);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
