using Harness.Host.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// Fase 2A.2 — o documento da esteira e a esteira materializada são a mesma coisa dita duas vezes.
///
/// O documento tinha 69 linhas para nove fases e descrevia os gates em prosa aproximada, enquanto
/// o banco cobrava critérios objetivos. Duas descrições da mesma regra, livres para divergir, e
/// nada as comparava: quem lesse o documento acreditaria numa regra que o produto não aplica.
///
/// Este teste reprova a divergência. Ele NÃO exige que o documento seja um despejo do código —
/// exige que cada fase exista nos dois lugares e que o critério do gate escrito no documento
/// contenha, literalmente, o que o banco cobra. Prosa em volta é livre; o critério não é.
/// </summary>
public sealed class StandardWorkflowDocParityTests
{
    [Fact]
    public void EveryPhaseOfTheSeededPlaybookAppearsInTheDocument()
    {
        var document = ReadDocument();
        var template = CanonicalWorkflowTemplates.PlaybookStandardTemplate;

        Assert.Equal(9, template.Phases.Count);
        foreach (var phase in template.Phases)
        {
            // "1-Triagem" no banco → "## 1. Triagem" no documento.
            var parts = phase.Split('-', 2);
            var heading = $"## {parts[0]}. {parts[1]}";
            Assert.Contains(heading, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryGateCriterionCobradoPeloBancoEstaEscritoNoDocumento()
    {
        var document = Normalize(ReadDocument());
        var template = CanonicalWorkflowTemplates.PlaybookStandardTemplate;

        var missing = new List<string>();
        foreach (var (phase, criteria) in template.GatesByPhase)
        {
            foreach (var criterion in criteria)
            {
                if (!document.Contains(Normalize(criterion), StringComparison.Ordinal))
                {
                    missing.Add($"{phase}: {criterion}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryPhaseHasAGate()
    {
        // Fase sem gate é fase que passa sozinha — o oposto de Default-FAIL.
        var template = CanonicalWorkflowTemplates.PlaybookStandardTemplate;
        foreach (var phase in template.Phases)
        {
            Assert.True(
                template.GatesByPhase.TryGetValue(phase, out var criteria) && criteria.Count > 0,
                $"A fase '{phase}' não declara gate.");
        }
    }

    [Fact]
    public void TheDocumentStatesTheHumanGatesAndTheFixedFlexibleContract()
    {
        // Normalizado: onde o parágrafo do Markdown quebrou não é divergência.
        var document = Normalize(ReadDocument());

        // Os dois gates HITL precisam estar ditos como tais: um leitor que não os encontre no
        // documento assume que a esteira inteira é automática.
        Assert.Contains("HITL obrigatório", document, StringComparison.Ordinal);
        Assert.Contains("Termo de Aceite aprovado pelo humano", document, StringComparison.Ordinal);
        Assert.Contains("Default-FAIL", document, StringComparison.Ordinal);
        Assert.Contains("Silêncio nunca é aprovação", document, StringComparison.Ordinal);

        // O contrato Fixed/Flexible dos artefatos vale para todos os templates e mora aqui.
        Assert.Contains("Fixed/Flexible", document, StringComparison.Ordinal);
    }

    /// <summary>
    /// Forma comparável: quebras de linha do Markdown viram espaço único. O texto do critério é
    /// idêntico; o que muda é onde o parágrafo quebrou, e isso não é divergência.
    /// </summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[])['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries));

    private static string ReadDocument() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "architecture", "workflows", "standard-workflow.md"));

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
