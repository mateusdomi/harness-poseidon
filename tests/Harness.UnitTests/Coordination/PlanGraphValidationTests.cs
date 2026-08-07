using Harness.Modules.Coordination.Application;
using Harness.SharedKernel.CodeGraph;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Plano declarado × grafo calculado (B6/F15) — o gate desta fase.
///
/// O caso que dá sentido a tudo aqui é o primeiro: dois cards que o plano jura independentes, mas
/// cujo código se referencia. Antes deste teste, esse plano era despachado em paralelo e o defeito
/// aparecia horas depois como conflito de merge, longe da causa.
/// </summary>
public sealed class PlanGraphValidationTests
{
    private const string Contrato = "T:App.Contrato";
    private const string Consumidor = "T:App.Consumidor";
    private const string Tela = "T:App.Tela";

    /// <summary>Consumidor depende de Contrato; Tela não depende de ninguém.</summary>
    private static CodeGraph Graph() => CodeGraph.Create(
        [
            new CodeGraphNode(Contrato, "App.Contrato", CodeGraphNodeKind.Type, "src/contrato.cs", "App"),
            new CodeGraphNode(Consumidor, "App.Consumidor", CodeGraphNodeKind.Type, "src/consumidor.cs", "App"),
            new CodeGraphNode(Tela, "App.Tela", CodeGraphNodeKind.Type, "src/tela.cs", "App")
        ],
        [new CodeGraphEdge(Consumidor, Contrato, CodeGraphEdgeKind.References)]);

    private static CardScope Card(string id, params string[] paths) => new(id, id, paths);

