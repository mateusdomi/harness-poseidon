using System.Security.Cryptography;
using System.Text.Json;
using Harness.Modules.Projects.Contracts;

namespace Harness.Modules.Projects.Application;

public static class ProjectStatusDigestService
{
    private static readonly string[] UnavailableSignals = ["agents", "budgets"];

    public static ProjectStatusDigestContract Create(ProjectStatusDigestSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var nextAction = source.PendingApprovals > 0
            ? "resolveApprovals"
            : source.TaskCounts.Blocked > 0
                ? "unblockTasks"
                : "reviewPhase";
        var value = new ProjectStatusDigestContract(
            source.ProjectId,
            source.AsOf,
            source.Progress,
            source.TaskCounts,
            new ProjectAttentionContract(
                source.TaskCounts.Blocked,
                source.PendingApprovals,
                ErrorAgents: 0,
                CriticalBudgets: 0),
            source.Workflow is null
                ? null
                : new ProjectWorkflowDigestContract(
                    source.Workflow.RunId,
                    source.Workflow.State,
                    source.Workflow.PhaseName,
                    source.Workflow.PhaseState,
                    source.Workflow.PendingGates),
            nextAction,
            source.RecentActivity,
            UnavailableSignals,
            Fingerprint: string.Empty);
        return value with { Fingerprint = ComputeFingerprint(value) };
    }

    private static string ComputeFingerprint(ProjectStatusDigestContract value)
    {
        var canonical = value with { Fingerprint = string.Empty };
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
}
