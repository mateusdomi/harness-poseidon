using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O freio de sequência sem progresso — a pergunta que o circuito do card não faz.
///
/// O circuito responde "esta falha é culpa do card?" e responde certo: zero token quer dizer
/// que ninguém julgou o enunciado. A consequência não intencional é que o pior sintoma
/// possível era o único sem freio nenhum. Medido em 2026-08-03: catorze tentativas idênticas
/// em uma hora e quarenta, nenhum circuito aberto, nenhum sinal de parada.
/// </summary>
public sealed class NoProgressBrakeTests
{
    [Fact]
    public void OPrimeiroRunSemProgressoMantemAJanelaNormal()
    {
        // Uma falha isolada é rotina: espaçar já na primeira puniria instabilidade comum.
        Assert.Equal(
            ChiefBacklogLoopService.NoProgressBackoff(1),
            ChiefBacklogLoopService.NoProgressBackoff(0));
    }

    [Fact]
    public void ARepeticaoDobraOIntervalo()
    {
        var primeiro = ChiefBacklogLoopService.NoProgressBackoff(1);
        var segundo = ChiefBacklogLoopService.NoProgressBackoff(2);
        var terceiro = ChiefBacklogLoopService.NoProgressBackoff(3);

        Assert.Equal(primeiro * 2, segundo);
        Assert.Equal(primeiro * 4, terceiro);
    }

    [Fact]
    public void OFreioSatura()
    {
        // Um contador alto não pode virar uma espera de dias nem estourar o TimeSpan no caminho.
        Assert.Equal(
            ChiefBacklogLoopService.MaximumNoProgressBackoff,
            ChiefBacklogLoopService.NoProgressBackoff(50));
        Assert.Equal(
            ChiefBacklogLoopService.MaximumNoProgressBackoff,
            ChiefBacklogLoopService.NoProgressBackoff(int.MaxValue));
    }

    [Fact]
    public void OCasoMedidoSairiaDoLacoEmMinutosEmVezDeUmaHoraEQuarenta()
    {
        // Catorze runs de zero token a cada ~4 min = 1h40 de nada. Com o freio, a mesma
        // sequência satura muito antes: a soma das esperas passa de uma hora já no oitavo.
        var acumulado = TimeSpan.Zero;
        for (var run = 1; run <= 8; run++)
        {
            acumulado += ChiefBacklogLoopService.NoProgressBackoff(run);
        }

        Assert.True(
            acumulado > TimeSpan.FromHours(1),
            $"esperava mais de uma hora de espaçamento acumulado, obtive {acumulado}");
    }
}
