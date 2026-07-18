namespace Harness.Host.Ipc;

public sealed record RunnerAttemptSnapshot(
    string RunnerId,
    string AttemptId,
    long LastSequence,
    int HeartbeatCount,
    IReadOnlyList<string> CheckpointIds,
    bool Completed,
    int InboxCount);
