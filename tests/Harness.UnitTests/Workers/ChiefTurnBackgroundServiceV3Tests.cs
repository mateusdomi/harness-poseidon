using Harness.Host.Workers;
using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Workers;

public sealed class ChiefTurnBackgroundServiceV3Tests
{
    [Fact]
    public void NegativeMentionOfHumanDecisionDoesNotCreatePendingInput()
    {
        Assert.False(ChiefTurnBackgroundService.IsPendingHumanDecision(
            "Escopo explícito assumido integralmente — nenhuma redução a MVP menor foi proposta, pois não há conflito real de prazo, orçamento ou decisão humana."));

        Assert.True(ChiefTurnBackgroundService.IsPendingHumanDecision(
            "Decisão humana necessária: escolher o fornecedor externo antes da BUILD."));
    }

    [Fact]
    public void V3UnderstandResponseUsesUnderstandingTerminology()
    {
        var output = new ChiefTurnOutput(
            "O projeto está em triagem, na etapa de triagem.",
            [],
            Intent: ChiefTurnIntent.PlanejarDemanda,
            IntentConfidence: 0.9,
            UnderstandingUpdate: new ChiefUnderstandingUpdate(ProjectSummary: "Sistema"));

        var normalized = ChiefTurnBackgroundService.NormalizeV3ActiveResponseTerminology(output);

        Assert.DoesNotContain("triagem", normalized.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entendimento", normalized.Response, StringComparison.OrdinalIgnoreCase);
    }
}