    [Fact]
    public void ADependencyThatExistsInTheCodeAndNotInThePlanBlocksDispatch()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-B", "src/consumidor.cs")],
            declaredEdges: [],
            Graph());

        Assert.False(validation.DispatchAllowed);
        Assert.Equal(PlanGraphValidationPolicy.ReasonBlocked, validation.ReasonCode);
        var divergence = Assert.Single(validation.Blocking);
        Assert.Equal(PlanGraphValidationPolicy.CodeUndeclaredDependency, divergence.Code);
        // A explicação tem de citar a aresta: bloqueio sem evidência é indistinguível de palpite.
        Assert.Equal([$"{Consumidor} -> {Contrato}"], divergence.Evidence);
        Assert.Contains("não foi declarada", divergence.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDependencyDeclaredInThePlanPassesClean()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-B", "src/consumidor.cs")],
            [new CardDependencyEdge("CARD-A", "CARD-B", "contrato")],
            Graph());

        Assert.True(validation.DispatchAllowed);
        Assert.Equal(PlanGraphValidationPolicy.ReasonConsistent, validation.ReasonCode);
        Assert.Empty(validation.Blocking);
        Assert.Equal(["CARD-A", "CARD-B"], validation.ValidatedCardIds);
    }

    [Fact]
    public void TwoCardsOnTheSameFileWithoutAnyOrderBlockDispatch()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-B", "src/contrato.cs")],
            declaredEdges: [],
            Graph());

        Assert.False(validation.DispatchAllowed);
        var divergence = Assert.Single(validation.Blocking);
        // Não é dependência: é dois agentes editando o mesmo tipo ao mesmo tempo.
        Assert.Equal(PlanGraphValidationPolicy.CodeSharedScope, divergence.Code);
        Assert.Equal([Contrato], divergence.Evidence);
    }

    [Fact]
    public void ADeclaredOrderWithoutCodeEvidenceIsRecordedAndDoesNotBlock()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-C", "src/tela.cs")],
            [new CardDependencyEdge("CARD-A", "CARD-C", "aprovacao-do-dono")],
            Graph());

        // O grafo vê acoplamento, não intenção. Uma ordem pode existir por regra de negócio, e
        // bloquear aqui seria o grafo se declarar mais sabido que o planejador.
        Assert.True(validation.DispatchAllowed);
        Assert.Empty(validation.Blocking);
        var observation = Assert.Single(validation.Advisory);
        Assert.Equal(PlanGraphValidationPolicy.CodeDeclaredWithoutEvidence, observation.Code);
        Assert.Contains("não bloqueia", observation.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentCardsWithNoCouplingPassWithoutObservations()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-C", "src/tela.cs")],
            declaredEdges: [],
            Graph());

        Assert.True(validation.DispatchAllowed);
        Assert.Empty(validation.All);
    }

    [Fact]
    public void ACardOutsideTheIndexIsUnverifiableAndIsNeverReportedAsValidated()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-FRONT", "frontend/src/tela.tsx")],
            declaredEdges: [],
            Graph());

        // Não bloqueia — senão o intervalo até o índice de TS existir paralisaria a campanha.
        Assert.True(validation.DispatchAllowed);
        Assert.Equal(["CARD-FRONT"], validation.UnverifiableCardIds);
        // E jamais sai como conferido: não conferido e conferido não podem usar a mesma porta.
        Assert.Equal(["CARD-A"], validation.ValidatedCardIds);
        Assert.DoesNotContain("CARD-FRONT", validation.ValidatedCardIds);
    }

    [Fact]
    public void AnUnverifiableCardDoesNotGenerateAFalseObservationAboutMissingEvidence()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-FRONT", "frontend/src/tela.tsx")],
            [new CardDependencyEdge("CARD-A", "CARD-FRONT", "contrato")],
            Graph());

        // Dizer "o plano declarou e o código não mostra" sobre um card que o índice não leu seria
        // apresentar ignorância como achado.
        Assert.Empty(validation.Advisory);
    }

    [Fact]
    public void TheDeclaredOrderCoversTheCouplingInEitherDirection()
    {
        // O plano pode ter acertado a existência da ordem e invertido o sentido; o grafo de código
        // não é a autoridade sobre qual card vem primeiro — só sobre haver acoplamento.
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-B", "src/consumidor.cs")],
            [new CardDependencyEdge("CARD-B", "CARD-A", "contrato")],
            Graph());

        Assert.True(validation.DispatchAllowed);
    }

    [Fact]
    public void ThePlanOfAnEmptyDemandIsConsistent()
    {
        var validation = PlanGraphValidationPolicy.Validate([], [], CodeGraph.Empty);

        Assert.True(validation.DispatchAllowed);
        Assert.Empty(validation.All);
        Assert.Empty(validation.ValidatedCardIds);
    }

    /// <summary>
    /// Recuperação de throughput da Fase 5 (2026-08-07): quatro cards de suporte (Briefing,
    /// DORA, etc.) sem superfície reconhecida herdavam o escopo INTEIRO do papel
    /// (CardPathScopePlanner, fallback conservador) e se bloqueavam aos pares — o backlog
    /// inteiro travava em "0 despachado(s)" com contas livres e nada rodando. Dois fallbacks
    /// idênticos não são evidência de colisão real; são evidência de que ninguém sabia de
    /// nenhum dos dois cards.
    /// </summary>
    [Fact]
    public void TwoCardsThatBothFellBackToTheWholeRoleScopeDoNotBlockEachOther()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-BRIEFING", "src/contrato.cs"), Card("CARD-DORA", "src/contrato.cs")],
            declaredEdges: [],
            Graph(),
            unnarrowedCardIds: new HashSet<string>(StringComparer.Ordinal) { "CARD-BRIEFING", "CARD-DORA" });

        Assert.True(validation.DispatchAllowed);
        Assert.Empty(validation.Blocking);
    }

    /// <summary>
    /// A segurança não afrouxa quando só UM lado é fallback: o escopo amplo genuinamente PODE
    /// tocar o arquivo estreito do outro card, e aí a colisão é real — não é o mesmo caso do
    /// achado acima, onde os dois lados eram igualmente vagos.
    /// </summary>
    [Fact]
    public void AFallbackScopeStillBlocksAgainstARealNarrowedScope()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-BROAD-FALLBACK", "src/contrato.cs"), Card("CARD-NARROW", "src/contrato.cs")],
            declaredEdges: [],
            Graph(),
            unnarrowedCardIds: new HashSet<string>(StringComparer.Ordinal) { "CARD-BROAD-FALLBACK" });

        Assert.False(validation.DispatchAllowed);
        var divergence = Assert.Single(validation.Blocking);
        Assert.Equal(PlanGraphValidationPolicy.CodeSharedScope, divergence.Code);
    }

    /// <summary>Sem o parâmetro novo (chamador antigo), o comportamento é idêntico ao de antes.</summary>
    [Fact]
    public void OmittingTheUnnarrowedSetPreservesThePreviousBehavior()
    {
        var validation = PlanGraphValidationPolicy.Validate(
            [Card("CARD-A", "src/contrato.cs"), Card("CARD-B", "src/contrato.cs")],
            declaredEdges: [],
            Graph());

        Assert.False(validation.DispatchAllowed);
        Assert.Equal(PlanGraphValidationPolicy.CodeSharedScope, Assert.Single(validation.Blocking).Code);
    }
}
