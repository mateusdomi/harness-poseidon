using Harness.Host.Agents;
using Harness.Host.Workflows;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.UnitTests.Workflows;

public sealed class WorkflowPhaseCardContextTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("approved", "review", true)]
    [InlineData("running", "development", false)]
    [InlineData("rejected", "corrections", false)]
    public void NewHumanInformationCreatesARevisionOnlyAfterThePreviousExecutionStabilizes(
        string state, string internalState, bool expected)
    {
        var card = new BoardTaskRecord(
            "tenant", "task", "project", "demand", "1-Triagem — Ficha", state,
            "low", null, null, 1, new(0, 0, 0), Now, Now, null, null, 1,
            internalState, "solicitation", "demand", "1-Triagem", "documento");

        Assert.Equal(
            expected,
            WorkflowPhaseDriver.NeedsDocumentRevision(Now.AddMinutes(1), card));
        Assert.False(WorkflowPhaseDriver.NeedsDocumentRevision(Now, card));
    }

    [Theory]
    [InlineData("3-Arquitetura", "SAD Ideal e Restrito", "Não tenho prazo fixo. Pode seguir.", false)]
    [InlineData("4-Planejamento", "Cronograma de releases", "Preciso até dezembro.", true)]
    [InlineData("3-Arquitetura", "SAD Ideal e Restrito", "Também quero registrar uma foto do equipamento.", true)]
    [InlineData("3-Arquitetura", "Threat Model STRIDE", "Uma pessoa responsável fará os registros.", true)]
    public void LateInformationOnlyRevisesDocumentsItCanMateriallyAffect(
        string phase,
        string objective,
        string message,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkflowPhaseDriver.IsDocumentRevisionRelevant(phase, objective, message));
    }

    private static readonly string[] ArchitectureObjectives =
    [
        "SAD Ideal e Restrito", "ADRs", "C4 (Contexto e Contêiner)", "DER",
        "Comparativo de trade-off", "Threat Model STRIDE", "Plano de Observabilidade",
    ];

    /// <summary>
    /// O motor de retrabalho medido na prova limpa de 2026-08-02: cinco decisões do usuário,
    /// cada uma sobre UM artefato, geraram uma corrente de atualizações de artefatos que a
    /// mensagem nem citava — "Decisão sobre o Plano de Observabilidade" reescrevendo o
    /// Comparativo de trade-off, o DER e o C4. As métricas da operação diziam que retrabalho
    /// era 73% do custo sem apontar de onde vinha; vinha daqui.
    /// </summary>
    [Theory]
    [InlineData(
        "Decisao sobre o Plano de Observabilidade: reduzir o escopo. Sem painel, sem alerta.",
        "Plano de Observabilidade")]
    [InlineData(
        "E sobre o SAD Ideal e Restrito que voce trouxe para eu decidir: vale reduzir.",
        "SAD Ideal e Restrito")]
    [InlineData(
        "Sobre a atualizacao do C4 que você trouxe: reduza o que essa parte precisa entregar.",
        "C4 (Contexto e Contêiner)")]
    public void ADecisionAboutOneArtifactDoesNotRewriteTheWholePhase(string message, string expected)
    {
        Assert.Equal(
            expected,
            WorkflowPhaseDriver.SingleObjectiveNamedBy(message, ArchitectureObjectives));
    }

    /// <summary>
    /// O estreitamento é tímido de propósito. Sem menção, ou com duas, volta a valer o
    /// conservador — perder uma regra de negócio continua sendo pior que uma revisão a mais.
    /// E a borda de palavra importa: "der" dentro de "perder" não nomeia o DER.
    /// </summary>
    [Theory]
    [InlineData("Também quero registrar uma foto do equipamento.")]
    [InlineData("Sobre o DER e o C4: mantenham os dois alinhados.")]
    [InlineData("Não quero perder o histórico de devoluções.")]
    public void AnAmbiguousOrSilentMessageKeepsTheConservativeBehaviour(string message)
    {
        Assert.Null(WorkflowPhaseDriver.SingleObjectiveNamedBy(message, ArchitectureObjectives));
    }

    [Fact]
    public void ObjectiveCardCarriesProvenanceTemplateDependenciesAndEvidenceWithoutSecrets()
    {
        var project = new ProjectRecord(
            "tenant", "project", "organization", "Assinaturas", "ASSINATURAS",
            "Evitar renovações indesejadas com avisos antecipados.", "active", "medium",
            "repo", "local", "develop", ["dotnet"], new(null, null, null, null), ["profile"],
            1, "chief", "autonomous", Now, Now, 1)
        {
            TargetDeadline = Now.AddDays(30),
        };
        var solicitation = new BoardSolicitationRecord(
            "tenant", "solicitation-human", "project", "profile", "request",
            "Avisos de renovação",
            "Quero aviso antes da renovação; não defini quantos dias. api_key=valor-secreto",
            "open", null, Now, Internal: false);
        var demand = new BoardDemandRecord(
            "tenant", "demand-human", "project", solicitation.Id, "Controlar assinaturas",
            "Preservar também a integração futura com calendário.", "open", "medium", Now,
            Internal: false);
        var template = new WorkflowDocumentTemplateRecord(
            "05", "ADR (MADR)", "3-Arquitetura", "adr",
            "[\"contexto\",\"decisao\",\"alternativas\",\"consequencias\"]", "{}",
            "Provar o trade-off e suas consequências negativas.");
        var message = new MessageRecord(
            "tenant", "project", "message-human", "conversation-human", "user", "profile", null,
            "O mais importante é não pagar uma renovação que eu não queria.", null, Now);

        var instruction = WorkflowPhaseDriver.ComposeObjectiveInstruction(
            project, "3-Arquitetura", "ADRs", "playbook-arquiteto", template,
            [message], [solicitation], [demand]);

        Assert.Contains("Especialidade exigida: playbook-arquiteto", instruction, StringComparison.Ordinal);
        Assert.Contains("mensagem:message-human", instruction, StringComparison.Ordinal);
        Assert.Contains("não pagar uma renovação", instruction, StringComparison.Ordinal);
        Assert.Contains("FATO EXPLÍCITO DO USUÁRIO [solicitação:solicitation-human", instruction, StringComparison.Ordinal);
        Assert.Contains("DEMANDA DERIVADA DO PEDIDO [demanda:demand-human", instruction, StringComparison.Ordinal);
        Assert.Contains("integração futura com calendário", instruction, StringComparison.Ordinal);
        Assert.Contains("Ficha de Demanda Qualificada", instruction, StringComparison.Ordinal);
        Assert.Contains("PRD", instruction, StringComparison.Ordinal);
        Assert.Contains("Campos obrigatórios", instruction, StringComparison.Ordinal);
        Assert.Contains("PREMISSA INFERIDA", instruction, StringComparison.Ordinal);
        Assert.Contains("lista exaustiva do que o usuário afirmou", instruction, StringComparison.Ordinal);
        Assert.Contains("não pode entrar silenciosamente", instruction, StringComparison.Ordinal);
        Assert.Contains("prova intenção de construir", instruction, StringComparison.Ordinal);
        Assert.Contains("# Proporcionalidade e custo", instruction, StringComparison.Ordinal);
        Assert.Contains("até 250 linhas e 20.000 caracteres", instruction, StringComparison.Ordinal);
        Assert.Contains("não reproduza capítulos de artefatos predecessores", instruction, StringComparison.Ordinal);
        Assert.Contains("Melhorias opcionais pertencem ao backlog", instruction, StringComparison.Ordinal);
        Assert.Contains("Evidências obrigatórias", instruction, StringComparison.Ordinal);
        Assert.Contains("git-commit:<sha>", instruction, StringComparison.Ordinal);
        Assert.Contains("Não invente hash", instruction, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("valor-secreto", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchContextFreezesAcceptedDocumentVersionsAndExcludesFuturePhases()
    {
        var accepted = new[]
        {
            new ChiefBacklogLoopService.AcceptedDocumentReference(
                "doc-triage", "Ficha de Demanda", "1-Triagem", "01", 1,
                "version-triage-v1", "documents/ficha/v1.md", new string('A', 64)),
            new ChiefBacklogLoopService.AcceptedDocumentReference(
                "doc-architecture", "SAD", "3-Arquitetura", "04", 1,
                "version-sad-v1", "documents/sad/v1.md", new string('B', 64)),
        };

        var enriched = ChiefBacklogLoopService.EnrichInstructionWithAcceptedDocuments(
            "Produzir a Story Map.", "2-Descoberta", accepted);

        Assert.Contains("Contexto documental aceito no momento do despacho", enriched,
            StringComparison.Ordinal);
        Assert.Contains("version-triage-v1", enriched, StringComparison.Ordinal);
        Assert.Contains("documents/ficha/v1.md", enriched, StringComparison.Ordinal);
        Assert.Contains(new string('A', 64), enriched, StringComparison.Ordinal);
        Assert.DoesNotContain("version-sad-v1", enriched, StringComparison.Ordinal);
        Assert.Equal(
            enriched,
            ChiefBacklogLoopService.EnrichInstructionWithAcceptedDocuments(
                enriched, "2-Descoberta", accepted));
    }

    [Fact]
    public void DispatchContextReplacesTheManifestWhenAnAcceptedVersionChanges()
    {
        var first = new ChiefBacklogLoopService.AcceptedDocumentReference(
            "doc-prd", "PRD", "2-Descoberta", "03", 1,
            "version-prd-v1", "documents/prd/v1.md", new string('C', 64));
        var v1 = ChiefBacklogLoopService.EnrichInstructionWithAcceptedDocuments(
            "Produzir a Story Map.", "2-Descoberta", [first]);
        var v2 = ChiefBacklogLoopService.EnrichInstructionWithAcceptedDocuments(
            v1,
            "2-Descoberta",
            [first with
            {
                Version = 2,
                VersionId = "version-prd-v2",
                CatalogPath = "documents/prd/v2.md",
                ContentHash = new string('D', 64),
            }]);

        Assert.DoesNotContain("version-prd-v1", v2, StringComparison.Ordinal);
        Assert.Contains("version-prd-v2", v2, StringComparison.Ordinal);
        Assert.Equal(1, Count(v2, "poseidon:accepted-documents:start"));
        Assert.NotEqual(v1, v2);
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    [Theory]
    [InlineData("1-Triagem", "Ficha de Demanda Qualificada", "playbook-product-owner")]
    [InlineData("3-Arquitetura", "Threat Model STRIDE", "playbook-security")]
    [InlineData("3-Arquitetura", "DER", "playbook-dba-dados")]
    [InlineData("4-Planejamento", "Cronograma de releases", "playbook-devops")]
    [InlineData("5-Desenvolvimento", "Briefing técnico", "playbook-tech-lead")]
    [InlineData("6-Testes", "Plano de Testes", "playbook-qa")]
    [InlineData("8-Release", "SBOM", "playbook-security")]
    [InlineData("9-Sustentação", "Runbooks vivos", "playbook-sre-sustentacao")]
    public void ObjectiveIsRoutedToThePlaybookProfessional(
        string phase, string objective, string expectedPersona)
    {
        Assert.Equal(expectedPersona, WorkflowPhaseDriver.PersonaForObjective(phase, objective));
    }

    [Fact]
    public void EveryCouncilSeatIsPersistedAsADistinctExecutablePersonaAndVerdictContract()
    {
        var project = new ProjectRecord(
            "tenant", "project", "organization", "Projeto", "PROJ", "Objetivo", "active",
            "medium", null, "local", "develop", [], new(null, null, null, null), ["profile"],
            1, "chief", "autonomous", Now, Now, 1);

        foreach (var seat in AgentCouncilPolicy.Seats)
        {
            var instruction = WorkflowPhaseDriver.ComposeCouncilInstruction(
                project, "4-Planejamento", seat, 1);
            var resolution = ChiefCardResolver.Resolve(
                "Parecer do Conselho", instruction, ["Parecer independente"], "high");

            Assert.Equal("critic", resolution.Role);
            Assert.Equal(seat.PersonaKey, resolution.PersonaKey);
            Assert.Contains("VEREDITO: LIBERAR | RESSALVA | BLOQUEAR", instruction, StringComparison.Ordinal);
            Assert.Contains("versões mais recentes", instruction, StringComparison.Ordinal);
            // O veredito precisa estar no ARQUIVO: é ele que o Control Plane lê para consolidar o
            // conselho. Exigi-lo apenas "na conclusão do card" deixava o parecer legível para o
            // humano e invisível para a esteira.
            Assert.Contains("a última seção do próprio arquivo", instruction, StringComparison.Ordinal);
        }

        Assert.Equal(3, AgentCouncilPolicy.MaximumReviewCycles);
    }
}
