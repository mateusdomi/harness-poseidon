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
    DateTimeOffset CreatedAt,

    /// <summary>
    /// O PAPEL do artefato: por que ele foi fornecido. `provided_frontend` é a proveniência que um
    /// card de frontend referencia para saber que a interface EXISTE e deve ser evoluída, não
    /// reconstruída. Default `other` preserva anexos anteriores ao campo.
    /// </summary>
    string Role = "other");

public sealed record SolicitationAttachmentCreateCommand(
    string TenantId,
    string Id,
    string SolicitationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StoragePath,
    DateTimeOffset OccurredAt,
    string Role = "other");
