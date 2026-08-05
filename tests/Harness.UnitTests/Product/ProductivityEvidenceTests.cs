using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// O relatório que a tese empresarial exige — e a honestidade que ele precisa manter: números
/// deriváveis do ledger, intervenção técnica separada de uso, e NENHUM multiplicador sem linha de
/// base medida.
/// </summary>
public sealed class ProductivityEvidenceTests
{
    private static DeliveryFacts Emprestimos() => new(
        "QA-PROVA-LIMPA-20260802-EMPRESTIMOS",
        DateTimeOffset.Parse("2026-08-02T21:02:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-04T14:36:00Z", System.Globalization.CultureInfo.InvariantCulture),
        PhasesCompleted: 5, PhasesTotal: 9, Cards: 57, AgentAttempts: 419,
        ApprovedAttempts: 40, RejectedAttempts: 200, HumanMessages: 15,
        HumanTechnicalInterventions: 6, CorrectiveCardsAutoCreated: 0,
        RequirementsSatisfied: 3, RequirementsTotal: 4, CommitsMerged: 40,
        BlindRetriesPrevented: 0);

    [Fact]
    public void ORelatorioSeparaUsoDeIntervencaoTecnica()
    {
        var report = ProductivityEvidence.Compose(Emprestimos());

        Assert.Contains("Mensagens humanas (uso) | 15", report, StringComparison.Ordinal);
        Assert.Contains("Intervenções técnicas humanas** | **6**", report, StringComparison.Ordinal);
    }

    [Fact]
    public void NenhumMultiplicadorEDeclaradoSemLinhaDeBase()
    {
        var report = ProductivityEvidence.Compose(Emprestimos());

        Assert.DoesNotContain("5x", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nenhum multiplicador de produtividade é declarado sem linha de base",
            report, StringComparison.Ordinal);
    }

    [Fact]
    public void TempoFasesECoberturaAparecemAuditaveis()
    {
        var report = ProductivityEvidence.Compose(Emprestimos());

        Assert.Contains("Fases concluídas | 5/9", report, StringComparison.Ordinal);
        Assert.Contains("Requisitos satisfeitos | 3/4", report, StringComparison.Ordinal);
        Assert.Contains("Tempo decorrido | 41", report, StringComparison.Ordinal);
    }
}
