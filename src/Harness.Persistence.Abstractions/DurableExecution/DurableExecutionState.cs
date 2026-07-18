namespace Harness.Persistence.Abstractions.DurableExecution;

public enum DurableExecutionState
{
    Ready,
    Running,
    Paused,
    WaitingForRetry,
    WaitingForSignal,
    Completed,
    Cancelled,
    DeadLetter,
}
