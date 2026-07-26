using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Coordination.Domain.Work;
using Harness.SharedKernel.Time;

namespace Harness.UnitTests.Coordination;

public sealed class WorkChainAggregateTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FD0";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FD1";
    private const string UserId = "01ARZ3NDEKTSV4RRFFQ69G5FD2";

    [Fact]
    public void SolicitationAndInstructionVersionsAreAppendOnly()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var first = chain.AddInstructionVersion(task.Id, "Implement first version.");
        var second = chain.AddInstructionVersion(task.Id, "Corrected instruction.");

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, first.Value.Version);
        Assert.Equal(2, second.Value.Version);
        Assert.Equal(first.Value.Id, second.Value.SupersedesId);
        Assert.Equal(64, second.Value.ContentHash.Length);
        Assert.Equal("Initial immutable request.", chain.Solicitation.Content);
        Assert.Equal(2, chain.Instructions.Count);
    }

    [Fact]
    public void OnlyLatestInstructionCanStartAndOnlyOneAttemptCanRun()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var first = chain.AddInstructionVersion(task.Id, "First.").Value;
        var second = chain.AddInstructionVersion(task.Id, "Second.").Value;

        var stale = chain.StartAttempt(task.Id, first.Id, "engineer");
        var running = chain.StartAttempt(task.Id, second.Id, "engineer");
        var duplicate = chain.StartAttempt(task.Id, second.Id, "other-engineer");

        Assert.Equal(WorkChainErrors.InstructionIsNotLatest, stale.Error);
        Assert.True(running.IsSuccess);
        Assert.Equal(1, running.Value.Number);
        Assert.Equal(WorkTaskState.Running, task.State);
        Assert.Equal(WorkChainErrors.ActiveAttemptExists, duplicate.Error);
    }

    [Fact]
    public void CompletionRequiresObjectiveEvidence()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var attempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;

        Assert.Equal(WorkChainErrors.EvidenceRequired, chain.CompleteAttempt(attempt.Id, []).Error);
        Assert.True(chain.CompleteAttempt(attempt.Id, ["test:unit:green"]).IsSuccess);
        Assert.Equal(WorkAttemptState.AwaitingReview, attempt.State);
        Assert.Equal(WorkTaskState.AwaitingReview, task.State);
    }

    [Fact]
    public void ExpiredLeaseAbandonsAttemptAndRequeuesSameInstruction()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var expiredAttempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;

        var expired = chain.ExpireAttemptLease(expiredAttempt.Id);
        var lateCompletion = chain.CompleteAttempt(expiredAttempt.Id, ["late:evidence"]);
        var retried = chain.StartAttempt(task.Id, instruction.Id, "replacement-engineer");

        Assert.True(expired.IsSuccess);
        Assert.Equal(WorkAttemptState.Abandoned, expiredAttempt.State);
        Assert.NotNull(expiredAttempt.CompletedAt);
        Assert.Equal(WorkTaskState.Running, task.State);
        Assert.Equal(WorkChainErrors.InvalidAttemptState, lateCompletion.Error);
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, retried.Value.Number);
        Assert.Equal(instruction.Id, retried.Value.InstructionVersionId);
    }

    [Fact]
    public void LeaseExpiryRejectsUnknownOrNonRunningAttempt()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var attempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;
        chain.CompleteAttempt(attempt.Id, ["test:green"]);

        var nonRunning = chain.ExpireAttemptLease(attempt.Id);
        var unknown = chain.ExpireAttemptLease(
            Harness.SharedKernel.Identifiers.EntityId<WorkAttemptTag>.Parse(
                "01ARZ3NDEKTSV4RRFFQ69G5FD3"));

        Assert.Equal(WorkChainErrors.InvalidAttemptState, nonRunning.Error);
        Assert.Equal(WorkChainErrors.AttemptNotFound, unknown.Error);
    }

    [Fact]
    public void CancellationTerminatesRunningTaskAndAttempt()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Medium);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var attempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;

        var cancelled = chain.CancelRunningTask(task.Id, attempt.Id);
        var lateCompletion = chain.CompleteAttempt(attempt.Id, ["late:evidence"]);
        var duplicateCancellation = chain.CancelRunningTask(task.Id, attempt.Id);

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(WorkTaskState.Cancelled, task.State);
        Assert.Equal(WorkAttemptState.Cancelled, attempt.State);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal(WorkChainErrors.InvalidAttemptState, lateCompletion.Error);
        Assert.Equal(WorkChainErrors.InvalidAttemptState, duplicateCancellation.Error);
    }

    [Theory]
    [InlineData(WorkRiskTier.Medium)]
    [InlineData(WorkRiskTier.High)]
    [InlineData(WorkRiskTier.Critical)]
    public void MediumOrHigherRiskRequiresIndependentReviewer(WorkRiskTier riskTier)
    {
        var chain = CreateChain();
        var task = CreateTask(chain, riskTier);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var attempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;
        chain.CompleteAttempt(attempt.Id, ["test:green"]);

        var selfReview = chain.ReviewAttempt(
            attempt.Id,
            "engineer",
            ReviewDecision.Approved,
            "Self approved.");
        var criticReview = chain.ReviewAttempt(
            attempt.Id,
            "critic",
            ReviewDecision.Approved,
            "Independent evidence reviewed.");

        Assert.Equal(WorkChainErrors.IndependentReviewerRequired, selfReview.Error);
        Assert.True(criticReview.IsSuccess);
        Assert.Equal(WorkAttemptState.Approved, attempt.State);
        Assert.Equal(WorkTaskState.Approved, task.State);
    }

    [Fact]
    public void RejectionRequiresNewInstructionBeforeAnotherAttempt()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Medium);
        var original = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var firstAttempt = chain.StartAttempt(task.Id, original.Id, "engineer").Value;
        chain.CompleteAttempt(firstAttempt.Id, ["test:failed"]);
        chain.ReviewAttempt(firstAttempt.Id, "critic", ReviewDecision.Rejected, "Gate failed.");

        var withoutCorrection = chain.StartAttempt(task.Id, original.Id, "engineer");
        var corrected = chain.AddInstructionVersion(task.Id, "Fix the failed gate.").Value;
        var secondAttempt = chain.StartAttempt(task.Id, corrected.Id, "engineer");

        Assert.Equal(WorkTaskState.Running, task.State);
        Assert.Equal(WorkChainErrors.CorrectionRequired, withoutCorrection.Error);
        Assert.True(secondAttempt.IsSuccess);
        Assert.Equal(2, secondAttempt.Value.Number);
    }

    [Fact]
    public void RejectionBeyondReviewLimitEscalatesAndRequiresReplanning()
    {
        var chain = CreateChain(maximumReviewCycles: 1);
        var task = CreateTask(chain, WorkRiskTier.Medium);
        var original = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var firstAttempt = chain.StartAttempt(task.Id, original.Id, "engineer").Value;
        chain.CompleteAttempt(firstAttempt.Id, ["test:first"]);
        chain.ReviewAttempt(firstAttempt.Id, "critic", ReviewDecision.Rejected, "First rejection.");
        var corrected = chain.AddInstructionVersion(task.Id, "Correct the first rejection.").Value;
        var secondAttempt = chain.StartAttempt(task.Id, corrected.Id, "engineer").Value;
        chain.CompleteAttempt(secondAttempt.Id, ["test:second"]);

        var review = chain.ReviewAttempt(
            secondAttempt.Id,
            "critic",
            ReviewDecision.Rejected,
            "Review limit exceeded.");
        var bypass = chain.AddInstructionVersion(task.Id, "Try to bypass escalation.");

        Assert.True(review.IsSuccess);
        Assert.Equal(WorkAttemptState.Rejected, secondAttempt.State);
        Assert.Equal(WorkTaskState.Escalated, task.State);
        Assert.Equal(WorkChainErrors.ReplanningRequired, bypass.Error);
        Assert.Equal(2, chain.Reviews.Count);

        var replanned = chain.ReplanEscalatedTask(
            task.Id,
            "Replan after review escalation.");
        var thirdAttempt = chain.StartAttempt(task.Id, replanned.Value.Id, "engineer");

        Assert.True(replanned.IsSuccess);
        Assert.Equal(3, replanned.Value.Version);
        Assert.Equal(corrected.Id, replanned.Value.SupersedesId);
        Assert.True(thirdAttempt.IsSuccess);
        Assert.Equal(3, thirdAttempt.Value.Number);
    }

    [Fact]
    public void ReplanningIsRejectedUnlessTaskIsEscalated()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);

        var result = chain.ReplanEscalatedTask(task.Id, "Invalid premature replan.");

        Assert.Equal(WorkChainErrors.TaskIsNotEscalated, result.Error);
        Assert.Empty(chain.Instructions);
        Assert.Equal(WorkTaskState.Ready, task.State);
    }

    [Fact]
    public void LowRiskMayUseSameProducerAndReviewer()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var attempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;
        chain.CompleteAttempt(attempt.Id, ["test:green"]);

        var review = chain.ReviewAttempt(
            attempt.Id,
            "engineer",
            ReviewDecision.Approved,
            "Low-risk policy permits this review.");

        Assert.True(review.IsSuccess);
        Assert.Equal(WorkTaskState.Approved, task.State);
    }

    [Fact]
    public void ApprovedTaskMustBeMergedBeforeItCanBeCompleted()
    {
        var chain = CreateChain();
        var task = CreateTask(chain, WorkRiskTier.Low);
        var instruction = chain.AddInstructionVersion(task.Id, "Implement.").Value;
        var attempt = chain.StartAttempt(task.Id, instruction.Id, "engineer").Value;
        chain.CompleteAttempt(attempt.Id, ["test:green"]);
        chain.ReviewAttempt(
            attempt.Id,
            "engineer",
            ReviewDecision.Approved,
            "Approved for merge.");

        var prematureCompletion = chain.CompleteMergedTask(task.Id);
        var merged = chain.MergeApprovedTask(task.Id);

        Assert.Equal(WorkChainErrors.TaskIsNotMerged, prematureCompletion.Error);
        Assert.True(merged.IsSuccess);
        Assert.Equal(WorkTaskState.Merged, task.State);

        var duplicateMerge = chain.MergeApprovedTask(task.Id);
        var completed = chain.CompleteMergedTask(task.Id);

        Assert.Equal(WorkChainErrors.TaskIsNotApproved, duplicateMerge.Error);
        Assert.True(completed.IsSuccess);
        Assert.Equal(WorkTaskState.Done, task.State);
        Assert.Equal(WorkChainErrors.TaskIsNotMerged, chain.CompleteMergedTask(task.Id).Error);
    }

    private static WorkChainAggregate CreateChain(int maximumReviewCycles = 3) =>
        WorkChainAggregate.Create(
        TenantId,
        ProjectId,
        UserId,
        "Initial immutable request.",
        new IncrementingClock(new DateTimeOffset(2026, 7, 18, 16, 0, 0, TimeSpan.Zero)),
        maximumReviewCycles);

    private static WorkTask CreateTask(WorkChainAggregate chain, WorkRiskTier riskTier)
    {
        var demand = chain.CreateDemand("Deliver feature", ["All gates are green."]);
        return chain.CreateTask(demand.Id, "Implement feature", riskTier, 3m).Value;
    }

    private sealed class IncrementingClock(DateTimeOffset initial) : IClock
    {
        private DateTimeOffset _now = initial;

        public DateTimeOffset UtcNow
        {
            get
            {
                var current = _now;
                _now = _now.AddMilliseconds(1);
                return current;
            }
        }
    }
}
