using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>Uma marcação da daily já persistida, do mais recente ao mais antigo (fato puro, sem IO).</summary>
public sealed record DeliveryDailyCaptureFacts(
    string Id, string Kind, string Note, string CapturedBy, DateTimeOffset CreatedAt);

/// <summary>
/// DEL-03 — Daily Copilot. Núcleo PURO e determinístico que compõe, a partir dos fatos já gravados
/// (Projeto 360 / sinais de atenção / previsão honesta) e das marcações persistidas da daily:
///  • o briefing pré-daily (o que mudou desde a última daily, o que precisa de atenção e as perguntas
///    recomendadas);
///  • o resumo pós-daily (marcações agrupadas por tipo + foto de saúde/previsão).
/// NÃO cria nem atualiza cards de PO — apenas lê, deriva e resume. Sem IO, sem números inventados.
/// </summary>
public static class DailyCopilotComposer
{
    // Mapa determinístico sinal → pergunta recomendada. Cada pergunta nasce de um sinal concreto e
    // observável nos dados; nada é sugerido "no vazio".
    private static readonly Dictionary<string, (string Topic, string Question)> QuestionBySignal =
        new(StringComparer.Ordinal)
        {
            ["pending_db_access"] = ("access", "Who is unblocking database access, and what is the target date?"),
            ["pending_environment"] = ("access", "Which environment or credential is still pending, and who owns it?"),
            ["architectural_decision"] = ("decision", "What architectural decision is open, and who needs to make the call?"),
            ["missing_doc"] = ("doc", "Which mandatory document is missing, and who will produce it?"),
            ["committed_date_risk"] = ("deadline", "The committed date is at risk — do we rescope, add capacity, or renegotiate?"),
            ["scope_change"] = ("scope", "A solicitation superseded a prior one — is the scope change acknowledged and sized?"),
            ["dependency"] = ("dependency", "Which dependency is blocking progress, and who owns resolving it?"),
            ["no_owner"] = ("dependency", "Active work has no assigned owner — who takes it?"),
            ["no_recent_update"] = ("risk", "There has been no recent activity — is this delivery actually progressing?"),
            ["execution_stuck"] = ("risk", "Execution shows no semantic progress — what does the stuck work need to move?"),
        };

    public static DailyBriefingContract Briefing(
        DeliveryProjectionInput input, IReadOnlyList<DeliveryDailyCaptureFacts> captures)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(captures);

        var aggregate = DeliveryFactsCalculator.Compute(input);
        var snapshot = Snapshot(aggregate);
        var lastDailyAt = captures.Count > 0 ? captures.Max(c => c.CreatedAt) : (DateTimeOffset?)null;
        var isFirstDaily = lastDailyAt is null;

        var changes = ChangesSinceLast(input, aggregate, lastDailyAt);
        var questions = RecommendedQuestions(aggregate);

