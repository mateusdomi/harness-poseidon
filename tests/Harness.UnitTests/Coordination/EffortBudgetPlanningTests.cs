using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Fase 1B — regressão permanente do orçamento determinístico no plano.
///
/// `EffortPolicy` existia pronta e não governava nada: o planner não gravava orçamento no card e o
/// despacho decidia esforço por hábito. A tendência de um orquestrador sem regra é sempre a mesma —
/// mais agentes, mais rodadas, revisão mais funda, como se esforço fosse gratuito e proporcional à
/// qualidade.
/// </summary>
public sealed class EffortBudgetPlanningTests
{
    [Fact]
    public void TheSameDemandAlwaysProducesTheSameBudgets()
    {
        var request = new DemandDecompositionRequest(
            "CAT-10 Publicar catálogo",
            "Implementar o endpoint de catálogo e a tela de listagem.",
            ["O catálogo responde 200.", "A tela lista os itens."],
            "high");

        var first = DemandDecompositionPlanner.Plan(request);
        var second = DemandDecompositionPlanner.Plan(request);

        // Determinismo é requisito, não elegância: se a mesma demanda receber orçamentos diferentes
        // em dias diferentes, ninguém consegue dizer se o resultado melhorou pelo plano ou pela
        // sorte.
        Assert.Equal(first.Cards.Count, second.Cards.Count);
        Assert.All(first.Cards, card => Assert.NotNull(card.Budget));
        for (var index = 0; index < first.Cards.Count; index++)
        {
            Assert.Equal(first.Cards[index].Budget, second.Cards[index].Budget);
        }
    }

    [Fact]
    public void ASmallDemandIsBudgetedForASingleAgent()
    {
        var plan = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
            "UI-02 Corrigir o rótulo do botão",
            "Trocar o texto do botão de salvar na tela de perfil.",
            ["O rótulo mostra 'Salvar alterações'."],
            "low",
            new DemandDecompositionHints(HasFrontendSurface: true, HasImplementationSurface: false)));

        // Demanda pequena e visual é UM agente. Três agentes numa troca de texto produzem três
        // versões da mesma linha e um conflito.
        Assert.All(plan.Cards, card =>
        {
            Assert.NotNull(card.Budget);
            Assert.Equal(1, card.Budget!.Agents);
            Assert.False(card.Budget.FanOutAllowed);
        });
    }

    [Fact]
    public void StructuralWorkIsReviewedDeeperThanVisualWork()
    {
        var structural = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
            "API-03 Alterar o contrato de pedidos",
            "Alterar o endpoint de pedidos e a migration da tabela de itens do pedido.",
            ["O contrato novo responde 200.", "A migration aplica em banco vazio e existente."],
            "critical",
            new DemandDecompositionHints(HasFrontendSurface: false, HasImplementationSurface: true)));
        var visual = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
            "UI-03 Ajustar espaçamento do cabeçalho",
            "Ajustar o espaçamento do cabeçalho na tela inicial.",
            ["O cabeçalho respeita o espaçamento do design."],
            "low",
            new DemandDecompositionHints(HasFrontendSurface: true, HasImplementationSurface: false)));

        var structuralDepth = structural.Cards
            .Where(card => card.CardType == "agent_task")
            .Max(card => card.Budget!.ReviewDepth);
        var visualDepth = visual.Cards
            .Where(card => card.CardType == "agent_task")
            .Max(card => card.Budget!.ReviewDepth);

        // Revisar mais fundo se paga onde a mudança é estrutural — e é desperdício onde o resultado
        // se confere olhando.
        Assert.True(
            structuralDepth > visualDepth,
            $"estrutural={structuralDepth} deveria ser mais fundo que visual={visualDepth}");
    }
}
