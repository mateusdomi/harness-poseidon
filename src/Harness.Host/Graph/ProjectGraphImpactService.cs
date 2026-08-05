using Harness.Modules.Workflows.Product.Graph;
using Harness.Persistence.Abstractions.Graph;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Graph;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Graph;

/// <summary>
/// A propagação de impacto e a semântica de STALE sobre a projeção persistida (Onda 2).
///
/// O contrato inteiro em três frases: mudança upstream NUNCA apaga nem reverte trabalho — marca
/// STALE com a causa (nó + versão). STALE gera trabalho de revalidação VISÍVEL (um card
/// idempotente por causa+versão), nunca ação destrutiva. Alcançado só por aresta Proposed é
/// informativo e não vira STALE — falso positivo é defeito da mesma severidade que falso
/// negativo.
/// </summary>
public sealed class ProjectGraphImpactService(
    ProjectGraphProjectionService projection,
    IProjectGraphStore? store,
    IWorkBoardStore board,
    IClock clock,
    ILogger<ProjectGraphImpactService> logger)
{
    private readonly ProjectGraphProjectionService _projection =
        projection ?? throw new ArgumentNullException(nameof(projection));
    private readonly IProjectGraphStore? _store = store;
    private readonly IWorkBoardStore _board = board ?? throw new ArgumentNullException(nameof(board));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<ProjectGraphImpactService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Relações de PREDECESSOR para readiness (Onda 2.3): quem alimenta o card.</summary>
    private static readonly HashSet<GraphRelationType> UpstreamRelations =
    [
        GraphRelationType.DependsOn,
        GraphRelationType.BlockedBy,
        GraphRelationType.DerivesFrom,
    ];

    public bool Enabled => _projection.Enabled && _store is not null;

    /// <summary>
    /// Gatilho de propagação: a fonte canônica do nó <paramref name="causeNodeId"/> mudou para
    /// <paramref name="causeVersion"/>. Marca Direct+Transitive como STALE (com causa) e garante
    /// o card de revalidação — idempotente por causa+versão.
    /// </summary>
    public async Task<ProjectGraphPropagationResult> PropagateAsync(
        string tenantId,
        string projectId,
        string causeNodeId,
        int causeVersion,
        string actorProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return new ProjectGraphPropagationResult(0, 0, null);
        }

        var snapshot = await _store!.GetAsync(tenantId, projectId, cancellationToken);
        var impacted = ImpactAnalysisService.Analyze(snapshot.Nodes, snapshot.Edges, causeNodeId);
        var blocking = impacted
            .Where(node => node.Classification != ImpactClassification.Possible)
            .ToArray();
        var possible = impacted.Count(node => node.Classification == ImpactClassification.Possible);

        if (blocking.Length == 0)
        {
            // Onda 2.4 — controle de falso positivo: mudança sem alcance estrutural não marca
            // nada e não cria trabalho nenhum.
            return new ProjectGraphPropagationResult(0, possible, null);
        }

        var now = _clock.UtcNow;
        await _store.MarkStaleAsync(
            new ProjectGraphStaleCommand(
                tenantId,
                projectId,
                [.. blocking.Select(node => new GraphStaleMark(node.NodeId, causeNodeId, causeVersion))],
                now),
            cancellationToken);

        var cause = snapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.Id, causeNodeId, StringComparison.Ordinal));
        var work = cause is null
            ? null
            : GraphRevalidationWorks.From(
                cause with { Version = causeVersion },
                impacted,
                snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal));

        string? taskId = null;
        if (work is not null)
        {
            taskId = await EnsureRevalidationCardAsync(
                tenantId, projectId, work, actorProfileId, cancellationToken);
        }

        LogPropagated(_logger, causeNodeId, blocking.Length, possible, null);
        return new ProjectGraphPropagationResult(blocking.Length, possible, taskId);
    }

    /// <summary>
    /// Os fatos de grafo do readiness (Onda 2.3): predecessores STALE e predecessores
    /// incompletos do card, pela travessia transitiva de depends_on/blocked_by/derives_from
    /// sobre arestas ACCEPTED. Com a flag desligada devolve fatos vazios — o avaliador se
    /// comporta exatamente como antes.
    /// </summary>
    public async Task<(IReadOnlyList<string> Stale, IReadOnlyList<string> Incomplete)>
        InspectUpstreamAsync(
            string tenantId,
            string projectId,
            string cardTaskId,
            CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return ([], []);
        }

        var snapshot = await _store!.GetAsync(tenantId, projectId, cancellationToken);
        var cardNodeId = GraphNode.DeterministicId(GraphNodeType.Card, cardTaskId);
        var nodesById = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!nodesById.ContainsKey(cardNodeId))
        {
            return ([], []);
        }

        // Travessia transitiva de predecessores: só arestas Accepted (Proposed NUNCA bloqueia),
        // só relações de insumo, sempre determinística.
        var upstream = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { cardNodeId };
        var queue = new Queue<string>();
        queue.Enqueue(cardNodeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in snapshot.Edges
                .Where(edge => edge.ValidUntil is null &&
                    edge.Status == GraphEdgeStatus.Accepted &&
                    UpstreamRelations.Contains(edge.RelationType) &&
                    string.Equals(edge.FromNodeId, current, StringComparison.Ordinal))
                .OrderBy(edge => edge.ToNodeId, StringComparer.Ordinal))
            {
                if (seen.Add(edge.ToNodeId))
                {
                    upstream.Add(edge.ToNodeId);
                    queue.Enqueue(edge.ToNodeId);
                }
            }
        }

        var stale = upstream
            .Where(id => nodesById[id].State == GraphNodeState.Stale)
            .ToArray();
        var incomplete = new List<string>();
        foreach (var id in upstream)
        {
            var node = nodesById[id];
            if (node.Type != GraphNodeType.Card || node.State == GraphNodeState.Retired)
            {
                continue;
            }

            var task = await _board.GetTaskAsync(tenantId, node.CanonicalSourceId, cancellationToken);
            if (task is not null && !string.Equals(task.State, "done", StringComparison.Ordinal))
            {
                incomplete.Add(id);
            }
        }

        return (stale, incomplete);
    }

    /// <summary>
    /// Revalidação aprovada limpa o STALE registrando de onde veio a confirmação.
    /// </summary>
    public async Task ClearStaleAsync(
        string tenantId, string projectId, string nodeId, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        await _store!.ClearStaleAsync(tenantId, projectId, nodeId, _clock.UtcNow, cancellationToken);
    }

    private async Task<string?> EnsureRevalidationCardAsync(
        string tenantId,
        string projectId,
        GraphRevalidationWork work,
        string actorProfileId,
        CancellationToken cancellationToken)
    {
        var tasks = await _board.ListTasksAsync(tenantId, projectId, null, null, 2000, cancellationToken);
        var prefix = $"{GraphRevalidationWorks.TitlePrefix} {work.Fingerprint}";
        var existing = tasks.FirstOrDefault(task =>
            task.Title.StartsWith(prefix, StringComparison.Ordinal) &&
            task.ArchivedAt is null &&
            !string.Equals(task.State, "cancelled", StringComparison.Ordinal) &&
            !string.Equals(task.State, "done", StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = _clock.UtcNow;
        var taskId = UlidValue.New(now).ToString();
        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId, taskId, projectId, null,
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                actorProfileId, work.Title,
                "high", null, null,
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                work.Instruction, now, null, "agent_task"),
            cancellationToken);
        _ = await _board.MoveTaskAsync(
            new BoardTaskMoveCommand(
                tenantId, taskId, "ready", $"graph-revalidation:{work.Fingerprint}", "agent",
                now.AddMilliseconds(4)),
            cancellationToken);
        return taskId;
    }

    private static readonly Action<ILogger, string, int, int, Exception?> LogPropagated =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(1, nameof(LogPropagated)),
            "Onda 2: mudança em {CauseNode} marcou {Stale} nó(s) STALE " +
            "({Possible} alcançado(s) só por aresta Proposed — informativos, sem STALE).");
}

public sealed record ProjectGraphPropagationResult(
    int StaleCount, int PossibleCount, string? RevalidationTaskId);
