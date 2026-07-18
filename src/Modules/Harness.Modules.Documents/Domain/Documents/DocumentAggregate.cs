using System.Collections.ObjectModel;
using Harness.Modules.Documents.Contracts;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Results;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Documents.Domain.Documents;

public sealed class DocumentAggregate
{
    private readonly IClock _clock;
    private readonly List<string> _classifications;
    private readonly List<DocumentVersion> _versions = [];
    private readonly List<DocumentApprovalRequest> _approvalRequests = [];

    private DocumentAggregate(
        EntityId<DocumentTag> id,
        string tenantId,
        string projectId,
        string title,
        DocumentKind kind,
        IReadOnlyList<string> classifications,
        string? phaseName,
        IClock clock)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        Title = title;
        Kind = kind;
        _classifications = [.. classifications];
        PhaseName = phaseName;
        _clock = clock;
        State = DocumentState.InElaboration;
        CreatedAt = clock.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
        Classifications = new ReadOnlyCollection<string>(_classifications);
        Versions = new ReadOnlyCollection<DocumentVersion>(_versions);
        ApprovalRequests = new ReadOnlyCollection<DocumentApprovalRequest>(_approvalRequests);
    }

    public EntityId<DocumentTag> Id { get; }

    public string TenantId { get; }

    public string ProjectId { get; }

    public string Title { get; }

    public DocumentKind Kind { get; }

    public DocumentState State { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<string> Classifications { get; }

    public string? PhaseName { get; private set; }

    public bool IsOrphan => PhaseName is null;

    public bool Inconsistent { get; private set; }

    public int CurrentVersion => _versions.Count;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<DocumentVersion> Versions { get; }

    public IReadOnlyList<DocumentApprovalRequest> ApprovalRequests { get; }

    public static DocumentAggregate Create(
        string tenantId,
        string projectId,
        string title,
        DocumentKind kind,
        IReadOnlyList<string> classifications,
        string? phaseName,
        string catalogPath,
        string contentHash,
        DocumentAuthorKind authorKind,
        string? authorId,
        IClock clock)
    {
        ValidateUlid(tenantId, nameof(tenantId));
        ValidateUlid(projectId, nameof(projectId));
        ValidateText(title, nameof(title), 500);
        ValidateEnum(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(clock);
        var normalizedClassifications = NormalizeClassifications(classifications);
        var normalizedPhase = NormalizeOptionalText(phaseName, nameof(phaseName), 200);
        ValidateVersionReference(catalogPath, contentHash, authorKind, authorId);

        var document = new DocumentAggregate(
            DocumentIdFactory.New<DocumentTag>(clock),
            tenantId,
            projectId,
            title,
            kind,
            normalizedClassifications,
            normalizedPhase,
            clock);
        document.AppendVersionCore(catalogPath, contentHash, authorKind, authorId);
        return document;
    }

    public Result<DocumentVersion> AppendVersion(
        string catalogPath,
        string contentHash,
        DocumentAuthorKind authorKind,
        string? authorId)
    {
        ValidateVersionReference(catalogPath, contentHash, authorKind, authorId);
        if (State != DocumentState.InElaboration)
        {
            return Result<DocumentVersion>.Failure(DocumentErrors.InvalidState);
        }

        var version = AppendVersionCore(catalogPath, contentHash, authorKind, authorId);
        Touch();
        return Result<DocumentVersion>.Success(version);
    }

    public void Classify(IReadOnlyList<string> classifications, string? phaseName)
    {
        var normalized = NormalizeClassifications(classifications);
        var normalizedPhase = NormalizeOptionalText(phaseName, nameof(phaseName), 200);
        _classifications.Clear();
        _classifications.AddRange(normalized);
        PhaseName = normalizedPhase;
        Touch();
    }

    public void SetInconsistent(bool inconsistent)
    {
        Inconsistent = inconsistent;
        Touch();
    }

    public Result Transition(DocumentState targetState)
    {
        ValidateEnum(targetState, nameof(targetState));
        if (!CanTransition(State, targetState))
        {
            return Result.Failure(DocumentErrors.InvalidState);
        }

        State = targetState;
        Touch();
        return Result.Success();
    }

    public Result<DocumentApprovalRequest> RequestApproval(
        string title,
        string description,
        DocumentApprovalPriority priority,
        DateTimeOffset? dueAt,
        string requestedByAgentId)
    {
        ValidateText(title, nameof(title), 500);
        ValidateText(description, nameof(description), 10_000);
        ValidateEnum(priority, nameof(priority));
        ValidateUlid(requestedByAgentId, nameof(requestedByAgentId));
        if (dueAt is not null && dueAt <= _clock.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(dueAt), "Approval due date must be in the future.");
        }

        if (_approvalRequests.Any(request => request.State == DocumentApprovalState.Pending))
        {
            return Result<DocumentApprovalRequest>.Failure(DocumentErrors.ApprovalAlreadyPending);
        }

        if (State != DocumentState.InReview)
        {
            return Result<DocumentApprovalRequest>.Failure(DocumentErrors.InvalidState);
        }

        var request = new DocumentApprovalRequest(
            DocumentIdFactory.New<DocumentApprovalRequestTag>(_clock),
            Id,
            _versions[^1].Id,
            title,
            description,
            priority,
            dueAt,
            requestedByAgentId,
            _clock.UtcNow);
        _approvalRequests.Add(request);
        State = DocumentState.AwaitingApproval;
        Touch();
        return Result<DocumentApprovalRequest>.Success(request);
    }

    public Result ResolveApproval(
        EntityId<DocumentApprovalRequestTag> approvalRequestId,
        DocumentApprovalDecision decision,
        string resolvedByProfileId,
        string? note)
    {
        ValidateEnum(decision, nameof(decision));
        ValidateUlid(resolvedByProfileId, nameof(resolvedByProfileId));
        var request = _approvalRequests.SingleOrDefault(candidate => candidate.Id == approvalRequestId);
        if (request is null)
        {
            return Result.Failure(DocumentErrors.ApprovalNotFound);
        }

        if (request.State != DocumentApprovalState.Pending)
        {
            return Result.Failure(DocumentErrors.ApprovalAlreadyResolved);
        }

        if (decision == DocumentApprovalDecision.Rejected && string.IsNullOrWhiteSpace(note))
        {
            return Result.Failure(DocumentErrors.RejectionNoteRequired);
        }

        request.State = decision == DocumentApprovalDecision.Approved
            ? DocumentApprovalState.Approved
            : DocumentApprovalState.Rejected;
        request.ResolvedByProfileId = resolvedByProfileId;
        request.ResolvedAt = _clock.UtcNow;
        request.ResolutionNote = string.IsNullOrWhiteSpace(note)
            ? null
            : NormalizeOptionalText(note, nameof(note), 10_000);
        State = decision == DocumentApprovalDecision.Approved
            ? DocumentState.Approved
            : DocumentState.InElaboration;
        Touch();
        return Result.Success();
    }

    public Result CancelApproval(
        EntityId<DocumentApprovalRequestTag> approvalRequestId,
        string reason)
    {
        ValidateText(reason, nameof(reason), 10_000);
        var request = _approvalRequests.SingleOrDefault(candidate => candidate.Id == approvalRequestId);
        if (request is null)
        {
            return Result.Failure(DocumentErrors.ApprovalNotFound);
        }

        if (request.State != DocumentApprovalState.Pending)
        {
            return Result.Failure(DocumentErrors.ApprovalAlreadyResolved);
        }

        request.State = DocumentApprovalState.Cancelled;
        request.ResolvedAt = _clock.UtcNow;
        request.ResolutionNote = reason.Trim();
        State = DocumentState.InReview;
        Touch();
        return Result.Success();
    }

    private DocumentVersion AppendVersionCore(
        string catalogPath,
        string contentHash,
        DocumentAuthorKind authorKind,
        string? authorId)
    {
        var previous = _versions.LastOrDefault();
        var version = new DocumentVersion(
            DocumentIdFactory.New<DocumentVersionTag>(_clock),
            (previous?.Version ?? 0) + 1,
            catalogPath,
            contentHash,
            previous?.Id,
            authorKind,
            authorId,
            _clock.UtcNow);
        _versions.Add(version);
        return version;
    }

    private void Touch()
    {
        Version++;
        UpdatedAt = _clock.UtcNow;
    }

    private static bool CanTransition(DocumentState current, DocumentState target) =>
        (current, target) switch
        {
            (DocumentState.Planned, DocumentState.InElaboration or DocumentState.NotApplicable) => true,
            (DocumentState.InElaboration, DocumentState.InReview or DocumentState.NotApplicable) => true,
            (DocumentState.InReview, DocumentState.InElaboration) => true,
            (DocumentState.Approved, DocumentState.Outdated or DocumentState.Superseded) => true,
            _ => false,
        };

    private static string[] NormalizeClassifications(
        IReadOnlyList<string> classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        if (classifications.Count > 50)
        {
            throw new ArgumentException("A document cannot have more than 50 classifications.", nameof(classifications));
        }

        var result = classifications
            .Select(value =>
            {
                ValidateText(value, nameof(classifications), 100);
                return value.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (result.Length != classifications.Count)
        {
            throw new ArgumentException("Document classifications must be unique.", nameof(classifications));
        }

        return result;
    }

    private static string? NormalizeOptionalText(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        ValidateText(value, parameterName, maximumLength);
        return value.Trim();
    }

    private static void ValidateVersionReference(
        string catalogPath,
        string contentHash,
        DocumentAuthorKind authorKind,
        string? authorId)
    {
        ValidateText(catalogPath, nameof(catalogPath), 1_024);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash, nameof(contentHash));
        ValidateEnum(authorKind, nameof(authorKind));
        if (!string.Equals(catalogPath, catalogPath.Trim(), StringComparison.Ordinal) ||
            Path.IsPathRooted(catalogPath) || catalogPath.Contains('\\') ||
            catalogPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Catalog path must be a canonical relative path.", nameof(catalogPath));
        }

        if (contentHash.Length != 64 || contentHash.Any(character =>
            !char.IsAsciiHexDigit(character) || char.IsLower(character)))
        {
            throw new ArgumentException("Content hash must be a canonical SHA-256 hex value.", nameof(contentHash));
        }

        if (authorId is not null)
        {
            ValidateUlid(authorId, nameof(authorId));
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }
    }

    private static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private static void ValidateEnum<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
