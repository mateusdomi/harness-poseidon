using Harness.Modules.Documents.Application;

namespace Harness.UnitTests.Documents;

public sealed class DocumentExportComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static ExportableDocument Document(
        string id, string title, string state = "approved", string? phase = "Planejamento",
        bool inconsistent = false, int version = 1) =>
        new(id, title, "prd", state, version, phase, inconsistent, Now);

    [Fact]
    public void OnlyApprovedAndConsistentDocumentsLeaveTheProduct()
    {
        var documents = new[]
        {
            Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Visao do produto"),
            Document("01BBBBBBBBBBBBBBBBBBBBBBBB", "Rascunho", state: "inElaboration"),
            Document("01CCCCCCCCCCCCCCCCCCCCCCCC", "Em revisao", state: "inReview"),
            Document("01DDDDDDDDDDDDDDDDDDDDDDDD", "Aguardando", state: "awaitingApproval"),
            Document("01EEEEEEEEEEEEEEEEEEEEEEEE", "Aprovado porem inconsistente", inconsistent: true),
        };

        var exportable = DocumentExportComposer.Exportable(documents);

        Assert.Single(exportable);
        Assert.Equal("Visao do produto", exportable[0].Title);
    }

    [Fact]
    public void ExportIsDeterministic()
    {
        var documents = new[]
        {
            Document("01BBBBBBBBBBBBBBBBBBBBBBBB", "Zeta", phase: "Descoberta"),
            Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Alfa", phase: "Descoberta"),
            Document("01CCCCCCCCCCCCCCCCCCCCCCCC", "Beta", phase: "Planejamento"),
        };

        var first = DocumentExportComposer.Exportable(documents).Select(item => item.Id);
        var second = DocumentExportComposer.Exportable(documents.Reverse()).Select(item => item.Id);

        Assert.Equal(first, second);
        Assert.Equal(
            ["01AAAAAAAAAAAAAAAAAAAAAAAA", "01BBBBBBBBBBBBBBBBBBBBBBBB", "01CCCCCCCCCCCCCCCCCCCCCCCC"],
            first);
    }

    [Fact]
    public void EntryPathIsReadableSafeAndCarriesTheVersion()
    {
        var path = DocumentExportComposer.EntryPath(
            Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Visão do Produto: Fase 1/2", phase: "Planejamento", version: 3));

        Assert.Equal("planejamento/visao-do-produto-fase-1-2-v3.md", path);
    }

    [Fact]
    public void DocumentWithoutPhaseGoesToItsOwnFolder()
    {
        var path = DocumentExportComposer.EntryPath(
            Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Notas soltas", phase: null));

        Assert.StartsWith("sem-etapa/", path, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageCarriesManifestFirstAndOneFilePerDocument()
    {
        var sources = new[]
        {
            new DocumentExportSource(
                Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Visao do produto"), "# Visao", "hash-a"),
            new DocumentExportSource(
                Document("01BBBBBBBBBBBBBBBBBBBBBBBB", "Especificacao"), "# Spec", "hash-b"),
        };

        var plan = DocumentExportComposer.Compose("01PROJECT0000000000000000", sources, Now);

        Assert.Equal(3, plan.Entries.Count);
        Assert.Equal(DocumentExportComposer.ManifestEntryPath, plan.Entries[0].Path);
        Assert.Equal(2, plan.DocumentCount);
        Assert.Contains(plan.Entries, entry => entry.Body == "# Visao");
        Assert.Contains(plan.Entries, entry => entry.Body == "# Spec");
    }

    [Fact]
    public void ManifestAnswersWhichVersionLeftTheProduct()
    {
        var sources = new[]
        {
            new DocumentExportSource(
                Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Visao do produto", version: 7),
                "# Visao", "sha256:abc"),
        };

        var plan = DocumentExportComposer.Compose("01PROJECT0000000000000000", sources, Now);

        Assert.Contains("\"version\": 7", plan.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("sha256:abc", plan.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("01AAAAAAAAAAAAAAAAAAAAAAAA", plan.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("Visao do produto", plan.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("01PROJECT0000000000000000", plan.ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDocumentsWithTheSameTitleBothSurvive()
    {
        var sources = new[]
        {
            new DocumentExportSource(
                Document("01AAAAAAAAAAAAAAAAAAAAAAAA", "Relatorio"), "primeiro", "hash-a"),
            new DocumentExportSource(
                Document("01BBBBBBBBBBBBBBBBBBBBBBBB", "Relatorio"), "segundo", "hash-b"),
        };

        var plan = DocumentExportComposer.Compose("01PROJECT0000000000000000", sources, Now);

        var paths = plan.Entries.Select(entry => entry.Path).ToArray();
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("primeiro", plan.Entries.Select(entry => entry.Body));
        Assert.Contains("segundo", plan.Entries.Select(entry => entry.Body));
    }

    [Fact]
    public void EmptyProjectStillProducesAnHonestManifest()
    {
        var plan = DocumentExportComposer.Compose("01PROJECT0000000000000000", [], Now);

        Assert.Single(plan.Entries);
        Assert.Equal(0, plan.DocumentCount);
        Assert.Contains("\"documentCount\": 0", plan.ManifestJson, StringComparison.Ordinal);
    }
}
