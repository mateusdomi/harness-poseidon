using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A distinção runtime entre entrada crua e entrada rica — o FLOW 2 empresarial.
///
/// O canon já mandava "validar, não reentrevistar"; o que faltava era o runtime saber QUANDO. A
/// classificação é determinística e por sinal estruturado, e o teste central é o do Prisma: um
/// projeto que chega com especificação build-ready, protótipo React e override de Oracle não pode
/// ser tratado como a frase "quero um sistema de riscos".
/// </summary>
public sealed class RichIntakeAnalyzerTests
{
    [Fact]
    public void PedidoCruSemFonteDeConteudoEDescoberta()
    {
        var assessment = RichIntakeAnalyzer.Analyze(
            "Quero um sistema simples para controlar empréstimos de equipamentos.",
            [], hasDeadline: false);

        Assert.Equal(IntakeFlow.Discovery, assessment.Flow);
        Assert.Empty(assessment.Signals);
    }

    /// <summary>
    /// Prazo e override de stack SOZINHOS não convertem o fluxo: pedido cru com prazo continua
    /// precisando de descoberta. O que não se pode é reperguntar o que um documento já respondeu —
    /// e sem documento não há o que reperguntar.
    /// </summary>
    [Fact]
    public void PrazoEOverrideSemFonteDeConteudoContinuamDescoberta()
    {
        var assessment = RichIntakeAnalyzer.Analyze(
            "Sistema de riscos. O banco deve ser Oracle.",
            [], hasDeadline: true,
            [new ProfileDirective(ProfileAuthority.ProjectRequirement, "database", "Oracle", "requisito")]);

        Assert.Equal(IntakeFlow.Discovery, assessment.Flow);
        Assert.True(assessment.Has(RichIntakeAnalyzer.SignalDeadline));
        Assert.True(assessment.Has(RichIntakeAnalyzer.SignalStackOverride));
    }

    /// <summary>O caso Prisma: requisitos + protótipo + override + prazo = ACCELERATOR.</summary>
    [Fact]
    public void EntradaDoPrismaEClassificadaComoAccelerator()
    {
        var assessment = RichIntakeAnalyzer.Analyze(
            "Especificação build-ready anexada; é a fonte única de verdade. " +
            "Critérios de aceite em Given/When/Then na seção 16. O banco deve ser Oracle.",
            [
                ("requirements_source", "Prisma_Especificacao_Tecnica_MVP.md"),
                ("provided_frontend", "bright-vision-interface-main.zip"),
            ],
            hasDeadline: true,
            [new ProfileDirective(ProfileAuthority.ProjectRequirement, "database", "Oracle", "requisito")]);

        Assert.Equal(IntakeFlow.Accelerator, assessment.Flow);
        Assert.True(assessment.Has(RichIntakeAnalyzer.SignalRequirementsSource));
        Assert.True(assessment.Has(RichIntakeAnalyzer.SignalProvidedFrontend));
        Assert.True(assessment.Has(RichIntakeAnalyzer.SignalAcceptanceCriteria));
        Assert.True(assessment.Has(RichIntakeAnalyzer.SignalSourceOfTruthDeclared));
    }

    [Fact]
    public void SoOProtipoFornecidoJaBastaParaAccelerator()
    {
        var assessment = RichIntakeAnalyzer.Analyze(
            "Segue o protótipo da interface.",
            [("provided_frontend", "app.zip")], hasDeadline: false);

        Assert.Equal(IntakeFlow.Accelerator, assessment.Flow);
    }

    /// <summary>
    /// A ordem de trabalho injetada precisa dizer o modo, as fontes e as três regras que valem
    /// dinheiro: não reperguntar, não reabrir decisão fechada, não substituir o protótipo.
    /// </summary>
    [Fact]
    public void AOrdemDeTrabalhoNomeiaAsFontesEAsRegras()
    {
        var assessment = RichIntakeAnalyzer.Analyze(
            "fonte única de verdade anexada",
            [("provided_frontend", "app.zip")], hasDeadline: true);

        var order = RichIntakeAnalyzer.ComposeWorkOrder(assessment);

        Assert.Contains("VALIDAR → NORMALIZAR → RASTREAR → PREENCHER LACUNAS", order, StringComparison.Ordinal);
        Assert.Contains("anexo:app.zip", order, StringComparison.Ordinal);
        Assert.Contains("NÃO pergunte", order, StringComparison.Ordinal);
        Assert.Contains("NÃO se reabre por preferência técnica", order, StringComparison.Ordinal);
        Assert.Contains("Substituir por design próprio exige", order, StringComparison.Ordinal);
    }

    [Fact]
    public void DescobertaNaoRecebeOrdemDeTrabalhoDeEntradaRica()
    {
        var assessment = RichIntakeAnalyzer.Analyze("quero um sistema", [], false);

        Assert.Equal(string.Empty, RichIntakeAnalyzer.ComposeWorkOrder(assessment));
    }
}
