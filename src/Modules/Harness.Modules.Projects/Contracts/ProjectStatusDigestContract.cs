namespace Harness.Modules.Projects.Contracts;

public sealed record ProjectProgressContract(decimal Executed, decimal Validated, decimal Approved);

public sealed record ProjectTaskCountsContract(
    int Backlog,
    int Ready,
    int Development,
    int Review,
    int Corrections,
    int TestsGates,
    int Blocked,
    int Done,
    int Total);

public sealed record ProjectAttentionContract(
    int BlockedTasks,
    int PendingApprovals,
    int ErrorAgents,
    int CriticalBudgets);

public sealed record ProjectWorkflowDigestContract(
    string RunId,
    string State,
    string? PhaseName,
    string? PhaseState,
    int PendingGates);

public sealed record ProjectActivityContract(
    string Id,
    string Action,
    string Detail,
    DateTimeOffset OccurredAt);

public sealed record ProjectStatusDigestSource(
    string ProjectId,
    DateTimeOffset AsOf,
    ProjectProgressContract Progress,
    ProjectTaskCountsContract TaskCounts,
    int PendingApprovals,
    ProjectWorkflowDigestContract? Workflow,
    IReadOnlyList<ProjectActivityContract> RecentActivity);

public sealed record ProjectStatusDigestContract(
    string ProjectId,
    DateTimeOffset AsOf,
    ProjectProgressContract Progress,
    ProjectTaskCountsContract TaskCounts,
    ProjectAttentionContract Attention,
    ProjectWorkflowDigestContract? Workflow,
    string NextAction,
    IReadOnlyList<ProjectActivityContract> RecentActivity,
    IReadOnlyList<string> UnavailableSignals,
    string Fingerprint);
