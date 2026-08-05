using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.WorkChain;

/// <summary>
/// Onda 0.9: a causa de rejeição é FECHADA e OBRIGATÓRIA.
///
/// 96 dos 98 reviews reprovados do histórico estavam com causa 'none' — a maior categoria de
/// falha da fábrica era anônima, e é o dado que alimenta o grafo de impacto e o aprendizado do
/// harness. Escrita nova reprovada sem causa tipada é RECUSADA; `unclassified` existe só para o
/// histórico migrado.
/// </summary>
public sealed class ReviewRejectionTaxonomyTests
{
    private static WorkAttemptReviewCommand Comando(string decision, string cause) =>
        new(
            "01ARZ3NDEKTSV4RRFFQ69G5T01", "01ARZ3NDEKTSV4RRFFQ69G5T02",
            "01ARZ3NDEKTSV4RRFFQ69G5T03", "01ARZ3NDEKTSV4RRFFQ69G5T04",
            "01ARZ3NDEKTSV4RRFFQ69G5T05", "critico", decision, "parecer", 1,
            "chave", DateTimeOffset.UtcNow)
        { RejectionCause = cause };

    [Theory]
    [InlineData("contextMissing")]
    [InlineData("missingSkill")]
    [InlineData("badDecomposition")]
    [InlineData("missingVerifier")]
    [InlineData("missingTool")]
    [InlineData("modelCapability")]
    [InlineData("specAmbiguity")]
    [InlineData("environmentFailure")]
    [InlineData("policyViolation")]
    [InlineData("acceptanceNotMet")]
    [InlineData("scopeViolation")]
    [InlineData("qualityBar")]
    [InlineData("other")]
    public void ReprovacaoComCausaTipadaEAceita(string cause)
    {
        WorkChainMutationValidator.Validate(Comando("rejected", cause));
    }

    /// <summary>A regra central: reprovar sem dizer por quê deixou de ser possível.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("unclassified")]
    public void ReprovacaoSemCausaTipadaERecusadaNoRegistro(string cause)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => WorkChainMutationValidator.Validate(Comando("rejected", cause)));

        Assert.Contains("typed rejection cause", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AprovacaoContinuaComNone()
    {
        WorkChainMutationValidator.Validate(Comando("approved", "none"));
    }

    [Fact]
    public void CausaForaDaTaxonomiaERecusada()
    {
        Assert.Throws<ArgumentException>(
            () => WorkChainMutationValidator.Validate(Comando("rejected", "vibes")));
    }
}
