namespace Harness.Persistence.Abstractions.Cockpit;

public interface ICockpitDigestStore
{
    Task<CockpitDigestRecord?> ReadAsync(
        string tenantId,
        string projectId,
        int activityLimit,
        CancellationToken cancellationToken = default);
}

public sealed record CockpitProgressRecord(decimal Executed, decimal Validated, decimal Approved);

public sealed record CockpitTaskCountsRecord(
    int Backlog,
    int Ready,
    int Development,
    int Review,
    int Corrections,
    int TestsGates,
    int Blocked,
    int Done)
{
    public int Total =>
        Backlog + Ready + Development + Review + Corrections + TestsGates + Blocked + Done;
}

public sealed record CockpitWorkflowRecord(
    string RunId,
    string State,
    string? PhaseName,
    string? PhaseState,
    int PendingGates);

public sealed record CockpitActivityRecord(
    string Id,
    string Action,
    string Detail,
    DateTimeOffset OccurredAt);

public sealed record CockpitDigestRecord(
    string ProjectId,
    DateTimeOffset AsOf,
    CockpitProgressRecord Progress,
    CockpitTaskCountsRecord TaskCounts,
    int PendingApprovals,
    CockpitWorkflowRecord? Workflow,
    IReadOnlyList<CockpitActivityRecord> RecentActivity);
