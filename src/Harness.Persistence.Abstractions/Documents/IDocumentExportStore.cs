namespace Harness.Persistence.Abstractions.Documents;

/// <summary>
/// Registro append-only das exportações de documentos. Cada pacote que sai do
/// produto é um fato: quem pediu, o que saiu e quando. Não há atualização nem
/// remoção — exportar de novo é outro fato, não a correção do anterior.
/// </summary>
public interface IDocumentExportStore
{
    Task RecordAsync(
        DocumentExportRecordCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Histórico do projeto, do mais recente para o mais antigo.</summary>
    Task<IReadOnlyList<DocumentExportRecord>> ListAsync(
        string tenantId,
        string projectId,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed record DocumentExportRecordCommand(
    string TenantId,
    string ExportId,
    string ProjectId,
    int DocumentCount,
    string RequestedByProfileId,
    string ManifestJson,
    DateTimeOffset OccurredAt);

public sealed record DocumentExportRecord(
    string ExportId,
    string ProjectId,
    int DocumentCount,
    string RequestedByProfileId,
    string ManifestJson,
    DateTimeOffset CreatedAt);
