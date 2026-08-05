using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Onda 0.7 — a prova de que os anexos são NAVEGÁVEIS por inteiro, com a especificação REAL do
/// Prisma como caso de teste.
///
/// O defeito observado em 04/08/2026: a chefe declarou ao dono que só recebera "o início de cada
/// anexo" — as seções 5–6 (metodologia de avaliação), 7 (perfis e permissões), 11 (auditoria),
/// 15 (dataset de referência) e 16 (critérios de aceite) da spec não existiam para ela, porque o
/// índice vetorial guarda 4.000 caracteres do documento inteiro. A missão exige exatamente estas
/// seções 100% navegáveis; é o que este arquivo verifica, contra o documento real.
/// </summary>
public sealed class AttachmentNavigationTests
{
    private static readonly Lazy<string> PrismaSpec = new(() => File.ReadAllText(
        Path.Combine(
            FindRepositoryRoot(),
            "docs", "architecture", "preflight", "prisma-especificacao-mvp-v3.0.md")));

    [Theory]
    [InlineData("5", "Metodologia")]
    [InlineData("6", "ITRC")]
    [InlineData("7", "Perfis")]
    [InlineData("11", "auditoria")]
    [InlineData("15", "Dataset")]
    [InlineData("16", "aceite")]
    public void AsSecoesQueFaltaramNoPrimeiroTurnoRealSaoNavegaveisPorInteiro(
        string sectionId, string expectedInTitle)
    {
        var sections = AttachmentSectionizer.Split(PrismaSpec.Value);

        var section = AttachmentSectionizer.Find(sections, sectionId);

        Assert.NotNull(section);
        Assert.Contains(expectedInTitle, section.Title, StringComparison.OrdinalIgnoreCase);

        // "Navegável por inteiro" quer dizer o CONTEÚDO, não o título: a seção carrega tudo o
        // que o documento original tem entre este título e o próximo.
        Assert.Contains($"## {section.Title}", PrismaSpec.Value, StringComparison.Ordinal);
        Assert.True(
            section.Content.Length > 200,
            $"a seção §{sectionId} veio com {section.Content.Length} chars — cheira a corte.");
        Assert.Contains(section.Content, PrismaSpec.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prova de COMPLETUDE: nenhum caractere do documento fica fora das seções. Sem isto,
    /// "resumo fiel por seção" seria promessa — um secionador que engole linhas entre seções
    /// passaria em qualquer teste de presença.
    /// </summary>
    [Fact]
    public void AsSecoesReconstroemADocumentoOriginalByteAByte()
    {
        var sections = AttachmentSectionizer.Split(PrismaSpec.Value);

        Assert.Equal(PrismaSpec.Value, AttachmentSectionizer.Reassemble(sections));
        Assert.True(sections.Count >= 16, $"a spec tem 16+ seções; vieram {sections.Count}.");
    }

    /// <summary>
    /// A spec tem pseudocódigo com <c>#</c> dentro de cercas de código (§6.2). Um "## " dentro
    /// de cerca é conteúdo — não pode virar seção fantasma que desloca a numeração.
    /// </summary>
    [Fact]
    public void TituloDentroDeCercaDeCodigoNaoViraSecao()
    {
        var document = "## 1. Real\ncorpo\n```python\n## comentario\n```\n## 2. Tambem real\nfim";

        var sections = AttachmentSectionizer.Split(document);

        Assert.Equal(2, sections.Count);
        Assert.Equal("1", sections[0].Id);
        Assert.Equal("2", sections[1].Id);
        Assert.Equal(document, AttachmentSectionizer.Reassemble(sections));
    }

    [Fact]
    public void DocumentoSemTitulosEUmaSecaoUnicaNavegavel()
    {
        var document = "texto corrido\nsem título nenhum";

        var sections = AttachmentSectionizer.Split(document);

        var only = Assert.Single(sections);
        Assert.Equal("preambulo", only.Id);
        Assert.Equal(document, AttachmentSectionizer.Reassemble(sections));
    }

    [Fact]
    public void FindAceitaVariantesDeEndereco()
    {
        var sections = AttachmentSectionizer.Split(PrismaSpec.Value);

        // A chefe pode pedir "§5", "5." ou o título — todas as formas chegam à mesma seção.
        Assert.NotNull(AttachmentSectionizer.Find(sections, "§5"));
        Assert.NotNull(AttachmentSectionizer.Find(sections, "5."));
        Assert.Equal(
            AttachmentSectionizer.Find(sections, "§5")!.Id,
            AttachmentSectionizer.Find(sections, "5.")!.Id);
        Assert.Null(AttachmentSectionizer.Find(sections, "99"));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "governance", "manifest.yaml")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
