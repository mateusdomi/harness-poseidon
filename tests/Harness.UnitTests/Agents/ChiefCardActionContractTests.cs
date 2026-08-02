using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A decisão do dono sobre um card escalado precisa ter forma de AÇÃO, não de texto.
///
/// Sem isto o laço de escalação ficava aberto: a Bruna chamava o dono, ele respondia reduzindo o
/// escopo, ela confirmava a decisão — e o card seguia escalado, enquanto ela anunciava que "essa
/// parte volta a andar". Progresso relatado sem progresso real é a pior falha para quem confia no
/// sistema de longe.
/// </summary>
public sealed class ChiefCardActionContractTests
{
    private const string Card = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static string Envelope(string cardActions) =>
        "{\"response\":\"Decisão registrada.\",\"demands\":[]," +
        "\"intent\":\"decidir_escalacao\",\"intentConfidence\":0.9," +
        "\"cardActions\":" + cardActions + "}";

    private static string Action(string action, string cardId, string instruction) =>
        "[{\"action\":\"" + action + "\",\"cardId\":\"" + cardId +
        "\",\"instruction\":\"" + instruction + "\"}]";

    [Fact]
    public void AnOwnerDecisionBecomesAReplanAction()
    {
        var output = ChiefTurnOutputContract.Parse(Envelope(Action(
            "replan",
            Card,
            "Registrar emprestimo, devolucao e atraso no log da propria aplicacao; sem painel.")));

        var action = Assert.Single(output.CardActions!);
        Assert.Equal("replan", action.Action);
        Assert.Equal(Card, action.CardId);
        Assert.Contains("sem painel", action.Instruction, StringComparison.Ordinal);
    }

    /// <summary>Ausente é o caso comum: a maioria dos turnos não mexe em card nenhum.</summary>
    [Fact]
    public void AbsentCardActionsAreSimplyNothing()
    {
        var output = ChiefTurnOutputContract.Parse(
            "{\"response\":\"Oi!\",\"demands\":[],\"intent\":\"conversa_geral\",\"intentConfidence\":0.9}");

        Assert.Null(output.CardActions);
    }

    /// <summary>
    /// Conjunto FECHADO. Interpretar texto do modelo como comando é o caminho por onde a
    /// autoridade vaza.
    /// </summary>
    [Fact]
    public void AnUnknownCardActionIsRefusedInsteadOfInterpreted()
    {
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(Envelope(Action(
                "cancelar", Card, "cancelar esse card agora mesmo, sem revisao"))));
    }

    /// <summary>
    /// O identificador é ULID de tamanho fixo: aceitar texto livre deixaria o modelo apontar para
    /// qualquer coisa.
    /// </summary>
    [Fact]
    public void ACardIdThatIsNotAUlidIsRefused()
    {
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(Envelope(Action(
                "replan", "o-card-da-observabilidade", "reduzir o escopo dessa parte do trabalho"))));
    }

    /// <summary>Uma instrução curta demais não redireciona trabalho nenhum.</summary>
    [Fact]
    public void AnEmptyInstructionIsRefused()
    {
        Assert.Throws<AgentOutputValidationException>(() =>
            ChiefTurnOutputContract.Parse(Envelope(Action("replan", Card, "menos"))));
    }
}
