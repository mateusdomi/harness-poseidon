using Harness.Persistence.Abstractions.Messaging;

namespace Harness.Host.Workers;

public interface IOutboxMessageSink
{
    Task DispatchAsync(OutboxLease message, CancellationToken cancellationToken = default);
}
