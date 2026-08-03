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

/// <summary>
/// A chave de idempotência do replanejamento identifica o COMANDO, não a intenção.
///
/// Terceiro cadeado dos seis assentos do Conselho, aparecido só depois que o segundo saiu: a
/// chave era estável enquanto a carga não era — o id da nova versão de instrução é sorteado a
/// cada rodada. O inbox guarda também as mutações RECUSADAS, então a primeira recusa gravava
/// chave+hash e toda rodada seguinte chegava com a mesma chave e um hash novo: conflito
/// permanente, com a causa técnica sumindo e o card parado.
/// </summary>
public sealed class ReplanIdempotencyKeyTests
{
    private const string Card = "01KZ4B7K34KMCSYNH0T36GF5NN";
    private const string Hash = "A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public void OMesmoComandoRepetidoDaAMesmaChave()
    {
        // Reenvio literal — o caso que a idempotência existe para proteger — continua sendo replay.
        Assert.Equal(
            ChiefBacklogLoopService.ReplanIdempotencyKey(Card, 14, Hash, "01KZ4C000000000000000000A1"),
            ChiefBacklogLoopService.ReplanIdempotencyKey(Card, 14, Hash, "01KZ4C000000000000000000A1"));
    }

    [Fact]
    public void UmaInstrucaoNovaDaUmaChaveNova()
    {
        // Sem isto, a recusa da primeira rodada envenenava a chave e nenhuma rodada posterior
        // conseguia ser sequer avaliada — nem depois de a causa da recusa ter sido corrigida.
        Assert.NotEqual(
            ChiefBacklogLoopService.ReplanIdempotencyKey(Card, 14, Hash, "01KZ4C000000000000000000A1"),
            ChiefBacklogLoopService.ReplanIdempotencyKey(Card, 14, Hash, "01KZ4C000000000000000000B2"));
    }

    [Fact]
    public void AChaveCabeNoLimiteDaCadeia()
    {
        // O validador da cadeia recusa chave acima de 200 caracteres, e uma chave recusada aqui
        // derrubaria o replanejamento por um motivo que nada tem a ver com o card.
        var key = ChiefBacklogLoopService.ReplanIdempotencyKey(
            Card, long.MaxValue, Hash, "01KZ4C000000000000000000A1");
        Assert.True(key.Length <= 200, $"chave com {key.Length} caracteres");
    }
}
