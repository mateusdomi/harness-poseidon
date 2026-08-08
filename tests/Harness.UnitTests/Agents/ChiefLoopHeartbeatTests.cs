using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A PROVA determinística da vivacidade do loop. Cada fato é uma pergunta que a fábrica não sabia
/// responder em 2026-08-08: "estou travada?", "estou ociosa à toa?", "ou é fim de fila honesto?".
/// Só timestamps — a mesma entrada sempre dá o mesmo veredito.
/// </summary>
public sealed class ChiefLoopHeartbeatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    private static ChiefLoopLiveness Read(ChiefLoopHeartbeat hb, DateTimeOffset now) =>
        hb.Read(now, StuckAfter, IdleBudget, Grace);

    [Fact]
    public void DentroDaCarenciaDeSubidaNuncaTravadoNemViolacao()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        // Nenhum ciclo ainda, mas dentro da carência: um deploy zera atividade por minutos.
        var live = Read(hb, T0.AddMinutes(2));
        Assert.False(live.Stuck);
        Assert.False(live.AntiIdleViolation);
        Assert.True(live.WithinStartupGrace);
    }

    [Fact]
    public void SemCicloAlgumAlemDaCarenciaEhTravado()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        // ExecuteAsync morreu na largada: nenhum ciclo abriu, e já passou da carência + limiar.
        var live = Read(hb, T0.AddMinutes(9));
        Assert.True(live.Stuck);
    }

    [Fact]
    public void CicloAbertoQueNaoFechaEhTravado()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        hb.CycleStarted(T0.AddMinutes(4));                 // abriu um ciclo…
        var live = Read(hb, T0.AddMinutes(10));            // …e travou dentro dele (hang no tick)
        Assert.True(live.Stuck);
    }

    [Fact]
    public void CiclosFechandoEmCadenciaNaoEhTravado()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        for (var minute = 4; minute <= 20; minute++)
        {
            hb.CycleStarted(T0.AddMinutes(minute));
            hb.CycleCompleted(T0.AddMinutes(minute), dispatchableCards: 0, eligibleAgents: 1, dispatched: 0);
        }

        var live = Read(hb, T0.AddMinutes(20).AddSeconds(30));
        Assert.False(live.Stuck);
    }

    [Fact]
    public void TrabalhoDespachavelComExecutorElegivelSemDespacharEhViolacao()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        // Os ciclos SEGUEM rodando a cada 30s (loop vivo, não travado), sempre vendo trabalho e
        // executor pronto, mas NUNCA despachando — por 16 min. Staleness fica ~0 (vivo); a régua
        // que estoura é a da ociosidade desde o último despacho (que nunca houve).
        for (var second = 30; second <= 16 * 60; second += 30)
        {
            hb.CycleStarted(T0.AddSeconds(second));
            hb.CycleCompleted(T0.AddSeconds(second), dispatchableCards: 3, eligibleAgents: 2, dispatched: 0);
        }

        var live = Read(hb, T0.AddMinutes(16));
        Assert.False(live.Stuck);
        Assert.True(live.AntiIdleViolation);
        Assert.Equal(3, live.DispatchableCards);
        Assert.Equal(2, live.EligibleAgents);
    }

    [Fact]
    public void TrabalhoDespachavelSemExecutorElegivelNaoEhViolacao()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        // Fila cheia mas TODA conta em cota/auth: ociosidade LEGÍTIMA, não bug de despacho.
        hb.CycleCompleted(T0.AddMinutes(10), dispatchableCards: 5, eligibleAgents: 0, dispatched: 0);
        var live = Read(hb, T0.AddMinutes(20));
        Assert.False(live.AntiIdleViolation);
    }

    [Fact]
    public void SemTrabalhoDespachavelNaoEhViolacaoMesmoComExecutores()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        // Produtos CONCLUÍDOS: nada despachável. O loop SEGUE vivo (ciclos a cada 30s) e os
        // executores ficam ociosos — fim de fila honesto, nem travamento nem violação.
        for (var second = 30; second <= 30 * 60; second += 30)
        {
            hb.CycleStarted(T0.AddSeconds(second));
            hb.CycleCompleted(T0.AddSeconds(second), dispatchableCards: 0, eligibleAgents: 4, dispatched: 0);
        }

        var live = Read(hb, T0.AddMinutes(30));
        Assert.False(live.AntiIdleViolation);
        Assert.True(live.Healthy);
    }

    [Fact]
    public void DespachoRecenteZeraAOciosidade()
    {
        var hb = new ChiefLoopHeartbeat(T0);
        // Havia trabalho e executor, e HOUVE despacho: a régua da ociosidade reinicia.
        hb.CycleCompleted(T0.AddMinutes(15), dispatchableCards: 2, eligibleAgents: 2, dispatched: 1);
        var live = Read(hb, T0.AddMinutes(17));
        Assert.False(live.AntiIdleViolation);
    }
}
