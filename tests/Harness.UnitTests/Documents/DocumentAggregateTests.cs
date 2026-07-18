using Harness.Modules.Documents.Contracts;
using Harness.Modules.Documents.Domain.Documents;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.UnitTests.Documents;

public sealed class DocumentAggregateTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string AgentId = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
    private const string ProfileId = "01ARZ3NDEKTSV4RRFFQ69G5FAZ";
    private static readonly string HashA = new('A', 64);
    private static readonly string HashB = new('B', 64);

    [Fact]
    public void CreationCatalogsImmutableInitialVersionAndMarksOrphan()
    {
        var classifications = new[] { "requirements" };
        var document = CreateDocument(classifications: classifications);
        classifications[0] = "mutated";

        Assert.Equal(DocumentState.InElaboration, document.State);
        Assert.Equal(1, document.Version);
        Assert.Equal(1, document.CurrentVersion);
        Assert.True(document.IsOrphan);
        Assert.Equal(["requirements"], document.Classifications);
        var version = Assert.Single(document.Versions);
        Assert.Equal(1, version.Version);
        Assert.Equal("documents/spec-v1.md", version.CatalogPath);
        Assert.Equal(HashA, version.ContentHash);
        Assert.Null(version.SupersedesId);
        Assert.Equal(DocumentAuthorKind.Agent, version.AuthorKind);
        Assert.Equal(AgentId, version.AuthorId);
    }

    [Fact]
    public void ClassificationAdoptsOrphanAndNormalizesLabels()
    {
        var document = CreateDocument();

        document.Classify(["ux", "architecture"], "Analysis");

        Assert.False(document.IsOrphan);
        Assert.Equal("Analysis", document.PhaseName);
        Assert.Equal(["architecture", "ux"], document.Classifications);
        Assert.Equal(2, document.Version);
        Assert.Throws<ArgumentException>(() =>
            document.Classify(["duplicate", "duplicate"], "Analysis"));
        Assert.Equal(["architecture", "ux"], document.Classifications);
    }

    [Fact]
    public void CorrectionCreatesNewImmutableVersionLinkedToPrevious()
    {
        var document = CreateDocument();
        var first = document.Versions[0];

        var appended = document.AppendVersion(
            "documents/spec-v2.md", HashB, DocumentAuthorKind.User, ProfileId);

        Assert.True(appended.IsSuccess);
        Assert.Equal(2, document.CurrentVersion);
        Assert.Equal(2, document.Version);
        Assert.Equal(first.Id, appended.Value.SupersedesId);
        Assert.Equal(HashA, first.ContentHash);
        Assert.Equal("documents/spec-v1.md", first.CatalogPath);
        Assert.True(document.Transition(DocumentState.InReview).IsSuccess);
        Assert.Equal(
            DocumentErrors.InvalidState,
            document.AppendVersion(
                "documents/spec-v3.md", new string('C', 64),
                DocumentAuthorKind.Chief, AgentId).Error);
    }

    [Fact]
    public void ApprovalBindsCurrentVersionAndCanApproveOnlyOnce()
    {
        var document = CreateDocument();
        Assert.True(document.Transition(DocumentState.InReview).IsSuccess);

        var requested = document.RequestApproval(
            "Approve spec",
            "Validate the current specification.",
            DocumentApprovalPriority.High,
            NewInstant().AddDays(2),
            AgentId);

        Assert.True(requested.IsSuccess);
        Assert.Equal(DocumentState.AwaitingApproval, document.State);
        Assert.Equal(DocumentApprovalState.Pending, requested.Value.State);
        Assert.Equal(document.Versions[^1].Id, requested.Value.DocumentVersionId);
        Assert.Equal(
            DocumentErrors.ApprovalAlreadyPending,
            document.RequestApproval(
                "Duplicate", "Duplicate", DocumentApprovalPriority.Low,
                null, AgentId).Error);

        var resolved = document.ResolveApproval(
            requested.Value.Id,
            DocumentApprovalDecision.Approved,
            ProfileId,
            note: null);

        Assert.True(resolved.IsSuccess);
        Assert.Equal(DocumentState.Approved, document.State);
        Assert.Equal(DocumentApprovalState.Approved, requested.Value.State);
        Assert.Equal(ProfileId, requested.Value.ResolvedByProfileId);
        Assert.Equal(
            DocumentErrors.ApprovalAlreadyResolved,
            document.ResolveApproval(
                requested.Value.Id,
                DocumentApprovalDecision.Approved,
                ProfileId,
                note: null).Error);
        Assert.True(document.Transition(DocumentState.Outdated).IsSuccess);
    }

    [Fact]
    public void RejectionRequiresNoteAndCorrectionUsesNewApproval()
    {
        var document = CreateDocument();
        document.Transition(DocumentState.InReview);
        var firstApproval = document.RequestApproval(
            "Approve", "Review", DocumentApprovalPriority.Medium, null, AgentId).Value;

        var missingNote = document.ResolveApproval(
            firstApproval.Id,
            DocumentApprovalDecision.Rejected,
            ProfileId,
            note: null);
        var rejected = document.ResolveApproval(
            firstApproval.Id,
            DocumentApprovalDecision.Rejected,
            ProfileId,
            "Missing acceptance criterion");

        Assert.Equal(DocumentErrors.RejectionNoteRequired, missingNote.Error);
        Assert.True(rejected.IsSuccess);
        Assert.Equal(DocumentState.InElaboration, document.State);
        Assert.Equal(DocumentApprovalState.Rejected, firstApproval.State);
        Assert.True(document.AppendVersion(
            "documents/spec-v2.md", HashB, DocumentAuthorKind.Agent, AgentId).IsSuccess);
        document.Transition(DocumentState.InReview);
        var secondApproval = document.RequestApproval(
            "Approve v2", "Review correction", DocumentApprovalPriority.Medium,
            null, AgentId).Value;
        Assert.NotEqual(firstApproval.Id, secondApproval.Id);
        Assert.Equal(document.Versions[^1].Id, secondApproval.DocumentVersionId);

        Assert.True(document.CancelApproval(secondApproval.Id, "Requirement withdrawn").IsSuccess);
        Assert.Equal(DocumentApprovalState.Cancelled, secondApproval.State);
        Assert.Equal(DocumentState.InReview, document.State);
    }

    [Fact]
    public void InvalidStateTransitionsAndUnknownApprovalsAreRejected()
    {
        var document = CreateDocument();

        Assert.Equal(
            DocumentErrors.InvalidState,
            document.Transition(DocumentState.Approved).Error);
        Assert.Equal(
            DocumentErrors.InvalidState,
            document.RequestApproval(
                "Approve", "Review", DocumentApprovalPriority.Low, null, AgentId).Error);
        Assert.Equal(
            DocumentErrors.ApprovalNotFound,
            document.ResolveApproval(
                EntityId<DocumentApprovalRequestTag>.Parse("01ARZ3NDEKTSV4RRFFQ69G5FBA"),
                DocumentApprovalDecision.Approved,
                ProfileId,
                null).Error);
    }

    [Fact]
    public void CatalogReferencesRejectTraversalAndNonCanonicalHashes()
    {
        Assert.Throws<ArgumentException>(() => CreateDocument(catalogPath: "../secret.md"));
        Assert.Throws<ArgumentException>(() => CreateDocument(catalogPath: "/tmp/secret.md"));
        Assert.Throws<ArgumentException>(() => CreateDocument(contentHash: new string('a', 64)));
        Assert.Throws<ArgumentException>(() => CreateDocument(contentHash: "ABC"));
        Assert.Throws<ArgumentException>(() => CreateDocument(authorId: "not-an-ulid"));

        var document = CreateDocument();
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RequestApproval(
            "Approve",
            "Review",
            DocumentApprovalPriority.Low,
            NewInstant().AddDays(-1),
            AgentId));
    }

    [Fact]
    public void InconsistencyIsObjectiveMetadataAndDoesNotRewriteContent()
    {
        var document = CreateDocument();
        var version = document.Versions[0];

        document.SetInconsistent(true);
        document.SetInconsistent(false);

        Assert.False(document.Inconsistent);
        Assert.Equal(3, document.Version);
        Assert.Same(version, document.Versions[0]);
        Assert.Equal(1, document.CurrentVersion);
    }

    private static DocumentAggregate CreateDocument(
        IReadOnlyList<string>? classifications = null,
        string? phaseName = null,
        string catalogPath = "documents/spec-v1.md",
        string? contentHash = null,
        string? authorId = AgentId) =>
        DocumentAggregate.Create(
            TenantId,
            ProjectId,
            "Delivery specification",
            DocumentKind.Spec,
            classifications ?? [],
            phaseName,
            catalogPath,
            contentHash ?? HashA,
            DocumentAuthorKind.Agent,
            authorId,
            new IncrementingClock(NewInstant()));

    private static DateTimeOffset NewInstant() =>
        new(2026, 7, 18, 17, 0, 0, TimeSpan.Zero);

    private sealed class IncrementingClock(DateTimeOffset initial) : IClock
    {
        private DateTimeOffset _current = initial;

        public DateTimeOffset UtcNow
        {
            get
            {
                var value = _current;
                _current = _current.AddMilliseconds(1);
                return value;
            }
        }
    }
}
