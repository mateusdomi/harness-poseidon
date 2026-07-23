using System.Globalization;
using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// DEL-04/DEL-05 — compositor PURO e determinístico de documentos de relatório. Não possui IO nem
/// números inventados: REUSA o Projeto 360 (DEL-02), o agregado de fatos e a previsão honesta (DEL-09)
/// já derivados dos stores. Cada tipo de relatório é uma seleção/organização dessas mesmas verdades em
/// seções — o dossiê de encerramento (DEL-05) é apenas mais um tipo com seu próprio template.
/// </summary>
public static class DeliveryReportComposer
{
    public static ReportDocument Compose(DeliveryReportSpec spec, DeliveryProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(input);

        var overview = Delivery360Projector.Project(input);
        var aggregate = DeliveryFactsCalculator.Compute(input);
        var facts = ReportFacts.Derive(input, aggregate);

        var sections = spec.Type switch
        {
            DeliveryReportType.WeeklyExecutiveStatus => WeeklyExecutiveStatus(overview, aggregate, facts),
            DeliveryReportType.MilestoneReport => MilestoneReport(input, overview, aggregate),
            DeliveryReportType.HomologationReadiness => Readiness(overview, aggregate, facts, production: false),
            DeliveryReportType.ProductionReadiness => Readiness(overview, aggregate, facts, production: true),
            DeliveryReportType.ClosureDossier => ClosureDossier(input, overview, aggregate, facts),
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };

        return new ReportDocument(
            DeliveryReportTokens.ToToken(spec.Type),
            Title(spec.Type, overview.ExecutiveSummary.Name),
            overview.DeliveryId,
            overview.ExecutiveSummary.Key,
            spec.Audience,
            spec.Classification,
            spec.GeneratedAt,
            sections);
    }

    private static string Title(DeliveryReportType type, string deliveryName) => type switch
    {
        DeliveryReportType.WeeklyExecutiveStatus => $"Status executivo semanal — {deliveryName}",
        DeliveryReportType.MilestoneReport => $"Relatório de marcos — {deliveryName}",
        DeliveryReportType.HomologationReadiness => $"Prontidão para homologação — {deliveryName}",
        DeliveryReportType.ProductionReadiness => $"Prontidão para produção — {deliveryName}",
        DeliveryReportType.ClosureDossier => $"Dossiê de encerramento — {deliveryName}",
        _ => deliveryName,
    };

    // -----------------------------------------------------------------------------------------------
    // Seções comuns
    // -----------------------------------------------------------------------------------------------

    private static ReportSection ExecutiveSummarySection(DeliveryExecutiveSummaryContract summary) =>
        new("executive_summary", "Resumo executivo",
        [
            new("Entrega", summary.Name),
            new("Chave", summary.Key),
            new("Criticidade", summary.Criticality),
            new("Saúde", summary.Health),
            new("Previsibilidade", summary.Predictability),
            new("Responsável", summary.Owner ?? "sem responsável atribuído"),
            new("Marcos", $"{summary.MilestonesDone}/{summary.MilestonesTotal} concluídos"),
            new("Tasks abertas", Int(summary.OpenTaskCount)),
            new("Tasks bloqueadas", Int(summary.BlockedTaskCount)),
            new("Data comprometida", Date(summary.CommittedDate)),
            new("Previsão", Date(summary.ForecastDate)),
            new("Sinais de atenção", Int(summary.AttentionSignalCount)),
        ], Table: null);

    private static ReportSection ForecastSection(DeliveryForecastContract forecast) =>
        new("forecast", "Previsão honesta",
        [
            new("Data prevista", Date(forecast.ForecastDate)),
            new("Confiança", forecast.Confidence),
            new("Score", Int(forecast.ConfidencePercent)),
            new("Evidência suficiente", Bool(forecast.HasSufficientEvidence)),
        ],
        new ReportTable(
            ["Sinal", "Detalhe"],
            forecast.Basis.Select(b => (IReadOnlyList<string>)[b.Signal, b.Detail]).ToArray()));

    private static ReportSection AttentionSection(
        string key, string title, IReadOnlyList<AttentionSignalContract> signals) =>
        new(key, title,
        [
            new("Total", Int(signals.Count)),
            new("Críticos", Int(signals.Count(s => s.Severity == "critical"))),
        ],
        new ReportTable(
            ["Código", "Severidade", "Detalhe"],
            signals.Select(s => (IReadOnlyList<string>)[s.Code, s.Severity, s.Detail]).ToArray()));

