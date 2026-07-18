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
        var createReceipt = Assert.Single(concurrentResults, receipt => !receipt.Replay);

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

        var append = AppendCommand();
        var appendResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                store.AppendVersionAsync(append, cancellationToken)));
        var appliedAppend = Assert.Single(
            appendResults,
            receipt => receipt.Status == DocumentMutationStatus.Applied);
        Assert.Equal(
            9,
            appendResults.Count(receipt =>
                receipt.Status == DocumentMutationStatus.IdempotentReplay));
        Assert.All(appendResults, receipt =>
        {
            Assert.Equal(command.DocumentId, receipt.DocumentId);
            Assert.Equal(2, receipt.DocumentVersion);
            Assert.Equal("in_elaboration", receipt.State);
            Assert.Equal(2, receipt.CurrentVersion);
            Assert.Equal(append.DocumentVersionId, receipt.DocumentVersionId);
            Assert.NotNull(receipt.LedgerSequence);
            Assert.Equal(64, receipt.LedgerHash?.Length);
            Assert.Equal(26, receipt.OutboxMessageId?.Length);
        });
        Assert.True(appliedAppend.LedgerSequence > createReceipt.LedgerSequence);
        Assert.Single(appendResults.Select(receipt => receipt.LedgerSequence).Distinct());
        Assert.Single(appendResults.Select(receipt => receipt.LedgerHash).Distinct());
        Assert.Single(appendResults.Select(receipt => receipt.OutboxMessageId).Distinct());

        var versioned = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(versioned);
        Assert.Equal(2, versioned.Version);
        Assert.Equal(2, versioned.CurrentVersion);
        Assert.Equal(2, versioned.Versions.Count);
        var secondVersion = versioned.Versions[1];
        Assert.Equal(append.DocumentVersionId, secondVersion.DocumentVersionId);
        Assert.Equal(2, secondVersion.Version);
        Assert.Equal(append.CatalogPath, secondVersion.CatalogPath);
        Assert.Equal(append.ContentHash, secondVersion.ContentHash);
        Assert.Equal(command.DocumentVersionId, secondVersion.SupersedesId);
        Assert.Equal(append.AuthorKind, secondVersion.AuthorKind);
        Assert.Equal(append.AuthorId, secondVersion.AuthorId);
        Assert.Equal(append.OccurredAt, secondVersion.CreatedAt);

        var stale = append with
        {
            DocumentVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FZS",
            CatalogPath = "docs/architecture/context-map-v3.md",
            ContentHash = new string('F', 64),
            IdempotencyKey = "document:append:stale",
            OccurredAt = append.OccurredAt.AddMinutes(1),
        };
        var staleResult = await store.AppendVersionAsync(stale, cancellationToken);
        Assert.Equal(DocumentMutationStatus.VersionConflict, staleResult.Status);
        Assert.Equal(2, staleResult.DocumentVersion);
        Assert.Equal(2, staleResult.CurrentVersion);
        Assert.Null(staleResult.LedgerSequence);
        Assert.Null(staleResult.LedgerHash);
        Assert.Null(staleResult.OutboxMessageId);
        Assert.Equal(
            DocumentMutationStatus.IdempotentReplay,
            (await store.AppendVersionAsync(stale, cancellationToken)).Status);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.AppendVersionAsync(
                stale with { CatalogPath = "docs/architecture/different.md" },
                cancellationToken));
        var afterStale = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(afterStale);
        Assert.Equal(2, afterStale.Version);
        Assert.Equal(2, afterStale.Versions.Count);

        var missing = append with
        {
            DocumentId = "01ARZ3NDEKTSV4RRFFQ69G5FZN",
            DocumentVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FZM",
            IdempotencyKey = "document:append:missing",
            OccurredAt = append.OccurredAt.AddMinutes(2),
        };
        var missingResult = await store.AppendVersionAsync(missing, cancellationToken);
        Assert.Equal(DocumentMutationStatus.NotFound, missingResult.Status);
        Assert.Null(missingResult.LedgerSequence);
        Assert.Null(missingResult.OutboxMessageId);
        Assert.Equal(
            DocumentMutationStatus.IdempotentReplay,
            (await store.AppendVersionAsync(missing, cancellationToken)).Status);

        var metadata = new DocumentMetadataUpdateCommand(
            command.TenantId,
            command.DocumentId,
            ["architecture", "decision", "governance"],
            "Review",
            Inconsistent: true,
            ExpectedDocumentVersion: 2,
            "document:metadata:adopt-review",
            append.OccurredAt.AddMinutes(3));
        var metadataResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                store.UpdateMetadataAsync(metadata, cancellationToken)));
        Assert.Single(
            metadataResults,
            receipt => receipt.Status == DocumentMutationStatus.Applied);
        Assert.Equal(
            9,
            metadataResults.Count(receipt =>
                receipt.Status == DocumentMutationStatus.IdempotentReplay));
        Assert.All(metadataResults, receipt =>
        {
            Assert.Equal(3, receipt.DocumentVersion);
            Assert.Equal(2, receipt.CurrentVersion);
            Assert.Equal("in_elaboration", receipt.State);
            Assert.NotNull(receipt.LedgerSequence);
            Assert.NotNull(receipt.OutboxMessageId);
        });
        var classified = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(classified);
        Assert.Equal(3, classified.Version);
        Assert.Equal("Review", classified.PhaseName);
        Assert.True(classified.Inconsistent);
        Assert.Equal(metadata.Classifications, classified.Classifications);
        Assert.Empty(classified.StateTransitions);

        var transition = new DocumentTransitionCommand(
            command.TenantId,
            command.DocumentId,
            "01ARZ3NDEKTSV4RRFFQ69G5FZK",
            "in_review",
            "Ready for critic review",
            "agent",
            "01ARZ3NDEKTSV4RRFFQ69G5FZJ",
            ExpectedDocumentVersion: 3,
            "document:transition:review",
            append.OccurredAt.AddMinutes(4));
        var transitionResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                store.TransitionAsync(transition, cancellationToken)));
        Assert.Single(
            transitionResults,
            receipt => receipt.Status == DocumentMutationStatus.Applied);
        Assert.Equal(
            9,
            transitionResults.Count(receipt =>
                receipt.Status == DocumentMutationStatus.IdempotentReplay));
        Assert.All(transitionResults, receipt =>
        {
            Assert.Equal(4, receipt.DocumentVersion);
            Assert.Equal(2, receipt.CurrentVersion);
            Assert.Equal("in_review", receipt.State);
            Assert.NotNull(receipt.LedgerSequence);
            Assert.NotNull(receipt.OutboxMessageId);
        });
        var inReview = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(inReview);
        Assert.Equal(4, inReview.Version);
        Assert.Equal("in_review", inReview.State);
        Assert.Equal(2, inReview.Versions.Count);
        var transitionSnapshot = Assert.Single(inReview.StateTransitions);
        Assert.Equal(transition.TransitionId, transitionSnapshot.TransitionId);
        Assert.Equal(4, transitionSnapshot.DocumentVersion);
        Assert.Equal("in_elaboration", transitionSnapshot.FromState);
        Assert.Equal("in_review", transitionSnapshot.ToState);
        Assert.Equal(transition.Note, transitionSnapshot.Note);
        Assert.Equal(transition.ActorKind, transitionSnapshot.ActorKind);
        Assert.Equal(transition.ActorId, transitionSnapshot.ActorId);
        Assert.Equal(transition.OccurredAt, transitionSnapshot.OccurredAt);

        var invalidTransition = transition with
        {
            TransitionId = "01ARZ3NDEKTSV4RRFFQ69G5FZH",
            TargetState = "approved",
            ExpectedDocumentVersion = 4,
            IdempotencyKey = "document:transition:invalid",
            OccurredAt = append.OccurredAt.AddMinutes(5),
        };
        var invalidResult = await store.TransitionAsync(invalidTransition, cancellationToken);
        Assert.Equal(DocumentMutationStatus.InvalidState, invalidResult.Status);
        Assert.Equal(4, invalidResult.DocumentVersion);
        Assert.Null(invalidResult.LedgerSequence);
        Assert.Null(invalidResult.OutboxMessageId);
        Assert.Equal(
            DocumentMutationStatus.IdempotentReplay,
            (await store.TransitionAsync(invalidTransition, cancellationToken)).Status);
        var afterInvalidTransition = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(afterInvalidTransition);
        Assert.Equal(4, afterInvalidTransition.Version);
        Assert.Single(afterInvalidTransition.StateTransitions);

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
        null,
        "01ARZ3NDEKTSV4RRFFQ69G5FZV",
        "docs/architecture/context-map.md",
        new string('D', 64),
        "agent",
        "01ARZ3NDEKTSV4RRFFQ69G5FZT",
        "document:create:architecture-decision",
        new DateTimeOffset(2026, 7, 18, 17, 0, 0, TimeSpan.Zero));

    private static DocumentVersionAppendCommand AppendCommand() => new(
        FoundationTransactionBehavior.TenantId,
        "01ARZ3NDEKTSV4RRFFQ69G5FZW",
        "01ARZ3NDEKTSV4RRFFQ69G5FZR",
        "docs/architecture/context-map-v2.md",
        new string('E', 64),
        "chief",
        "01ARZ3NDEKTSV4RRFFQ69G5FZP",
        1,
        "document:append:context-map-v2",
        new DateTimeOffset(2026, 7, 18, 17, 5, 0, TimeSpan.Zero));
}
