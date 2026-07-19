namespace Harness.Persistence.Abstractions.Prototyping;

public interface IVisualReferenceAssetStore
{
    Task<VisualReferenceAssetRecord> CreateAsync(
        VisualReferenceAssetCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VisualReferenceAssetRecord>> ListAsync(
        string tenantId,
        string referenceId,
        CancellationToken cancellationToken = default);
}

public sealed record VisualReferenceAssetRecord(
    string TenantId,
    string Id,
    string ReferenceId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StoragePath,
    DateTimeOffset CreatedAt);

public sealed record VisualReferenceAssetCreateCommand(
    string TenantId,
    string Id,
    string ReferenceId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StoragePath,
    DateTimeOffset OccurredAt);
