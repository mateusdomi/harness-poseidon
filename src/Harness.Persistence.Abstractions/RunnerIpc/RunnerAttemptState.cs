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
    long Version,
    /// <summary>Fencing do despacho que autoriza esta tentativa. 0 = tentativa anterior ao B5.</summary>
    long FencingToken = 0);
