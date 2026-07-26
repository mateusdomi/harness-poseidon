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

public sealed class MultimodalIntakeService : IMultimodalIntakeService
{
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/svg+xml",
        "audio/mpeg", "audio/wav", "audio/ogg", "audio/mp4",
        "application/pdf", "text/plain", "text/markdown", "application/json"
    };

    public Task<MultimodalIntakeResult> ProcessAttachmentAsync(
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
        var preview = GeneratePreview(contentType, content);
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
            IsAllowedType: isAllowed,
            SecurityScanStatus: isAllowed ? "passed" : "flagged_unsupported_type",
            ProcessedAt: DateTimeOffset.UtcNow);

        return Task.FromResult(result);
    }

    private static string GeneratePreview(string contentType, byte[] content)
    {
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var text = System.Text.Encoding.UTF8.GetString(content);
            var sanitized = Regex.Replace(text, @"\s+", " ").Trim();
            return sanitized.Length > 200 ? sanitized[..200] + "..." : sanitized;
        }

        return $"[{contentType} binary payload, {content.Length} bytes]";
    }
}
