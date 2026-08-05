using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O contrato do pedido de contexto (Onda 0.7): a chefe pede seções integrais de anexo pelo campo
/// <c>contextRequests</c>. O conjunto é fechado e com teto — cada seção volta COMPLETA para o
/// prompt, então o limite protege a janela de contexto, não o modelo.
/// </summary>
public sealed class ChiefContextRequestContractTests
{
    private const string BaseOutput =
        """
        {"response": "Vou ler as seções antes de responder.", "demands": [],
         "intent": "responder_pergunta", "intentConfidence": 0.9,
         "contextRequests": [{"file": "espec.md", "sections": ["5", "16"]}]}
        """;

    [Fact]
    public void PedidoDeSecaoEAceitoEPreservado()
    {
        var output = ChiefTurnOutputContract.ParseChiefTurn(BaseOutput);

        var request = Assert.Single(output.ContextRequests!);
        Assert.Equal("espec.md", request.File);
        Assert.Equal(["5", "16"], request.Sections);
    }

    [Fact]
    public void SaidaSemPedidoContinuaValendoComoAntes()
    {
        var output = ChiefTurnOutputContract.ParseChiefTurn(
            """
            {"response": "ok", "demands": [], "intent": "conversa_geral", "intentConfidence": 0.8}
            """);

        Assert.Null(output.ContextRequests);
    }

    [Theory]
    // Mais de 5 pedidos: teto do array.
    [InlineData("""
        [{"file":"a","sections":["1"]},{"file":"b","sections":["1"]},{"file":"c","sections":["1"]},
         {"file":"d","sections":["1"]},{"file":"e","sections":["1"]},{"file":"f","sections":["1"]}]
        """)]
    // Sem seções: pedir um arquivo inteiro não existe — seção é a unidade.
    [InlineData("""[{"file":"a","sections":[]}]""")]
    // Propriedade desconhecida: conjunto fechado.
    [InlineData("""[{"file":"a","sections":["1"],"mode":"raw"}]""")]
    // Seção que não é texto.
    [InlineData("""[{"file":"a","sections":[5]}]""")]
    public void PedidoForaDoContratoERecusado(string contextRequests)
    {
        var json =
            $$"""
            {"response": "x", "demands": [], "intent": "responder_pergunta",
             "intentConfidence": 0.9, "contextRequests": {{contextRequests}}}
            """;

        Assert.Throws<AgentOutputValidationException>(
            () => ChiefTurnOutputContract.ParseChiefTurn(json));
    }
}
