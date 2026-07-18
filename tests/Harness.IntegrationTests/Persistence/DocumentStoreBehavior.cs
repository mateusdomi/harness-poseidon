using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;

namespace Harness.IntegrationTests.Persistence;

internal static class DocumentStoreBehavior
{
    public static async Task AssertAsync(
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        var command = Command();
        var concurrentResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => store.CreateAsync(command, cancellationToken)));

        Assert.Single(concurrentResults, receipt => !receipt.Replay);
        Assert.Equal(9, concurrentResults.Count(receipt => receipt.Replay));
        Assert.All(concurrentResults, receipt =>
        {
            Assert.Equal(command.DocumentId, receipt.DocumentId);
            Assert.Equal(command.DocumentVersionId, receipt.DocumentVersionId);
            Assert.Equal(1, receipt.DocumentVersion);
            Assert.True(receipt.LedgerSequence > 0);
            Assert.Equal(64, receipt.LedgerHash.Length);
            Assert.Equal(26, receipt.OutboxMessageId.Length);
        });
        Assert.Single(concurrentResults.Select(receipt => receipt.LedgerSequence).Distinct());
        Assert.Single(concurrentResults.Select(receipt => receipt.LedgerHash).Distinct());
        Assert.Single(concurrentResults.Select(receipt => receipt.OutboxMessageId).Distinct());

        var snapshot = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(command.TenantId, snapshot.TenantId);
        Assert.Equal(command.ProjectId, snapshot.ProjectId);
        Assert.Equal(command.DocumentId, snapshot.DocumentId);
        Assert.Equal(command.Title, snapshot.Title);
        Assert.Equal(command.Kind, snapshot.Kind);
        Assert.Equal("in_elaboration", snapshot.State);
        Assert.Equal(1, snapshot.CurrentVersion);
        Assert.Equal(command.PhaseName, snapshot.PhaseName);
        Assert.False(snapshot.Inconsistent);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(command.OccurredAt, snapshot.CreatedAt);
        Assert.Equal(command.OccurredAt, snapshot.UpdatedAt);
        Assert.Equal(command.Classifications, snapshot.Classifications);
        var version = Assert.Single(snapshot.Versions);
        Assert.Equal(command.DocumentVersionId, version.DocumentVersionId);
        Assert.Equal(1, version.Version);
        Assert.Equal(command.CatalogPath, version.CatalogPath);
        Assert.Equal(command.ContentHash, version.ContentHash);
        Assert.Null(version.SupersedesId);
        Assert.Equal(command.AuthorKind, version.AuthorKind);
        Assert.Equal(command.AuthorId, version.AuthorId);
        Assert.Equal(command.OccurredAt, version.CreatedAt);
        Assert.Empty(snapshot.ApprovalRequests);
        Assert.Empty(snapshot.StateTransitions);

        var conflict = command with { Title = "Different document" };
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => store.CreateAsync(conflict, cancellationToken));
        var unchanged = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(unchanged);
        Assert.Equal(snapshot.Version, unchanged.Version);
        Assert.Equal(snapshot.Title, unchanged.Title);
        Assert.Equal(snapshot.Versions, unchanged.Versions);
        Assert.Equal(snapshot.Classifications, unchanged.Classifications);

        Assert.Null(await store.ReadAsync(
            command.TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FZZ",
            cancellationToken));

        var traversal = command with
        {
            DocumentId = "01ARZ3NDEKTSV4RRFFQ69G5FZY",
            DocumentVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FZX",
            CatalogPath = "docs/../secret.md",
            IdempotencyKey = "document:create:traversal",
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAsync(traversal, cancellationToken));
    }

    public static DocumentCreateCommand Command() => new(
        FoundationTransactionBehavior.TenantId,
        FoundationTransactionBehavior.ProjectId,
        "01ARZ3NDEKTSV4RRFFQ69G5FZW",
        "Architecture decision record",
        "design",
        ["architecture", "governance"],
        "Foundation",
        "01ARZ3NDEKTSV4RRFFQ69G5FZV",
        "docs/architecture/context-map.md",
        new string('D', 64),
        "agent",
        "01ARZ3NDEKTSV4RRFFQ69G5FZT",
        "document:create:architecture-decision",
        new DateTimeOffset(2026, 7, 18, 17, 0, 0, TimeSpan.Zero));
}
