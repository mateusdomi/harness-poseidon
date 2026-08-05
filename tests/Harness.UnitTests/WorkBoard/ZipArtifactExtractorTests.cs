using System.IO.Compression;
using Harness.Host.WorkBoard;

namespace Harness.UnitTests.WorkBoard;

/// <summary>
/// A materialização confinada do artefato fornecido. O ZIP vem de FORA — protótipo exportado de um
/// builder, mandado por quem pediu o software — e por isso cada defesa aqui é contra um ataque com
/// nome: Zip Slip, symlink, bomba de descompressão, contagem excessiva e overwrite.
/// </summary>
public sealed class ZipArtifactExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-zip-{Guid.NewGuid():N}");

    public ZipArtifactExtractorTests() => Directory.CreateDirectory(_root);

    private string ZipPath => Path.Combine(_root, "artefato.zip");

    private string Destination => Path.Combine(_root, "workspace", "provided-frontend");

    [Fact]
    public void UmPrototipoLegitimoEMaterializadoIntegralmente()
    {
        using (var archive = ZipFile.Open(ZipPath, ZipArchiveMode.Create))
        {
            Escrever(archive, "package.json", "{\"name\":\"prototipo\"}");
            Escrever(archive, "src/App.tsx", "export default function App() { return null; }");
            Escrever(archive, "src/components/Login.tsx", "// tela de login");
        }

        var result = ZipArtifactExtractor.Extract(ZipPath, Destination);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(3, result.FileCount);
        Assert.Contains("src/components/Login.tsx", result.Files);
        Assert.True(File.Exists(Path.Combine(Destination, "src", "App.tsx")));
    }

    /// <summary>Zip Slip: `../` canonicaliza para fora do destino e é rejeição, não normalização.</summary>
    [Fact]
    public void EntradaComPontoPontoBarraERejeitada()
    {
        using (var archive = ZipFile.Open(ZipPath, ZipArchiveMode.Create))
        {
            Escrever(archive, "../fora-do-destino.txt", "escapei");
        }

        var result = ZipArtifactExtractor.Extract(ZipPath, Destination);

        Assert.False(result.Succeeded);
        Assert.Contains("Zip Slip", result.Reason!, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "workspace", "fora-do-destino.txt")));
    }

    /// <summary>
    /// Bomba de descompressão: o teto é verificado no CONSUMO do stream, porque o tamanho declarado
    /// no cabeçalho pode mentir — validar metadado seria validar a palavra do atacante.
    /// </summary>
    [Fact]
    public void ConteudoQueExpandeAlemDoTetoInterrompeELimpa()
    {
        using (var archive = ZipFile.Open(ZipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("gigante.bin", CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            var zeros = new byte[1024 * 1024];
            for (var written = 0L; written <= ZipArtifactExtractor.MaxExpandedBytes; written += zeros.Length)
            {
                stream.Write(zeros, 0, zeros.Length);
            }
        }

        var result = ZipArtifactExtractor.Extract(ZipPath, Destination);

        Assert.False(result.Succeeded);
        Assert.Contains("bomba de descompressão", result.Reason!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public void DestinoComConteudoExistenteNaoEsobrescrito()
    {
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "trabalho-em-curso.txt"), "não me apague");
        using (var archive = ZipFile.Open(ZipPath, ZipArchiveMode.Create))
        {
            Escrever(archive, "a.txt", "x");
        }

        var result = ZipArtifactExtractor.Extract(ZipPath, Destination);

        Assert.False(result.Succeeded);
        Assert.Contains("não está vazio", result.Reason!, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Destination, "trabalho-em-curso.txt")));
    }

    [Fact]
    public void ArquivoQueNaoEZipERejeitadoComMotivo()
    {
        File.WriteAllText(ZipPath, "isto é texto, não um zip");

        var result = ZipArtifactExtractor.Extract(ZipPath, Destination);

        Assert.False(result.Succeeded);
        Assert.Contains("não é um ZIP válido", result.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Symlink dentro do ZIP: o modo unix S_IFLNK nos atributos externos identifica a entrada, e
    /// ela é rejeitada por inteiro — materializar o link deixaria a PRÓXIMA leitura escapar do
    /// confinamento com aparência de arquivo legítimo.
    /// </summary>
    [Fact]
    public void EntradaSymlinkERejeitada()
    {
        using (var archive = ZipFile.Open(ZipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link-malicioso");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000); // S_IFLNK | 0777
            using var stream = entry.Open();
            var alvo = System.Text.Encoding.UTF8.GetBytes("/etc/passwd");
            stream.Write(alvo, 0, alvo.Length);
        }

        var result = ZipArtifactExtractor.Extract(ZipPath, Destination);

        Assert.False(result.Succeeded);
        Assert.Contains("link simbólico", result.Reason!, StringComparison.Ordinal);
    }

    /// <summary>O ZIP REAL do protótipo Lovable, quando presente na máquina: prova com o artefato de verdade.</summary>
    [Fact]
    public void OZipRealDoLovableExtraiQuandoDisponivel()
    {
        var real = "/Users/mateus/Downloads/bright-vision-interface-main.zip";
        if (!File.Exists(real))
        {
            // Máquina sem o artefato: nada a provar aqui, e inventar um sucesso seria mentir.
            return;
        }

        var result = ZipArtifactExtractor.Extract(real, Destination);

        Assert.True(result.Succeeded, result.Reason);
        Assert.True(result.FileCount > 0);
    }

    private static void Escrever(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = new StreamWriter(entry.Open());
        stream.Write(content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sobra temporária não é falha.
        }
    }
}
