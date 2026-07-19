using System.IO.Compression;

namespace Harness.Modules.Coordination.Domain;

public sealed record AttachmentIngestRequest(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content);

public sealed record AttachmentIngestDecision(bool Accepted, string Code, string Detail)
{
    public static AttachmentIngestDecision Permit() =>
        new(true, "accepted", "O anexo passou por todas as validações de ingestão.");

    public static AttachmentIngestDecision Deny(string code, string detail) =>
        new(false, code, detail);
}

/// <summary>
/// Política de segurança de upload do PO Assistant: tipo permitido, tamanho,
/// nome sem path traversal, bloqueio de conteúdo executável por assinatura e
/// inspeção anti zip-bomb. ZIPs nunca são executados; apenas inspecionados.
/// </summary>
public static class AttachmentIngestPolicy
{
    public const long MaximumSizeBytes = 10L * 1024 * 1024;
    public const int MaximumZipEntries = 512;
    public const long MaximumZipUncompressedBytes = 100L * 1024 * 1024;
    public const int MaximumZipCompressionRatio = 200;

    private static readonly HashSet<string> AllowedExtensions = new(
        [".md", ".txt", ".pdf", ".png", ".jpg", ".jpeg", ".csv", ".xlsx", ".docx", ".zip"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BlockedExtensions = new(
        [".exe", ".dll", ".so", ".dylib", ".sh", ".bat", ".cmd", ".ps1", ".app",
         ".msi", ".com", ".scr", ".jar", ".vbs", ".js", ".mjs", ".wasm"],
        StringComparer.OrdinalIgnoreCase);

    public static AttachmentIngestDecision Evaluate(AttachmentIngestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nameDecision = ValidateFileName(request.FileName);
        if (!nameDecision.Accepted)
        {
            return nameDecision;
        }

        var extension = Path.GetExtension(request.FileName);
        if (BlockedExtensions.Contains(extension))
        {
            return AttachmentIngestDecision.Deny(
                "executable_extension",
                $"A extensão '{extension}' é executável e não é aceita como anexo.");
        }

        if (!AllowedExtensions.Contains(extension))
        {
            return AttachmentIngestDecision.Deny(
                "unsupported_type",
                $"A extensão '{extension}' não pertence à allowlist de anexos.");
        }

        if (string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 200)
        {
            return AttachmentIngestDecision.Deny(
                "invalid_content_type",
                "O content type do anexo é obrigatório e limitado a 200 caracteres.");
        }

        if (request.Content.Length == 0)
        {
            return AttachmentIngestDecision.Deny(
                "empty_content",
                "O anexo não pode ser vazio.");
        }

        if (request.Content.Length > MaximumSizeBytes)
        {
            return AttachmentIngestDecision.Deny(
                "size_exceeded",
                $"O anexo excede o limite de {MaximumSizeBytes} bytes.");
        }

        if (HasExecutableSignature(request.Content.Span))
        {
            return AttachmentIngestDecision.Deny(
                "executable_content",
                "O conteúdo do anexo carrega assinatura de executável e foi bloqueado.");
        }

        return string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
            ? InspectZip(request.Content)
            : AttachmentIngestDecision.Permit();
    }

    private static AttachmentIngestDecision ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200)
        {
            return AttachmentIngestDecision.Deny(
                "invalid_file_name",
                "O nome do anexo é obrigatório e limitado a 200 caracteres.");
        }

        if (fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains('\\', StringComparison.Ordinal) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Any(char.IsControl) ||
            Path.IsPathRooted(fileName))
        {
            return AttachmentIngestDecision.Deny(
                "path_traversal",
                "O nome do anexo não pode conter separadores de caminho, '..' ou caracteres de controle.");
        }

        return AttachmentIngestDecision.Permit();
    }

    private static bool HasExecutableSignature(ReadOnlySpan<byte> content)
    {
        if (content.Length < 4)
        {
            return false;
        }

        // MZ (PE), ELF, Mach-O (32/64, ambas as ordens), universal binary e shebang.
        return (content[0] == 0x4D && content[1] == 0x5A) ||
            (content[0] == 0x7F && content[1] == 0x45 && content[2] == 0x4C && content[3] == 0x46) ||
            (content[0] == 0xFE && content[1] == 0xED && content[2] == 0xFA) ||
            (content[0] == 0xCF && content[1] == 0xFA && content[2] == 0xED && content[3] == 0xFE) ||
            (content[0] == 0xCE && content[1] == 0xFA && content[2] == 0xED && content[3] == 0xFE) ||
            (content[0] == 0xCA && content[1] == 0xFE && content[2] == 0xBA && content[3] == 0xBE) ||
            (content[0] == (byte)'#' && content[1] == (byte)'!');
    }

    private static AttachmentIngestDecision InspectZip(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumZipEntries)
            {
                return AttachmentIngestDecision.Deny(
                    "zip_entry_limit",
                    $"O ZIP excede o limite de {MaximumZipEntries} entradas.");
            }

            long totalUncompressed = 0;
            var head = new byte[4];
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal) ||
                    Path.IsPathRooted(entry.FullName) ||
                    entry.FullName.Contains('\\', StringComparison.Ordinal))
                {
                    return AttachmentIngestDecision.Deny(
                        "zip_path_traversal",
                        $"A entrada '{entry.FullName}' tenta escapar do diretório de extração.");
                }

                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var entryExtension = Path.GetExtension(entry.Name);
                if (BlockedExtensions.Contains(entryExtension) ||
                    string.Equals(entryExtension, ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return AttachmentIngestDecision.Deny(
                        "zip_blocked_entry",
                        $"A entrada '{entry.FullName}' carrega tipo bloqueado dentro do ZIP.");
                }

                totalUncompressed += entry.Length;
                if (totalUncompressed > MaximumZipUncompressedBytes)
                {
                    return AttachmentIngestDecision.Deny(
                        "zip_bomb",
                        "O tamanho descomprimido do ZIP excede o limite de segurança.");
                }

                var compressed = Math.Max(1, entry.CompressedLength);
                if (entry.Length / compressed > MaximumZipCompressionRatio)
                {
                    return AttachmentIngestDecision.Deny(
                        "zip_bomb",
                        $"A entrada '{entry.FullName}' excede a razão máxima de compressão.");
                }

                using var entryStream = entry.Open();
                var read = entryStream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
                if (HasExecutableSignature(head.AsSpan(0, read)))
                {
                    return AttachmentIngestDecision.Deny(
                        "zip_executable_entry",
                        $"A entrada '{entry.FullName}' carrega assinatura de executável.");
                }
            }

            return AttachmentIngestDecision.Permit();
        }
        catch (InvalidDataException)
        {
            return AttachmentIngestDecision.Deny(
                "zip_corrupted",
                "O ZIP está corrompido ou não é um arquivo ZIP válido.");
        }
    }
}
