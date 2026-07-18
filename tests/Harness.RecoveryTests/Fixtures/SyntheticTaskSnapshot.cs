namespace Harness.RecoveryTests.Fixtures;

public enum SyntheticTaskState
{
    Pending,
    Running,
    Completed,
}

public sealed record SyntheticTaskSnapshot(
    string Id,
    SyntheticTaskState State,
    int TotalSteps,
    int CompletedSteps,
    int CheckpointCount,
    int AttemptCount,
    int ReconciliationCount,
    string? Owner,
    DateTimeOffset LastHeartbeatAt);
