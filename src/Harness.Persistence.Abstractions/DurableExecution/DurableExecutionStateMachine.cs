namespace Harness.Persistence.Abstractions.DurableExecution;

public static class DurableExecutionStateMachine
{
    public static bool CanTransition(DurableExecutionState from, DurableExecutionState to) => from switch
    {
        DurableExecutionState.Ready => to is
            DurableExecutionState.Running or
            DurableExecutionState.Paused or
            DurableExecutionState.Cancelled,
        DurableExecutionState.Running => to is
            DurableExecutionState.Paused or
            DurableExecutionState.WaitingForRetry or
            DurableExecutionState.WaitingForSignal or
            DurableExecutionState.Completed or
            DurableExecutionState.Cancelled or
            DurableExecutionState.DeadLetter,
        DurableExecutionState.Paused => to is
            DurableExecutionState.Ready or
            DurableExecutionState.Cancelled,
        DurableExecutionState.WaitingForRetry => to is
            DurableExecutionState.Ready or
            DurableExecutionState.Paused or
            DurableExecutionState.Cancelled or
            DurableExecutionState.DeadLetter,
        DurableExecutionState.WaitingForSignal => to is
            DurableExecutionState.Ready or
            DurableExecutionState.Paused or
            DurableExecutionState.Cancelled or
            DurableExecutionState.DeadLetter,
        DurableExecutionState.Completed or
        DurableExecutionState.Cancelled or
        DurableExecutionState.DeadLetter => false,
        _ => throw new ArgumentOutOfRangeException(nameof(from), from, "Unknown durable execution state."),
    };

    public static bool IsTerminal(DurableExecutionState state) => state is
        DurableExecutionState.Completed or
        DurableExecutionState.Cancelled or
        DurableExecutionState.DeadLetter;
}
