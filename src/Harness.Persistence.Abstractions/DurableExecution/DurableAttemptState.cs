namespace Harness.Persistence.Abstractions.DurableExecution;

public enum DurableAttemptState
{
    Running,
    Completed,
    Failed,
    Abandoned,
    Cancelled,
}
