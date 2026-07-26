using System.Diagnostics;
using Harness.Host.Observability;
using Harness.Persistence.Abstractions.Messaging;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workers;

public sealed class OutboxDispatcherBackgroundService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IOutboxMessageSink _sink;
    private readonly IClock _clock;
    private readonly OutboxDispatcherOptions _options;

    public OutboxDispatcherBackgroundService(
        IOutboxStore store,
        IOutboxMessageSink sink,
        IClock clock,
        OutboxDispatcherOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<int> DispatchAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var releasedClaims = await _store.ReleaseExpiredClaimsAsync(
            _clock.UtcNow,
            cancellationToken);
        PoseidonTelemetry.RecordOutboxRecoveredClaims(releasedClaims);
        var handled = 0;
        while (handled < _options.MaximumBatchSize)
        {
            var lease = await _store.TryAcquireNextAsync(
                _options.Owner,
                _options.LeaseDuration,
                _clock.UtcNow,
                cancellationToken);
            if (lease is null)
            {
                break;
            }

            using var activity = PoseidonTelemetry.ActivitySource.StartActivity(
                "poseidon.outbox.dispatch",
                ActivityKind.Producer);
            activity?.SetTag("tenant_id", lease.TenantId);
            activity?.SetTag("message_id", lease.MessageId);
            activity?.SetTag("outbox.attempt", lease.Attempts + 1);
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await _sink.DispatchAsync(lease, cancellationToken);
                var mutation = await _store.MarkDispatchedAsync(
                    new OutboxDispatchCommand(
                        lease.MessageId,
                        lease.Owner,
                        lease.FencingToken,
                        _clock.UtcNow),
                    cancellationToken);
                var result = mutation.Status.ToString().ToLowerInvariant();
                activity?.SetTag("outbox.result", result);
                PoseidonTelemetry.RecordOutboxDispatch(
                    result,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetTag("outbox.result", "cancelled");
                PoseidonTelemetry.RecordOutboxDispatch(
                    "cancelled",
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.SetTag("error.type", exception.GetType().FullName);
                var mutation = await _store.RecordFailureAsync(
                    new OutboxFailureCommand(
                        lease.MessageId,
                        UlidValue.New(_clock.UtcNow).ToString(),
                        lease.Owner,
                        lease.FencingToken,
                        exception.GetType().Name,
                        _options.RetryPolicy,
                        _clock.UtcNow),
                    cancellationToken);
                var result = mutation.Status == OutboxMutationStatus.DeadLettered
                    ? "dead_lettered"
                    : "retry_scheduled";
                activity?.SetTag("outbox.result", result);
                activity?.SetTag(
                    "outbox.mutation_status",
                    mutation.Status.ToString().ToLowerInvariant());
                PoseidonTelemetry.RecordOutboxDispatch(
                    result,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            handled++;
        }

        return handled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await DispatchAvailableAsync(stoppingToken);
                if (handled == 0)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
