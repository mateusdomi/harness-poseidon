namespace Harness.Host.Realtime;

public static class EventTypeCatalog
{
    public static IReadOnlySet<string> All { get; } = new SortedSet<string>(StringComparer.Ordinal)
    {
        "agent.statusChanged",
        "approval.requested",
        "approval.resolved",
        "attempt.completed",
        "attempt.failed",
        "attempt.heartbeat",
        "attempt.started",
        "audit.eventAppended",
        "chat.turnChunk",
        "chat.turnCompleted",
        "chat.turnStarted",
        "chief.turnStateChanged",
        "decision.requested",
        "decision.resolved",
        "demand.created",
        "document.stateChanged",
        "gate.changed",
        "message.appended",
        "notification.created",
        "progress.updated",
        "project.created",
        "prototype.created",
        "prototype.stateChanged",
        "quota.updated",
        "run.logAppended",
        "task.created",
        "task.stateChanged",
        "tool.catalogChanged",
        "workflow.versionPublished",
    };
}
