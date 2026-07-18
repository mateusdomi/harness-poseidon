namespace Harness.Persistence.Abstractions.DurableExecution;

public static class DurableExecutionStateCodec
{
    public static string ToStorage(DurableExecutionState state) => state switch
    {
        DurableExecutionState.Ready => "ready",
        DurableExecutionState.Running => "running",
        DurableExecutionState.Paused => "paused",
        DurableExecutionState.WaitingForRetry => "waiting_retry",
        DurableExecutionState.WaitingForSignal => "waiting_signal",
        DurableExecutionState.Completed => "completed",
        DurableExecutionState.Cancelled => "cancelled",
        DurableExecutionState.DeadLetter => "dead_letter",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown durable execution state."),
    };

    public static DurableExecutionState Parse(string value) => value switch
    {
        "ready" => DurableExecutionState.Ready,
        "running" => DurableExecutionState.Running,
        "paused" => DurableExecutionState.Paused,
        "waiting_retry" => DurableExecutionState.WaitingForRetry,
        "waiting_signal" => DurableExecutionState.WaitingForSignal,
        "completed" => DurableExecutionState.Completed,
        "cancelled" => DurableExecutionState.Cancelled,
        "dead_letter" => DurableExecutionState.DeadLetter,
        _ => throw new ArgumentException("Unknown durable execution state value.", nameof(value)),
    };
}