    private static ReportSection TechnicalHealthSection(DeliveryTechnicalHealthContract health) =>
        new("technical_health", "Saúde técnica",
            [new("Indicadores", Int(health.Indicators.Count))],
            new ReportTable(
                ["Indicador", "Status", "Valor", "Detalhe"],
                health.Indicators
                    .Select(i => (IReadOnlyList<string>)[i.Label, i.Status, i.Value, i.Detail])
                    .ToArray()));

    private static ReportSection DocumentationSection(DeliveryDocumentationContract docs) =>
        new("documentation", "Documentação",
            [new("Presentes", $"{docs.Present}/{docs.Expected} esperados")],
            new ReportTable(
                ["Tipo", "Rótulo", "Presente", "Estado"],
                docs.Checklist
                    .Select(c => (IReadOnlyList<string>)[c.Kind, c.Label, Bool(c.Present), c.State ?? "-"])
                    .ToArray()));

    private static ReportSection MetricsSection(DeliveryValueMetricsContract metrics) =>
        new("value_metrics", "Valor & métricas",
        [
            new("Custo total (USD)", metrics.TotalCostUsd.ToString("0.####", CultureInfo.InvariantCulture)),
            new("Tasks (features)", Int(metrics.TotalTasks)),
            new("Features", Int(metrics.Features.Count)),
        ],
        new ReportTable(
            ["Feature", "Tasks", "Tentativas", "Sucessos", "Falhas", "Custo USD"],
            metrics.Features
                .Select(f => (IReadOnlyList<string>)
                [
                    f.FeatureId, Int(f.TaskCount), Int(f.AttemptCount), Int(f.SuccessCount),
                    Int(f.FailureCount), f.TotalCostUsd.ToString("0.####", CultureInfo.InvariantCulture),
                ])
                .ToArray()));

    private static ReportSection DecisionsSection(DeliveryDecisionsContract decisions) =>
        new("decisions", "Decisões",
        [
            new("Total", Int(decisions.Total)),
            new("Abertas", Int(decisions.Open)),
        ],
        new ReportTable(
            ["Task", "Estado", "Detalhe", "Resolvida"],
            decisions.Decisions
                .Select(d => (IReadOnlyList<string>)[d.TaskId, d.State, d.Detail, Bool(d.Resolved)])
                .ToArray()));

    // -----------------------------------------------------------------------------------------------
    // DEL-04 — tipos de relatório
    // -----------------------------------------------------------------------------------------------

    private static IReadOnlyList<ReportSection> WeeklyExecutiveStatus(
        Delivery360Contract overview, DeliveryAggregate aggregate, ReportFacts facts) =>
    [
        ExecutiveSummarySection(overview.ExecutiveSummary),
        AttentionSection("attention", "Precisa de atenção", aggregate.AttentionSignals),
        ForecastSection(overview.PlanAndMilestones.Forecast),
        new ReportSection("progress", "Progresso da semana",
        [
            new("Tasks concluídas", $"{facts.DoneTasks}/{facts.TotalTasks}"),
            new("Tentativas aprovadas", $"{facts.ApprovedAttempts}/{facts.TotalAttempts}"),
            new("Retrabalho", Int(facts.ReworkTasks)),
            new("Execuções travadas", Int(facts.StuckTasks)),
        ], Table: null),
        MetricsSection(overview.ValueAndMetrics),
    ];

    private static IReadOnlyList<ReportSection> MilestoneReport(
        DeliveryProjectionInput input, Delivery360Contract overview, DeliveryAggregate aggregate) =>
    [
        ExecutiveSummarySection(overview.ExecutiveSummary),
        new ReportSection("milestones", "Marcos",
        [
            new("Total", Int(aggregate.MilestonesTotal)),
            new("Concluídos", Int(aggregate.MilestonesDone)),
            new("Data comprometida", Date(aggregate.CommittedDate)),
        ],
        new ReportTable(
            ["Marco", "Estado", "Criado em"],
            input.Demands
                .OrderBy(d => d.CreatedAt)
                .Select(d => (IReadOnlyList<string>)[d.Id, d.State, Date(d.CreatedAt)])
                .ToArray())),
        ForecastSection(overview.PlanAndMilestones.Forecast),
        AttentionSection("risks", "Riscos & dependências", aggregate.AttentionSignals),
    ];

