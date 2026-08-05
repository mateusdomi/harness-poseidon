using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// O elo que faltou em 2026-08-04: a reprovação do portão precisa virar TRABALHO sem que uma pessoa
/// leia o diagnóstico e traduza.
///
/// Naquele run o portão sabia que não havia interface, publicava um código de falha, e a fábrica
/// parou. Alguém teve de entender que "não atende ao DoD" significava "não existe tela" e escrever
/// os cards à mão. Enquanto essa tradução depender de gente, a autonomia tem um buraco do tamanho
/// exato do produto.
/// </summary>
public sealed class ProductGapCorrectionTests
{
    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs(
            "emprestimos", "Sistema web para controlar empréstimos de equipamentos.", []));

    private static ProductDeliveryVerdict Reprovado(params ProductEvidenceFinding[] findings) =>
        new(false, ProductModality.Web, [], findings);

    private static ProductEvidenceFinding Falta(ProductEvidenceKind kind, ProductEvidenceGap gap) =>
        new(kind, gap, $"{kind} está {gap}.");

    [Fact]
    public void CadaLacunaProduzUmTrabalhoCorretivoComCriterioDeAceite()
    {
        var correcoes = ProductGapCorrections.From(
            Reprovado(
                Falta(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed),
                Falta(ProductEvidenceKind.E2EJourneyPassed, ProductEvidenceGap.Missing)),
            Web());

        Assert.Equal(2, correcoes.Count);

        var interface_ = correcoes.Single(c => c.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.Contains("não tem interface", interface_.Title, StringComparison.Ordinal);

        // O corpo precisa responder sozinho: o que falta, por que o perfil exige, e o que fecha.
        Assert.Contains("Critério de aceite", interface_.Instruction, StringComparison.Ordinal);
        Assert.Contains("FrontendPresent", interface_.Instruction, StringComparison.Ordinal);
        Assert.Contains("Web", interface_.Instruction, StringComparison.Ordinal);
        Assert.Contains("peça, não produto", interface_.Instruction, StringComparison.Ordinal);

        // E precisa impedir a saída fácil: ajustar a verificação em vez da causa.
        Assert.Contains("não ajuste a verificação", interface_.Instruction, StringComparison.Ordinal);
        Assert.Contains(
            "afirmar que ficou pronto não fecha", interface_.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// A idempotência é por NATUREZA da lacuna, nunca pelo texto — que muda de redação a cada
    /// execução do modelo. Sem isto, cada avaliação criaria um card novo para o mesmo problema.
    /// </summary>
    [Fact]
    public void AMesmaLacunaProduzAMesmaImpressaoDigitalEmAvaliacoesDiferentes()
    {
        var primeira = ProductGapCorrections.From(
            Reprovado(new ProductEvidenceFinding(
                ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed,
                "nenhum manifesto declarando React foi encontrado")),
            Web());

        var segunda = ProductGapCorrections.From(
            Reprovado(new ProductEvidenceFinding(
                ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed,
                "a entrega continua sem qualquer projeto de interface, oito horas depois")),
            Web());

        Assert.Equal(primeira[0].Fingerprint, segunda[0].Fingerprint);
        Assert.Equal(primeira[0].Title, segunda[0].Title);
    }

    /// <summary>
    /// Mudar a NATUREZA da falta é outro problema: `Missing` virou `Failed` significa que a
    /// evidência passou a existir e reprovou. Tratar como o mesmo card esconderia o movimento.
    /// </summary>
    [Fact]
    public void MudarANaturezaDaLacunaProduzOutroTrabalho()
    {
        var ausente = ProductGapCorrections.From(
            Reprovado(Falta(ProductEvidenceKind.OpenApiGenerated, ProductEvidenceGap.Missing)), Web());
        var reprovada = ProductGapCorrections.From(
            Reprovado(Falta(ProductEvidenceKind.OpenApiGenerated, ProductEvidenceGap.Failed)), Web());

        Assert.NotEqual(ausente[0].Fingerprint, reprovada[0].Fingerprint);
    }

    [Fact]
    public void PortaoSatisfeitoNaoGeraTrabalhoNenhum()
    {
        var satisfeito = new ProductDeliveryVerdict(true, ProductModality.Web, [], []);

        Assert.Empty(ProductGapCorrections.From(satisfeito, Web()));
    }

    /// <summary>
    /// A regressão exata de 2026-08-04: entrega Web sem interface precisa produzir, sozinha, o
    /// trabalho que uma pessoa teve de criar à mão naquele dia.
    /// </summary>
    [Fact]
    public void EntregaWebSemInterfaceProduzOTrabalhoQueUmaPessoaTeveDeCriarAMao()
    {
        var correcoes = ProductGapCorrections.From(
            Reprovado(
                Falta(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed),
                Falta(ProductEvidenceKind.FrontendBuild, ProductEvidenceGap.Failed),
                Falta(ProductEvidenceKind.FrontendBackendIntegration, ProductEvidenceGap.Missing),
                Falta(ProductEvidenceKind.E2EJourneyPassed, ProductEvidenceGap.Failed)),
            Web());

        Assert.Equal(4, correcoes.Count);
        Assert.All(correcoes, correcao =>
            Assert.StartsWith(ProductGapCorrections.TitlePrefix, correcao.Title, StringComparison.Ordinal));

        // Quatro lacunas, quatro impressões digitais distintas: nenhuma engole a outra.
        Assert.Equal(4, correcoes.Select(c => c.Fingerprint).Distinct(StringComparer.Ordinal).Count());
    }
}
