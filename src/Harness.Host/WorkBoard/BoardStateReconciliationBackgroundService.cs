using System.Text.Json;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

public sealed record BoardStateReconciliationOptions(
    TimeSpan PollInterval,
    int PageSize)
{
    public void Validate()
    {
        if (PollInterval < TimeSpan.FromSeconds(10) || PollInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (PageSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }
    }
}

/// <summary>
/// Executa no boot e periodicamente. Corrige apenas projeções determinísticas;
/// ambiguidades geram auditoria uma vez por versão observada do card.
/// </summary>
public sealed class BoardStateReconciliationBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> ReconciliationFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2101, nameof(ReconciliationFailure)),
            "Board state reconciliation cycle failed with {ExceptionType}.");

    private readonly ILocalProfileStore _profiles;
    private readonly IWorkBoardStore _board;
    private readonly IAuditEventStore _audit;
    private readonly IClock _clock;
    private readonly BoardStateReconciliationOptions _options;
    private readonly ILogger<BoardStateReconciliationBackgroundService> _logger;
    private readonly HashSet<string> _reportedAttention = new(StringComparer.Ordinal);

    public BoardStateReconciliationBackgroundService(
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        IAuditEventStore audit,
        IClock clock,
        BoardStateReconciliationOptions options,
        ILogger<BoardStateReconciliationBackgroundService> logger)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    public async Task<BoardStateReconciliationResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var tenantIds = (await _profiles.ListAsync(cancellationToken))
            .Select(profile => profile.TenantId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scanned = 0;
        var corrected = 0;
        var attention = 0;

        foreach (var tenantId in tenantIds)
        {
            for (var offset = 0;; offset += _options.PageSize)
            {
                var page = await _board.PageTasksAsync(
                    tenantId,
                    new BoardTaskPageQuery(
                        null, null, null, null, null, null, "active", null,
                        offset, _options.PageSize),
                    cancellationToken);

                foreach (var task in page.Items)
                {
                    scanned++;
                    var attempts = await _board.ListAttemptsAsync(
                        tenantId, task.Id, null, 100, cancellationToken);
                    var decision = BoardStateReconciliationEvaluator.Evaluate(
                        new BoardReconciliationFacts(
                            task.Id,
                            task.State,
                            task.InternalState,
                            task.ArchivedAt is not null,
                            attempts.Select(attempt => attempt.State).ToArray()));
                    if (decision is null)
                    {
                        continue;
                    }

                    if (decision.Kind == BoardStateReconciliationEvaluator.Correction &&
                        decision.TargetState is not null)
                    {
                        await _board.MoveTaskAsync(
                            new BoardTaskMoveCommand(
                                tenantId,
                                task.Id,
                                decision.TargetState,
                                decision.ReasonCode,
                                "system",
                                _clock.UtcNow),
                            cancellationToken);
                        await AppendAuditAsync(
                            tenantId, task, "board.reconciled", decision, cancellationToken);
                        corrected++;
                        continue;
                    }

                    var fingerprint =
                        $"{tenantId}:{task.Id}:{task.Version}:{decision.ReasonCode}";
                    if (_reportedAttention.Add(fingerprint))
                    {
                        await AppendAuditAsync(
                            tenantId,
                            task,
                            "board.reconciliationAttention",
                            decision,
                            cancellationToken);
                    }
                    attention++;
                }

                if (offset + page.Items.Count >= page.Total || page.Items.Count == 0)
                {
                    break;
                }
            }
        }

        return new BoardStateReconciliationResult(
            tenantIds.Length, scanned, corrected, attention);
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
                ReconciliationFailure(_logger, exception.GetType().Name, exception);
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

    private Task<AuditEventRecord> AppendAuditAsync(
        string tenantId,
        BoardTaskRecord task,
        string action,
        BoardReconciliationDecision decision,
        CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditEventAppendCommand(
                tenantId,
                "system",
                null,
                action,
                "task",
                task.Id,
                JsonSerializer.Serialize(new
                {
                    task.ProjectId,
                    taskId = task.Id,
                    from = task.State,
                    to = decision.TargetState,
                    decision.ReasonCode,
                }),
                _clock.UtcNow),
            cancellationToken);
}

public sealed record BoardStateReconciliationResult(
    int TenantsScanned,
    int TasksScanned,
    int TasksCorrected,
    int AttentionItems);
