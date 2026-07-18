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
        await _store.ReleaseExpiredClaimsAsync(_clock.UtcNow, cancellationToken);
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

            try
            {
                await _sink.DispatchAsync(lease, cancellationToken);
                await _store.MarkDispatchedAsync(
                    new OutboxDispatchCommand(
                        lease.MessageId,
                        lease.Owner,
                        lease.FencingToken,
                        _clock.UtcNow),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await _store.RecordFailureAsync(
                    new OutboxFailureCommand(
                        lease.MessageId,
                        UlidValue.New(_clock.UtcNow).ToString(),
                        lease.Owner,
                        lease.FencingToken,
                        exception.GetType().Name,
                        _options.RetryPolicy,
                        _clock.UtcNow),
                    cancellationToken);
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
