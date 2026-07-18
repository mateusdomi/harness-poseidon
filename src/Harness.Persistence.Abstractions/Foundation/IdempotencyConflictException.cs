namespace Harness.Persistence.Abstractions.Foundation;

public sealed class IdempotencyConflictException(string message) : InvalidOperationException(message);
