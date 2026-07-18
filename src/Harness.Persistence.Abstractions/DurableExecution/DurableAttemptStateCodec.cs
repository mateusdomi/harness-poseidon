namespace Harness.Persistence.Abstractions.DurableExecution;

public static class DurableAttemptStateCodec
{
    public static string ToStorage(DurableAttemptState state) => state switch
    {
        DurableAttemptState.Running => "running",
        DurableAttemptState.Completed => "completed",
        DurableAttemptState.Failed => "failed",
        DurableAttemptState.Abandoned => "abandoned",
        DurableAttemptState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown durable attempt state."),
    };

    public static DurableAttemptState Parse(string value) => value switch
    {
        "running" => DurableAttemptState.Running,
        "completed" => DurableAttemptState.Completed,
        "failed" => DurableAttemptState.Failed,
        "abandoned" => DurableAttemptState.Abandoned,
        "cancelled" => DurableAttemptState.Cancelled,
        _ => throw new ArgumentException("Unknown durable attempt state value.", nameof(value)),
    };
}
