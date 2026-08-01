using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

internal static class DocumentStoreBehavior
{
    public static async Task AssertAsync(
        IDocumentStore store,
        IDocumentCatalogStore catalog,
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
        var versionPage = await catalog.PageVersionsAsync(
            command.TenantId, command.DocumentId, 0, 1, cancellationToken);
        Assert.Single(versionPage.Items); Assert.Equal(2, versionPage.Total);
        Assert.Equal(append.DocumentVersionId, versionPage.Items[0].Id);
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
        var catalogPage = await catalog.PageDocumentsAsync(command.TenantId, new(
            command.ProjectId, "%architecture%", command.Kind, "in_elaboration", "Review",
            "governance", false, true, 0, 15), cancellationToken);
        Assert.Single(catalogPage.Items); Assert.Equal(1, catalogPage.Total);
        Assert.Equal(command.DocumentId, catalogPage.Items[0].Id);

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

        var firstApproval = new DocumentApprovalRequestCommand(
            command.TenantId,
            command.DocumentId,
            "01ARZ3NDEKTSV4RRFFQ69G5FZG",
            "01ARZ3NDEKTSV4RRFFQ69G5FZF",
            "Approve architecture decision",
            "Critic must review the current immutable document version.",
            "high",
            append.OccurredAt.AddDays(1),
            "01ARZ3NDEKTSV4RRFFQ69G5FZJ",
            ExpectedDocumentVersion: 4,
            "document:approval:first-request",
            append.OccurredAt.AddMinutes(6));
        var firstApprovalResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                store.RequestApprovalAsync(firstApproval, cancellationToken)));
        Assert.Single(
            firstApprovalResults,
            receipt => receipt.Status == DocumentMutationStatus.Applied);
        Assert.Equal(
            9,
            firstApprovalResults.Count(receipt =>
                receipt.Status == DocumentMutationStatus.IdempotentReplay));
        Assert.All(firstApprovalResults, receipt =>
        {
            Assert.Equal(5, receipt.DocumentVersion);
            Assert.Equal("awaiting_approval", receipt.State);
            Assert.Equal(firstApproval.ApprovalRequestId, receipt.ApprovalRequestId);
            Assert.Equal("pending", receipt.ApprovalState);
            Assert.NotNull(receipt.LedgerSequence);
            Assert.NotNull(receipt.OutboxMessageId);
        });
        var pendingSnapshot = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(pendingSnapshot);
        Assert.Equal(5, pendingSnapshot.Version);
        Assert.Equal("awaiting_approval", pendingSnapshot.State);
        var pending = Assert.Single(pendingSnapshot.ApprovalRequests);
        Assert.Equal(firstApproval.ApprovalRequestId, pending.ApprovalRequestId);
        Assert.Equal(append.DocumentVersionId, pending.DocumentVersionId);
        Assert.Equal("pending", pending.State);
        Assert.Equal(1, pending.Version);
        Assert.Equal(2, pendingSnapshot.StateTransitions.Count);
        var approvalPage = await catalog.PageApprovalsAsync(command.TenantId, new(
            command.ProjectId, null, "pending", "high", "week", append.OccurredAt, 0, 1),
            cancellationToken);
        Assert.Single(approvalPage.Items); Assert.True(approvalPage.Total >= 1);
        Assert.Equal(firstApproval.ApprovalRequestId, approvalPage.Items[0].Id);

        var duplicatePending = firstApproval with
        {
            ApprovalRequestId = "01ARZ3NDEKTSV4RRFFQ69G5FZE",
            TransitionId = "01ARZ3NDEKTSV4RRFFQ69G5FZD",
            ExpectedDocumentVersion = 5,
            IdempotencyKey = "document:approval:duplicate-pending",
            OccurredAt = append.OccurredAt.AddMinutes(7),
        };
        var duplicatePendingResult = await store.RequestApprovalAsync(
            duplicatePending,
            cancellationToken);
        Assert.Equal(
            DocumentMutationStatus.ApprovalAlreadyPending,
            duplicatePendingResult.Status);
        Assert.Null(duplicatePendingResult.LedgerSequence);
        Assert.Null(duplicatePendingResult.OutboxMessageId);

        var cancel = new DocumentApprovalCancelCommand(
            command.TenantId,
            command.DocumentId,
            firstApproval.ApprovalRequestId,
            "01ARZ3NDEKTSV4RRFFQ69G5FZC",
            "Scope changed before review",
            "system",
            ActorId: null,
            ExpectedDocumentVersion: 5,
            "document:approval:first-cancel",
            append.OccurredAt.AddMinutes(8));
        var cancelResult = await store.CancelApprovalAsync(cancel, cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, cancelResult.Status);
        Assert.Equal(6, cancelResult.DocumentVersion);
        Assert.Equal("in_review", cancelResult.State);
        Assert.Equal("cancelled", cancelResult.ApprovalState);
        Assert.NotNull(cancelResult.LedgerSequence);
        var cancelledSnapshot = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(cancelledSnapshot);
        Assert.Equal("cancelled", Assert.Single(cancelledSnapshot.ApprovalRequests).State);
        Assert.Equal(3, cancelledSnapshot.StateTransitions.Count);

        var secondApproval = firstApproval with
        {
            ApprovalRequestId = "01ARZ3NDEKTSV4RRFFQ69G5FZB",
            TransitionId = "01ARZ3NDEKTSV4RRFFQ69G5FZA",
            ExpectedDocumentVersion = 6,
            IdempotencyKey = "document:approval:second-request",
            OccurredAt = append.OccurredAt.AddMinutes(9),
        };
        var secondRequestResult = await store.RequestApprovalAsync(
            secondApproval,
            cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, secondRequestResult.Status);
        Assert.Equal(7, secondRequestResult.DocumentVersion);

        var missingNote = new DocumentApprovalResolveCommand(
            command.TenantId,
            command.DocumentId,
            secondApproval.ApprovalRequestId,
            "01ARZ3NDEKTSV4RRFFQ69G5FYZ",
            "rejected",
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            Note: null,
            ExpectedDocumentVersion: 7,
            "document:approval:reject-missing-note",
            append.OccurredAt.AddMinutes(10));
        var missingNoteResult = await store.ResolveApprovalAsync(
            missingNote,
            cancellationToken);
        Assert.Equal(
            DocumentMutationStatus.RejectionNoteRequired,
            missingNoteResult.Status);
        Assert.Null(missingNoteResult.LedgerSequence);
        Assert.Null(missingNoteResult.OutboxMessageId);
        Assert.Equal(
            DocumentMutationStatus.IdempotentReplay,
            (await store.ResolveApprovalAsync(missingNote, cancellationToken)).Status);

        var rejection = missingNote with
        {
            TransitionId = "01ARZ3NDEKTSV4RRFFQ69G5FYY",
            Note = "Evidence does not cover the failure mode.",
            IdempotencyKey = "document:approval:reject-with-note",
            OccurredAt = append.OccurredAt.AddMinutes(11),
        };
        var rejectionResult = await store.ResolveApprovalAsync(rejection, cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, rejectionResult.Status);
        Assert.Equal(8, rejectionResult.DocumentVersion);
        Assert.Equal("in_elaboration", rejectionResult.State);
        Assert.Equal("rejected", rejectionResult.ApprovalState);

        var correctedVersion = append with
        {
            DocumentVersionId = "01ARZ3NDEKTSV4RRFFQ69G5FYX",
            CatalogPath = "docs/architecture/context-map-v3.md",
            ContentHash = new string('F', 64),
            ExpectedDocumentVersion = 8,
            IdempotencyKey = "document:append:context-map-v3",
            OccurredAt = append.OccurredAt.AddMinutes(12),
        };
        var correctionResult = await store.AppendVersionAsync(
            correctedVersion,
            cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, correctionResult.Status);
        Assert.Equal(9, correctionResult.DocumentVersion);
        Assert.Equal(3, correctionResult.CurrentVersion);

        var correctedReview = transition with
        {
            TransitionId = "01ARZ3NDEKTSV4RRFFQ69G5FYW",
            ExpectedDocumentVersion = 9,
            IdempotencyKey = "document:transition:corrected-review",
            OccurredAt = append.OccurredAt.AddMinutes(13),
        };
        var correctedReviewResult = await store.TransitionAsync(
            correctedReview,
            cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, correctedReviewResult.Status);
        Assert.Equal(10, correctedReviewResult.DocumentVersion);

        var finalApproval = firstApproval with
        {
            ApprovalRequestId = "01ARZ3NDEKTSV4RRFFQ69G5FYV",
            TransitionId = "01ARZ3NDEKTSV4RRFFQ69G5FYT",
            ExpectedDocumentVersion = 10,
            IdempotencyKey = "document:approval:final-request",
            OccurredAt = append.OccurredAt.AddMinutes(14),
        };
        var finalRequestResult = await store.RequestApprovalAsync(
            finalApproval,
            cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, finalRequestResult.Status);
        Assert.Equal(11, finalRequestResult.DocumentVersion);

        var approval = new DocumentApprovalResolveCommand(
            command.TenantId,
            command.DocumentId,
            finalApproval.ApprovalRequestId,
            "01ARZ3NDEKTSV4RRFFQ69G5FYS",
            "approved",
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            "Reviewed against the current evidence.",
            ExpectedDocumentVersion: 11,
            "document:approval:final-approve",
            append.OccurredAt.AddMinutes(15));
        var approvalResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                store.ResolveApprovalAsync(approval, cancellationToken)));
        Assert.Single(
            approvalResults,
            receipt => receipt.Status == DocumentMutationStatus.Applied);
        Assert.Equal(
            9,
            approvalResults.Count(receipt =>
                receipt.Status == DocumentMutationStatus.IdempotentReplay));
        Assert.All(approvalResults, receipt =>
        {
            Assert.Equal(12, receipt.DocumentVersion);
            Assert.Equal("approved", receipt.State);
            Assert.Equal("approved", receipt.ApprovalState);
            Assert.NotNull(receipt.LedgerSequence);
            Assert.NotNull(receipt.OutboxMessageId);
        });

        var approved = await store.ReadAsync(
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        Assert.NotNull(approved);
        Assert.Equal(12, approved.Version);
        Assert.Equal("approved", approved.State);
        Assert.Equal(3, approved.CurrentVersion);
        Assert.Equal(3, approved.Versions.Count);
        Assert.Equal(3, approved.ApprovalRequests.Count);
        Assert.Equal(
            ["cancelled", "rejected", "approved"],
            approved.ApprovalRequests.Select(item => item.State));
        Assert.All(approved.ApprovalRequests, item => Assert.Equal(2, item.Version));
        Assert.Equal(8, approved.StateTransitions.Count);
        Assert.Equal(
            [
                "in_review",
                "awaiting_approval",
                "in_review",
                "awaiting_approval",
                "in_elaboration",
                "in_review",
                "awaiting_approval",
                "approved",
            ],
            approved.StateTransitions.Select(item => item.ToState));

        await AssertManualEditRebindsPendingApprovalAsync(
            store, command.TenantId, command.ProjectId, cancellationToken);

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
        new DateTimeOffset(2026, 7, 18, 17, 0, 0, TimeSpan.Zero),
        "01ARZ3NDEKTSV4RRFFQ69G5FF2");

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

    private static async Task AssertManualEditRebindsPendingApprovalAsync(
        IDocumentStore store, string tenantId, string projectId,
        CancellationToken cancellationToken)
    {
        var at = new DateTimeOffset(2026, 7, 18, 19, 0, 0, TimeSpan.Zero);
        string Id(int offset) => UlidValue.New(at.AddMilliseconds(offset)).ToString();
        var documentId = Id(1); var initialVersionId = Id(2); var editedVersionId = Id(3);
        var approvalId = Id(4);
        await store.CreateAsync(new(
            tenantId, projectId, documentId, "Manual approval edit", "spec", [], null,
            initialVersionId, $"docs/manual/{documentId}-v1.md", new string('A', 64),
            "agent", Id(5), $"document:create:{documentId}", at, Id(13)), cancellationToken);
        var review = await store.TransitionAsync(new(
            tenantId, documentId, Id(6), "in_review", null, "user", Id(7), 1,
            $"document:review:{documentId}", at.AddMinutes(1)), cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, review.Status);
        var requested = await store.RequestApprovalAsync(new(
            tenantId, documentId, approvalId, Id(8), "Approve manual edit", "Review",
            "high", null, Id(9), 2, $"document:approval:{documentId}",
            at.AddMinutes(2)), cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, requested.Status);
        var appended = await store.AppendVersionAsync(new(
            tenantId, documentId, editedVersionId, $"docs/manual/{documentId}-v2.md",
            new string('B', 64), "user", Id(10), 3,
            $"document:append:{documentId}", at.AddMinutes(3)), cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, appended.Status);
        var edited = await store.ReadAsync(tenantId, documentId, cancellationToken);
        Assert.NotNull(edited);
        Assert.Equal("awaiting_approval", edited.State);
        Assert.Equal(2, edited.CurrentVersion);
        var pending = Assert.Single(edited.ApprovalRequests);
        Assert.Equal(editedVersionId, pending.DocumentVersionId);
        Assert.Equal(2, pending.Version);
        var resolved = await store.ResolveApprovalAsync(new(
            tenantId, documentId, approvalId, Id(11), "approved", Id(12),
            "Saved and approved by reviewer.", 4, $"document:resolve:{documentId}",
            at.AddMinutes(4)), cancellationToken);
        Assert.Equal(DocumentMutationStatus.Applied, resolved.Status);
        Assert.Equal("approved", resolved.State);
    }
}
