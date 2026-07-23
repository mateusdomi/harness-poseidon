using System.Globalization;
using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// Núcleo PURO e determinístico compartilhado por DEL-01 (portfólio) e DEL-02 (360). Deriva, apenas
/// a partir dos fatos gravados, os agregados da entrega: contagens de tasks, marcos (= demandas),
/// dependências abertas, validações pendentes, variação média histórica, os SINAIS DE ATENÇÃO
/// tipados e o input honesto de previsão. Sem IO, sem números inventados.
/// </summary>
public static class DeliveryFactsCalculator
{
    public const string DoneState = "done";
    public const string BacklogState = "backlog";
    public const string BlockedState = "blocked";

    private static readonly string[] DbKeywords =
        ["banco de dados", "banco", "database", " db ", "acesso ao banco", "acesso a dados"];
    private static readonly string[] EnvironmentKeywords =
        ["ambiente", "environment", "homolog", "deploy", "infraestrutura", "infra", "credencial", "credential"];
    private static readonly string[] DecisionKeywords =
        ["decisão", "decisao", "arquitet", "architect", "decision", "trade-off", "tradeoff"];
    private static readonly string[] DependencyKeywords =
        ["depend", "dependência", "dependencia", "aguard", "waiting", "blocked by", "bloqueado por", "espera"];

    // Uma entrega é considerada "sem atualização recente" após este período sem atividade, se houver
    // trabalho em aberto. Limiar operacional, não uma data inventada.
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