    private static IReadOnlyList<ReportSection> Readiness(
        Delivery360Contract overview, DeliveryAggregate aggregate, ReportFacts facts, bool production)
    {
        // Critérios de prontidão derivados dos fatos. Produção é ESTRITAMENTE mais exigente que
        // homologação: exige runbook presente e ZERO sinais críticos, além de tudo já exigido.
        var missingDocs = overview.Documentation.Expected - overview.Documentation.Present;
        var criticalSignals = aggregate.AttentionSignals.Count(s => s.Severity == "critical");
        var runbookPresent = overview.Documentation.Checklist
            .Any(c => c.Kind == "runbook" && c.Present);

        var criteria = new List<(string Label, bool Met, string Detail)>
        {
            ("Sem tasks bloqueadas", aggregate.BlockedTaskCount == 0, $"{aggregate.BlockedTaskCount} bloqueada(s)"),
            ("Sem dependências abertas", aggregate.OpenDependencies == 0, $"{aggregate.OpenDependencies} aberta(s)"),
            ("Sem validações pendentes", aggregate.PendingValidations == 0, $"{aggregate.PendingValidations} pendente(s)"),
            ("Sem execuções travadas", facts.StuckTasks == 0, $"{facts.StuckTasks} travada(s)"),
            ("Documentação completa", missingDocs == 0, $"{missingDocs} faltando"),
        };
        if (production)
        {
            criteria.Add(("Todos os marcos concluídos",
                aggregate.MilestonesTotal > 0 && aggregate.MilestonesDone == aggregate.MilestonesTotal,
                $"{aggregate.MilestonesDone}/{aggregate.MilestonesTotal}"));
            criteria.Add(("Runbook operacional presente", runbookPresent, Bool(runbookPresent)));
            criteria.Add(("Sem sinais críticos", criticalSignals == 0, $"{criticalSignals} crítico(s)"));
        }

        var ready = criteria.All(c => c.Met);
        var label = production ? "produção" : "homologação";

        return
        [
            ExecutiveSummarySection(overview.ExecutiveSummary),
            new ReportSection("readiness", $"Prontidão para {label}",
                [new("Veredito", ready ? "pronto" : "não pronto")],
                new ReportTable(
                    ["Critério", "Atendido", "Detalhe"],
                    criteria.Select(c => (IReadOnlyList<string>)[c.Label, Bool(c.Met), c.Detail]).ToArray())),
            TechnicalHealthSection(overview.TechnicalHealth),
            DocumentationSection(overview.Documentation),
            AttentionSection("open_risks", "Riscos em aberto", aggregate.AttentionSignals),
        ];
    }

    // -----------------------------------------------------------------------------------------------
    // DEL-05 — Dossiê de encerramento
    // -----------------------------------------------------------------------------------------------

