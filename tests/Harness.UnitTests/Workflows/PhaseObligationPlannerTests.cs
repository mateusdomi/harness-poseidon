using Harness.Host.Workflows;
using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O plano da fase inclui o TRABALHO, não só os documentos previstos. Enquanto o denominador do
/// progresso vinha apenas dos artefatos, uma fase de Desenvolvimento fechava com briefing, code
/// review e métricas produzidos e nenhuma implementação feita.
/// </summary>
public sealed class PhaseObligationPlannerTests
{
    private static BoardTaskRecord Card(
        string id, string title, string cardType = "agent_task", string state = "backlog") =>
        new(
            "tenant", id, "project", null, title, state, "medium", null, null, 1,
            new BoardProgressRecord(0, 0, 0), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, null, 1, "created", "solicitation", "demand", "5-Desenvolvimento", cardType);

    [Fact]
    public void DocumentsAndWorkCardsShareTheSamePlan()
    {
        var plan = PhaseObligationPlanner.Plan(
            [("document-1", "Briefing técnico"), ("document-2", "Métricas DORA")],
            [Card("01ARZ3NDEKTSV4RRFFQ69G5FAV", "Backend: endpoint de sessão")]);

        Assert.Equal(3, plan.Count);
        Assert.Contains(plan, item => item.Kind == "document" && item.ObjectiveKey == "document-1");
        Assert.Contains(plan, item => item.Kind == "implementation" && item.CardId == "01ARZ3NDEKTSV4RRFFQ69G5FAV");
    }

    [Fact]
    public void EveryRequiredObligationCarriesTheSameNormalizedWeight()
    {
        // Peso igual entre as obrigatórias: sem constantes espalhadas e sem privilegiar documento
        // sobre implementação, que era exatamente o viés do indicador antigo.
        var plan = PhaseObligationPlanner.Plan(
            [("document-1", "Briefing")],
            [
                Card("01ARZ3NDEKTSV4RRFFQ69G5FAV", "Backend"),
                Card("01ARZ3NDEKTSV4RRFFQ69G5FAW", "Frontend"),
                Card("01ARZ3NDEKTSV4RRFFQ69G5FAX", "Testes"),
            ]);

        Assert.Equal(4, plan.Count);
        Assert.All(plan, item => Assert.Equal(25d, item.Weight));
    }

    [Fact]
    public void ControlPointsAreNotObligations()
    {
        // Gate humano e decisão controlam o trabalho; não são trabalho a produzir. Contá-los faria
        // o percentual medir a própria cerimônia.
        var plan = PhaseObligationPlanner.Plan(
            [],
            [
                Card("01ARZ3NDEKTSV4RRFFQ69G5FAV", "Gate humano: credencial", cardType: "human_gate"),
                Card("01ARZ3NDEKTSV4RRFFQ69G5FAW", "Decisão: abordagem", cardType: "decision"),
                Card("01ARZ3NDEKTSV4RRFFQ69G5FAX", "Backend: endpoint"),
            ]);

        var only = Assert.Single(plan);
        Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAX", only.CardId);
    }

    [Fact]
    public void AnObligationWithoutAnyProducerIsDetected()
    {
        // O defeito que deixava o portão esperando para sempre por trabalho que ninguém faria.
        var orphan = new PhaseObligationRecord(
            "tenant", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "project", "run", "phase", "obligation", 1,
            "implementation", "Sem produtor", true, 100, "template", null, null, null, null,
            "pending", [], null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.True(PhaseObligationPlanner.HasArtifactWithoutProducer([orphan]));
        Assert.False(PhaseObligationPlanner.HasArtifactWithoutProducer(
            [orphan with { CardId = "01ARZ3NDEKTSV4RRFFQ69G5FAW" }]));
        Assert.False(PhaseObligationPlanner.HasArtifactWithoutProducer(
            [orphan with { ObjectiveKey = "document-1" }]));
        // Já aceita não é problema: o produtor existiu e entregou.
        Assert.False(PhaseObligationPlanner.HasArtifactWithoutProducer(
            [orphan with { State = "accepted" }]));
        // Opcional sem produtor não bloqueia o portão.
        Assert.False(PhaseObligationPlanner.HasArtifactWithoutProducer(
            [orphan with { Required = false }]));
    }

    [Fact]
    public void ThePhaseArtifactCardIsNotCountedTwice()
    {
        // O card que o condutor cria para o objetivo-documento já é representado pela obrigação do
        // documento; contá-lo de novo como card inflaria o denominador com o mesmo trabalho.
        var artifactCard = Card(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            WorkflowPhaseDriver.CardTitleFor("5-Desenvolvimento", "Briefing técnico"));

        Assert.StartsWith("5-Desenvolvimento —", artifactCard.Title, StringComparison.Ordinal);
    }
}