    public static DeliveryAggregate Compute(DeliveryProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var tasks = input.Tasks;
        var openTasks = tasks.Where(t => !IsDone(t)).ToArray();
        var blockedTasks = tasks.Where(IsBlocked).ToArray();
        var pendingValidationTasks = tasks
            .Where(t => t.State is "review" or "testsGates")
            .ToArray();

        // Marcos = demandas (unidades de entrega do projeto). Um marco está concluído quando TODAS as
        // suas tasks estão 'done' e existe ao menos uma task — nunca marcamos "concluído" no vazio.
        var tasksByDemand = tasks
            .Where(t => t.DemandId is not null)
            .GroupBy(t => t.DemandId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var milestonesTotal = input.Demands.Count;
        var milestonesDone = input.Demands.Count(demand =>
            tasksByDemand.TryGetValue(demand.Id, out var demandTasks) &&
            demandTasks.Length > 0 &&
            demandTasks.All(IsDone));

        // Data comprometida = a maior DueAt registrada entre as tasks (a data-alvo de entrega). Nula
        // quando nenhuma task tem prazo — nesse caso a previsão será honestamente "sem data".
        DateTimeOffset? committedDate = tasks
            .Where(t => t.DueAt.HasValue)
            .Select(t => t.DueAt!.Value)
            .DefaultIfEmpty()
            .Max();
        if (committedDate == default(DateTimeOffset))
        {
            committedDate = null;
        }

        // Variação média histórica = média de (UpdatedAt - DueAt) em dias sobre tasks 'done' que
        // tinham prazo. Nula quando não há histórico datado. Estritamente derivada de dados reais.
        var variances = tasks
            .Where(t => IsDone(t) && t.DueAt.HasValue)
            .Select(t => (t.UpdatedAt - t.DueAt!.Value).TotalDays)
            .ToArray();
        double? averageVarianceDays = variances.Length > 0 ? variances.Average() : null;

        var openDependencies = openTasks.Count(t => MatchesReason(t, DependencyKeywords));

        var lastActivity = tasks.Count > 0
            ? tasks.Max(t => t.UpdatedAt) is var maxTask && maxTask > input.Project.LastActivityAt
                ? maxTask
                : input.Project.LastActivityAt
            : input.Project.LastActivityAt;

        var forecastInput = new ForecastInput(
            milestonesTotal, milestonesDone, openDependencies, pendingValidationTasks.Length,
            averageVarianceDays, committedDate, input.AsOf);
        var forecast = DeliveryForecaster.Forecast(forecastInput);

        var owner = ResolveOwner(tasks);
        var signals = DeriveAttentionSignals(
            input, openTasks, openDependencies, committedDate, forecast, lastActivity);

        return new DeliveryAggregate(
            OpenTaskCount: openTasks.Length,
            BlockedTaskCount: blockedTasks.Length,
            MilestonesTotal: milestonesTotal,
            MilestonesDone: milestonesDone,
            OpenDependencies: openDependencies,
            PendingValidations: pendingValidationTasks.Length,
            AverageVarianceDays: averageVarianceDays,
            CommittedDate: committedDate,
            LastActivityAt: lastActivity,
            Owner: owner,
            Forecast: forecast,
            AttentionSignals: signals);
    }

    public static string Health(IReadOnlyList<AttentionSignalContract> signals)
    {
        if (signals.Any(s => s.Severity == "critical"))
        {
            return "red";
        }

        return signals.Any(s => s.Severity == "warning") ? "yellow" : "green";
    }

    public static string Predictability(DeliveryAggregate aggregate)
    {
        var forecast = aggregate.Forecast;
        if (!forecast.HasSufficientEvidence || aggregate.CommittedDate is null ||
            forecast.ForecastDate is null)
        {
            return "unknown";
        }

        var committed = aggregate.CommittedDate.Value;
        var predicted = forecast.ForecastDate.Value;
        if (predicted > committed.AddDays(3))
        {
            return "off_track";
        }

        if (predicted > committed || forecast.Confidence == ForecastConfidence.Low)
        {
            return "at_risk";
        }

        return "on_track";
    }

    private static List<AttentionSignalContract> DeriveAttentionSignals(
        DeliveryProjectionInput input,
        DeliveryTaskFacts[] openTasks,
        int openDependencies,
        DateTimeOffset? committedDate,
        ForecastResult forecast,
        DateTimeOffset lastActivity)
    {
        var signals = new List<AttentionSignalContract>();
        var allDone = input.Tasks.Count > 0 && input.Tasks.All(IsDone);
        var hasOpenWork = openTasks.Length > 0;

        if (openTasks.Any(t => MatchesReason(t, DbKeywords)))
        {
            signals.Add(new("pending_db_access", "critical",
                "One or more tasks are waiting on database access."));
        }

        if (openTasks.Any(t => MatchesReason(t, EnvironmentKeywords)))
        {
            signals.Add(new("pending_environment", "warning",
                "One or more tasks are waiting on an environment or credential."));
        }

        if (openTasks.Any(t => string.Equals(t.CardType, "decision", StringComparison.Ordinal)) ||
            openTasks.Any(t => MatchesReason(t, DecisionKeywords)))
        {
            signals.Add(new("architectural_decision", "warning",
                "An architectural decision is pending resolution."));
        }

        var missingDocs = MissingDocKinds(input.Documents);
        if (missingDocs.Count > 0)
        {
            signals.Add(new("missing_doc", "warning",
                $"Expected documentation missing: {string.Join(", ", missingDocs)}."));
        }

        var committedRisk =
            (committedDate is not null && input.AsOf > committedDate && !allDone) ||
            (committedDate is not null && forecast.ForecastDate is not null &&
             forecast.ForecastDate > committedDate);
        if (committedRisk)
        {
            signals.Add(new("committed_date_risk", "critical",
                "The committed date is at risk given the current forecast."));
        }

        if (input.Solicitations.Any(s => s.SupersedesId is not null))
        {
            signals.Add(new("scope_change", "warning",
                "A solicitation superseded a prior one (scope changed)."));
        }

        if (openDependencies > 0)
        {
            signals.Add(new("dependency", "warning",
                $"{openDependencies} task(s) blocked on an unresolved dependency."));
        }

        if (openTasks.Any(t =>
                !string.Equals(t.State, BacklogState, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(t.AssigneeAgentId)))
        {
            signals.Add(new("no_owner", "warning",
                "Active work exists without an assigned owner."));
        }

        if (hasOpenWork && input.AsOf - lastActivity > StaleThreshold)
        {
            var days = (int)Math.Floor((input.AsOf - lastActivity).TotalDays);
            signals.Add(new("no_recent_update", "warning",
                $"No activity for {days} day(s) while work is open."));
        }

        if (input.StuckTaskCount > 0)
        {
            signals.Add(new("execution_stuck", "critical",
                $"{input.StuckTaskCount} task(s) show no semantic progress (PLAT-04 stuck detection)."));
        }

        return signals;
    }

    // Documentação esperada de uma entrega técnica (checklist mínimo). Presença = existe ao menos um
    // documento daquele tipo no projeto.
    public static readonly IReadOnlyList<(string Kind, string Label)> ExpectedDocs =
    [
        ("spec", "Especificação"),
        ("design", "Design técnico"),
        ("runbook", "Runbook operacional"),
        ("report", "Relatório de entrega"),
    ];

    public static IReadOnlyList<string> MissingDocKinds(IReadOnlyList<DeliveryDocumentFacts> documents)
    {
        var present = documents
            .Select(d => d.Kind)
            .ToHashSet(StringComparer.Ordinal);
        return ExpectedDocs
            .Where(expected => !present.Contains(expected.Kind))
            .Select(expected => expected.Kind)
            .ToArray();
    }

    private static string? ResolveOwner(IReadOnlyList<DeliveryTaskFacts> tasks)
    {
        // Dono = o agente com mais tasks ativas atribuídas; nulo se nenhum tem dono.
        var byOwner = tasks
            .Where(t => !IsDone(t) && !string.IsNullOrWhiteSpace(t.AssigneeAgentId))
            .GroupBy(t => t.AssigneeAgentId!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return byOwner?.Key;
    }

    public static bool IsDone(DeliveryTaskFacts task) =>
        string.Equals(task.State, DoneState, StringComparison.Ordinal);

    private static bool IsBlocked(DeliveryTaskFacts task) =>
        string.Equals(task.State, BlockedState, StringComparison.Ordinal) ||
        !string.IsNullOrWhiteSpace(task.BlockedReason);

    private static bool MatchesReason(DeliveryTaskFacts task, string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(task.BlockedReason))
        {
            return false;
        }

        var reason = task.BlockedReason.ToLower(CultureInfo.InvariantCulture);
        return keywords.Any(keyword => reason.Contains(keyword, StringComparison.Ordinal));
    }
}

/// <summary>Agregado puro derivado dos fatos, base para os contratos DEL-01/DEL-02.</summary>
public sealed record DeliveryAggregate(
    int OpenTaskCount,
    int BlockedTaskCount,
    int MilestonesTotal,
    int MilestonesDone,
    int OpenDependencies,
    int PendingValidations,
    double? AverageVarianceDays,
    DateTimeOffset? CommittedDate,
    DateTimeOffset LastActivityAt,
    string? Owner,
    ForecastResult Forecast,
    IReadOnlyList<AttentionSignalContract> AttentionSignals);
