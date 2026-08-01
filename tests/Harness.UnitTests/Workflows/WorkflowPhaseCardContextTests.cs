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
        Assert.Contains("Evidências obrigatórias", instruction, StringComparison.Ordinal);
        Assert.Contains("git-commit:<sha>", instruction, StringComparison.Ordinal);
        Assert.Contains("Não invente hash", instruction, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("valor-secreto", instruction, StringComparison.Ordinal);
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
        }

        Assert.Equal(3, AgentCouncilPolicy.MaximumReviewCycles);
    }
}
