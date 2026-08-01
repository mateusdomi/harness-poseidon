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
        Assert.Equal("council.cleared", verdict.ReasonCode);
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
    public void NoOpinionAtAllNeverClearsTheTransition()
    {
        Assert.False(AgentCouncilPolicy.Consolidate([]).MayProceed);
    }
}
