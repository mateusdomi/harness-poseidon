using Harness.SharedKernel.Results;

namespace Harness.Modules.Documents.Domain.Documents;

public static class DocumentErrors
{
    public static ErrorDescriptor InvalidState { get; } =
        new("documents.document.invalidState", "Documents.Document.InvalidState");

    public static ErrorDescriptor ApprovalAlreadyPending { get; } =
        new("documents.approval.alreadyPending", "Documents.Approval.AlreadyPending");

    public static ErrorDescriptor ApprovalNotFound { get; } =
        new("documents.approval.notFound", "Documents.Approval.NotFound");

    public static ErrorDescriptor ApprovalAlreadyResolved { get; } =
        new("documents.approval.alreadyResolved", "Documents.Approval.AlreadyResolved");

    public static ErrorDescriptor RejectionNoteRequired { get; } =
        new("documents.approval.rejectionNoteRequired", "Documents.Approval.RejectionNoteRequired");
}
