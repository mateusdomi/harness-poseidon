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
        Assert.Null(await store.ReadAsync(
            command.TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FF4",
            cancellationToken));
    }
}