    private static IReadOnlyList<ReportSection> ClosureDossier(
        DeliveryProjectionInput input, Delivery360Contract overview, DeliveryAggregate aggregate,
        ReportFacts facts)
    {
        var architecture = overview.Documentation.Checklist.FirstOrDefault(c => c.Kind == "design");
        var elapsedDays = Math.Max(0, (int)Math.Floor((input.AsOf - input.Project.CreatedAt).TotalDays));
        var solicitationsTotal = input.Solicitations.Count;
        var scopeChanges = input.Solicitations.Count(s => s.SupersedesId is not null);
        var residualRisks = aggregate.AttentionSignals;

        return
        [
            ExecutiveSummarySection(overview.ExecutiveSummary),
            new ReportSection("solicited_delivered", "Solicitado × entregue",
            [
                new("Solicitações", Int(solicitationsTotal)),
                new("Mudanças de escopo", Int(scopeChanges)),
                new("Marcos entregues", $"{aggregate.MilestonesDone}/{aggregate.MilestonesTotal}"),
                new("Tasks concluídas", $"{facts.DoneTasks}/{facts.TotalTasks}"),
            ], Table: null),
            new ReportSection("architecture", "Arquitetura final",
            [
                new("Referência de arquitetura",
                    architecture is { Present: true }
                        ? $"documento de design presente (estado: {architecture.State ?? "-"})"
                        : "documento de design ausente"),
            ], Table: null),
            new ReportSection("elapsed_time", "Tempo decorrido",
            [
                new("Início", Date(input.Project.CreatedAt)),
                new("Referência", Date(input.AsOf)),
                new("Dias decorridos", Int(elapsedDays)),
            ], Table: null),
            DecisionsSection(overview.Decisions),
            MetricsSection(overview.ValueAndMetrics),
            new ReportSection("defects", "Defeitos",
            [
                new("Tentativas falhas", Int(facts.FailedAttempts)),
                new("Tentativas rejeitadas", Int(facts.RejectedAttempts)),
                new("Tasks com retrabalho", Int(facts.ReworkTasks)),
            ], Table: null),
            new ReportSection("value", "Valor entregue",
            [
                new("Custo total (USD)",
                    overview.ValueAndMetrics.TotalCostUsd.ToString("0.####", CultureInfo.InvariantCulture)),
                new("Tasks entregues", Int(facts.DoneTasks)),
                new("Marcos entregues", Int(aggregate.MilestonesDone)),
            ], Table: null),
            AttentionSection("residual_risks", "Riscos residuais", residualRisks),
            new ReportSection("lessons", "Lições aprendidas",
                [new("Observações derivadas", Int(facts.Lessons.Count))],
                new ReportTable(
                    ["Observação"],
                    facts.Lessons.Select(l => (IReadOnlyList<string>)[l]).ToArray())),
            new ReportSection("support", "Sustentação",
            [
                new("Responsável principal", aggregate.Owner ?? "sem responsável atribuído"),
                new("Responsáveis ativos", string.Join(", ", facts.Owners) is { Length: > 0 } o ? o : "nenhum"),
            ], Table: null),
        ];
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "sim" : "não";

    private static string Date(DateTimeOffset? value) =>
        value is null
            ? "sem data"
            : value.Value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Fatos derivados adicionais (tentativas/retrabalho/lições) recalculados dos fatos brutos.</summary>
    private sealed record ReportFacts(
        int TotalTasks,
        int DoneTasks,
        int TotalAttempts,
        int ApprovedAttempts,
        int RejectedAttempts,
        int FailedAttempts,
        int ReworkTasks,
        int StuckTasks,
        IReadOnlyList<string> Owners,
        IReadOnlyList<string> Lessons)
    {
        public static ReportFacts Derive(DeliveryProjectionInput input, DeliveryAggregate aggregate)
        {
            var tasks = input.Tasks;
            var attempts = input.Attempts;
            var totalTasks = tasks.Count;
            var doneTasks = tasks.Count(DeliveryFactsCalculator.IsDone);
            var totalAttempts = attempts.Count;
            var approved = attempts.Count(a => string.Equals(a.State, "approved", StringComparison.OrdinalIgnoreCase));
            var rejected = attempts.Count(a => string.Equals(a.State, "rejected", StringComparison.OrdinalIgnoreCase));
            var failed = attempts.Count(a =>
                string.Equals(a.State, "rejected", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(a.FailureReason));
            var rework = attempts
                .GroupBy(a => a.TaskId, StringComparer.Ordinal)
                .Count(g => g.Count() > 1);
            var owners = tasks
                .Where(t => !DeliveryFactsCalculator.IsDone(t) && !string.IsNullOrWhiteSpace(t.AssigneeAgentId))
                .Select(t => t.AssigneeAgentId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            // Lições determinísticas: observações derivadas dos fatos (nunca texto livre inventado).
            var lessons = new List<string>();
            if (rework > 0)
            {
                lessons.Add($"{rework} task(s) exigiram múltiplas tentativas (retrabalho).");
            }

            if (input.StuckTaskCount > 0)
            {
                lessons.Add($"{input.StuckTaskCount} task(s) ficaram sem progresso semântico (PLAT-04).");
            }

            if (aggregate.AverageVarianceDays is { } variance && Math.Abs(variance) > 2)
            {
                lessons.Add($"Variação média de prazo de {variance:+0.#;-0.#;0} dia(s) sobre marcos concluídos.");
            }

            if (scopeChanged(input))
            {
                lessons.Add("O escopo mudou ao menos uma vez (solicitação substituída).");
            }

            if (lessons.Count == 0)
            {
                lessons.Add("Sem desvios materiais registrados nos fatos da entrega.");
            }

            return new ReportFacts(
                totalTasks, doneTasks, totalAttempts, approved, rejected, failed, rework,
                input.StuckTaskCount, owners, lessons);

            static bool scopeChanged(DeliveryProjectionInput i) =>
                i.Solicitations.Any(s => s.SupersedesId is not null);
        }
    }
}

/// <summary>Metadados de composição do relatório: tipo, público-alvo, classificação e instante da foto.</summary>
public sealed record DeliveryReportSpec(
    DeliveryReportType Type,
    string Audience,
    string Classification,
    DateTimeOffset GeneratedAt);