        return new DailyBriefingContract(
            input.Project.ProjectId,
            input.AsOf,
            isFirstDaily,
            lastDailyAt,
            snapshot,
            changes,
            aggregate.AttentionSignals,
            questions);
    }

    public static DailySummaryContract Summary(
        DeliveryProjectionInput input, IReadOnlyList<DeliveryDailyCaptureFacts> captures)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(captures);

        var aggregate = DeliveryFactsCalculator.Compute(input);
        var snapshot = Snapshot(aggregate);

        var ordered = captures
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id, StringComparer.Ordinal)
            .Select(c => new DailyCaptureContract(
                c.Id, input.Project.ProjectId, c.Kind, c.Note, c.CapturedBy, c.CreatedAt))
            .ToArray();

        // Agrupa por tipo na ordem canônica das marcações, mantendo determinismo independente da
        // ordem de captura. Só emite tipos que de fato ocorreram.
        var counts = captures
            .GroupBy(c => c.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byKind = DailyCaptureKinds.All
            .Where(counts.ContainsKey)
            .Select(kind => new DailyCaptureKindCountContract(kind, counts[kind]))
            .ToArray();

        var sessionSince = captures.Count > 0 ? captures.Min(c => c.CreatedAt) : (DateTimeOffset?)null;

        return new DailySummaryContract(
            input.Project.ProjectId,
            input.AsOf,
            sessionSince,
            captures.Count,
            byKind,
            ordered,
            snapshot,
            CreatedPoCards: false);
    }

    private static DailySnapshotContract Snapshot(DeliveryAggregate aggregate)
    {
        var health = DeliveryFactsCalculator.Health(aggregate.AttentionSignals);
        var predictability = DeliveryFactsCalculator.Predictability(aggregate);
        var forecast = new DeliveryForecastContract(
            null,
            aggregate.Forecast.ForecastDate,
            DeliveryForecaster.ToConfidenceString(aggregate.Forecast.Confidence),
            aggregate.Forecast.ConfidencePercent,
            aggregate.Forecast.HasSufficientEvidence,
            aggregate.Forecast.Basis.Select(b => new ForecastBasisContract(b.Signal, b.Detail)).ToArray(),
            null);
        return new DailySnapshotContract(
            health, predictability, aggregate.OpenTaskCount, aggregate.BlockedTaskCount,
            aggregate.MilestonesTotal, aggregate.MilestonesDone, forecast);
    }

    private static List<DailyChangeContract> ChangesSinceLast(
        DeliveryProjectionInput input, DeliveryAggregate aggregate, DateTimeOffset? lastDailyAt)
    {
        var changes = new List<DailyChangeContract>();

        if (lastDailyAt is null)
        {
            // Primeira daily: não há "desde a última"; reportamos o estado inicial observável.
            changes.Add(new("first_daily",
                $"First daily — {aggregate.MilestonesTotal} milestone(s), {aggregate.OpenTaskCount} open task(s), " +
                $"{aggregate.BlockedTaskCount} blocked."));
            return changes;
        }

        var since = lastDailyAt.Value;
        var updatedTasks = input.Tasks.Count(t => t.UpdatedAt > since);
        if (updatedTasks > 0)
        {
            changes.Add(new("tasks_updated", $"{updatedTasks} task(s) updated since the last daily."));
        }

        var newDemands = input.Demands.Count(d => d.CreatedAt > since);
        if (newDemands > 0)
        {
            changes.Add(new("milestones_added", $"{newDemands} new milestone(s) added since the last daily."));
        }

        var newForecasts = input.ForecastHistory.Count(f => f.CreatedAt > since);
        if (newForecasts > 0)
        {
            changes.Add(new("forecast_recorded",
                $"{newForecasts} new forecast(s) recorded since the last daily."));
        }

        var newSolicitations = input.Solicitations.Count(s => s.CreatedAt > since);
        if (newSolicitations > 0)
        {
            changes.Add(new("solicitations_added",
                $"{newSolicitations} new solicitation(s) since the last daily."));
        }

        if (changes.Count == 0)
        {
            changes.Add(new("no_changes", "No recorded changes since the last daily."));
        }

        return changes;
    }

    private static List<DailyQuestionContract> RecommendedQuestions(DeliveryAggregate aggregate)
    {
        // Uma pergunta por sinal de atenção presente, sem duplicar (o mesmo código pode ocorrer uma
        // vez). Ordem estável = ordem em que os sinais foram derivados.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var questions = new List<DailyQuestionContract>();
        foreach (var signal in aggregate.AttentionSignals)
        {
            if (!seen.Add(signal.Code))
            {
                continue;
            }

            if (QuestionBySignal.TryGetValue(signal.Code, out var mapped))
            {
                questions.Add(new(mapped.Topic, mapped.Question));
            }
        }

        if (questions.Count == 0)
        {
            questions.Add(new("status",
                "No attention signals — confirm the plan still holds and the next milestone is on track."));
        }

        return questions;
    }
}
