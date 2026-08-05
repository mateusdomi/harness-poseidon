using System.IO.Compression;

namespace Harness.Host.WorkBoard;

/// <summary>O desfecho de uma materialização de artefato. Falha nomeia o motivo, nunca é silêncio.</summary>
public sealed record ZipExtractionResult(
    bool Succeeded,
    string? Reason,
    int FileCount,
    long ExpandedBytes,
    IReadOnlyList<string> Files)
{
    public static ZipExtractionResult Fail(string reason) => new(false, reason, 0, 0, []);
}

/// <summary>
/// Materializa um artefato ZIP fornecido pelo usuário dentro de um diretório CONTROLADO do
/// workspace do projeto.
///
/// O chat guarda o ZIP como blob com hash — e continua assim de propósito: extração automática no
/// intake seria superfície de ataque sem necessidade. A extração só acontece quando um EXECUTOR
/// precisa trabalhar nos arquivos, e acontece aqui, com as defesas que um arquivo vindo de fora
/// exige:
///
/// - <b>Zip Slip</b>: cada entrada é canonicalizada e precisa cair DENTRO do destino; `../` e
///   caminho absoluto são rejeição, não normalização silenciosa;
/// - <b>symlink</b>: entradas de link simbólico não são materializadas — um link para fora do
///   destino transformaria a próxima leitura em fuga do confinamento;
/// - <b>zip bomb</b>: teto de bytes DESCOMPRIMIDOS e de contagem de arquivos, verificados durante a
///   extração (o cabeçalho mente; o stream não);
/// - <b>overwrite</b>: o destino precisa nascer vazio — materializar por cima de trabalho existente
///   mistura artefato com produção.
/// </summary>
public static class ZipArtifactExtractor
{
    /// <summary>200 MB descomprimidos. O protótipo típico tem poucos MB; um estouro aqui é ataque ou engano.</summary>
    public const long MaxExpandedBytes = 200L * 1024 * 1024;

    /// <summary>Teto de entradas. Projetos de interface reais ficam ordens de grandeza abaixo.</summary>
    public const int MaxFileCount = 5_000;

    public static ZipExtractionResult Extract(string zipPath, string destinationDirectory)
    {
        if (!File.Exists(zipPath))
        {
            return ZipExtractionResult.Fail($"O artefato não existe em {zipPath}.");
        }

        string destination;
        try
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                return ZipExtractionResult.Fail(
                    "O destino não está vazio: materializar por cima de conteúdo existente " +
                    "misturaria o artefato com trabalho em curso.");
            }

            Directory.CreateDirectory(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ZipExtractionResult.Fail($"Destino inacessível: {exception.GetType().Name}.");
        }

        var files = new List<string>();
        long expanded = 0;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > MaxFileCount)
            {
                return ZipExtractionResult.Fail(
                    $"O arquivo declara {archive.Entries.Count} entradas; o teto é {MaxFileCount}. " +
                    "Contagem excessiva é a assinatura de uma bomba de descompressão.");
            }

            foreach (var entry in archive.Entries)
            {
                // Diretório puro: só cria, nada a escrever.
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

                // Symlink em ZIP (unix mode S_IFLNK no external attributes). Não materializar:
                // um link apontando para fora do destino faria a próxima leitura escapar do
                // confinamento com aparência de arquivo legítimo.
                var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixMode == 0xA000)
                {
                    return ZipExtractionResult.Fail(
                        $"A entrada `{entry.FullName}` é um link simbólico; artefatos fornecidos " +
                        "não podem conter links.");
                }

                // Canonicaliza e confina. `../`, absoluto e unidades (`C:`) caem todos aqui.
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !string.Equals(target, destination, StringComparison.Ordinal))
                {
                    return ZipExtractionResult.Fail(
                        $"A entrada `{entry.FullName}` escapa do destino (Zip Slip).");
                }

                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // Extração por stream com teto verificado NO CONSUMO: o tamanho declarado no
                // cabeçalho pode mentir, e é exatamente assim que uma bomba passa por validação
                // de metadado.
                using var source = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    expanded += read;
                    if (expanded > MaxExpandedBytes)
                    {
                        output.Dispose();
                        TryCleanup(destination);
                        return ZipExtractionResult.Fail(
                            $"O conteúdo descomprimido excedeu {MaxExpandedBytes} bytes. " +
                            "Teto de expansão é a defesa contra bomba de descompressão.");
                    }

                    output.Write(buffer, 0, read);
                }

                files.Add(Path.GetRelativePath(destination, target).Replace('\\', '/'));
                if (files.Count > MaxFileCount)
                {
                    TryCleanup(destination);
                    return ZipExtractionResult.Fail($"Mais de {MaxFileCount} arquivos extraídos.");
                }
            }
        }
        catch (InvalidDataException)
        {
            TryCleanup(destination);
            return ZipExtractionResult.Fail("O arquivo não é um ZIP válido.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryCleanup(destination);
            return ZipExtractionResult.Fail($"Falha de E/S na extração: {exception.GetType().Name}.");
        }

        files.Sort(StringComparer.Ordinal);
        return new ZipExtractionResult(true, null, files.Count, expanded, files);
    }

    private static void TryCleanup(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Uma extração rejeitada com resíduo é reportada pela razão principal; o resíduo em
            // diretório controlado não é explorável.
        }
    }
}
