using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Harness.Modules.Coordination.Application;

public sealed record MultimodalIntakeResult(
    string AssetId,
    string TenantId,
    string ResourceId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hash,
    string PreviewSnippet,
    string ExtractionStatus,
    bool IsAllowedType,
    string SecurityScanStatus,
    DateTimeOffset ProcessedAt);

public interface IMultimodalIntakeService
{
    Task<MultimodalIntakeResult> ProcessAttachmentAsync(
        string tenantId,
        string resourceId,
        string fileName,
        string contentType,
        byte[] content,
        long maxSizeBytes = 50 * 1024 * 1024,
        CancellationToken cancellationToken = default);
}

public sealed record ArtifactContentExtraction(string Status, string Text);

/// <summary>
/// Extração é uma fronteira substituível porque o Host produtivo a executa em sandbox. O fallback
/// local existe apenas para testes puros e só decodifica texto; jamais interpreta binário.
/// </summary>
public interface IArtifactContentExtractor
{
    Task<ArtifactContentExtraction> ExtractAsync(
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken = default);
}

public sealed class TextOnlyArtifactContentExtractor : IArtifactContentExtractor
{
    public Task<ArtifactContentExtraction> ExtractAsync(
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var extracted = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase);
        if (!extracted)
        {
            return Task.FromResult(new ArtifactContentExtraction(
                "stored_not_interpreted",
                $"[FONTE NÃO INTERPRETADA: {contentType}, {content.Length} bytes. " +
                "O arquivo foi armazenado, mas seu conteúdo não foi lido nesta etapa.]"));
        }

        var text = System.Text.Encoding.UTF8.GetString(content);
        var sanitized = Regex.Replace(text, @"\s+", " ").Trim();
        return Task.FromResult(new ArtifactContentExtraction("extracted", sanitized));
    }
}

public sealed class MultimodalIntakeService(IArtifactContentExtractor? extractor = null)
    : IMultimodalIntakeService
{
    private readonly IArtifactContentExtractor _extractor =
        extractor ?? new TextOnlyArtifactContentExtractor();
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/svg+xml",
        "audio/mpeg", "audio/wav", "audio/ogg", "audio/mp4",
        "application/pdf", "text/plain", "text/markdown", "application/json",
        // Tipos que a esteira de anexos do PO Assistant aceita por extensão (.csv, .xlsx,
        // .docx, .zip). O binário genérico entra porque a assinatura executável já é bloqueada
        // pela política de ingestão; o que este scanner nega é mime DECLARADO como perigoso.
        "text/csv", "application/zip", "application/x-zip-compressed",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/octet-stream",
    };

    public async Task<MultimodalIntakeResult> ProcessAttachmentAsync(
        string tenantId,
        string resourceId,
        string fileName,
        string contentType,
        byte[] content,
        long maxSizeBytes = 50 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length > maxSizeBytes)
        {
            throw new ArgumentException($"Attachment size {content.Length} exceeds limit of {maxSizeBytes} bytes.", nameof(content));
        }

        var isAllowed = AllowedMimeTypes.Contains(contentType);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var extraction = await _extractor.ExtractAsync(
            fileName, contentType, content, cancellationToken);
        var preview = NormalizePreview(extraction.Text);
        var assetId = Guid.NewGuid().ToString("N");

        var result = new MultimodalIntakeResult(
            AssetId: assetId,
            TenantId: tenantId,
            ResourceId: resourceId,
            FileName: fileName,
            ContentType: contentType,
            SizeBytes: content.Length,
            Sha256Hash: sha256,
            PreviewSnippet: preview,
            ExtractionStatus: extraction.Status,
            IsAllowedType: isAllowed,
            // Isto é uma allowlist de tipo, não antivírus. Chamá-la de `passed` afirmava uma
            // verificação de malware que nunca ocorreu.
            SecurityScanStatus: isAllowed ? "type_allowlisted" : "flagged_unsupported_type",
            ProcessedAt: DateTimeOffset.UtcNow);

        return result;
    }

    private static string NormalizePreview(string text)
    {
        var sanitized = Regex.Replace(text, @"\s+", " ").Trim();

        // Onda 0.7: o corte passa a se DECLARAR. Este preview alimenta a memória de contexto da
        // chefe, e um resumo que termina em "..." foi lido como o documento inteiro no primeiro
        // turno real do Prisma — a chefe só descobriu a mutilação porque foi honesta sobre o que
        // via. O conteúdo integral fica navegável por seção via `contextRequests`.
        return sanitized.Length > 4000
            ? sanitized[..4000] +
                " … [RESUMO TRUNCADO: este trecho cobre só o início do documento; o conteúdo " +
                "integral está disponível por seção no índice navegável de anexos.]"
            : sanitized;
    }
}
