using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Coordination.Domain.Work;

namespace Harness.UnitTests.Coordination;

public sealed class WorkTaskTransitionPolicyTests
{
    public static TheoryData<WorkTaskState, WorkTaskState, WorkTaskTransitionEvent>
        AllowedTransitions =>
        new()
        {
            { WorkTaskState.Draft, WorkTaskState.Triaged, WorkTaskTransitionEvent.Triaged },
            {
                WorkTaskState.Triaged,
                WorkTaskState.Ready,
                WorkTaskTransitionEvent.RequirementsCompleted
            },
            { WorkTaskState.Ready, WorkTaskState.Assigned, WorkTaskTransitionEvent.LeaseAcquired },
            {
                WorkTaskState.Assigned,
                WorkTaskState.Running,
                WorkTaskTransitionEvent.HeartbeatConfirmed
            },
            {
                WorkTaskState.Running,
                WorkTaskState.Review,
                WorkTaskTransitionEvent.AttemptSubmitted
            },
            { WorkTaskState.Running, WorkTaskState.Blocked, WorkTaskTransitionEvent.Blocked },
            { WorkTaskState.Running, WorkTaskState.Ready, WorkTaskTransitionEvent.LeaseExpired },
            { WorkTaskState.Blocked, WorkTaskState.Ready, WorkTaskTransitionEvent.Unblocked },
            {
                WorkTaskState.Review,
                WorkTaskState.Approved,
                WorkTaskTransitionEvent.ReviewApproved
            },
            {
                WorkTaskState.Review,
                WorkTaskState.Running,
                WorkTaskTransitionEvent.ReviewRejected
            },
            {
                WorkTaskState.Review,
                WorkTaskState.Escalated,
                WorkTaskTransitionEvent.ReviewLimitExceeded
            },
            { WorkTaskState.Escalated, WorkTaskState.Ready, WorkTaskTransitionEvent.Replanned },
            {
                WorkTaskState.Approved,
                WorkTaskState.Merged,
                WorkTaskTransitionEvent.MergeCompleted
            },
            {
                WorkTaskState.Merged,
                WorkTaskState.Done,
                WorkTaskTransitionEvent.DeliveryCompleted
            },
            {
                WorkTaskState.Running,
                WorkTaskState.Cancelled,
                WorkTaskTransitionEvent.Cancelled
            },
        };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void CanonicalTransitionIsAllowed(
        WorkTaskState from,
        WorkTaskState to,
        WorkTaskTransitionEvent @event)
    {
        Assert.True(WorkTaskTransitionPolicy.IsAllowed(from, to, @event));
    }

    [Theory]
    [InlineData(
        WorkTaskState.Draft,
        WorkTaskState.Running,
        WorkTaskTransitionEvent.HeartbeatConfirmed)]
    [InlineData(
        WorkTaskState.Review,
        WorkTaskState.Done,
        WorkTaskTransitionEvent.ReviewApproved)]
    [InlineData(
        WorkTaskState.Done,
        WorkTaskState.Ready,
        WorkTaskTransitionEvent.Replanned)]
    [InlineData(
        WorkTaskState.Cancelled,
        WorkTaskState.Running,
        WorkTaskTransitionEvent.HeartbeatConfirmed)]
    // Dar por entregue um card que nunca passou por revisão pularia justamente o ponto onde o
    // sistema verifica alguma coisa. (Draft → Running já é coberto acima.)
    [InlineData(
        WorkTaskState.Running,
        WorkTaskState.Done,
        WorkTaskTransitionEvent.ReviewApproved)]
    public void ShortcutOrTerminalTransitionIsRejected(
        WorkTaskState from,
        WorkTaskState to,
        WorkTaskTransitionEvent @event)
    {
        Assert.False(WorkTaskTransitionPolicy.IsAllowed(from, to, @event));
    }

    [Fact]
    public void LegacyNamesRemainAliasesDuringPersistenceMigration()
    {
        Assert.Equal(WorkTaskState.Review, WorkTaskState.AwaitingReview);
        Assert.Equal(WorkTaskState.Done, WorkTaskState.Completed);
    }
}
