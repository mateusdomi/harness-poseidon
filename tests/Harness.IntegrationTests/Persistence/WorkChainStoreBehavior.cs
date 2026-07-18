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
            Rationale = "Evidence proves the acceptance criteria.",
            IdempotencyKey = "work-chain:attempt:review:critic",
            OccurredAt = chain.OccurredAt.AddMinutes(4),
        };
        var reviewed = await store.ReviewAttemptAsync(review, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, reviewed.Status);
        Assert.Equal(4, reviewed.TaskVersion);
        Assert.Equal("completed", reviewed.TaskState);
        Assert.Equal("approved", reviewed.AttemptState);
        Assert.NotNull(reviewed.OutboxMessageId);
        var reviewedReplay = await store.ReviewAttemptAsync(review, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, reviewedReplay.Status);
        Assert.Equal(reviewed.LedgerHash, reviewedReplay.LedgerHash);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.ReviewAttemptAsync(
                review with { Rationale = "Changed command under the same key." },
                cancellationToken));

        var final = await store.ReadAsync(chain.TenantId, chain.SolicitationId, cancellationToken);
        Assert.NotNull(final);
        Assert.Equal("completed", final.TaskState);
        Assert.Equal(4, final.TaskVersion);
        Assert.Equal(1, final.AttemptCount);
        Assert.Equal(1, final.EvidenceCount);
        Assert.Equal(1, final.ReviewCount);
    }
}
