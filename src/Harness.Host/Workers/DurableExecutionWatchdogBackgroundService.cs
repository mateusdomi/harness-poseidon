using Harness.Persistence.Abstractions.DurableExecution;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workers;

public sealed class DurableExecutionWatchdogBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> MaintenanceFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2001, nameof(MaintenanceFailure)),
            "Durable maintenance cycle failed with {ExceptionType}.");

    private readonly IDurableExecutionEngine _engine;
    private readonly IClock _clock;
    private readonly DurableExecutionWatchdogOptions _options;
    private readonly ILogger<DurableExecutionWatchdogBackgroundService> _logger;

    public DurableExecutionWatchdogBackgroundService(
        IDurableExecutionEngine engine,
        IClock clock,
        DurableExecutionWatchdogOptions options,
        ILogger<DurableExecutionWatchdogBackgroundService> logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    public async Task<DurableMaintenanceResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var tenantIds = await _engine.ListMaintenanceTenantsAsync(cancellationToken);
        var timersFired = 0;
        var requeued = 0;
        var deadLettered = 0;
        var executionIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tenantId in tenantIds)
        {
            timersFired += await _engine.FireDueTimersAsync(
                tenantId,
                now,
                cancellationToken);
            var reconciliation = await _engine.ReconcileAsync(
                tenantId,
                now.Subtract(_options.HeartbeatTimeout),
                now,
                cancellationToken);
            requeued += reconciliation.Requeued;
            deadLettered += reconciliation.DeadLettered;
            executionIds.UnionWith(reconciliation.ExecutionIds);
        }

        return new DurableMaintenanceResult(
            tenantIds.Count,
            timersFired,
            requeued,
            deadLettered,
            executionIds.ToArray());
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
                MaintenanceFailure(_logger, exception.GetType().Name, null);
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

public sealed record DurableMaintenanceResult(
    int TenantsScanned,
    int TimersFired,
    int Requeued,
    int DeadLettered,
    IReadOnlyList<string> ExecutionIds);
