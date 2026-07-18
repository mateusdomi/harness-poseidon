using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.IntegrationTests.Persistence;

[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible",
    Justification = "The same behavior must execute unchanged against both persistence providers.")]
internal static class WorkChainStoreBehavior
{
    public static async Task AssertAsync(
        IWorkChainStore store,
        CancellationToken cancellationToken)
    {
        const string instruction = "Implement the immutable work-chain transaction.";
        var command = new WorkChainCreateCommand(
            FoundationTransactionBehavior.TenantId,
            FoundationTransactionBehavior.ProjectId,
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            "01ARZ3NDEKTSV4RRFFQ69G5FF0",
            "Build the first persisted work chain.",
            "01ARZ3NDEKTSV4RRFFQ69G5FF1",
            "Persist the demand",
            "[\"State is atomic\",\"Audit is complete\"]",
            "01ARZ3NDEKTSV4RRFFQ69G5FF2",
            "Create work-chain transaction",
            "medium",
            5m,
            "01ARZ3NDEKTSV4RRFFQ69G5FF3",
            instruction,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instruction))),
            "work-chain:create:first",
            new DateTimeOffset(2026, 7, 18, 16, 10, 0, TimeSpan.Zero));

        var receipts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.CreateAsync(command, cancellationToken)));
        Assert.Single(receipts, receipt => !receipt.Replay);
        Assert.Equal(9, receipts.Count(receipt => receipt.Replay));
        Assert.Single(receipts.Select(receipt => receipt.LedgerHash).Distinct(StringComparer.Ordinal));
        Assert.Single(receipts.Select(receipt => receipt.OutboxMessageId).Distinct(StringComparer.Ordinal));

        var snapshot = await store.ReadAsync(
            command.TenantId,
            command.SolicitationId,
            cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(command.ProjectId, snapshot.ProjectId);
        Assert.Equal(command.SolicitationContent, snapshot.SolicitationContent);
        Assert.Equal(command.DemandId, snapshot.DemandId);
        Assert.Equal(command.TaskId, snapshot.TaskId);
        Assert.Equal("ready", snapshot.TaskState);
        Assert.Equal(1, snapshot.TaskVersion);
        Assert.Equal("medium", snapshot.RiskTier);
        Assert.Equal(5m, snapshot.Weight);
        Assert.Equal(command.InstructionVersionId, snapshot.InstructionVersionId);
        Assert.Equal(1, snapshot.InstructionVersion);
        Assert.Equal(command.InstructionContentHash, snapshot.InstructionContentHash);
        Assert.Equal(0, snapshot.AttemptCount);
        Assert.Equal(0, snapshot.EvidenceCount);
        Assert.Equal(0, snapshot.ReviewCount);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.CreateAsync(
                command with { TaskTitle = "Different command with reused key" },
                cancellationToken));
        var afterConflict = await store.ReadAsync(
            command.TenantId,
            command.SolicitationId,
            cancellationToken);
        Assert.Equal(snapshot, afterConflict);

        await AssertMutationsAsync(store, command, cancellationToken);
        Assert.Null(await store.ReadAsync(
            command.TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FF4",
            cancellationToken));
    }

    private static async Task AssertMutationsAsync(
        IWorkChainStore store,
        WorkChainCreateCommand chain,
        CancellationToken cancellationToken)
    {
        const string attemptId = "01ARZ3NDEKTSV4RRFFQ69G5FF5";
        var start = new WorkAttemptStartCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            chain.InstructionVersionId,
            attemptId,
            "software-engineer",
            1,
            "work-chain:attempt:start:first",
            chain.OccurredAt.AddMinutes(1));
        var started = await Task.WhenAll(
            store.StartAttemptAsync(start, cancellationToken),
            store.StartAttemptAsync(start, cancellationToken));
        Assert.Single(started, item => item.Status == WorkChainMutationStatus.Applied);
        Assert.Single(started, item => item.Status == WorkChainMutationStatus.IdempotentReplay);
        Assert.All(started, item => Assert.Equal(2, item.TaskVersion));
        Assert.Single(started.Select(item => item.LedgerHash).Distinct(StringComparer.Ordinal));

        var staleStart = start with
        {
            AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FF6",
            IdempotencyKey = "work-chain:attempt:start:stale",
        };
        var stale = await store.StartAttemptAsync(staleStart, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.VersionConflict, stale.Status);
        Assert.Equal(2, stale.TaskVersion);
        Assert.Null(stale.LedgerSequence);
        var staleReplay = await store.StartAttemptAsync(staleStart, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, staleReplay.Status);
        Assert.Equal(2, staleReplay.TaskVersion);

        var running = await store.ReadAsync(chain.TenantId, chain.SolicitationId, cancellationToken);
        Assert.NotNull(running);
        Assert.Equal("running", running.TaskState);
        Assert.Equal(2, running.TaskVersion);
        Assert.Equal(1, running.AttemptCount);

        var complete = new WorkAttemptCompleteCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            attemptId,
            2,
            [new WorkEvidenceInput("01ARZ3NDEKTSV4RRFFQ69G5FF7", "tests:green")],
            "work-chain:attempt:complete:first",
            chain.OccurredAt.AddMinutes(2));
        var completed = await store.CompleteAttemptAsync(complete, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);
        Assert.Equal(3, completed.TaskVersion);
        Assert.Equal("awaiting_review", completed.TaskState);
        Assert.NotNull(completed.LedgerSequence);
        var completedReplay = await store.CompleteAttemptAsync(complete, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, completedReplay.Status);
        Assert.Equal(completed.LedgerHash, completedReplay.LedgerHash);

        var selfReview = new WorkAttemptReviewCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            attemptId,
            "01ARZ3NDEKTSV4RRFFQ69G5FF8",
            "software-engineer",
            "approved",
            "Self review must be rejected for medium risk.",
            3,
            "work-chain:attempt:review:self",
            chain.OccurredAt.AddMinutes(3));
        var selfReviewReceipt = await store.ReviewAttemptAsync(selfReview, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IndependentReviewerRequired, selfReviewReceipt.Status);
        Assert.Null(selfReviewReceipt.LedgerSequence);

        var review = selfReview with
        {
            ReviewId = "01ARZ3NDEKTSV4RRFFQ69G5FF9",
            ReviewerAgentId = "critic-qa",
            Decision = "rejected",
            Rationale = "The first evidence exposes a failed gate.",
            IdempotencyKey = "work-chain:attempt:review:critic",
            OccurredAt = chain.OccurredAt.AddMinutes(4),
        };
        var reviewed = await store.ReviewAttemptAsync(review, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, reviewed.Status);
        Assert.Equal(4, reviewed.TaskVersion);
        Assert.Equal("ready", reviewed.TaskState);
        Assert.Equal("rejected", reviewed.AttemptState);
        Assert.NotNull(reviewed.OutboxMessageId);
        var reviewedReplay = await store.ReviewAttemptAsync(review, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, reviewedReplay.Status);
        Assert.Equal(reviewed.LedgerHash, reviewedReplay.LedgerHash);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.ReviewAttemptAsync(
                review with { Rationale = "Changed command under the same key." },
                cancellationToken));

        var correctionRequired = start with
        {
            AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FFA",
            ExpectedTaskVersion = 4,
            IdempotencyKey = "work-chain:attempt:start:correction-required",
            OccurredAt = chain.OccurredAt.AddMinutes(5),
        };
        var withoutCorrection = await store.StartAttemptAsync(correctionRequired, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, withoutCorrection.Status);
        Assert.Equal(4, withoutCorrection.TaskVersion);

        const string correctedContent = "Correct the failed gate without mutating the original instruction.";
        var correction = new WorkInstructionVersionCreateCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            "01ARZ3NDEKTSV4RRFFQ69G5FFB",
            correctedContent,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(correctedContent))),
            4,
            "work-chain:instruction:correct:first",
            chain.OccurredAt.AddMinutes(6));
        var corrected = await store.AddInstructionVersionAsync(correction, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, corrected.Status);
        Assert.Equal(5, corrected.TaskVersion);
        Assert.Equal(correction.InstructionVersionId, corrected.InstructionVersionId);
        Assert.Equal(2, corrected.InstructionVersion);
        var correctedReplay = await store.AddInstructionVersionAsync(correction, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, correctedReplay.Status);
        Assert.Equal(corrected.LedgerHash, correctedReplay.LedgerHash);

        var secondStart = new WorkAttemptStartCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            correction.InstructionVersionId,
            "01ARZ3NDEKTSV4RRFFQ69G5FFC",
            "software-engineer",
            5,
            "work-chain:attempt:start:second",
            chain.OccurredAt.AddMinutes(7));
        var secondStarted = await store.StartAttemptAsync(secondStart, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, secondStarted.Status);
        Assert.Equal(6, secondStarted.TaskVersion);

        var secondComplete = new WorkAttemptCompleteCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            secondStart.AttemptId,
            6,
            [new WorkEvidenceInput("01ARZ3NDEKTSV4RRFFQ69G5FFD", "tests:corrected-green")],
            "work-chain:attempt:complete:second",
            chain.OccurredAt.AddMinutes(8));
        var secondCompleted = await store.CompleteAttemptAsync(secondComplete, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, secondCompleted.Status);
        Assert.Equal(7, secondCompleted.TaskVersion);

        var approval = new WorkAttemptReviewCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            secondStart.AttemptId,
            "01ARZ3NDEKTSV4RRFFQ69G5FFE",
            "critic-qa",
            "approved",
            "The corrected evidence proves the gate.",
            7,
            "work-chain:attempt:review:second",
            chain.OccurredAt.AddMinutes(9));
        var approved = await store.ReviewAttemptAsync(approval, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, approved.Status);
        Assert.Equal(8, approved.TaskVersion);
        Assert.Equal("completed", approved.TaskState);

        var final = await store.ReadAsync(chain.TenantId, chain.SolicitationId, cancellationToken);
        Assert.NotNull(final);
        Assert.Equal("completed", final.TaskState);
        Assert.Equal(8, final.TaskVersion);
        Assert.Equal(correction.InstructionVersionId, final.InstructionVersionId);
        Assert.Equal(2, final.InstructionVersion);
        Assert.Equal(2, final.AttemptCount);
        Assert.Equal(2, final.EvidenceCount);
        Assert.Equal(2, final.ReviewCount);

        var aggregate = await store.ReadAggregateAsync(
            chain.TenantId,
            chain.SolicitationId,
            cancellationToken);
        Assert.NotNull(aggregate);
        Assert.Equal(chain.UserId, aggregate.UserId);
        var demand = Assert.Single(aggregate.Demands);
        Assert.Equal(["State is atomic", "Audit is complete"], demand.AcceptanceCriteria);
        var task = Assert.Single(demand.Tasks);
        Assert.Equal(8, task.Version);
        Assert.Equal([1, 2], task.Instructions.Select(item => item.Version));
        Assert.Null(task.Instructions[0].SupersedesId);
        Assert.Equal(task.Instructions[0].InstructionVersionId, task.Instructions[1].SupersedesId);
        Assert.Equal([1, 2], task.Attempts.Select(item => item.Number));
        Assert.Equal(["rejected", "approved"], task.Attempts.Select(item => item.State));
        Assert.All(task.Attempts, item => Assert.Single(item.Evidence));
        Assert.Equal("rejected", task.Attempts[0].Review?.Decision);
        Assert.Equal("approved", task.Attempts[1].Review?.Decision);
        Assert.Null(await store.ReadAggregateAsync(
            chain.TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FFF",
            cancellationToken));
    }
}
