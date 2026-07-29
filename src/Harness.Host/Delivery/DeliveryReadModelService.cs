using Harness.Modules.Delivery.Application;
using Harness.Modules.Governance.Metrics;
using Harness.Persistence.Abstractions.Delivery;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Time;

namespace Harness.Host.Delivery;

/// <summary>
/// Camada de leitura da Central de Entregas. NÃO possui dados próprios: materializa o
/// <see cref="DeliveryProjectionInput"/> puro a partir dos stores REUSADOS (Projects, Coordination,
/// Documents, Governance/PLAT-04 + o histórico durável de previsões) e delega toda a lógica aos
/// projetores puros do módulo Delivery. A única tabela nova é o histórico append-only de previsões.
/// </summary>
public sealed class DeliveryReadModelService(
    IProjectStore projects,
    IWorkBoardStore board,
    IDocumentCatalogStore documents,
    IDeliveryForecastStore forecasts,
    SemanticStuckDetector stuckDetector,
    IClock clock)
{
    private const int PageSize = 200;
    private const int MaxPages = 25;

    private readonly IProjectStore _projects = projects;
    private readonly IWorkBoardStore _board = board;
    private readonly IDocumentCatalogStore _documents = documents;
    private readonly IDeliveryForecastStore _forecasts = forecasts;
    private readonly SemanticStuckDetector _stuckDetector = stuckDetector;
    private readonly IClock _clock = clock;

    public async Task<IReadOnlyList<DeliveryProjectionInput>> BuildPortfolioAsync(
        string tenantId, CancellationToken token)
    {
        var inputs = new List<DeliveryProjectionInput>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _projects.ListAsync(tenantId, afterId, PageSize, token);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var project in batch)
            {
                inputs.Add(await BuildInputAsync(tenantId, project, token));
            }

            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }

        return inputs;
    }

    public async Task<DeliveryProjectionInput?> BuildForProjectAsync(
        string tenantId, string projectId, CancellationToken token)
    {
        var project = await _projects.GetAsync(tenantId, projectId, token);
        return project is null ? null : await BuildInputAsync(tenantId, project, token);
    }

    public async Task<DeliveryProjectionInput> BuildInputAsync(
        string tenantId, ProjectRecord project, CancellationToken token)
    {
        var demands = await PageDemandsAsync(tenantId, project.Id, token);
        var tasks = await PageTasksAsync(tenantId, project.Id, token);
        var solicitations = await PageSolicitationsAsync(tenantId, project.Id, token);
        var docs = await PageDocumentsAsync(tenantId, project.Id, token);
        var rows = await _board.ListFeatureAttemptRowsAsync(tenantId, project.Id, token);
        var storedForecasts = await _forecasts.ListByProjectAsync(tenantId, project.Id, 200, token);

        var attempts = rows
            .Select(row => new DeliveryAttemptFacts(
                row.TaskId, row.AttemptNumber, row.State, row.FailureReason, row.InstructionContentHash))
            .ToArray();

        var featureSnapshot = FeatureMetricsAggregator.Aggregate(
            project.Id,
            rows.Select(row => new FeatureAttemptInput(
                row.TaskId, row.TaskTitle, row.State, row.OperationalState,
                row.CostUsd, row.TokensInput, row.TokensOutput, row.DurationMs)).ToArray());
        var featureMetrics = featureSnapshot.Features
            .Select(f => new DeliveryFeatureMetricFacts(
                f.FeatureId, f.TaskCount, f.AttemptCount, f.SuccessCount, f.FailureCount,
                f.TotalCostUsd, f.TotalTokensInput, f.TotalTokensOutput, f.TotalDurationMs))
            .ToArray();

        var stuckCount = CountStuck(rows);

        return new DeliveryProjectionInput(
            new DeliveryProjectFacts(
                project.Id, project.Name, project.Key, project.Criticality, project.ChiefAgentId,
                project.CreatedAt, project.LastActivityAt, project.TargetDeadline),
            _clock.UtcNow,
            solicitations,
            demands,
            tasks,
            attempts,
            docs,
            stuckCount,
            featureMetrics,
            storedForecasts
                .Select(f => new DeliveryStoredForecast(
                    f.Id, f.ForecastDate, f.Confidence, f.ConfidencePercent, f.HasSufficientEvidence,
                    f.Basis.Select(b => new DeliveryStoredForecastBasis(b.Signal, b.Detail)).ToArray(),
                    f.CreatedAt))
                .ToArray());
    }

    private int CountStuck(IReadOnlyList<FeatureAttemptRow> rows)
    {
        var count = 0;
        foreach (var group in rows.GroupBy(row => row.TaskId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(row => row.AttemptNumber).ToArray();
            var history = ordered.Select(row => new StuckAttemptSignal(
                row.AttemptNumber,
                FeatureMetricsAggregator.Classify(row.State, row.OperationalState) switch
                {
                    AttemptOutcome.Succeeded => StuckOutcome.Succeeded,
                    AttemptOutcome.Failed => StuckOutcome.Failed,
                    _ => StuckOutcome.Pending,
                },
                row.FailureReason,
                row.InstructionContentHash)).ToArray();
            if (_stuckDetector.Evaluate(history).IsStuck)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<DeliveryDemandFacts[]> PageDemandsAsync(
        string tenantId, string projectId, CancellationToken token)
    {
        var result = new List<DeliveryDemandFacts>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListDemandsAsync(tenantId, projectId, null, afterId, PageSize, token);
            result.AddRange(batch.Select(d => new DeliveryDemandFacts(d.Id, d.State, d.CreatedAt)));
            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }

        return result.ToArray();
    }

    private async Task<DeliveryTaskFacts[]> PageTasksAsync(
        string tenantId, string projectId, CancellationToken token)
    {
        var result = new List<DeliveryTaskFacts>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListTasksAsync(tenantId, projectId, null, afterId, PageSize, token);
            result.AddRange(batch
                .Where(t => t.ArchivedAt is null)
                .Select(t => new DeliveryTaskFacts(
                    t.Id, t.DemandId, t.State, t.CardType, t.AssigneeAgentId, t.BlockedReason,
                    t.DueAt, t.UpdatedAt)));
            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }

        return result.ToArray();
    }

    private async Task<DeliverySolicitationFacts[]> PageSolicitationsAsync(
        string tenantId, string projectId, CancellationToken token)
    {
        var result = new List<DeliverySolicitationFacts>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListSolicitationsAsync(tenantId, projectId, afterId, PageSize, token);
            result.AddRange(batch.Select(s =>
                new DeliverySolicitationFacts(s.Id, s.State, s.SupersedesId, s.CreatedAt)));
            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }

        return result.ToArray();
    }

    private async Task<DeliveryDocumentFacts[]> PageDocumentsAsync(
        string tenantId, string projectId, CancellationToken token)
    {
        var result = new List<DeliveryDocumentFacts>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _documents.ListDocumentsAsync(tenantId, projectId, afterId, PageSize, token);
            result.AddRange(batch.Select(d => new DeliveryDocumentFacts(d.Kind, d.State)));
            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }

        return result.ToArray();
    }
}
