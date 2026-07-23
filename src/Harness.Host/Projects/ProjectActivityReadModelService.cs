using Harness.Modules.Projects.Application;
using Harness.Persistence.Abstractions.Delivery;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.Projects;

/// <summary>
/// CAT-07 — camada de leitura da "atividade recente" de um projeto. Não tem tabela própria: MATERIALIZA
/// eventos a partir dos stores duráveis já existentes (work board: demandas, tarefas, solicitações,
/// tentativas; e o histórico de previsões da Central de Entregas) e delega ordenação/paginação/
/// humanização ao projetor PURO <see cref="ProjectActivityProjector"/>. Cada evento mapeia 1:1 a uma
/// linha gravada — nada é inventado. É read-only e aditivo; não altera comportamento existente.
/// </summary>
public sealed class ProjectActivityReadModelService(
    IWorkBoardStore board,
    IDeliveryForecastStore forecasts)
{
    private const int PageSize = 200;
    private const int MaxPages = 25;

    private readonly IWorkBoardStore _board = board;
    private readonly IDeliveryForecastStore _forecasts = forecasts;

    /// <summary>
    /// Coleta a janela de atividade do projeto (tenant-scoped) a partir dos stores duráveis. O
    /// projetor cuida da ordem (mais recente primeiro) e da paginação; aqui só materializamos os
    /// eventos brutos, cada um preso a um instante realmente gravado.
    /// </summary>
    public async Task<IReadOnlyList<ProjectActivityEvent>> CollectAsync(
        string tenantId, string projectId, CancellationToken token)
    {
        var events = new List<ProjectActivityEvent>();

        await foreach (var demand in PageDemandsAsync(tenantId, projectId, token))
        {
            events.Add(new ProjectActivityEvent(
                demand.Id, "demand_created", demand.CreatedAt, "demand", demand.Id,
                Title: demand.Title, State: demand.State));
        }

        var tasks = new List<BoardTaskRecord>();
        await foreach (var task in PageTasksAsync(tenantId, projectId, token))
        {
            tasks.Add(task);
            events.Add(new ProjectActivityEvent(
                task.Id, "task_created", task.CreatedAt, "task", task.Id,
                Title: task.Title, State: task.State));

            // Uma tarefa cujo UpdatedAt difere do CreatedAt mudou de estado após criada: refletimos
            // essa transição no instante REAL do último update (sem inventar transições intermediárias).
            if (task.UpdatedAt > task.CreatedAt)
            {
                events.Add(new ProjectActivityEvent(
                    task.Id, "task_updated", task.UpdatedAt, "task", task.Id,
                    Title: task.Title, State: task.State, AgentId: task.AssigneeAgentId));
            }
        }

        await foreach (var solicitation in PageSolicitationsAsync(tenantId, projectId, token))
        {
            events.Add(new ProjectActivityEvent(
                solicitation.Id, "solicitation_created", solicitation.CreatedAt, "solicitation",
                solicitation.Id, Title: solicitation.Title, State: solicitation.State));
        }

        foreach (var task in tasks)
        {
            await foreach (var attempt in PageAttemptsAsync(tenantId, task.Id, token))
            {
                events.Add(new ProjectActivityEvent(
                    attempt.Id, "attempt_started", attempt.StartedAt, "attempt", attempt.Id,
                    Title: task.Title, State: attempt.State, AgentId: attempt.AgentId,
                    AttemptNumber: attempt.Number));

                if (attempt.FinishedAt is { } finishedAt)
                {
                    events.Add(new ProjectActivityEvent(
                        attempt.Id, "attempt_finished", finishedAt, "attempt", attempt.Id,
                        Title: task.Title, State: attempt.State, AgentId: attempt.AgentId,
                        AttemptNumber: attempt.Number));
                }
            }
        }

        foreach (var forecast in await _forecasts.ListByProjectAsync(tenantId, projectId, PageSize, token))
        {
            events.Add(new ProjectActivityEvent(
                forecast.Id, "forecast_recorded", forecast.CreatedAt, "forecast", forecast.Id,
                State: forecast.Confidence));
        }

        return events;
    }

    private async IAsyncEnumerable<BoardDemandRecord> PageDemandsAsync(
        string tenantId, string projectId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListDemandsAsync(tenantId, projectId, null, afterId, PageSize, token);
            foreach (var record in batch)
            {
                yield return record;
            }

            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }
    }

    private async IAsyncEnumerable<BoardTaskRecord> PageTasksAsync(
        string tenantId, string projectId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListTasksAsync(tenantId, projectId, null, afterId, PageSize, token);
            foreach (var record in batch)
            {
                yield return record;
            }

            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }
    }

    private async IAsyncEnumerable<BoardSolicitationRecord> PageSolicitationsAsync(
        string tenantId, string projectId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListSolicitationsAsync(tenantId, projectId, afterId, PageSize, token);
            foreach (var record in batch)
            {
                yield return record;
            }

            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }
    }

    private async IAsyncEnumerable<BoardAttemptRecord> PageAttemptsAsync(
        string tenantId, string taskId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _board.ListAttemptsAsync(tenantId, taskId, afterId, PageSize, token);
            foreach (var record in batch)
            {
                yield return record;
            }

            if (batch.Count < PageSize)
            {
                break;
            }

            afterId = batch[^1].Id;
        }
    }
}
