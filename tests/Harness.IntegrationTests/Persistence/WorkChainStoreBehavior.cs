using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;

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
            "low",
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
        Assert.Equal("draft", snapshot.TaskState);
        Assert.Equal(1, snapshot.TaskVersion);
        Assert.Equal("low", snapshot.RiskTier);
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

        await AssertInitialLifecycleAsync(store, command, cancellationToken);
        await AssertMutationsAsync(store, command, cancellationToken);
        await AssertLeaseExpiryAsync(store, command, cancellationToken);
        await AssertCancellationAsync(store, command, cancellationToken);
        await AssertBlockingAsync(store, command, cancellationToken);
        await AssertReviewEscalationAsync(store, command, cancellationToken);
        Assert.Null(await store.ReadAsync(
            command.TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FF4",
            cancellationToken));
    }

    private static async Task AssertBlockingAsync(
        IWorkChainStore store,
        WorkChainCreateCommand template,
        CancellationToken cancellationToken)
    {
        var chain = template with
        {
            SolicitationId = "01ARZ3NDEKTSV4RRFFQ69G5FD0",
            DemandId = "01ARZ3NDEKTSV4RRFFQ69G5FD1",
            TaskId = "01ARZ3NDEKTSV4RRFFQ69G5FD2",
            InstructionVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FD3",
            IdempotencyKey = "work-chain:create:blocking",
            OccurredAt = template.OccurredAt.AddDays(3),
        };
        await store.CreateAsync(chain, cancellationToken);
        await AssertInitialLifecycleAsync(store, chain, cancellationToken);

        var start = new WorkAttemptStartCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            chain.InstructionVersionId,
            "01ARZ3NDEKTSV4RRFFQ69G5FD4",
            "blocked-owner",
            3,
            "work-chain:attempt:start:blocking",
            chain.OccurredAt.AddMinutes(1));
        var started = await store.StartAttemptAsync(start, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);
        Assert.Equal(5, started.TaskVersion);

        var block = new WorkTaskBlockCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            start.AttemptId,
            "agent",
            start.ProducerAgentId,
            "An external dependency is unavailable.",
            "dependency:external-api",
            5,
            "work-chain:task:block:first",
            chain.OccurredAt.AddMinutes(2));
        var blocked = await store.BlockRunningTaskAsync(block, cancellationToken);
        var blockReplay = await store.BlockRunningTaskAsync(block, cancellationToken);

        Assert.Equal(WorkChainMutationStatus.Applied, blocked.Status);
        Assert.Equal(6, blocked.TaskVersion);
        Assert.Equal("blocked", blocked.TaskState);
        Assert.Equal("abandoned", blocked.AttemptState);
        Assert.NotNull(blocked.LedgerSequence);
        Assert.NotNull(blocked.LedgerHash);
        Assert.NotNull(blocked.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, blockReplay.Status);
        Assert.Equal(blocked.LedgerHash, blockReplay.LedgerHash);

        var prematureRetry = await store.StartAttemptAsync(
            start with
            {
                AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FD5",
                ExpectedTaskVersion = 6,
                IdempotencyKey = "work-chain:attempt:start:while-blocked",
                OccurredAt = chain.OccurredAt.AddMinutes(3),
            },
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, prematureRetry.Status);
        Assert.Equal("blocked", prematureRetry.TaskState);
        Assert.Null(prematureRetry.LedgerSequence);

        var unblock = new WorkTaskUnblockCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            "chief",
            "bruna",
            "The dependency recovered and its health check is green.",
            "dependency:external-api:healthy",
            6,
            "work-chain:task:unblock:first",
            chain.OccurredAt.AddMinutes(4));
        var unblocked = await store.UnblockTaskAsync(unblock, cancellationToken);
        var unblockReplay = await store.UnblockTaskAsync(unblock, cancellationToken);

        Assert.Equal(WorkChainMutationStatus.Applied, unblocked.Status);
        Assert.Equal(7, unblocked.TaskVersion);
        Assert.Equal("ready", unblocked.TaskState);
        Assert.Equal("abandoned", unblocked.AttemptState);
        Assert.NotNull(unblocked.LedgerSequence);
        Assert.NotNull(unblocked.LedgerHash);
        Assert.NotNull(unblocked.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, unblockReplay.Status);
        Assert.Equal(unblocked.LedgerHash, unblockReplay.LedgerHash);

        var retry = await store.StartAttemptAsync(
            start with
            {
                AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FD6",
                ProducerAgentId = "replacement-owner",
                ExpectedTaskVersion = 7,
                IdempotencyKey = "work-chain:attempt:start:after-unblock",
                OccurredAt = chain.OccurredAt.AddMinutes(5),
            },
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, retry.Status);
        Assert.Equal(9, retry.TaskVersion);

        var lateCompletion = await store.CompleteAttemptAsync(
            new WorkAttemptCompleteCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                start.AttemptId,
                9,
                [new WorkEvidenceInput(
                    "01ARZ3NDEKTSV4RRFFQ69G5FD7",
                    "late:evidence")],
                "work-chain:attempt:complete:blocked",
                chain.OccurredAt.AddMinutes(6)),
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, lateCompletion.Status);
        Assert.Null(lateCompletion.LedgerSequence);

        var aggregate = await store.ReadAggregateAsync(
            chain.TenantId,
            chain.SolicitationId,
            cancellationToken);
        Assert.NotNull(aggregate);
        var task = Assert.Single(Assert.Single(aggregate.Demands).Tasks);
        Assert.Equal("running", task.State);
        Assert.Equal(["abandoned", "running"], task.Attempts.Select(item => item.State));
        Assert.Equal(
            [chain.InstructionVersionId, chain.InstructionVersionId],
            task.Attempts.Select(item => item.InstructionVersionId));
        Assert.NotNull(task.Attempts[0].CompletedAt);
    }

    private static async Task AssertReviewEscalationAsync(
        IWorkChainStore store,
        WorkChainCreateCommand template,
        CancellationToken cancellationToken)
    {
        var chain = template with
        {
            SolicitationId = "01ARZ3NDEKTSV4RRFFQ69G5ZZ0",
            DemandId = "01ARZ3NDEKTSV4RRFFQ69G5ZZ1",
            TaskId = "01ARZ3NDEKTSV4RRFFQ69G5ZZ2",
            InstructionVersionId = "01ARZ3NDEKTSV4RRFFQ69G5ZZ3",
            IdempotencyKey = "work-chain:create:review-escalation",
            OccurredAt = template.OccurredAt.AddDays(4),
        };
        await store.CreateAsync(chain, cancellationToken);
        await AssertInitialLifecycleAsync(store, chain, cancellationToken);

        var taskVersion = 3L;
        var instructionVersion = 1;
        var instructionId = chain.InstructionVersionId;
        for (var cycle = 1; cycle <= 4; cycle++)
        {
            var cycleAt = chain.OccurredAt.AddMinutes(cycle * 10);
            var attemptId = UlidValue.New(cycleAt).ToString();
            var start = new WorkAttemptStartCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                instructionId,
                attemptId,
                "reviewed-owner",
                taskVersion,
                $"work-chain:attempt:start:review-cycle:{cycle}",
                cycleAt);
            var started = await store.StartAttemptAsync(start, cancellationToken);
            Assert.Equal(WorkChainMutationStatus.Applied, started.Status);
            taskVersion += 2;
            Assert.Equal(taskVersion, started.TaskVersion);

            var completed = await store.CompleteAttemptAsync(
                new WorkAttemptCompleteCommand(
                    chain.TenantId,
                    chain.SolicitationId,
                    chain.TaskId,
                    attemptId,
                    taskVersion,
                    [new WorkEvidenceInput(
                        UlidValue.New(cycleAt.AddMinutes(1)).ToString(),
                        $"tests:review-cycle:{cycle}")],
                    $"work-chain:attempt:complete:review-cycle:{cycle}",
                    cycleAt.AddMinutes(1)),
                cancellationToken);
            Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);
            taskVersion++;
            Assert.Equal(taskVersion, completed.TaskVersion);

            var reviewed = await store.ReviewAttemptAsync(
                new WorkAttemptReviewCommand(
                    chain.TenantId,
                    chain.SolicitationId,
                    chain.TaskId,
                    attemptId,
                    UlidValue.New(cycleAt.AddMinutes(2)).ToString(),
                    "independent-reviewer",
                    "rejected",
                    $"Objective gate failed in review cycle {cycle}.",
                    taskVersion,
                    $"work-chain:attempt:review:cycle:{cycle}",
                    cycleAt.AddMinutes(2)),
                cancellationToken);
            Assert.Equal(WorkChainMutationStatus.Applied, reviewed.Status);
            taskVersion++;
            Assert.Equal(taskVersion, reviewed.TaskVersion);
            Assert.Equal(cycle == 4 ? "escalated" : "running", reviewed.TaskState);
            Assert.NotNull(reviewed.LedgerHash);
            Assert.NotNull(reviewed.OutboxMessageId);

            if (cycle == 4)
            {
                continue;
            }

            var correctionContent = $"Correct the objective failure from review cycle {cycle}.";
            instructionId = UlidValue.New(cycleAt.AddMinutes(3)).ToString();
            var corrected = await store.AddInstructionVersionAsync(
                new WorkInstructionVersionCreateCommand(
                    chain.TenantId,
                    chain.SolicitationId,
                    chain.TaskId,
                    instructionId,
                    correctionContent,
                    Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(correctionContent))),
                    taskVersion,
                    $"work-chain:instruction:review-cycle:{cycle}",
                    cycleAt.AddMinutes(3)),
                cancellationToken);
            Assert.Equal(WorkChainMutationStatus.Applied, corrected.Status);
            taskVersion++;
            instructionVersion++;
            Assert.Equal(taskVersion, corrected.TaskVersion);
            Assert.Equal("ready", corrected.TaskState);
            Assert.Equal(instructionVersion, corrected.InstructionVersion);
        }

        const string replanContent =
            "Replan the card after review escalation with a corrected execution strategy.";
        var bypass = await store.AddInstructionVersionAsync(
            new WorkInstructionVersionCreateCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                UlidValue.New(chain.OccurredAt.AddMinutes(51)).ToString(),
                replanContent,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replanContent))),
                taskVersion,
                "work-chain:instruction:escalation-bypass",
                chain.OccurredAt.AddMinutes(51)),
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, bypass.Status);
        Assert.Null(bypass.LedgerSequence);

        var replan = new WorkTaskReplanCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            UlidValue.New(chain.OccurredAt.AddMinutes(52)).ToString(),
            replanContent,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replanContent))),
            "bruna",
            "Review cycle limit requires explicit replanning.",
            "review-escalation:decision",
            taskVersion,
            "work-chain:task:replan:first",
            chain.OccurredAt.AddMinutes(52));
        var replanned = await store.ReplanEscalatedTaskAsync(replan, cancellationToken);
        var replanReplay = await store.ReplanEscalatedTaskAsync(replan, cancellationToken);

        Assert.Equal(WorkChainMutationStatus.Applied, replanned.Status);
        taskVersion++;
        instructionVersion++;
        Assert.Equal(taskVersion, replanned.TaskVersion);
        Assert.Equal("ready", replanned.TaskState);
        Assert.Equal("rejected", replanned.AttemptState);
        Assert.Equal(replan.InstructionVersionId, replanned.InstructionVersionId);
        Assert.Equal(instructionVersion, replanned.InstructionVersion);
        Assert.NotNull(replanned.LedgerSequence);
        Assert.NotNull(replanned.LedgerHash);
        Assert.NotNull(replanned.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, replanReplay.Status);
        Assert.Equal(replanned.LedgerHash, replanReplay.LedgerHash);

        var restarted = await store.StartAttemptAsync(
            new WorkAttemptStartCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                replan.InstructionVersionId,
                UlidValue.New(chain.OccurredAt.AddMinutes(53)).ToString(),
                "replanned-owner",
                taskVersion,
                "work-chain:attempt:start:after-replan",
                chain.OccurredAt.AddMinutes(53)),
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, restarted.Status);
        taskVersion += 2;
        Assert.Equal(taskVersion, restarted.TaskVersion);

        var aggregate = await store.ReadAggregateAsync(
            chain.TenantId,
            chain.SolicitationId,
            cancellationToken);
        Assert.NotNull(aggregate);
        var task = Assert.Single(Assert.Single(aggregate.Demands).Tasks);
        Assert.Equal("running", task.State);
        Assert.Equal(instructionVersion, task.Instructions.Count);
        Assert.Equal(
            ["rejected", "rejected", "rejected", "rejected", "running"],
            task.Attempts.Select(item => item.State));
        Assert.Equal(replan.InstructionVersionId, task.Instructions[^1].InstructionVersionId);
    }

    private static async Task AssertCancellationAsync(
        IWorkChainStore store,
        WorkChainCreateCommand template,
        CancellationToken cancellationToken)
    {
        var chain = template with
        {
            SolicitationId = "01ARZ3NDEKTSV4RRFFQ69G5FC0",
            DemandId = "01ARZ3NDEKTSV4RRFFQ69G5FC1",
            TaskId = "01ARZ3NDEKTSV4RRFFQ69G5FC2",
            InstructionVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FC3",
            IdempotencyKey = "work-chain:create:cancellation",
            OccurredAt = template.OccurredAt.AddDays(2),
        };
        await store.CreateAsync(chain, cancellationToken);
        await AssertInitialLifecycleAsync(store, chain, cancellationToken);

        var start = new WorkAttemptStartCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            chain.InstructionVersionId,
            "01ARZ3NDEKTSV4RRFFQ69G5FC4",
            "cancelled-owner",
            3,
            "work-chain:attempt:start:cancellation",
            chain.OccurredAt.AddMinutes(1));
        var started = await store.StartAttemptAsync(start, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

        var cancellation = new WorkTaskCancellationCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            start.AttemptId,
            "chief",
            "bruna",
            "The user cancelled the active card.",
            "conversation:cancel-command",
            5,
            "work-chain:task:cancel:first",
            chain.OccurredAt.AddMinutes(2));
        var cancelled = await store.CancelRunningTaskAsync(
            cancellation,
            cancellationToken);
        var replay = await store.CancelRunningTaskAsync(
            cancellation,
            cancellationToken);

        Assert.Equal(WorkChainMutationStatus.Applied, cancelled.Status);
        Assert.Equal(6, cancelled.TaskVersion);
        Assert.Equal("cancelled", cancelled.TaskState);
        Assert.Equal("cancelled", cancelled.AttemptState);
        Assert.NotNull(cancelled.LedgerSequence);
        Assert.NotNull(cancelled.LedgerHash);
        Assert.NotNull(cancelled.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, replay.Status);
        Assert.Equal(cancelled.LedgerHash, replay.LedgerHash);

        var lateCompletion = await store.CompleteAttemptAsync(
            new WorkAttemptCompleteCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                start.AttemptId,
                6,
                [new WorkEvidenceInput(
                    "01ARZ3NDEKTSV4RRFFQ69G5FC5",
                    "late:evidence")],
                "work-chain:attempt:complete:cancelled",
                chain.OccurredAt.AddMinutes(3)),
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, lateCompletion.Status);

        var retry = await store.StartAttemptAsync(
            start with
            {
                AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FC6",
                ExpectedTaskVersion = 6,
                IdempotencyKey = "work-chain:attempt:start:after-cancellation",
                OccurredAt = chain.OccurredAt.AddMinutes(4),
            },
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, retry.Status);

        var aggregate = await store.ReadAggregateAsync(
            chain.TenantId,
            chain.SolicitationId,
            cancellationToken);
        Assert.NotNull(aggregate);
        var task = Assert.Single(Assert.Single(aggregate.Demands).Tasks);
        Assert.Equal("cancelled", task.State);
        var attempt = Assert.Single(task.Attempts);
        Assert.Equal("cancelled", attempt.State);
        Assert.NotNull(attempt.CompletedAt);
    }

    private static async Task AssertLeaseExpiryAsync(
        IWorkChainStore store,
        WorkChainCreateCommand template,
        CancellationToken cancellationToken)
    {
        var chain = template with
        {
            SolicitationId = "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            DemandId = "01ARZ3NDEKTSV4RRFFQ69G5FB1",
            TaskId = "01ARZ3NDEKTSV4RRFFQ69G5FB2",
            InstructionVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FB3",
            IdempotencyKey = "work-chain:create:lease-expiry",
            OccurredAt = template.OccurredAt.AddDays(1),
        };
        await store.CreateAsync(chain, cancellationToken);
        await AssertInitialLifecycleAsync(store, chain, cancellationToken);

        var start = new WorkAttemptStartCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            chain.InstructionVersionId,
            "01ARZ3NDEKTSV4RRFFQ69G5FB4",
            "lease-owner",
            3,
            "work-chain:attempt:start:lease-expiry",
            chain.OccurredAt.AddMinutes(1));
        var started = await store.StartAttemptAsync(start, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

        var expiry = new WorkAttemptLeaseExpiredCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            start.AttemptId,
            5,
            "work-chain:attempt:lease-expired",
            chain.OccurredAt.AddMinutes(2));
        var expired = await store.ExpireAttemptLeaseAsync(expiry, cancellationToken);
        var replay = await store.ExpireAttemptLeaseAsync(expiry, cancellationToken);

        Assert.Equal(WorkChainMutationStatus.Applied, expired.Status);
        Assert.Equal(6, expired.TaskVersion);
        Assert.Equal("ready", expired.TaskState);
        Assert.Equal("abandoned", expired.AttemptState);
        Assert.NotNull(expired.LedgerSequence);
        Assert.NotNull(expired.LedgerHash);
        Assert.NotNull(expired.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, replay.Status);
        Assert.Equal(expired.LedgerHash, replay.LedgerHash);

        var lateCompletion = await store.CompleteAttemptAsync(
            new WorkAttemptCompleteCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                start.AttemptId,
                6,
                [new WorkEvidenceInput("01ARZ3NDEKTSV4RRFFQ69G5FB6", "late:evidence")],
                "work-chain:attempt:complete:expired",
                chain.OccurredAt.AddMinutes(3)),
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, lateCompletion.Status);
        Assert.Null(lateCompletion.LedgerSequence);

        var retry = await store.StartAttemptAsync(
            start with
            {
                AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FB5",
                ExpectedTaskVersion = 6,
                IdempotencyKey = "work-chain:attempt:start:after-expiry",
                OccurredAt = chain.OccurredAt.AddMinutes(4),
            },
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, retry.Status);
        Assert.Equal(8, retry.TaskVersion);

        var aggregate = await store.ReadAggregateAsync(
            chain.TenantId,
            chain.SolicitationId,
            cancellationToken);
        Assert.NotNull(aggregate);
        var task = Assert.Single(Assert.Single(aggregate.Demands).Tasks);
        Assert.Equal("running", task.State);
        Assert.Equal(["abandoned", "running"], task.Attempts.Select(item => item.State));
        Assert.Equal(
            [chain.InstructionVersionId, chain.InstructionVersionId],
            task.Attempts.Select(item => item.InstructionVersionId));
        Assert.NotNull(task.Attempts[0].CompletedAt);
        Assert.Null(task.Attempts[0].Review);
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
            3,
            "work-chain:attempt:start:first",
            chain.OccurredAt.AddMinutes(1));
        var assignment = await store.AssignTaskAsync(
            new WorkTaskAssignmentCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                chain.InstructionVersionId,
                start.ProducerAgentId,
                "chief",
                "bruna",
                $"attempt:{attemptId}",
                3,
                "work-chain:task:assign:first",
                chain.OccurredAt.AddSeconds(50)),
            cancellationToken);
        var assignmentReplay = await store.AssignTaskAsync(
            new WorkTaskAssignmentCommand(
                chain.TenantId,
                chain.SolicitationId,
                chain.TaskId,
                chain.InstructionVersionId,
                start.ProducerAgentId,
                "chief",
                "bruna",
                $"attempt:{attemptId}",
                3,
                "work-chain:task:assign:first",
                chain.OccurredAt.AddSeconds(50)),
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, assignment.Status);
        Assert.Equal(4, assignment.TaskVersion);
        Assert.Equal("assigned", assignment.TaskState);
        Assert.NotNull(assignment.LedgerSequence);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, assignmentReplay.Status);
        Assert.Equal(assignment.LedgerHash, assignmentReplay.LedgerHash);
        start = start with { ExpectedTaskVersion = 4 };
        var wrongAssignee = await store.StartAttemptAsync(
            start with
            {
                AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FCA",
                ProducerAgentId = "unassigned-engineer",
                IdempotencyKey = "work-chain:attempt:start:wrong-assignee",
            },
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, wrongAssignee.Status);
        Assert.Equal(4, wrongAssignee.TaskVersion);
        Assert.Null(wrongAssignee.LedgerSequence);

        var started = await Task.WhenAll(
            store.StartAttemptAsync(start, cancellationToken),
            store.StartAttemptAsync(start, cancellationToken));
        Assert.Single(started, item => item.Status == WorkChainMutationStatus.Applied);
        Assert.Single(started, item => item.Status == WorkChainMutationStatus.IdempotentReplay);
        Assert.All(started, item => Assert.Equal(5, item.TaskVersion));
        Assert.Single(started.Select(item => item.LedgerHash).Distinct(StringComparer.Ordinal));

        var staleStart = start with
        {
            AttemptId = "01ARZ3NDEKTSV4RRFFQ69G5FF6",
            IdempotencyKey = "work-chain:attempt:start:stale",
        };
        var stale = await store.StartAttemptAsync(staleStart, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.VersionConflict, stale.Status);
        Assert.Equal(5, stale.TaskVersion);
        Assert.Null(stale.LedgerSequence);
        var staleReplay = await store.StartAttemptAsync(staleStart, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, staleReplay.Status);
        Assert.Equal(5, staleReplay.TaskVersion);

        var running = await store.ReadAsync(chain.TenantId, chain.SolicitationId, cancellationToken);
        Assert.NotNull(running);
        Assert.Equal("running", running.TaskState);
        Assert.Equal(5, running.TaskVersion);
        Assert.Equal(1, running.AttemptCount);

        var complete = new WorkAttemptCompleteCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            attemptId,
            5,
            [new WorkEvidenceInput("01ARZ3NDEKTSV4RRFFQ69G5FF7", "tests:green")],
            "work-chain:attempt:complete:first",
            chain.OccurredAt.AddMinutes(2));
        var completed = await store.CompleteAttemptAsync(complete, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);
        Assert.Equal(6, completed.TaskVersion);
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
            "Self review must be rejected even for low risk.",
            6,
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
        Assert.Equal(7, reviewed.TaskVersion);
        Assert.Equal("running", reviewed.TaskState);
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
            ExpectedTaskVersion = 7,
            IdempotencyKey = "work-chain:attempt:start:correction-required",
            OccurredAt = chain.OccurredAt.AddMinutes(5),
        };
        var withoutCorrection = await store.StartAttemptAsync(correctionRequired, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, withoutCorrection.Status);
        Assert.Equal(7, withoutCorrection.TaskVersion);
        Assert.Equal("running", withoutCorrection.TaskState);

        const string correctedContent = "Correct the failed gate without mutating the original instruction.";
        var correction = new WorkInstructionVersionCreateCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            "01ARZ3NDEKTSV4RRFFQ69G5FFB",
            correctedContent,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(correctedContent))),
            7,
            "work-chain:instruction:correct:first",
            chain.OccurredAt.AddMinutes(6));
        var corrected = await store.AddInstructionVersionAsync(correction, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, corrected.Status);
        Assert.Equal(8, corrected.TaskVersion);
        Assert.Equal("ready", corrected.TaskState);
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
            8,
            "work-chain:attempt:start:second",
            chain.OccurredAt.AddMinutes(7));
        var secondStarted = await store.StartAttemptAsync(secondStart, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, secondStarted.Status);
        Assert.Equal(10, secondStarted.TaskVersion);

        var secondComplete = new WorkAttemptCompleteCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            secondStart.AttemptId,
            10,
            [new WorkEvidenceInput("01ARZ3NDEKTSV4RRFFQ69G5FFD", "tests:corrected-green")],
            "work-chain:attempt:complete:second",
            chain.OccurredAt.AddMinutes(8));
        var secondCompleted = await store.CompleteAttemptAsync(secondComplete, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, secondCompleted.Status);
        Assert.Equal(11, secondCompleted.TaskVersion);

        var approval = new WorkAttemptReviewCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            secondStart.AttemptId,
            "01ARZ3NDEKTSV4RRFFQ69G5FFE",
            "critic-qa",
            "approved",
            "The corrected evidence proves the gate.",
            11,
            "work-chain:attempt:review:second",
            chain.OccurredAt.AddMinutes(9));
        var approved = await store.ReviewAttemptAsync(approval, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, approved.Status);
        Assert.Equal(12, approved.TaskVersion);
        Assert.Equal("approved", approved.TaskState);

        var delivery = new WorkTaskDeliveryCompleteCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            "delivery-reconciler",
            "integration:premature",
            12,
            "work-chain:task:complete:premature",
            chain.OccurredAt.AddMinutes(10));
        var prematureCompletion = await store.CompleteMergedTaskAsync(
            delivery,
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, prematureCompletion.Status);
        Assert.Null(prematureCompletion.LedgerSequence);

        var merge = new WorkTaskMergeCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            "merge-coordinator",
            "git:develop@approved",
            12,
            "work-chain:task:merge:first",
            chain.OccurredAt.AddMinutes(11));
        var merged = await store.MergeApprovedTaskAsync(merge, cancellationToken);
        var mergedReplay = await store.MergeApprovedTaskAsync(merge, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, merged.Status);
        Assert.Equal(13, merged.TaskVersion);
        Assert.Equal("merged", merged.TaskState);
        Assert.Equal("approved", merged.AttemptState);
        Assert.NotNull(merged.LedgerHash);
        Assert.NotNull(merged.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, mergedReplay.Status);
        Assert.Equal(merged.LedgerHash, mergedReplay.LedgerHash);

        var completedDelivery = await store.CompleteMergedTaskAsync(
            delivery with
            {
                EvidenceReference = "reconciliation:projections-and-effects",
                ExpectedTaskVersion = 13,
                IdempotencyKey = "work-chain:task:complete:first",
                OccurredAt = chain.OccurredAt.AddMinutes(12),
            },
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, completedDelivery.Status);
        Assert.Equal(14, completedDelivery.TaskVersion);
        Assert.Equal("completed", completedDelivery.TaskState);
        Assert.Equal("approved", completedDelivery.AttemptState);
        Assert.NotNull(completedDelivery.LedgerHash);
        Assert.NotNull(completedDelivery.OutboxMessageId);

        var final = await store.ReadAsync(chain.TenantId, chain.SolicitationId, cancellationToken);
        Assert.NotNull(final);
        Assert.Equal("completed", final.TaskState);
        Assert.Equal(14, final.TaskVersion);
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
        Assert.Equal(14, task.Version);
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

    private static async Task AssertInitialLifecycleAsync(
        IWorkChainStore store,
        WorkChainCreateCommand chain,
        CancellationToken cancellationToken)
    {
        var prematureReady = new WorkTaskLifecycleCommand(
            chain.TenantId,
            chain.SolicitationId,
            chain.TaskId,
            "chief",
            "bruna",
            "Definition of Ready has not been triaged yet.",
            "requirements:pending",
            1,
            $"work-chain:task:ready:premature:{chain.TaskId}",
            chain.OccurredAt.AddSeconds(10));
        var premature = await store.MarkTaskReadyAsync(
            prematureReady,
            cancellationToken);
        Assert.Equal(WorkChainMutationStatus.InvalidState, premature.Status);
        Assert.Null(premature.LedgerSequence);

        var triage = prematureReady with
        {
            Reason = "Demand scope and risk were triaged.",
            EvidenceReference = "triage:accepted",
            IdempotencyKey = $"work-chain:task:triage:{chain.TaskId}",
            OccurredAt = chain.OccurredAt.AddSeconds(20),
        };
        var triaged = await store.TriageTaskAsync(triage, cancellationToken);
        var triagedReplay = await store.TriageTaskAsync(triage, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, triaged.Status);
        Assert.Equal(2, triaged.TaskVersion);
        Assert.Equal("triaged", triaged.TaskState);
        Assert.NotNull(triaged.LedgerSequence);
        Assert.NotNull(triaged.LedgerHash);
        Assert.NotNull(triaged.OutboxMessageId);
        Assert.Equal(WorkChainMutationStatus.IdempotentReplay, triagedReplay.Status);
        Assert.Equal(triaged.LedgerHash, triagedReplay.LedgerHash);

        var readiness = prematureReady with
        {
            Reason = "Acceptance criteria and dependencies satisfy the Definition of Ready.",
            EvidenceReference = "dor:validated",
            ExpectedTaskVersion = 2,
            IdempotencyKey = $"work-chain:task:ready:{chain.TaskId}",
            OccurredAt = chain.OccurredAt.AddSeconds(30),
        };
        var ready = await store.MarkTaskReadyAsync(readiness, cancellationToken);
        Assert.Equal(WorkChainMutationStatus.Applied, ready.Status);
        Assert.Equal(3, ready.TaskVersion);
        Assert.Equal("ready", ready.TaskState);
        Assert.NotNull(ready.LedgerSequence);
        Assert.NotNull(ready.LedgerHash);
        Assert.NotNull(ready.OutboxMessageId);
    }
}
