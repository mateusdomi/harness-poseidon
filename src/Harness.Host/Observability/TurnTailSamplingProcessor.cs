using System.Diagnostics;
using Harness.Modules.Coordination.Application;
using OpenTelemetry;

namespace Harness.Host.Observability;

/// <summary>
/// Amostragem de CAUDA dos turnos do Chefe (F12/B4).
///
/// A decisão acontece em <see cref="OnEnd"/>, e não no início, porque só no fim se conhece a
/// duração e o desfecho — que é exatamente o que separa o turno interessante do turno rotineiro.
/// Amostragem de cabeça sorteia antes de saber, e por isso descarta com a mesma probabilidade o
/// turno banal e o turno de trinta minutos que travou.
///
/// Retirar o sinalizador <see cref="ActivityTraceFlags.Recorded"/> é o mecanismo padrão do
/// OpenTelemetry para descartar um span já encerrado: o exportador não o publica. Spans que não
/// são turnos do Chefe passam intocados — esta política não opina sobre o resto da telemetria.
/// </summary>
internal sealed class TurnTailSamplingProcessor(TurnTailSamplingOptions? options = null)
    : BaseProcessor<Activity>
{
    internal const string ChiefTurnActivityName = "poseidon.chief.turn";
    internal const string OutcomeTagName = "turn_result";
    internal const string GuardBlockedTagName = "loop_guard_blocked";
    internal const string DecisionTagName = "tail_sampling.reason";

    private readonly TurnTailSamplingOptions _options = options ?? new TurnTailSamplingOptions();

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!string.Equals(data.OperationName, ChiefTurnActivityName, StringComparison.Ordinal))
        {
            base.OnEnd(data);
            return;
        }

        var decision = TurnTailSamplingPolicy.Decide(
            new TurnTailSamplingFacts(
                data.TraceId.ToString(),
                data.Duration,
                ReadTag(data, OutcomeTagName) ?? TurnTailSamplingPolicy.CompletedOutcome,
                GuardBlocked: string.Equals(
                    ReadTag(data, GuardBlockedTagName), "true", StringComparison.OrdinalIgnoreCase)),
            _options);

        // O motivo fica no span retido: sem ele, quem lê o traço não sabe por que aquele turno
        // sobreviveu à amostragem e não consegue interpretar a distribuição do que restou.
        data.SetTag(DecisionTagName, decision.ReasonCode);
        if (!decision.Keep)
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }

        base.OnEnd(data);
    }

    private static string? ReadTag(Activity activity, string name) =>
        activity.GetTagItem(name)?.ToString();
}
