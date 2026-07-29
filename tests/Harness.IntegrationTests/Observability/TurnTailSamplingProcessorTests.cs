using System.Diagnostics;
using Harness.Host.Observability;
using Harness.Modules.Coordination.Application;

namespace Harness.IntegrationTests.Observability;

/// <summary>
/// F12/B4 — a amostragem de cauda decide com o turno ENCERRADO. O que estes testes protegem é o
/// caso que a amostragem de cabeça erra por construção: o turno raro que falhou ou demorou demais
/// não pode ser descartado pela mesma fração que descarta o turno banal.
/// </summary>
public sealed class TurnTailSamplingProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("Poseidon.Tests.TailSampling");
    private readonly ActivityListener _listener;

    public TurnTailSamplingProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Poseidon.Tests.TailSampling",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public void FailedTurnIsAlwaysRetained()
    {
        using var activity = StartTurn();
        activity.SetTag(TurnTailSamplingProcessor.OutcomeTagName, "failed");
        activity.Stop();

        new TurnTailSamplingProcessor().OnEnd(activity);

        Assert.True(activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded));
        Assert.Equal(
            TurnTailSamplingPolicy.ReasonNotCompleted,
            activity.GetTagItem(TurnTailSamplingProcessor.DecisionTagName));
    }

    [Fact]
    public void TurnInterruptedByALoopGuardIsAlwaysRetained()
    {
        using var activity = StartTurn();
        activity.SetTag(TurnTailSamplingProcessor.OutcomeTagName, "completed");
        activity.SetTag(TurnTailSamplingProcessor.GuardBlockedTagName, "true");
        activity.Stop();

        new TurnTailSamplingProcessor().OnEnd(activity);

        Assert.True(activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded));
        Assert.Equal(
            TurnTailSamplingPolicy.ReasonGuardBlocked,
            activity.GetTagItem(TurnTailSamplingProcessor.DecisionTagName));
    }

    [Fact]
    public void OrdinaryCompletedTurnsAreSampledDown()
    {
        // Fração agressiva para tornar o descarte observável sem depender de sorteio.
        var processor = new TurnTailSamplingProcessor(
            new TurnTailSamplingOptions(BaselineKeepEvery: 1_000_000));
        var dropped = 0;
        for (var index = 0; index < 20; index++)
        {
            using var activity = StartTurn();
            activity.SetTag(TurnTailSamplingProcessor.OutcomeTagName, "completed");
            activity.Stop();
            processor.OnEnd(activity);
            if (!activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded))
            {
                dropped++;
            }
        }

        Assert.True(dropped > 0, "nenhum turno rotineiro foi amostrado para fora");
    }

    [Fact]
    public void SpansThatAreNotChiefTurnsPassUntouched()
    {
        using var activity = _source.StartActivity("poseidon.outra.coisa", ActivityKind.Internal);
        Assert.NotNull(activity);
        activity!.Stop();

        new TurnTailSamplingProcessor(new TurnTailSamplingOptions(BaselineKeepEvery: 1_000_000))
            .OnEnd(activity);

        Assert.True(activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded));
        Assert.Null(activity.GetTagItem(TurnTailSamplingProcessor.DecisionTagName));
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private Activity StartTurn()
    {
        var activity = _source.StartActivity(
            TurnTailSamplingProcessor.ChiefTurnActivityName, ActivityKind.Consumer);
        Assert.NotNull(activity);
        return activity!;
    }
}
