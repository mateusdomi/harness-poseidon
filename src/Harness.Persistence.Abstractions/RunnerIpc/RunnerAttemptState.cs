namespace Harness.Persistence.Abstractions.RunnerIpc;

public sealed record RunnerAttemptState(
    string RunnerId,
    string AttemptId,
    long LastSequence,
    int HeartbeatCount,
    IReadOnlyList<string> CheckpointIds,
    bool Completed,
    int InboxCount,
    int OutboxCount,
    long Version);
