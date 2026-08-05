using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A trava que impede a fábrica de gastar quota repetindo o mesmo erro.
///
/// A evidência que a justifica é do run real: um card de interface acumulou <b>7 tentativas e 0
/// aprovações</b>; outro somou 5. Cada uma consumiu conta e cota para bater na mesma parede, e o
/// único sinal de progresso que a plataforma tinha — "o modelo emitiu tokens" — todas satisfaziam.
///
/// A partir de 05/08 a cota é recurso escasso, e isto deixa de ser desperdício para virar risco de
/// prazo.
/// </summary>
public sealed class NoProgressGuardTests
{
    private static ProductEvidenceFinding Falta(ProductEvidenceKind kind, ProductEvidenceGap gap) =>
        new(kind, gap, $"{kind} está {gap}.");

    private static ProductEvidenceFinding[] MesmaParede() =>
    [
        Falta(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed),
        Falta(ProductEvidenceKind.E2EJourneyPassed, ProductEvidenceGap.Missing),
    ];

    [Fact]
    public void PrimeiraTentativaNuncaERepeticao()
    {
        var verdict = NoProgressGuard.Evaluate([MesmaParede()]);

        Assert.False(verdict.BlindRetryForbidden);
        Assert.Contains("primeira tentativa", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UmaAvaliacaoSemProgressoAindaNaoProibeRepetir()
    {
        var verdict = NoProgressGuard.Evaluate([MesmaParede(), MesmaParede()]);

        Assert.False(verdict.BlindRetryForbidden);
        Assert.Contains("abaixo do limite", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duas avaliações seguidas batendo na mesma parede: a terceira tentativa idêntica está
    /// proibida. Proibir não é desistir — o veredito nomeia as lacunas que a próxima tentativa
    /// precisa atacar de outro jeito.
    /// </summary>
    [Fact]
    public void DuasAvaliacoesSeguidasSemProgressoProibemARepeticaoCega()
    {
        var verdict = NoProgressGuard.Evaluate([MesmaParede(), MesmaParede(), MesmaParede()]);

        Assert.True(verdict.BlindRetryForbidden);
        Assert.Equal(2, verdict.ConsecutiveStalledAttempts);
        Assert.Equal(2, verdict.DominantGaps.Count);
        Assert.Contains("gastaria cota", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("mudar o card, o contexto ou o executor", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fechar UMA lacuna já é progresso e libera a repetição: a fábrica está convergindo, mesmo que
    /// devagar. Exigir progresso total transformaria a trava num freio de mão.
    /// </summary>
    [Fact]
    public void FecharUmaLacunaLiberaARepeticao()
    {
        var agora = new[] { Falta(ProductEvidenceKind.E2EJourneyPassed, ProductEvidenceGap.Missing) };

        var verdict = NoProgressGuard.Evaluate([agora, MesmaParede(), MesmaParede()]);

        Assert.False(verdict.BlindRetryForbidden);
    }

    /// <summary>
    /// O caso que um saldo simples esconderia: resolveu duas e criou duas. Houve trabalho; não houve
    /// progresso. A trava precisa enxergar isso, porque é o padrão de uma fábrica girando em falso.
    /// </summary>
    [Fact]
    public void ResolverDuasECriarDuasNaoContaComoProgresso()
    {
        ProductEvidenceFinding[] antes =
        [
            Falta(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed),
            Falta(ProductEvidenceKind.BackendBuild, ProductEvidenceGap.Failed),
        ];
        ProductEvidenceFinding[] depois =
        [
            Falta(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed),
            Falta(ProductEvidenceKind.BackendBuild, ProductEvidenceGap.Failed),
            Falta(ProductEvidenceKind.PersistenceVerified, ProductEvidenceGap.Missing),
        ];

        var verdict = NoProgressGuard.Evaluate([depois, antes, antes]);

        Assert.True(verdict.BlindRetryForbidden);
    }

    /// <summary>
    /// A repetição é reconhecida pela NATUREZA da lacuna, nunca pelo texto do diagnóstico — que
    /// muda de redação a cada execução do modelo. Compará-lo faria toda repetição parecer novidade,
    /// que é o modo de falha que esta trava existe para impedir.
    /// </summary>
    [Fact]
    public void ARedacaoDoDiagnosticoNaoDisfarcaARepeticao()
    {
        ProductEvidenceFinding[] primeira =
        [
            new(ProductEvidenceKind.FrontendBuild, ProductEvidenceGap.Failed, "erro TS2304 em App.tsx"),
        ];
        ProductEvidenceFinding[] segunda =
        [
            new(ProductEvidenceKind.FrontendBuild, ProductEvidenceGap.Failed, "erro TS2551 em Lista.tsx"),
        ];
        ProductEvidenceFinding[] terceira =
        [
            new(ProductEvidenceKind.FrontendBuild, ProductEvidenceGap.Failed, "falha no build do Vite"),
        ];

        var verdict = NoProgressGuard.Evaluate([terceira, segunda, primeira]);

        Assert.True(verdict.BlindRetryForbidden);
        Assert.Single(verdict.DominantGaps);
    }

    /// <summary>
    /// O run histórico, reproduzido: sete avaliações batendo na mesma parede. A trava dispara na
    /// terceira, e as quatro seguintes — que custaram conta, contexto e cota — não teriam
    /// acontecido do mesmo jeito.
    /// </summary>
    [Fact]
    public void OPadraoDeSeteTentativasDoRunRealTeriaSidoInterrompidoNaTerceira()
    {
        var historico = Enumerable.Repeat(MesmaParede(), 7)
            .Cast<IReadOnlyList<ProductEvidenceFinding>>()
            .ToList();

        var naTerceira = NoProgressGuard.Evaluate(historico.Take(3).ToList());

        Assert.True(naTerceira.BlindRetryForbidden);
    }
}
