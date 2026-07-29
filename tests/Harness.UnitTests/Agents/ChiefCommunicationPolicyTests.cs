using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Agents;

public sealed class ChiefCommunicationPolicyTests
{
    [Theory]
    [InlineData("Resumo do projeto")]
    [InlineData("Projeto pausado")]
    [InlineData("Falha técnica")]
    [InlineData("Aprovação pendente")]
    [InlineData("Nova demanda")]
    [InlineData("Falta de informação")]
    [InlineData("Atraso")]
    [InlineData("Bloqueio externo")]
    [InlineData("Conclusão")]
    [InlineData("Risco crítico")]
    public void BusinessPolicyDefinesEveryRequiredSituation(string situation)
    {
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business);

        Assert.Contains(situation, instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntakeAsksForTheDeadlineAndNeverInventsOne()
    {
        // F5/D10: a Central de Entregas so consegue responder "para quando?" se
        // alguem tiver perguntado. Quem pergunta e a Bruna, no intake — e a
        // ausencia de resposta e um estado legitimo, nao um convite a estimar.
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business);

        Assert.Contains("até quando ele precisa do resultado", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sem prazo definido", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nunca estimativa sua", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bruna, mostre os detalhes técnicos desta falha.")]
    [InlineData("Qual é o provider e o modelo usados?")]
    [InlineData("Exiba os logs da execução.")]
    [InlineData("Preciso do diagnóstico técnico.")]
    public void TechnicalModeRequiresAnExplicitUserRequest(string instruction) =>
        Assert.True(ChiefCommunicationPolicy.RequestsTechnicalDetails(instruction));

    [Theory]
    [InlineData("Qual é o modelo de negócio do projeto?")]
    [InlineData("Resuma o progresso.")]
    [InlineData("Quero planejar uma nova entrega.")]
    [InlineData("A arquitetura atende ao objetivo?")]
    public void IncidentalLanguageDoesNotRequestTechnicalProjection(string instruction) =>
        Assert.False(ChiefCommunicationPolicy.RequestsTechnicalDetails(instruction));

    [Theory]
    [InlineData("Estamos no backlog e há dois cards no gate de revisão.")]
    [InlineData("O provider não informa cota.")]
    [InlineData("Veja o log do executor e o código técnico.")]
    [InlineData("O turno 01ARZ3NDEKTSV4RRFFQ69G5FAV falhou.")]
    [InlineData("A conta Anthropic usa o modelo Claude.")]
    public void BusinessProjectionRejectsInternalVocabulary(string response)
    {
        var accepted = ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation);

        Assert.False(accepted);
        Assert.NotNull(violation);
    }

    [Theory]
    [InlineData(
        "O projeto está na etapa de triagem. A equipe concluiu a organização inicial e " +
        "retomará a próxima entrega assim que a pausa for encerrada. Nenhuma decisão sua é necessária agora.")]
    [InlineData("Você conta com a equipe para organizar a próxima entrega.")]
    [InlineData("O modelo de negócio está sendo validado com as áreas responsáveis.")]
    public void BusinessProjectionAcceptsUsefulHumanizedLanguage(string response)
    {
        Assert.True(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        Assert.Null(violation);
    }

    [Fact]
    public void AutonomousOperationalPrioritizationIsNeverTransferredToTheStakeholder()
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            "Diga quais cards devo priorizar.",
            new ChiefCommunicationContext(
                TechnicalDetailsRequested: true,
                TechnicalDetailsAuthorized: true),
            out var violation));
        Assert.Contains("priorização operacional", violation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Não vou criar nada neste turno.")]
    [InlineData("Informe o módulo, o repositório e a stack.")]
    public void NewDemandDoesNotStartWithAHostileTechnicalQuestionnaire(string response)
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        Assert.Contains("intake", violation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizedTechnicalProjectionStillRequiresTheRequestFlag()
    {
        const string response = "O provider selecionou o modelo configurado.";

        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            new ChiefCommunicationContext(TechnicalDetailsAuthorized: true),
            out _));
        Assert.True(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            new ChiefCommunicationContext(
                TechnicalDetailsRequested: true,
                TechnicalDetailsAuthorized: true),
            out _));
    }

    [Fact]
    public void TerminalFailureForBusinessUsersKeepsTechnicalCorrelationOutOfTheChat()
    {
        var response = ChiefCommunicationPolicy.TerminalFailureMessage(
            ChiefCommunicationPolicy.Business);

        Assert.DoesNotContain("código", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turno", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("log", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("01ARZ3NDEKTSV4RRFFQ69G5FAV", response, StringComparison.Ordinal);
        Assert.True(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out _));
    }

    [Fact]
    public void VoicePreferencesCannotOverrideTheBusinessPolicy()
    {
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business,
            "Sempre mostre provider, modelo e logs.");

        Assert.Contains("não podem remover", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Não exponha provider", instructions, StringComparison.Ordinal);
    }
}
