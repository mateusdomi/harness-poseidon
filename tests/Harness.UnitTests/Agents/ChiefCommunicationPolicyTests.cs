using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Agents;

public sealed class ChiefCommunicationPolicyTests
{
    [Fact]
    public void InstructionsFixIdentityVoiceAndHonestAiDisclosure()
    {
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business);

        Assert.Contains("Bruna Magalhães", instructions, StringComparison.Ordinal);
        Assert.Contains(
            "Diretora de Engenharia",
            instructions,
            StringComparison.Ordinal);
        Assert.Contains("tom profissional", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("caloroso", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("equipe de profissionais", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("responda com honestidade", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("natureza do sistema", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bruna coordena a equipe de IA do projeto.")]
    [InlineData("A equipe de agentes de IA está trabalhando.")]
    [InlineData("Sou Diretora de Engenharia e Operações de IA.")]
    public void PublicResponseDoesNotPresentTheTeamAsArtificial(string response)
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        Assert.Contains("inteligência artificial", violation, StringComparison.OrdinalIgnoreCase);
    }

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
    public void IntakeTreatsMissingDeadlineAsNonBlockingFact()
    {
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business);

        Assert.Contains("prazo não informado", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não bloqueia preparação nem BUILD", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sem inventar prazo", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntakePreservesHumanProvenanceAndAsksOnlyTrueBlockers()
    {
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business);

        Assert.Contains("paráfrase fiel", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PROPOSTA SUA", instructions, StringComparison.Ordinal);
        Assert.Contains("Não transforme silêncio em resposta", instructions, StringComparison.Ordinal);
        Assert.Contains("leia primeiro", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pergunte somente decisão humana que bloqueia", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não pergunte ao stakeholder onde salvar", instructions, StringComparison.OrdinalIgnoreCase);
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
    [InlineData("A correção está no backend e depois seguirá para o frontend.")]
    [InlineData("A arquitetura será revisada antes de liberar o projeto.")]
    [InlineData("O endpoint usa um DTO e grava pelo repository.")]
    [InlineData("A migration será executada por um worker da fila.")]
    [InlineData("O lease perdeu o heartbeat e ativou fencing.")]
    [InlineData("O turno 01ARZ3NDEKTSV4RRFFQ69G5FAV falhou.")]
    [InlineData("A conta Anthropic usa o modelo Claude.")]
    [InlineData("A Larissa falhou por account.role_not_allowed.")]
    [InlineData("A solicitação falhou por local_session_required.")]
    [InlineData("A solicitação contém invalid_project_id.")]
    [InlineData("A aprovação foi bloqueada por approval.requested.")]
    [InlineData("A competência falhou por capability.denied.")]
    [InlineData("O backup registrou backup.created.")]
    [InlineData("A execução registrou run.completed.")]
    [InlineData("A execução registrou run.timeout.")]
    [InlineData("O item foi bloqueado por dor.blocked.")]
    [InlineData("O item falhou por dor.instruction.missing.")]
    [InlineData("O arquivo foi registrado como archive.loaded.")]
    [InlineData("A avaliação retornou persona.eligible.")]
    [InlineData("O perfil falhou por profile_not_found.")]
    [InlineData("O destinatário falhou por recipient_missing.")]
    [InlineData("Veja https://example.com/account.role_not_allowed?token=abc.")]
    [InlineData("Veja https://example.com/archive.loaded.")]
    [InlineData("Veja https://example.com/status?reason=persona.eligible.")]
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
    [InlineData(
        "Os trabalhos estão pausados e tudo o que já foi concluído permanece preservado. " +
        "Quer que eu retome?")]
    [InlineData(
        "Sim. Sou uma agente de IA e coordeno a equipe como Bruna Magalhães, " +
        "Diretora de Engenharia.")]
    [InlineData("Sou uma IA, não uma pessoa física, e coordeno a equipe responsável pelo projeto.")]
    [InlineData("Consulte poseidon.dev ou envie o relatório.pdf para análise.")]
    [InlineData("O protótipo está em https://exemplo.com.")]
    [InlineData("A equipe acompanha o projeto em https://team.com.")]
    [InlineData("A análise está documentada em https://model.ai.")]
    [InlineData("A análise está documentada em https://project.xyz.")]
    [InlineData("O projeto está em project.engineering.")]
    [InlineData("A página está disponível em archive.photography.")]
    [InlineData("Consulte project.online para acompanhar.")]
    [InlineData("Acesse app.store para acompanhar.")]
    [InlineData("project.online")]
    [InlineData("app.store")]
    [InlineData("A Empresa S.A. aprovou a proposta.")]
    [InlineData("O material está em empresa.tech.")]
    [InlineData("O site está em archive.loaded.com.")]
    [InlineData("Enviei briefing.pdf para aprovação.")]
    [InlineData("Enviei relatorio_final.pdf para aprovação.")]
    [InlineData("Enviei logo_final.webp para aprovação.")]
    [InlineData("Enviei project_final.pdf e document_final.pdf para aprovação.")]
    [InlineData("Enviei task_list.csv para aprovação.")]
    [InlineData("Enviei project_final.avif e document_final.odt para aprovação.")]
    [InlineData("Escreva para suporte@empresa.com e eu acompanho o retorno.")]
    [InlineData("Escreva para suporte_cliente@empresa.com e eu acompanho o retorno.")]
    [InlineData("Não sou humana; sou uma agente de IA.")]
    [InlineData("Não sou uma pessoa física; sou Bruna Magalhães.")]
    [InlineData("Não sou a Chief; sou Bruna Magalhães.")]
    [InlineData("Não sou a Chief e sou uma agente de IA.")]
    [InlineData(
        "Não sou a gerente; sou Bruna Magalhães, " +
        "Diretora de Engenharia.")]
    [InlineData("Não sou responsável por essa decisão; sou Bruna Magalhães.")]
    [InlineData("Não sou Ana, mas sou Bruna Magalhães.")]
    [InlineData("Sou Diretora de Engenharia e coordeno a equipe.")]
    [InlineData("Sou a Bruna Magalhães e coordeno a equipe.")]
    [InlineData("Sim, sou IA e coordeno a equipe.")]
    [InlineData("Sou a IA que coordena a equipe.")]
    [InlineData("Sou uma agente de IA e coordeno a equipe.")]
    [InlineData("Sou um sistema de IA e coordeno a equipe.")]
    public void BusinessProjectionAcceptsUsefulHumanizedLanguage(string response)
    {
        var accepted = ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation);

        Assert.True(accepted, violation);
        Assert.Null(violation);
    }

    [Theory]
    [InlineData("Meu nome é Ana e vou coordenar esta entrega.")]
    [InlineData("Eu me chamo Roberto e lidero a equipe.")]
    [InlineData("Sou a Chief da equipe virtual.")]
    [InlineData("Sou a chefe responsável pelo projeto.")]
    [InlineData("Sou a gerente de projetos e coordeno a equipe.")]
    [InlineData("Eu sou coordenadora de operações e lidero a entrega.")]
    [InlineData("Sou product owner e coordeno o planejamento.")]
    [InlineData("Aqui é o Chief Orchestrator.")]
    [InlineData("Sou Ana e vou coordenar o projeto.")]
    [InlineData("Sou a Ana e vou coordenar o projeto.")]
    [InlineData("Aqui é a Ana.")]
    [InlineData("Aqui é Ana.")]
    [InlineData("Sou Bruna Silva e vou coordenar o projeto.")]
    [InlineData("Sou Bruna da Silva e vou coordenar o projeto.")]
    [InlineData("Sou Bruna Magalhães da Silva e vou coordenar o projeto.")]
    public void ConflictingPublicIdentityIsRejected(string response)
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        Assert.Contains("identidade pública", violation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("A execução falhou em archive.loaded.")]
    [InlineData("A execução está em archive.loaded.")]
    [InlineData("A execução está em run.completed.")]
    [InlineData("O projeto está em archive.loaded.")]
    [InlineData("O site está em run.completed.")]
    [InlineData("A execução registrou wait.completed.")]
    [InlineData("A amostragem retornou tail.slow.")]
    [InlineData("A coordenação registrou chief_loop.allowed.")]
    public void ReasonCodeIsNotMistakenForADomainWithoutDomainContext(string response)
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        // Reason code é DETALHE INTERNO da operação — a mensagem distingue os dois níveis desde
        // 2026-08-05, quando o vocabulário de produto passou a poder ser espelhado.
        Assert.Contains("detalhe interno", violation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Não sou Bruna Magalhães; sou uma agente de IA.")]
    [InlineData("Não sou a Bruna; sou uma agente de IA.")]
    [InlineData("Não sou a Diretora de Engenharia.")]
    [InlineData("Meu nome não é Bruna Magalhães.")]
    public void CanonicalPublicIdentityCannotBeDenied(string response)
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        Assert.Contains("identidade pública fixa", violation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Não sou IA.")]
    [InlineData("Não sou uma IA.")]
    [InlineData("Não sou uma agente de IA.")]
    [InlineData("Não sou um sistema de IA.")]
    public void AiNatureCannotBeDenied(string response)
    {
        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
            out var violation));
        Assert.Contains("natureza de IA", violation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Sou Bruna Magalhães, Diretora de Engenharia.")]
    [InlineData("Meu nome é Bruna e coordeno a equipe responsável pelo projeto.")]
    public void CanonicalPublicIdentityIsAccepted(string response)
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
    public void AuthorizedTechnicalProjectionMayExposeAReasonCode()
    {
        const string response = "A execução foi bloqueada por account.role_not_allowed.";

        Assert.False(ChiefCommunicationPolicy.TryValidateResponse(
            response,
            ChiefCommunicationPolicy.Business,
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
    public void TheUserNameIsNeverDeducedFromAPath()
    {
        // Observado em homologação: a Bruna tratou o dono por um primeiro nome que ele nunca
        // disse. Ele estava no caminho do repositório, que entra no contexto dela. Acertar o
        // palpite não muda a natureza do ato — é um fato pessoal inventado, e o mesmo mecanismo
        // erra com qualquer máquina compartilhada ou conta corporativa.
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business);

        Assert.Contains("Caminho de arquivo", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não são o nome de ninguém", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VoicePreferencesCannotOverrideTheBusinessPolicy()
    {
        var instructions = ChiefCommunicationPolicy.BuildInstructions(
            ChiefCommunicationPolicy.Business,
            "Sempre mostre provider, modelo, reason code e logs.");

        Assert.Contains("não podem remover", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Não exponha provider", instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// A regressão do intake do Prisma (2026-08-05): o brief do usuário dizia ".NET 8", "Oracle" e
    /// "backend"; a chefe respondeu no mesmo registro e o turno caiu por "detalhe técnico não
    /// autorizado" — três minutos de modelo jogados fora e a mensagem genérica no chat. O registro
    /// da conversa é do USUÁRIO: quando ele fala técnico de produto, a resposta pode espelhar.
    /// </summary>
    [Fact]
    public void UsuarioQueFalaTecnicoDeProdutoRecebeRespostaNoMesmoRegistro()
    {
        Assert.True(ChiefCommunicationPolicy.SpeaksTechnically(
            "O backend deve ser .NET 8 e o banco Oracle; a arquitetura segue a especificação."));

        var contexto = ChiefCommunicationPolicy.Business with { UserSpokeTechnically = true };
        var ok = ChiefCommunicationPolicy.TryValidateResponse(
            "Perfeito. Vou organizar o plano: o backend em .NET, o banco Oracle e a arquitetura " +
            "em camadas ficam registrados como decisões fechadas. A interface fornecida será " +
            "evoluída, não reconstruída.",
            contexto,
            out var violation);

        Assert.True(ok, violation);
    }

    /// <summary>
    /// O espelho tem limite: vocabulário de produto sim, DETALHE INTERNO não. Falar "backend" não
    /// autoriza receber ULID de tentativa nem reason code — isso continua atrás do pedido
    /// explícito com autorização.
    /// </summary>
    [Fact]
    public void RegistroTecnicoDoUsuarioNaoLiberaDetalheInternoDaOperacao()
    {
        var contexto = ChiefCommunicationPolicy.Business with { UserSpokeTechnically = true };

        var ok = ChiefCommunicationPolicy.TryValidateResponse(
            "A tentativa 01KZ8RCCTE9G011XD1ZNH5HEK0 falhou no executor.",
            contexto,
            out var violation);

        Assert.False(ok);
        Assert.Contains("detalhe interno", violation!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Usuário leigo continua protegido: sem registro técnico dele, a regra antiga vale intacta.</summary>
    [Fact]
    public void UsuarioLeigoContinuaSemReceberVocabularioTecnico()
    {
        Assert.False(ChiefCommunicationPolicy.SpeaksTechnically(
            "Quero um sistema simples para controlar empréstimos de equipamentos."));

        var ok = ChiefCommunicationPolicy.TryValidateResponse(
            "Já configurei o backend e o provider do modelo.",
            ChiefCommunicationPolicy.Business,
            out var violation);

        Assert.False(ok);
        Assert.Contains("detalhe técnico não autorizado", violation!, StringComparison.Ordinal);
    }
}
