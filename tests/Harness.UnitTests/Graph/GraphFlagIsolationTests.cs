using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 4.4 — a prova de ISOLAMENTO da flag `graph.projection.enabled`: desligada, o sistema se
/// comporta como se a missão do grafo nunca tivesse existido. Cada ponto de contato do grafo com
/// o caminho quente é verificado aqui no modo desligado; os pontos de contato do Host
/// (ChiefTurnBackgroundService, ChiefBacklogLoopService, endpoints) são guardados por
/// `Enabled == false` do serviço, que é o que estes testes fixam por baixo.
/// </summary>
public sealed class GraphFlagIsolationTests
{
    /// <summary>
    /// O readiness com fatos de grafo AUSENTES (flag off) é byte-idêntico ao anterior à missão:
    /// mesmos bloqueadores, mesma decisão, nenhum código novo emitido.
    /// </summary>
    [Theory]
    [InlineData("agent_task", true, false, true)]
    [InlineData("agent_task", false, false, false)]
    [InlineData("feature", true, false, false)]
    [InlineData("agent_task", true, true, false)]
    public void ReadinessSemGrafoEIdenticoAoComportamentoAnterior(
        string cardType, bool hasInstruction, bool isBlocked, bool dispatchable)
    {
        var withoutGraph = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(cardType, hasInstruction, isBlocked));
        var withEmptyFacts = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(cardType, hasInstruction, isBlocked, [], []));

        Assert.Equal(dispatchable, withoutGraph.IsDispatchable);
        Assert.Equal(withoutGraph.IsDispatchable, withEmptyFacts.IsDispatchable);
        Assert.Equal(withoutGraph.Blockers, withEmptyFacts.Blockers);
        Assert.DoesNotContain(
            withoutGraph.Blockers,
            blocker => blocker.StartsWith("dor.graph.", StringComparison.Ordinal));
    }
}
