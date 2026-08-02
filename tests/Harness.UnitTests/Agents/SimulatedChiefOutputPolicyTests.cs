using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.Modules.Conversations.Application;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A resposta do executor simulado passa pelo MESMO validador de comunicação que a do executor
/// real. Se ela não satisfaz a política, o modo simulado está quebrado por construção: o turno
/// falha com um veredito correto sobre um texto que o próprio sistema escreveu.
/// </summary>
public sealed class SimulatedChiefOutputPolicyTests
{
    [Theory]
    [InlineData("Oi, tudo bem?")]
    // O caso que quebrou o dogfood: o usuário escreve vocabulário técnico e a resposta o repetia.
    [InlineData("Precisamos expor o status do serviço.\n" +
                "DEMANDA: Implementar endpoint de status | Adicionar o endpoint /status ao serviço")]
    [InlineData("Quero um provider novo, com worktree, branch e gates de arquitetura.")]
    public async Task ASaidaDoSimuladoSatisfazAPoliticaDeNegocio(string instruction)
    {
        var result = await new FakeAgentExecutor().ExecuteAsync(
            Request(instruction),
            CancellationToken.None);

        var output = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        Assert.True(
            ChiefCommunicationPolicy.TryValidateResponse(
                output.Response, ChiefCommunicationPolicy.Business, out var violation),
            $"A resposta simulada violou a política: {violation}");
    }

    [Fact]
    public async Task ARespostaSimuladaNaoDevolveOTextoDoUsuario()
    {
        const string secretish = "Adicionar o endpoint /status ao serviço";
        var result = await new FakeAgentExecutor().ExecuteAsync(
            Request($"Preciso de ajuda.\nDEMANDA: Status | {secretish}"),
            CancellationToken.None);

        var output = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        Assert.DoesNotContain(secretish, output.Response, StringComparison.Ordinal);
    }

    private static AgentExecutionRequest Request(string instruction) =>
        new(
            "01ARZ3NDEKTSV4RRFFQ69G5FA1",
            "01ARZ3NDEKTSV4RRFFQ69G5FA2",
            "01ARZ3NDEKTSV4RRFFQ69G5FA3",
            "01ARZ3NDEKTSV4RRFFQ69G5FA5",
            instruction,
            "{}",
            AppContext.BaseDirectory);

    [Fact]
    public void AConfirmacaoDeterministicaTambemNaoEcoaAMensagem()
    {
        var reply = string.Concat(ConversationApplicationService.ComposeDeterministicReply(
            "Adicionar o endpoint /status ao serviço"));

        Assert.DoesNotContain("endpoint", reply, StringComparison.OrdinalIgnoreCase);
        Assert.True(ChiefCommunicationPolicy.TryValidateResponse(
            reply, ChiefCommunicationPolicy.Business, out _));
    }
}
