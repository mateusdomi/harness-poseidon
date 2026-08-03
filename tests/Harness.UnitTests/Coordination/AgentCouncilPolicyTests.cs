using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// B13 — o conselho de agentes na saída do Planejamento.
///
/// É o último ponto em que corrigir ainda é barato: dali em diante, cada decisão errada custa
/// código escrito, revisado e refeito.
/// </summary>
public sealed class AgentCouncilPolicyTests
{
    [Fact]
    public void TheCouncilIsConvenedOnlyLeavingPlanning()
    {
        Assert.True(AgentCouncilPolicy.ShouldConvene("4-Planejamento", producedDocumentCount: 3));

        foreach (var phase in new[] { "1-Triagem", "3-Arquitetura", "5-Desenvolvimento", "8-Release" })
        {
            Assert.False(AgentCouncilPolicy.ShouldConvene(phase, 3));
        }
    }

    [Fact]
    public void AnEmptyPlanningDoesNotSummonFiveSpecialists()
    {
        // Convocar o conselho para revisar o vazio gasta cota e ensina a fábrica a tratá-lo como
        // ritual — e ritual é o que se cumpre sem ler.
        Assert.False(AgentCouncilPolicy.ShouldConvene("4-Planejamento", producedDocumentCount: 0));
    }

    [Fact]
    public void EverySeatAsksAQuestionOnlyItAsks()
    {
        // Três agentes com a mesma lente produzem a mesma cegueira três vezes e dão a ela
        // aparência de consenso.
        Assert.True(AgentCouncilPolicy.Seats.Count >= AgentCouncilPolicy.MinimumCouncil);
        Assert.Equal(
            AgentCouncilPolicy.Seats.Count,
            AgentCouncilPolicy.Seats.Select(seat => seat.PersonaKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            AgentCouncilPolicy.Seats.Count,
            AgentCouncilPolicy.Seats.Select(seat => seat.Lens).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(AgentCouncilPolicy.Seats, seat => string.IsNullOrWhiteSpace(seat.Lens));
    }

    [Fact]
    public void OneBlockingFindingHoldsTheTransitionEvenAgainstTheMajority()
    {
        // O conselho NÃO é votação. Se o especialista de segurança encontra uma falha explorável,
        // quatro pareceres favoráveis não a tornam menos explorável: maioria decide preferência,
        // evidência decide risco.
        var verdict = AgentCouncilPolicy.Consolidate([
            new("playbook-arquiteto", false, false, "Arquitetura coerente."),
            new("playbook-tech-lead", false, false, "Cards executáveis."),
            new("playbook-qa", false, false, "Critérios verificáveis."),
            new("playbook-dba-dados", false, false, "Modelo sustenta o crescimento."),
            new("playbook-security", true, false, "Token de sessão sem expiração no fluxo proposto."),
        ]);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.blocking_finding", verdict.ReasonCode);
        Assert.Contains(verdict.Dissent, item => item.Contains("security", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcernsSurviveInTheRecordEvenWhenNothingBlocks()
    {
        // A ressalva de hoje costuma ser o incidente de depois. Apagá-la por não bloquear é
        // perder o aviso.
        var verdict = AgentCouncilPolicy.Consolidate([
            new("playbook-arquiteto", false, true, "A visão restrita aceita dívida sem prazo de revisão."),
            new("playbook-tech-lead", false, false, "Cards executáveis."),
            new("playbook-qa", false, false, "Critérios verificáveis."),
        ]);

        Assert.True(verdict.MayProceed);
        // D2: pareceres sem autoria atribuível liberam a fase e declaram que a diversidade não
        // pôde ser medida. "Desconhecida" nunca é lido como "suficiente" — é a única leitura que
        // não deixa seis prompts do mesmo modelo passarem por seis opiniões.
        Assert.Equal("council.cleared_diversity_unknown", verdict.ReasonCode);
        Assert.Single(verdict.Dissent);
    }

    [Fact]
    public void AnIncompleteCouncilDoesNotClearTheTransition()
    {
        // Default-FAIL: dois pareceres favoráveis não são um conselho, são dois pareceres.
        var verdict = AgentCouncilPolicy.Consolidate([
            new("playbook-arquiteto", false, false, "Tudo certo."),
            new("playbook-qa", false, false, "Tudo certo."),
        ]);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.incomplete", verdict.ReasonCode);
    }

    [Fact]
    public void CouncilUsesTheActualAttemptVerdictInsteadOfTheCardState()
    {
        var seat = AgentCouncilPolicy.Seats.Single(value => value.PersonaKey == "playbook-security");

        var blocking = AgentCouncilPolicy.FromExecution(
            seat,
            "VEREDITO: BLOQUEAR\nRESUMO: sessão sem expiração.\nEVIDÊNCIAS: docs/threat-model.md");
        var advisory = AgentCouncilPolicy.FromExecution(
            seat,
            "VEREDITO: RESSALVA\nRESUMO: explicitar risco residual.");
        var clear = AgentCouncilPolicy.FromExecution(
            seat,
            "VEREDITO: LIBERAR\nRESUMO: cobertura adequada.");

        Assert.True(blocking!.IsBlocking);
        Assert.True(advisory!.HasConcern);
        Assert.False(clear!.IsBlocking);
        Assert.False(clear.HasConcern);
        Assert.Null(AgentCouncilPolicy.FromExecution(seat, null));
    }

    [Fact]
    public void ABlockedLegacyCardStillProducesABlockingOpinion()
    {
        var seat = AgentCouncilPolicy.Seats[0];
        var opinion = AgentCouncilPolicy.FromExecution(
            seat, null, "Inconsistência entre SAD e plano de release.");

        Assert.True(opinion!.IsBlocking);
        Assert.Contains("SAD", opinion.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOpinionsHoldTheGateInsteadOfPassingByOmission()
    {
        // Enquanto os pareceres não voltam, o conselho está incompleto e o portão NÃO abre.
        // Ausência de parecer não é parecer favorável — é o Default-FAIL aplicado ao conselho.
        // Sem isto, convocar cinco conselheiros e não esperar por eles seria teatro.
        var apenasUmVoltou = AgentCouncilPolicy.Consolidate([
            new("playbook-arquiteto", false, false, "Parecer entregue."),
        ]);

        Assert.False(apenasUmVoltou.MayProceed);
        Assert.Equal("council.incomplete", apenasUmVoltou.ReasonCode);
    }

    [Fact]
    public void NoOpinionAtAllNeverClearsTheTransition()
    {
        Assert.False(AgentCouncilPolicy.Consolidate([]).MayProceed);
    }

    [Fact]
    public void AssentoBloqueadoSemParecerNaoViraAchadoTecnico()
    {
        // Observado em execução real: o card de parecer não pôde ser despachado, e o motivo
        // operacional foi publicado como se fosse a opinião do arquiteto — o Control Plane chegou
        // a abrir "corrigir achado de playbook-arquiteto" para uma opinião que ninguém deu.
        var seat = AgentCouncilPolicy.Seats[0];
        var opinion = AgentCouncilPolicy.FromExecution(
            seat, attemptSummary: null, blockedReason: "Especialidade sem escopo de escrita.");

        Assert.NotNull(opinion);
        // Continua segurando a fase: conselho incompleto não libera nada.
        Assert.True(opinion.IsBlocking);
        Assert.True(opinion.IsOperational);
        Assert.Contains("não pôde ser ouvido", opinion.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ParecerRealPrevaleceSobreBloqueioOperacional()
    {
        // Quando o conselheiro EFETIVAMENTE opinou, o veredito dele é o que vale — mesmo que o
        // card tenha terminado bloqueado por outro motivo.
        var seat = AgentCouncilPolicy.Seats[0];
        var opinion = AgentCouncilPolicy.FromExecution(
            seat,
            "VEREDITO: BLOQUEAR\nRESUMO: o modelo de dados não sustenta o requisito de aviso.",
            blockedReason: "algum bloqueio operacional");

        Assert.NotNull(opinion);
        Assert.True(opinion.IsBlocking);
        Assert.False(opinion.IsOperational);
        Assert.Contains("modelo de dados", opinion.Summary, StringComparison.Ordinal);
    }
}
