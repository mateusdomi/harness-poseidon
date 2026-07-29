using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// DEL-01 — Portfólio de Entregas. Projeta PURA e deterministicamente uma entrega (um projeto) em um
/// resumo por saúde/previsibilidade, com os sinais de "precisa da minha atenção". Não é um Kanban:
/// lista entregas, não cards. A previsão embutida é honesta (DEL-09).
/// </summary>
public static class DeliveryPortfolioProjector
{
    public static DeliverySummaryContract Summarize(DeliveryProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var aggregate = DeliveryFactsCalculator.Compute(input);
        var forecast = aggregate.Forecast;

        return new DeliverySummaryContract(
            DeliveryId: input.Project.ProjectId,
            ProjectId: input.Project.ProjectId,
            Name: input.Project.Name,
            Key: input.Project.Key,
            Health: DeliveryFactsCalculator.Health(aggregate.AttentionSignals),
            Predictability: DeliveryFactsCalculator.Predictability(aggregate),
            Owner: aggregate.Owner,
            CommittedDate: aggregate.CommittedDate,
            ForecastDate: forecast.ForecastDate,
            ForecastConfidence: DeliveryForecaster.ToConfidenceString(forecast.Confidence),
            MilestonesTotal: aggregate.MilestonesTotal,
            MilestonesDone: aggregate.MilestonesDone,
            OpenTaskCount: aggregate.OpenTaskCount,
            BlockedTaskCount: aggregate.BlockedTaskCount,
            LastActivityAt: aggregate.LastActivityAt,
            StartedAt: input.Project.CreatedAt,
            TargetDeadline: input.Project.TargetDeadline,
            AttentionSignals: aggregate.AttentionSignals);
    }

    /// <summary>Filtro da visão "Precisa da minha atenção": entregas com ao menos um sinal.</summary>
    public static bool NeedsAttention(DeliverySummaryContract summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.AttentionSignals.Count > 0;
    }
}
