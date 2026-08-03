using Harness.Modules.Operations;

namespace Harness.UnitTests.Operations;

/// <summary>
/// As duas maneiras de perder a madrugada, fixadas em teste: parar cedo com trabalho aberto,
/// e relançar cegamente uma sessão que morre na largada até queimar a cota inteira.
/// </summary>
public sealed class IntegratorRelaunchPolicyTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    private static IntegratorOutcome Outcome(
        int exitCode = 0,
        double minutes = 30,
        string tail = "",
        int shortRuns = 0) =>
        new(exitCode, TimeSpan.FromMinutes(minutes), tail, shortRuns);

    [Fact]
    public void ASessionThatWorkedAndExitedIsRelaunchedImmediately()
    {
        var verdict = IntegratorRelaunchPolicy.Decide(Outcome(), Cooldown);

        Assert.Equal(RelaunchDecision.Relaunch, verdict.Decision);
        Assert.Equal(Cooldown, verdict.Delay);
    }

    /// <summary>
    /// Código de saída diferente de zero também é YIELD: a sessão pode ter morrido no meio do
    /// trabalho, e o que decide se acabou é o gate, não o processo.
    /// </summary>
    [Fact]
    public void ACrashAfterRealWorkIsStillAYieldAndNotAReasonToStop()
    {
        var verdict = IntegratorRelaunchPolicy.Decide(Outcome(exitCode: 137, minutes: 42), Cooldown);

        Assert.Equal(RelaunchDecision.Relaunch, verdict.Decision);
    }

    [Fact]
    public void QuotaIsWaitedOutInsteadOfRetriedInALoop()
    {
        var verdict = IntegratorRelaunchPolicy.Decide(
            Outcome(exitCode: 1, minutes: 0.05, tail: "Claude usage limit reached"),
            Cooldown);

        Assert.Equal(RelaunchDecision.WaitExternal, verdict.Decision);
        Assert.True(verdict.Delay >= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void RepeatedShortRunsBackOffExponentially()
    {
        var first = IntegratorRelaunchPolicy.Decide(Outcome(minutes: 0.1, shortRuns: 1), Cooldown);
        var third = IntegratorRelaunchPolicy.Decide(Outcome(minutes: 0.1, shortRuns: 3), Cooldown);

        Assert.Equal(RelaunchDecision.Relaunch, third.Decision);
        Assert.True(third.Delay > first.Delay);
    }

    /// <summary>
    /// O teto existe para o caso oposto: uma falha transitória às 2h não pode empurrar a
    /// próxima tentativa para depois do amanhecer.
    /// </summary>
    [Fact]
    public void BackoffIsCappedSoTheNightNeverStopsForGood()
    {
        Assert.Equal(
            IntegratorRelaunchPolicy.MaximumBackoff,
            IntegratorRelaunchPolicy.Backoff(Cooldown, consecutiveShortRuns: 20));
    }

    [Fact]
    public void AMissingIntegratorCommandAbortsInsteadOfSpinning()
    {
        var verdict = IntegratorRelaunchPolicy.Decide(
            Outcome(exitCode: 127, minutes: 0.01, tail: "/bin/sh: claude: command not found"),
            Cooldown);

        Assert.Equal(RelaunchDecision.Abort, verdict.Decision);
    }

    [Fact]
    public void AnUnconfiguredCommandAborts()
    {
        var verdict = IntegratorRelaunchPolicy.Decide(Outcome(exitCode: -1, minutes: 0), Cooldown);

        Assert.Equal(RelaunchDecision.Abort, verdict.Decision);
    }

    /// <summary>
    /// Uma sessão longa que MENCIONA cota em algum ponto do transcript não é uma sessão que
    /// morreu de cota — mas a cauda é o fim da sessão, então o sinal é do desfecho. O padrão
    /// na dúvida continua sendo continuar, e é isso que este teste protege: nenhum motivo
    /// desconhecido vira parada.
    /// </summary>
    [Fact]
    public void AnUnrecognizedFailureStillRelaunches()
    {
        var verdict = IntegratorRelaunchPolicy.Decide(
            Outcome(exitCode: 1, minutes: 0.2, tail: "algo estranho aconteceu", shortRuns: 1),
            Cooldown);

        Assert.Equal(RelaunchDecision.Relaunch, verdict.Decision);
    }
}
