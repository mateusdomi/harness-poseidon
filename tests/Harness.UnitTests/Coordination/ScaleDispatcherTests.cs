using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class ScaleDispatcherTests
{
    [Fact]
    public void ScaleDispatcherDispatchesHighPriorityCardsFirstWithinLimit()
    {
        var buffer = new CardPrioritizedBuffer();
        var now = DateTimeOffset.UtcNow;

        buffer.Enqueue("card-low", "worker", CardPriority.Low, now);
        buffer.Enqueue("card-high", "worker", CardPriority.High, now);
        buffer.Enqueue("card-critic", "critic", CardPriority.Normal, now);

        var dispatcher = new ScaleDispatcher();
        var result = dispatcher.Dispatch(buffer, globalMaxConcurrency: 2, currentRunningCount: 0);

        Assert.Equal(2, result.DispatchedWorkerCards.Count + result.DispatchedCriticCards.Count);
        Assert.Contains("card-high", result.DispatchedWorkerCards);
        Assert.Contains("card-critic", result.DispatchedCriticCards);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal("dispatched_successfully", result.DispatchReason);
    }

    [Fact]
    public void ScaleDispatcherDefersAllWhenGlobalConcurrencyIsFull()
    {
        var buffer = new CardPrioritizedBuffer();
        buffer.Enqueue("card-1", "worker", CardPriority.High, DateTimeOffset.UtcNow);

        var dispatcher = new ScaleDispatcher();
        var result = dispatcher.Dispatch(buffer, globalMaxConcurrency: 5, currentRunningCount: 5);

        Assert.Empty(result.DispatchedWorkerCards);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal("global_concurrency_limit_reached", result.DispatchReason);
    }

    /// <summary>
    /// Inanição: com a mesma prioridade, o desempate era a ordem de enfileiramento DAQUELA
    /// rodada — então o card que perde uma rodada volta para o fim na seguinte e nunca sai. Em
    /// 03/08/2026 dois entregáveis de Arquitetura ficaram treze horas em `ready` enquanto cards
    /// criados depois passavam à frente. Quem espera há mais tempo vai primeiro.
    /// </summary>
    [Fact]
    public void AmongEqualPrioritiesTheCardThatHasWaitedLongestGoesFirst()
    {
        var buffer = new CardPrioritizedBuffer();
        var now = new DateTimeOffset(2026, 8, 3, 11, 0, 0, TimeSpan.Zero);

        // Enfileirados na ordem "errada" de propósito: o mais NOVO entra primeiro.
        buffer.Enqueue("card-novo", "backend-specialist", CardPriority.Low, now);
        buffer.Enqueue("card-antigo", "backend-specialist", CardPriority.Low, now.AddHours(-13));

        Assert.Equal("card-antigo", buffer.Dequeue()!.CardId);
        Assert.Equal("card-novo", buffer.Dequeue()!.CardId);
    }

    /// <summary>A espera não atropela a prioridade: urgência ainda vem antes de antiguidade.</summary>
    [Fact]
    public void WaitingLongerNeverOvertakesAHigherPriority()
    {
        var buffer = new CardPrioritizedBuffer();
        var now = new DateTimeOffset(2026, 8, 3, 11, 0, 0, TimeSpan.Zero);

        buffer.Enqueue("card-antigo-baixo", "backend-specialist", CardPriority.Low, now.AddDays(-2));
        buffer.Enqueue("card-novo-alto", "backend-specialist", CardPriority.High, now);

        Assert.Equal("card-novo-alto", buffer.Dequeue()!.CardId);
    }
}
