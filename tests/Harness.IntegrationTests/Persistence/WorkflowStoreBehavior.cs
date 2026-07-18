using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.IntegrationTests.Persistence;

internal static class WorkflowStoreBehavior
{
    public static async Task AssertAsync(IWorkflowStore store, CancellationToken cancellationToken)
    {
        var phases = new WorkflowPhaseCreateInput[]
        {
            new(
                "01ARZ3NDEKTSV4RRFFQ69G5FH2",
                "analysis",
                "Analysis",
                1,
                [
                    new("01ARZ3NDEKTSV4RRFFQ69G5FH3", "requirements", "Requirements", "document", 2m),
                    new("01ARZ3NDEKTSV4RRFFQ69G5FH4", "analysis-gate", "Analysis gate", "gate", 1m),
                ],
                [
                    new(
                        "01ARZ3NDEKTSV4RRFFQ69G5FH5",
                        "01ARZ3NDEKTSV4RRFFQ69G5FH4",
                        "analysis-gate",
                        "Analysis gate",
                        "validated",
                        ["01ARZ3NDEKTSV4RRFFQ69G5FH3"]),
                ]),
        };
        var command = new WorkflowDefinitionCreateCommand(
            FoundationTransactionBehavior.TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FH0",
            "Delivery workflow",
            "01ARZ3NDEKTSV4RRFFQ69G5FH1",
            1,
            WorkflowDefinitionContentHash.Compute(phases),
            phases,
            "workflow:definition:publish:first",
            new DateTimeOffset(2026, 7, 18, 16, 45, 0, TimeSpan.Zero));

        var receipts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.CreatePublishedDefinitionAsync(command, cancellationToken)));

        Assert.Single(receipts, receipt => !receipt.Replay);
        Assert.Equal(9, receipts.Count(receipt => receipt.Replay));
        Assert.Single(receipts.Select(receipt => receipt.LedgerHash).Distinct(StringComparer.Ordinal));
        Assert.Single(receipts.Select(receipt => receipt.OutboxMessageId).Distinct(StringComparer.Ordinal));

        var snapshot = await store.ReadDefinitionAsync(
            command.TenantId, command.DefinitionId, cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(command.Name, snapshot.Name);
        Assert.Equal(command.DefinitionVersionId, snapshot.DefinitionVersionId);
        Assert.Equal("published", snapshot.Status);
        Assert.Equal(command.ContentHash, snapshot.ContentHash);
        Assert.Equal(1, snapshot.PhaseCount);
        Assert.Equal(2, snapshot.ObjectiveCount);
        Assert.Equal(1, snapshot.GateCount);
        Assert.Equal(1, snapshot.RequirementCount);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.CreatePublishedDefinitionAsync(
                command with { Name = "Changed under the same key" },
                cancellationToken));
        Assert.Equal(snapshot, await store.ReadDefinitionAsync(
            command.TenantId, command.DefinitionId, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreatePublishedDefinitionAsync(
                command with { ContentHash = new string('0', 64) },
                cancellationToken));
        Assert.Null(await store.ReadDefinitionAsync(
            command.TenantId, "01ARZ3NDEKTSV4RRFFQ69G5FHZ", cancellationToken));

        await AssertRunCreationAsync(store, command, cancellationToken);
    }

    private static async Task AssertRunCreationAsync(
        IWorkflowStore store,
        WorkflowDefinitionCreateCommand definition,
        CancellationToken cancellationToken)
    {
        var command = new WorkflowRunCreateCommand(
            definition.TenantId,
            FoundationTransactionBehavior.ProjectId,
            definition.DefinitionVersionId,
            "01ARZ3NDEKTSV4RRFFQ69G5FH6",
            "workflow:run:create:first",
            definition.OccurredAt.AddMinutes(1));
        var receipts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.CreateRunAsync(command, cancellationToken)));

        Assert.Single(receipts, receipt => !receipt.Replay);
        Assert.Equal(9, receipts.Count(receipt => receipt.Replay));
        Assert.All(receipts, receipt => Assert.Equal(1, receipt.RunVersion));
        Assert.Single(receipts.Select(receipt => receipt.LedgerHash).Distinct(StringComparer.Ordinal));
        Assert.Single(receipts.Select(receipt => receipt.OutboxMessageId).Distinct(StringComparer.Ordinal));

        var snapshot = await store.ReadRunAsync(command.TenantId, command.RunId, cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(command.ProjectId, snapshot.ProjectId);
        Assert.Equal(command.DefinitionVersionId, snapshot.DefinitionVersionId);
        Assert.Equal("pending", snapshot.State);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(command.OccurredAt, snapshot.CreatedAt);
        Assert.Null(snapshot.StartedAt);
        Assert.Null(snapshot.CompletedAt);
        Assert.Equal(1, snapshot.PhaseCount);
        Assert.Equal(0, snapshot.ActivePhaseCount);
        Assert.Equal(2, snapshot.ObjectiveCount);
        Assert.Equal(1, snapshot.GateCount);
        Assert.Equal(0m, snapshot.Executed);
        Assert.Equal(0m, snapshot.Validated);
        Assert.Equal(0m, snapshot.Approved);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.CreateRunAsync(
                command with { RunId = "01ARZ3NDEKTSV4RRFFQ69G5FH7" },
                cancellationToken));
        Assert.Equal(snapshot, await store.ReadRunAsync(
            command.TenantId, command.RunId, cancellationToken));
        Assert.Null(await store.ReadRunAsync(
            command.TenantId, "01ARZ3NDEKTSV4RRFFQ69G5FHY", cancellationToken));

        await AssertRunTransitionsAsync(store, command, cancellationToken);
    }

    private static async Task AssertRunTransitionsAsync(
        IWorkflowStore store,
        WorkflowRunCreateCommand run,
        CancellationToken cancellationToken)
    {
        var start = new WorkflowRunTransitionCommand(
            run.TenantId,
            run.RunId,
            WorkflowRunTransition.Start,
            1,
            "workflow:run:start:first",
            run.OccurredAt.AddMinutes(1));
        var receipts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.TransitionRunAsync(start, cancellationToken)));
        Assert.Single(receipts, receipt => receipt.Status == WorkflowRunMutationStatus.Applied);
        Assert.Equal(9, receipts.Count(receipt =>
            receipt.Status == WorkflowRunMutationStatus.IdempotentReplay));
        Assert.All(receipts, receipt =>
        {
            Assert.Equal(2, receipt.RunVersion);
            Assert.Equal("running", receipt.RunState);
        });
        Assert.Single(receipts.Select(receipt => receipt.LedgerHash).Distinct(StringComparer.Ordinal));
        Assert.Single(receipts.Select(receipt => receipt.OutboxMessageId).Distinct(StringComparer.Ordinal));

        var started = await store.ReadRunAsync(run.TenantId, run.RunId, cancellationToken);
        Assert.NotNull(started);
        Assert.Equal("running", started.State);
        Assert.Equal(2, started.Version);
        Assert.Equal(start.OccurredAt, started.StartedAt);
        Assert.Equal(1, started.ActivePhaseCount);

        var stale = new WorkflowRunTransitionCommand(
            run.TenantId,
            run.RunId,
            WorkflowRunTransition.Pause,
            1,
            "workflow:run:pause:stale",
            start.OccurredAt.AddMinutes(1));
        var staleReceipt = await store.TransitionRunAsync(stale, cancellationToken);
        Assert.Equal(WorkflowRunMutationStatus.VersionConflict, staleReceipt.Status);
        Assert.Equal(2, staleReceipt.RunVersion);
        Assert.Null(staleReceipt.LedgerSequence);
        Assert.Equal(
            WorkflowRunMutationStatus.IdempotentReplay,
            (await store.TransitionRunAsync(stale, cancellationToken)).Status);

        var paused = await store.TransitionRunAsync(
            stale with
            {
                ExpectedRunVersion = 2,
                IdempotencyKey = "workflow:run:pause:first",
                OccurredAt = stale.OccurredAt.AddMinutes(1),
            },
            cancellationToken);
        Assert.Equal(WorkflowRunMutationStatus.Applied, paused.Status);
        Assert.Equal(3, paused.RunVersion);
        Assert.Equal("paused", paused.RunState);

        var resumed = await store.TransitionRunAsync(
            stale with
            {
                Transition = WorkflowRunTransition.Resume,
                ExpectedRunVersion = 3,
                IdempotencyKey = "workflow:run:resume:first",
                OccurredAt = stale.OccurredAt.AddMinutes(2),
            },
            cancellationToken);
        Assert.Equal(WorkflowRunMutationStatus.Applied, resumed.Status);
        Assert.Equal(4, resumed.RunVersion);
        Assert.Equal("running", resumed.RunState);

        var invalid = start with
        {
            ExpectedRunVersion = 4,
            IdempotencyKey = "workflow:run:start:invalid",
            OccurredAt = start.OccurredAt.AddMinutes(4),
        };
        var invalidReceipt = await store.TransitionRunAsync(invalid, cancellationToken);
        Assert.Equal(WorkflowRunMutationStatus.InvalidState, invalidReceipt.Status);
        Assert.Equal(4, invalidReceipt.RunVersion);
        Assert.Null(invalidReceipt.OutboxMessageId);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.TransitionRunAsync(
                invalid with { Transition = WorkflowRunTransition.Cancel },
                cancellationToken));

        var missing = await store.TransitionRunAsync(
            start with
            {
                RunId = "01ARZ3NDEKTSV4RRFFQ69G5FHY",
                IdempotencyKey = "workflow:run:start:missing",
                OccurredAt = start.OccurredAt.AddMinutes(5),
            },
            cancellationToken);
        Assert.Equal(WorkflowRunMutationStatus.NotFound, missing.Status);

        var cancellable = run with
        {
            RunId = "01ARZ3NDEKTSV4RRFFQ69G5FH7",
            IdempotencyKey = "workflow:run:create:cancellable",
            OccurredAt = run.OccurredAt.AddMinutes(10),
        };
        await store.CreateRunAsync(cancellable, cancellationToken);
        var cancel = new WorkflowRunTransitionCommand(
            cancellable.TenantId,
            cancellable.RunId,
            WorkflowRunTransition.Cancel,
            1,
            "workflow:run:cancel:first",
            cancellable.OccurredAt.AddMinutes(1));
        var cancelledReceipt = await store.TransitionRunAsync(cancel, cancellationToken);
        Assert.Equal(WorkflowRunMutationStatus.Applied, cancelledReceipt.Status);
        Assert.Equal(2, cancelledReceipt.RunVersion);
        Assert.Equal("cancelled", cancelledReceipt.RunState);
        var cancelled = await store.ReadRunAsync(
            cancellable.TenantId, cancellable.RunId, cancellationToken);
        Assert.NotNull(cancelled);
        Assert.Equal(cancel.OccurredAt, cancelled.CompletedAt);
        Assert.Null(cancelled.StartedAt);
        Assert.Equal(0, cancelled.ActivePhaseCount);
        Assert.Equal(
            WorkflowRunMutationStatus.IdempotentReplay,
            (await store.TransitionRunAsync(cancel, cancellationToken)).Status);
    }
}
