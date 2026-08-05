using Harness.Host.Agents;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Onda 0.1 — a TOPOLOGIA do Conselho, provada contra o cenário exato que travou em 03/08/2026.
///
/// O cenário: duas contas de papel `critic`, uma sem cota. Seis assentos entregaram parecer; cada
/// parecer, sendo card comum, exigia revisão por OUTRO crítico — e o Conselho consumia o próprio
/// elenco em recursão (`critic.none_available`), sem jamais fechar. A topologia correta:
///
///   N opiniões independentes → consolidação DETERMINÍSTICA → qualquer bloqueante segura o gate
///
/// e o parecer NÃO reentra no loop actor/critic, porque ele já É a revisão (ADR-0008).
/// </summary>
public sealed class CouncilTopologyTests
{
    /// <summary>
    /// O cenário de 03/08, injetado: seis pareceres entregues por DUAS contas (uma predominante,
    /// como no dia), nenhum bloqueante. Antes, isto nunca fechava; agora fecha por consolidação —
    /// e a diversidade real da mesa fica declarada, não escondida.
    /// </summary>
    [Fact]
    public void OCenarioTravadoDe0308FechaSemRecursao()
    {
        CouncilOpinion Parecer(string seat, string conta) =>
            new(seat, IsBlocking: false, HasConcern: false,
                $"VEREDITO: APROVAR — parecer do assento {seat}.",
                IsOperational: false, conta, "antigravity");

        var opinioes = new[]
        {
            Parecer("playbook-product-owner", "worker-antigravity-review"),
            Parecer("playbook-arquiteto", "worker-antigravity-review"),
            Parecer("playbook-tech-lead", "worker-antigravity-review"),
            Parecer("playbook-seguranca", "worker-antigravity-review"),
            Parecer("playbook-dba-dados", "worker-codex-critic"),
            Parecer("playbook-qa", "worker-antigravity-review"),
        };

        var verdict = AgentCouncilPolicy.Consolidate(opinioes);

        // FECHA. Em 03/08 este exato conjunto ficou em `critic.none_available` para sempre.
        Assert.True(verdict.MayProceed);
        Assert.StartsWith("council.cleared", verdict.ReasonCode, StringComparison.Ordinal);

        // E a mesa real fica dita: 6 lentes em 2 inteligências.
        Assert.NotNull(verdict.Diversity);
        Assert.Equal(6, verdict.Diversity.Seats);
        Assert.Equal(2, verdict.Diversity.DistinctAccounts);
    }

    /// <summary>
    /// A metade estrutural da topologia: o card de parecer NÃO reentra no loop actor/critic.
    /// Nenhum crítico extra é consumido para revisar crítica — é a regra que quebra a recursão.
    /// </summary>
    [Fact]
    public void OParecerNaoReentraNoLoopActorCritic()
    {
        var parecer = new BoardTaskRecord(
            "t", "id", "p", null, "Conselho 4-Planejamento — playbook-arquiteto (ciclo 1)",
            "awaiting_review", "low", null, null, 1,
            new BoardProgressRecord(0, 0, 0), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, null, 1, "awaiting_review", "s", "d", "4-Planejamento", "council");

        Assert.True(ChiefBacklogLoopService.IsCouncilOpinionCard(parecer));

        // E um card comum com título de conselho NÃO herda a dispensa: a regra é por tipo.
        var comum = parecer with { CardType = "agent_task" };
        Assert.False(ChiefBacklogLoopService.IsCouncilOpinionCard(comum));
    }

    /// <summary>Com um bloqueante no meio dos seis, o gate segura — maioria não vence evidência.</summary>
    [Fact]
    public void UmBloqueanteEntreSeisSeguraOGateMesmoComRecursaoResolvida()
    {
        var opinioes = new[]
        {
            new CouncilOpinion("playbook-product-owner", false, false, "ok", false, "a", "p"),
            new CouncilOpinion("playbook-arquiteto", false, false, "ok", false, "a", "p"),
            new CouncilOpinion("playbook-tech-lead", false, false, "ok", false, "b", "p"),
            new CouncilOpinion("playbook-seguranca", true, false, "VEREDITO: BLOQUEAR — falha explorável.", false, "a", "p"),
            new CouncilOpinion("playbook-dba-dados", false, false, "ok", false, "b", "p"),
            new CouncilOpinion("playbook-qa", false, false, "ok", false, "a", "p"),
        };

        var verdict = AgentCouncilPolicy.Consolidate(opinioes);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.blocking_finding", verdict.ReasonCode);
    }
}
