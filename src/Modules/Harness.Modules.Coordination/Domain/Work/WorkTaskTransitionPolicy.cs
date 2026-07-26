using Harness.Modules.Coordination.Contracts;

namespace Harness.Modules.Coordination.Domain.Work;

public static class WorkTaskTransitionPolicy
{
    private static readonly HashSet<Transition> AllowedTransitions =
    [
        new(WorkTaskState.Draft, WorkTaskState.Triaged, WorkTaskTransitionEvent.Triaged),
        new(
            WorkTaskState.Triaged,
            WorkTaskState.Ready,
            WorkTaskTransitionEvent.RequirementsCompleted),
        new(WorkTaskState.Ready, WorkTaskState.Assigned, WorkTaskTransitionEvent.LeaseAcquired),
        new(
            WorkTaskState.Assigned,
            WorkTaskState.Running,
            WorkTaskTransitionEvent.HeartbeatConfirmed),
        new(
            WorkTaskState.Running,
            WorkTaskState.Review,
            WorkTaskTransitionEvent.AttemptSubmitted),
        new(WorkTaskState.Running, WorkTaskState.Blocked, WorkTaskTransitionEvent.Blocked),
        new(WorkTaskState.Running, WorkTaskState.Ready, WorkTaskTransitionEvent.LeaseExpired),
        new(WorkTaskState.Blocked, WorkTaskState.Ready, WorkTaskTransitionEvent.Unblocked),
        new(
            WorkTaskState.Review,
            WorkTaskState.Approved,
            WorkTaskTransitionEvent.ReviewApproved),
        new(
            WorkTaskState.Review,
            WorkTaskState.Running,
            WorkTaskTransitionEvent.ReviewRejected),
        new(
            WorkTaskState.Review,
            WorkTaskState.Escalated,
            WorkTaskTransitionEvent.ReviewLimitExceeded),
        new(WorkTaskState.Escalated, WorkTaskState.Ready, WorkTaskTransitionEvent.Replanned),
        new(
            WorkTaskState.Approved,
            WorkTaskState.Merged,
            WorkTaskTransitionEvent.MergeCompleted),
        new(WorkTaskState.Merged, WorkTaskState.Done, WorkTaskTransitionEvent.DeliveryCompleted),
        new(WorkTaskState.Running, WorkTaskState.Cancelled, WorkTaskTransitionEvent.Cancelled),
    ];

    public static bool IsAllowed(
        WorkTaskState from,
        WorkTaskState to,
        WorkTaskTransitionEvent @event) =>
        AllowedTransitions.Contains(new Transition(from, to, @event));

    private readonly record struct Transition(
        WorkTaskState From,
        WorkTaskState To,
        WorkTaskTransitionEvent Event);
}
