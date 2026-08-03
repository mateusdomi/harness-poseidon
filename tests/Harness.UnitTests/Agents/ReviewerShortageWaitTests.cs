using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Falta de revisor é ESPERA, não falha do card.
///
/// Medido em 2026-08-03: os seis assentos do Conselho da fase 4 da prova limpa entregaram, a
/// única outra conta com papel de crítico estava em resfriamento, e o teto de quatro adiamentos
/// com backoff de cinco minutos os escalou em oito minutos — como se a ENTREGA tivesse problema.
/// Quatro horas depois o crítico já havia voltado e os seis continuavam escalados, porque
/// escalação de card não-revisado não tinha caminho de volta.
///
/// Contar tentativas mede a frequência com que perguntamos. O que decide aqui é há quanto tempo
/// ninguém pode responder — por isso o critério é o relógio, e por isso ele tem fim.
/// </summary>
public sealed class ReviewerShortageWaitTests
{
    private const string Shortage = "critic.none_available";

    [Fact]
    public void SemRevisorNoElencoNaoHaOQueEsperar()
    {
        // Nenhuma conta com papel de crítico serve este ator: esperar não muda esse fato, e
        // adiar em silêncio esconderia do dono um impedimento real.
        Assert.False(
            ChiefBacklogLoopService.ShouldWaitForReviewer(
                reviewerMayReturn: false, Shortage, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void RevisorOcupadoDentroDaCarenciaEEspera()
    {
        // O caso medido: o crítico existe e está apenas em resfriamento. Aos vinte minutos — onde
        // o teto antigo escalava — a resposta certa ainda é aguardar.
        Assert.True(
            ChiefBacklogLoopService.ShouldWaitForReviewer(
                reviewerMayReturn: true, Shortage, TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void PassadaACarenciaOImpedimentoEReal()
    {
        // Esperar para sempre é o erro simétrico de escalar cedo demais: ninguém saberia que a
        // entrega está parada.
        Assert.False(
            ChiefBacklogLoopService.ShouldWaitForReviewer(
                reviewerMayReturn: true,
                Shortage,
                ChiefBacklogLoopService.ReviewerShortageGrace));
    }

    [Fact]
    public void FalhaDoExecutorDoCriticoContinuaContando()
    {
        // Ali houve tentativa real de revisão, e ela quebrou. Insistir sem limite nessa é
        // exatamente o laço que o teto de falhas existe para impedir.
        Assert.False(
            ChiefBacklogLoopService.ShouldWaitForReviewer(
                reviewerMayReturn: true,
                "critic.executor_unavailable",
                TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ACarenciaCobreUmaJanelaDeCotaTipica()
    {
        // O teto antigo dava vinte minutos — menos que qualquer janela de cota de provedor.
        Assert.True(
            ChiefBacklogLoopService.ReviewerShortageGrace >= TimeSpan.FromHours(1),
            $"carência de {ChiefBacklogLoopService.ReviewerShortageGrace} é menor que uma " +
            "janela de cota típica; o card voltaria a escalar por culpa de terceiro");
    }
}
