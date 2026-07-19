namespace Harness.Persistence.Abstractions.Coordination;

public interface ISolicitationAttachmentStore
{
    Task<SolicitationAttachmentRecord> CreateAsync(
        SolicitationAttachmentCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SolicitationAttachmentRecord>> ListAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default);
}

public sealed record SolicitationAttachmentRecord(
    string TenantId,
    string Id,
    string SolicitationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string State,
    string StoragePath,
    DateTimeOffset CreatedAt);

public sealed record SolicitationAttachmentCreateCommand(
    string TenantId,
    string Id,
    string SolicitationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StoragePath,
    DateTimeOffset OccurredAt);
