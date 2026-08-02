using Harness.Modules.Operations;

namespace Harness.UnitTests.Operations;

/// <summary>
/// A correção importante aqui é o que NÃO se mede.
///
/// Concluir "só cabem dois agentes" a partir de "211 MB livres" é ler mal o macOS: ele usa
/// a RAM disponível agressivamente como cache, e RAM livre baixa com swap zerado descreve um
/// sistema saudável. O episódio dos 35 GB não prova que sete agentes são inviáveis — prova
/// que várias operações PESADAS simultâneas são perigosas.
/// </summary>
public sealed class HostResourcePolicyTests
{
    private static HostPressure Healthy(int heavy = 0, int exclusive = 0) =>
        new("normal", SwapUsedBytes: 0, PageoutDelta: 0, HeavyRunning: heavy, ExclusiveRunning: exclusive);

    [Fact]
    public void LowFreeRamWithoutSwapIsNotPressure()
    {
        // Exatamente o quadro observado: pouquíssima RAM livre, swap zerado, sem pageouts.
        var pressure = new HostPressure("normal", SwapUsedBytes: 0, PageoutDelta: 0, HeavyRunning: 0, ExclusiveRunning: 0);

        Assert.False(HostResourcePolicy.IsCritical(pressure));
        Assert.True(HostResourcePolicy.ShouldAdmitNewWork(pressure));
    }

    [Fact]
    public void SwapInUseWithPageoutsIsRealPressure()
    {
        var pressure = new HostPressure("warn", SwapUsedBytes: 512L * 1024 * 1024, PageoutDelta: 4096, HeavyRunning: 1, ExclusiveRunning: 0);

        Assert.True(HostResourcePolicy.IsCritical(pressure));
        Assert.False(HostResourcePolicy.ShouldAdmitNewWork(pressure));
    }

    [Fact]
    public void LightWorkRunsEvenUnderPressureBecauseItIsNotWhatCrushesTheHost()
    {
        var pressure = new HostPressure("critical", 1024, 8192, HeavyRunning: 1, ExclusiveRunning: 0);

        Assert.True(HostResourcePolicy.CanStart(ResourceClass.Light, pressure));
        Assert.False(HostResourcePolicy.CanStart(ResourceClass.Heavy, pressure));
    }

    [Fact]
    public void OnlyOneHeavyRunsByDefault()
    {
        Assert.True(HostResourcePolicy.CanStart(ResourceClass.Heavy, Healthy()));
        Assert.False(HostResourcePolicy.CanStart(ResourceClass.Heavy, Healthy(heavy: 1)));
    }

    [Fact]
    public void HeavyLimitMayRiseOnlyWhenExplicitlyRaised()
    {
        Assert.True(HostResourcePolicy.CanStart(ResourceClass.Heavy, Healthy(heavy: 1), heavyLimit: 2));
    }

    [Fact]
    public void AnExclusiveOperationNeverSharesTheHost()
    {
        Assert.False(HostResourcePolicy.CanStart(ResourceClass.Exclusive, Healthy(heavy: 1)));
        Assert.True(HostResourcePolicy.CanStart(ResourceClass.Exclusive, Healthy()));
    }

    [Fact]
    public void NothingStartsWhileAnExclusiveOperationHoldsTheHost()
    {
        var busy = Healthy(exclusive: 1);

        Assert.False(HostResourcePolicy.CanStart(ResourceClass.Light, busy));
        Assert.False(HostResourcePolicy.CanStart(ResourceClass.Heavy, busy));
    }
}
