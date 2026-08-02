using Harness.Modules.Operations;

namespace Harness.UnitTests.Operations;

/// <summary>
/// R2 — esperar precisa ser observável.
///
/// Em 2026-08-02 a Integradora ficou nove minutos bloqueada num laço que perguntava
/// "existem ao menos duas mensagens?" enquanto a resposta que ela esperava já existia havia
/// vinte e quatro segundos. Durante esse tempo não investigou turno, invocação de modelo,
/// worker, banco nem logs. A pergunta estava errada: contagem genérica não identifica nada.
/// </summary>
public sealed class WaitClassifierTests
{
    private static WaitFacts Facts(
        string? subject = "turn:01ABC",
        bool process = false,
        bool modelCall = false,
        bool heartbeat = false,
        bool output = false,
        bool resource = false,
        int ageMinutes = 1) =>
        new(subject, process, modelCall, heartbeat, output, resource, TimeSpan.FromMinutes(ageMinutes));

    [Fact]
    public void AProcessWithRealActivityIsRunning()
    {
        Assert.Equal(WaitState.Running, WaitClassifier.Classify(Facts(process: true)));
    }

    [Fact]
    public void AnInFlightModelCallIsALegitimateObservableWait()
    {
        Assert.Equal(WaitState.WaitingObservable, WaitClassifier.Classify(Facts(modelCall: true)));
    }

    [Fact]
    public void WaitingForAHeavySlotIsItsOwnStateNotAGenericWait()
    {
        Assert.Equal(WaitState.WaitingResource, WaitClassifier.Classify(Facts(resource: true)));
    }

    /// <summary>
    /// Sem PID, sem chamada, sem heartbeat e sem delta, passado o limiar: não se espera mais.
    /// Diagnostica-se.
    /// </summary>
    [Fact]
    public void NoSignalPastTheThresholdIsStalledAndNotWaiting()
    {
        Assert.Equal(WaitState.Stalled, WaitClassifier.Classify(Facts(ageMinutes: 30)));
    }

    /// <summary>
    /// O coração da regra: sem SUJEITO não existe espera legítima. "Aguardando" sem saber
    /// apontar o quê é indistinguível de estar travado — e foi assim que nove minutos se
    /// perderam contando mensagens em vez de acompanhar um `turn_id`.
    /// </summary>
    [Fact]
    public void WaitingWithoutASubjectIdIsAlwaysStalled()
    {
        Assert.Equal(WaitState.Stalled, WaitClassifier.Classify(Facts(subject: null, ageMinutes: 0)));
        Assert.Equal(WaitState.Stalled, WaitClassifier.Classify(Facts(subject: "  ", modelCall: true)));
    }

    [Fact]
    public void AFreshSubjectWithoutSignalIsStillGivenAChance()
    {
        Assert.Equal(WaitState.WaitingObservable, WaitClassifier.Classify(Facts(ageMinutes: 1)));
    }

    /// <summary>
    /// O teto de bloqueio é do próprio código, não da memória de quem escreve o comando.
    /// </summary>
    [Fact]
    public void ObservabilityBlocksLongerThanThirtySecondsAreRejected()
    {
        Assert.True(WaitClassifier.IsAcceptableBlock(TimeSpan.FromSeconds(30)));
        Assert.False(WaitClassifier.IsAcceptableBlock(TimeSpan.FromMinutes(9)));
    }
}
