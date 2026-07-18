namespace Harness.SharedKernel.RunnerIpc;

public static class RunnerMessageTypes
{
    public const string Heartbeat = "heartbeat";
    public const string Checkpoint = "checkpoint";
    public const string Completion = "completion";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Heartbeat,
        Checkpoint,
        Completion,
    };
}
