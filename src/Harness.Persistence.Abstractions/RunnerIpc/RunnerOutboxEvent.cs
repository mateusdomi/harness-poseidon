using Harness.SharedKernel.RunnerIpc;

namespace Harness.Persistence.Abstractions.RunnerIpc;

public static class RunnerOutboxEvent
{
    public static string TypeFor(string runnerMessageType) => runnerMessageType switch
    {
        RunnerMessageTypes.Heartbeat => "attempt.heartbeat",
        RunnerMessageTypes.Checkpoint => "attempt.checkpointed",
        RunnerMessageTypes.Completion => "attempt.completed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(runnerMessageType),
            runnerMessageType,
            "Unsupported Runner IPC message type."),
    };
}
