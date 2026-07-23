using System.Globalization;
using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// DEL-06 — núcleo PURO e determinístico das métricas de entrega. Deriva, ESTRITAMENTE dos fatos
/// gravados (tentativas de execução, planos/marcos, previsões, documentos), as métricas DORA por
/// aplicação e as métricas próprias da entrega. Regra de honestidade central: quando o INSUMO de uma
/// métrica não está registrado (p.ex. carimbos de tempo de deploy que este plano de controle não
/// captura), a métrica é "não medida" — nunca um número fabricado. Sem IO, sem invenção.
/// </summary>
public static class DeliveryMetricsCalculator
{
    public const string DoraCategory = "dora";
    public const string OwnCategory = "own";

    private static readonly string[] AccessKeywords =
    [
        "banco de dados", "banco", "database", " db ", "acesso ao banco", "acesso a dados",
        "ambiente", "environment", "homolog", "deploy", "infraestrutura", "infra",
        "credencial", "credential", "acesso", "access",
    ];

    public static DeliveryMetricsContract Compute(DeliveryProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var aggregate = DeliveryFactsCalculator.Compute(input);

        return new DeliveryMetricsContract(
            input.Project.ProjectId,
            input.AsOf,
            Dora(input),
            Own(input, aggregate));
    }

    private static IReadOnlyList<DeliveryMetricContract> Dora(DeliveryProjectionInput input)
    {
        var attempts = input.Attempts;
        var totalAttempts = attempts.Count;
        var failedAttempts = attempts.Count(a =>
            string.Equals(a.State, "rejected", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(a.FailureReason));
        var tasksWithAttempts = attempts
            .Select(a => a.TaskId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var reworkTasks = attempts
            .GroupBy(a => a.TaskId, StringComparer.Ordinal)
            .Count(g => g.Count() > 1);

        return
        [
            // Precisam de carimbos de tempo commit→deploy / evento→recuperação que este plano de
            // controle NÃO registra por tentativa: honestamente "não medidas".
            NotMeasured("change_lead_time", "Change lead time", DoraCategory,
                "No per-change commit-to-deploy timestamps are recorded; lead time cannot be derived."),
            NotMeasured("deployment_frequency", "Deployment frequency", DoraCategory,
                "No deployment events with timestamps are recorded; frequency over time cannot be derived."),
            NotMeasured("failed_deploy_recovery", "Failed-deploy recovery time", DoraCategory,
                "No deploy failure and recovery timestamps are recorded; recovery time cannot be derived."),

            // Dependem apenas de CONTAGENS de tentativas de execução (o evento de mudança registrado
            // neste plano de controle): deriváveis de forma honesta.
            totalAttempts == 0
                ? NotMeasured("change_fail_rate", "Change fail rate", DoraCategory,
                    "No execution attempts are recorded yet.")
                : Measured("change_fail_rate", "Change fail rate", DoraCategory,
                    Percent(failedAttempts, totalAttempts), "%",
                    $"{failedAttempts} of {totalAttempts} recorded execution attempts failed."),
            tasksWithAttempts == 0
                ? NotMeasured("deploy_rework", "Deploy rework", DoraCategory,
                    "No execution attempts are recorded yet.")
                : Measured("deploy_rework", "Deploy rework", DoraCategory,
                    Percent(reworkTasks, tasksWithAttempts), "%",
                    $"{reworkTasks} of {tasksWithAttempts} changed items required more than one attempt (rework)."),
        ];
    }

    private static IReadOnlyList<DeliveryMetricContract> Own(
        DeliveryProjectionInput input, DeliveryAggregate aggregate)
    {
        var expectedDocs = DeliveryFactsCalculator.ExpectedDocs.Count;
        var missingDocs = DeliveryFactsCalculator.MissingDocKinds(input.Documents).Count;
        var presentDocs = expectedDocs - missingDocs;

        var scopeChanges = input.Solicitations.Count(s => s.SupersedesId is not null);
        var homologDefects = input.Tasks.Count(t =>
            string.Equals(t.State, "corrections", StringComparison.Ordinal));
        var waitingAccess = input.Tasks.Count(t =>
            !DeliveryFactsCalculator.IsDone(t) && MatchesAccess(t.BlockedReason));

        return
        [
            ForecastAccuracy(input, aggregate),
            Measured("documentation_coverage", "% documentação", OwnCategory,
                Percent(presentDocs, expectedDocs), "%",
                $"{presentDocs} of {expectedDocs} expected documents present."),
            Measured("homologation_defects", "Defeitos de homologação", OwnCategory,
                homologDefects.ToString(CultureInfo.InvariantCulture), "count",
                $"{homologDefects} task(s) currently in the corrections state."),
            Measured("scope_changes", "Mudanças de escopo", OwnCategory,
                scopeChanges.ToString(CultureInfo.InvariantCulture), "count",
                $"{scopeChanges} solicitation(s) superseded a prior one (scope change)."),
            Measured("open_dependencies", "Dependências", OwnCategory,
                aggregate.OpenDependencies.ToString(CultureInfo.InvariantCulture), "count",
                $"{aggregate.OpenDependencies} open task(s) blocked on an unresolved dependency."),
            // A CONTAGEM de itens esperando acesso é registrada; a DURAÇÃO de espera não é (não há
            // carimbo de quando o bloqueio começou) — por isso a unidade é 'count', não tempo.
            Measured("time_waiting_access", "Tempo aguardando acesso", OwnCategory,
                waitingAccess.ToString(CultureInfo.InvariantCulture), "count",
                waitingAccess == 0
                    ? "No open task is blocked waiting on access; waiting duration is not recorded."
                    : $"{waitingAccess} open task(s) blocked waiting on access; waiting duration is not recorded."),
            Measured("planned_vs_realized_value", "Valor planejado × realizado", OwnCategory,
                $"{aggregate.MilestonesDone}/{aggregate.MilestonesTotal}", "milestones",
                $"{aggregate.MilestonesDone} of {aggregate.MilestonesTotal} planned milestone(s) realized."),
        ];
    }

    // Acurácia de previsão: só é MENSURÁVEL quando há uma data realizada para comparar. Usamos como
    // data realizada o maior UpdatedAt entre as tasks concluídas de uma entrega já 100% concluída, e
    // comparamos com a data da previsão mais recente que tinha data. Sem entrega concluída ou sem
    // previsão com data, é honestamente "não medida".
    private static DeliveryMetricContract ForecastAccuracy(
        DeliveryProjectionInput input, DeliveryAggregate aggregate)
    {
        var complete = aggregate.MilestonesTotal > 0 &&
            aggregate.MilestonesDone == aggregate.MilestonesTotal;
        var latestDated = input.ForecastHistory
            .Where(f => f.ForecastDate is not null)
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var doneTasks = input.Tasks.Where(DeliveryFactsCalculator.IsDone).ToArray();

        if (!complete || latestDated is null || doneTasks.Length == 0)
        {
            return NotMeasured("forecast_accuracy", "Acurácia da previsão", OwnCategory,
                "No realized delivery date is recorded to compare against a dated forecast.");
        }

        var realized = doneTasks.Max(t => t.UpdatedAt);
        var varianceDays = (int)Math.Round(
            (realized - latestDated.ForecastDate!.Value).TotalDays, MidpointRounding.AwayFromZero);
        return Measured("forecast_accuracy", "Acurácia da previsão", OwnCategory,
            varianceDays.ToString("+0;-0;0", CultureInfo.InvariantCulture), "days",
            $"Realized delivery landed {Math.Abs(varianceDays)} day(s) " +
            $"{(varianceDays >= 0 ? "after" : "before")} the latest dated forecast.");
    }

    private static bool MatchesAccess(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var lowered = reason.ToLower(CultureInfo.InvariantCulture);
        return AccessKeywords.Any(keyword => lowered.Contains(keyword, StringComparison.Ordinal));
    }

    private static string Percent(int numerator, int denominator) =>
        denominator == 0
            ? "0"
            : Math.Round(numerator * 100d / denominator, 1)
                .ToString("0.#", CultureInfo.InvariantCulture);

    private static DeliveryMetricContract Measured(
        string key, string label, string category, string value, string unit, string basis) =>
        new(key, label, category, true, value, unit, basis);

    private static DeliveryMetricContract NotMeasured(
        string key, string label, string category, string basis) =>
        new(key, label, category, false, null, null, basis);
}
