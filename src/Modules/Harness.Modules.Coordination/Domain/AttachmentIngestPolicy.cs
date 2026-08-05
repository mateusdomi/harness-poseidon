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
        [".md", ".txt", ".pdf", ".png", ".jpg", ".jpeg", ".csv", ".xlsx", ".docx", ".zip",
         ".mp3", ".wav", ".ogg", ".m4a"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O que é bloqueado DENTRO de um ZIP: binário nativo e ZIP aninhado — e nada mais.
    ///
    /// A lista é deliberadamente MENOR que a de anexo direto. Um ZIP de projeto fornecido (o caso
    /// FLOW 2: protótipo React exportado de um builder) contém `.js`, `.sh`, `.mjs` legítimos por
    /// definição — são código-fonte, que é exatamente o que o artefato existe para carregar.
    /// Bloqueá-los tornava TODO protótipo real inanexável: medido em 2026-08-05, o ZIP do Lovable
    /// caiu no `eslint.config.js`. O que continua perigoso num ZIP é executável NATIVO (também
    /// pego por assinatura de bytes, defesa que independe do nome) e ZIP dentro de ZIP.
    /// </summary>
    private static readonly HashSet<string> BlockedZipEntryExtensions = new(
        [".exe", ".dll", ".so", ".dylib", ".msi", ".com", ".scr", ".app"],
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

        if (!HasExpectedSignature(extension, request.Content.Span))
        {
            return AttachmentIngestDecision.Deny(
                "signature_mismatch",
                $"O conteúdo não corresponde ao formato declarado pela extensão '{extension}'.");
        }

        return extension.ToLowerInvariant() is ".zip" or ".docx" or ".xlsx"
            ? InspectZip(request.Content, extension)
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

    private static bool HasExecutableSignature(ReadOnlySpan<byte> content) =>
        HasNativeExecutableSignature(content) ||
        (content.Length >= 2 && content[0] == (byte)'#' && content[1] == (byte)'!');

    /// <summary>
    /// Assinaturas de executável NATIVO: MZ (PE), ELF, Mach-O (32/64, ambas as ordens) e binário
    /// universal. O shebang fica FORA de propósito: dentro de um ZIP de projeto fornecido, um
    /// `tools/build.sh` com `#!/bin/sh` é código-fonte legítimo — foi exatamente a forma da
    /// entrega real de empréstimos — e nada aqui o executa. Para anexo DIRETO o shebang continua
    /// bloqueado (junto com a extensão), porque um script solto não é documento de intake.
    /// </summary>
    private static bool HasNativeExecutableSignature(ReadOnlySpan<byte> content)
    {
        if (content.Length < 4)
        {
            return false;
        }

        return (content[0] == 0x4D && content[1] == 0x5A) ||
            (content[0] == 0x7F && content[1] == 0x45 && content[2] == 0x4C && content[3] == 0x46) ||
            (content[0] == 0xFE && content[1] == 0xED && content[2] == 0xFA) ||
            (content[0] == 0xCF && content[1] == 0xFA && content[2] == 0xED && content[3] == 0xFE) ||
            (content[0] == 0xCE && content[1] == 0xFA && content[2] == 0xED && content[3] == 0xFE) ||
            (content[0] == 0xCA && content[1] == 0xFE && content[2] == 0xBA && content[3] == 0xBE);
    }

    private static bool HasExpectedSignature(string extension, ReadOnlySpan<byte> content) =>
        extension.ToLowerInvariant() switch
        {
            ".pdf" => content.StartsWith("%PDF-"u8),
            ".png" => content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".zip" or ".docx" or ".xlsx" => content.StartsWith(new byte[] { 0x50, 0x4B }),
            ".mp3" => content.StartsWith("ID3"u8) ||
                (content.Length >= 2 && content[0] == 0xFF && (content[1] & 0xE0) == 0xE0),
            ".wav" => content.Length >= 12 && content.StartsWith("RIFF"u8) &&
                content[8..].StartsWith("WAVE"u8),
            ".ogg" => content.StartsWith("OggS"u8),
            ".m4a" => content.Length >= 12 && content[4..].StartsWith("ftyp"u8),
            _ => true,
        };

    private static AttachmentIngestDecision InspectZip(
        ReadOnlyMemory<byte> content,
        string extension)
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
            var names = new HashSet<string>(StringComparer.Ordinal);
            var head = new byte[4];
            foreach (var entry in archive.Entries)
            {
                names.Add(entry.FullName);
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
                if (BlockedZipEntryExtensions.Contains(entryExtension) ||
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
                if (HasNativeExecutableSignature(head.AsSpan(0, read)))
                {
                    return AttachmentIngestDecision.Deny(
                        "zip_executable_entry",
                        $"A entrada '{entry.FullName}' carrega assinatura de executável.");
                }
            }

            if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase) &&
                !names.Contains("word/document.xml"))
            {
                return AttachmentIngestDecision.Deny(
                    "office_structure_invalid",
                    "O DOCX não contém a estrutura documental esperada.");
            }

            if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) &&
                !names.Contains("xl/workbook.xml"))
            {
                return AttachmentIngestDecision.Deny(
                    "office_structure_invalid",
                    "O XLSX não contém a estrutura de planilha esperada.");
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
