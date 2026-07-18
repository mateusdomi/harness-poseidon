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
    }
}
