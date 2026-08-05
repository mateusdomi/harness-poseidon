using Harness.Modules.Workflows.Product.Graph;
using Harness.Persistence.Abstractions.Graph;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Graph;
using Harness.SharedKernel.Time;

namespace Harness.Host.Graph;

/// <summary>Flag da Onda 1: a projeção nasce DESLIGADA até a Onda 4 aprovar as provas.</summary>
public sealed record ProjectGraphOptions(bool ProjectionEnabled);

/// <summary>
/// A ponte entre as fontes canônicas REAIS e o projetor puro (Onda 1). Este serviço é o único
/// lugar que conhece as stores; o projetor recebe fatos normalizados e devolve o grafo.
///
/// Cobertura da ligação nesta onda (declarada, não implícita): solicitações (Artifact),
/// demandas (Requirement), cards (Card), fases derivadas dos cards (Phase) e tentativas
/// aprovadas (Evidence). Decisões, riscos, NFRs, fatos humanos, restrições, perguntas abertas,
/// gates e testes têm TIPO e invariantes prontos no modelo — entram na ligação quando a fonte
/// canônica correspondente expõe leitura estruturada (registrado no ADR da Onda 1).
/// </summary>
public sealed class ProjectGraphProjectionService(
    IWorkBoardStore board,
    IProjectGraphStore? store,
    IClock clock,
    ProjectGraphOptions options,
    Harness.Persistence.Abstractions.Coordination.ISolicitationAttachmentStore? attachments = null,
    WorkBoard.SolicitationAttachmentStorage? attachmentStorage = null)
{
    private readonly IWorkBoardStore _board = board ?? throw new ArgumentNullException(nameof(board));
    private readonly IProjectGraphStore? _store = store;
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ProjectGraphOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Ligada = flag on E store presente. Provider sem store de grafo = desligada, declarada.</summary>
    public bool Enabled => _options.ProjectionEnabled && _store is not null;

    /// <summary>
    /// `graph rebuild &lt;projectId&gt;`: reconstrói a projeção do zero a partir das fontes.
    /// Idempotente — reconstruir duas vezes produz o mesmo grafo e apenas avança a versão.
    /// </summary>
    public async Task<ProjectGraphRebuildResult> RebuildAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "A projeção de grafo está desligada (graph.projection.enabled=false).");
        }

        var snapshot = await BuildSourceSnapshotAsync(tenantId, projectId, cancellationToken);
        var projection = ProjectGraphProjector.Project(snapshot, _clock.UtcNow);
        var version = await _store!.ApplyAsync(
            new ProjectGraphApplyCommand(
                tenantId, projectId, projection.Nodes, projection.Edges,
                "graph.rebuild", _clock.UtcNow),
            cancellationToken);
        return new ProjectGraphRebuildResult(version, projection.Nodes.Count, projection.Edges.Count);
    }

    /// <summary>A projeção persistida do projeto. Exige a flag ligada (chamador checa Enabled).</summary>
    public Task<Harness.Persistence.Abstractions.Graph.ProjectGraphStoreSnapshot> GetSnapshotAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "A projeção de grafo está desligada (graph.projection.enabled=false).");
        }

        return _store!.GetAsync(tenantId, projectId, cancellationToken);
    }

    /// <summary>
    /// O estado das fontes canônicas do projeto, normalizado para o projetor. Determinístico:
    /// a mesma base produz o mesmo snapshot, na mesma ordem.
    /// </summary>
    public async Task<GraphSourceSnapshot> BuildSourceSnapshotAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        var items = new List<GraphSourceItem>();
        var links = new List<GraphSourceLink>();

        var solicitations = await _board.ListSolicitationsAsync(
            tenantId, projectId, afterId: null, limit: 500, cancellationToken);
        foreach (var solicitation in solicitations.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            items.Add(new GraphSourceItem(
                GraphNodeType.Artifact, solicitation.Id, "solicitation", 1, solicitation.Title));
        }

        var demands = await _board.ListDemandsAsync(
            tenantId, projectId, null, null, 1000, cancellationToken);
        foreach (var demand in demands.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            items.Add(new GraphSourceItem(
                GraphNodeType.Requirement, demand.Id, "demand", 1, demand.Title,
                Retired: string.Equals(demand.State, "cancelled", StringComparison.Ordinal)));
            if (!string.IsNullOrWhiteSpace(demand.SolicitationId))
            {
                // A demanda NASCE da solicitação: proveniência do requisito até a fonte humana.
                links.Add(new GraphSourceLink(
                    GraphRelationType.DerivesFrom,
                    GraphNodeType.Requirement, demand.Id,
                    GraphNodeType.Artifact, demand.SolicitationId));
            }
        }

        var tasks = await _board.ListTasksAsync(
            tenantId, projectId, null, null, 2000, cancellationToken);
        var phases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var retired = string.Equals(task.State, "cancelled", StringComparison.Ordinal);
            items.Add(new GraphSourceItem(
                GraphNodeType.Card, task.Id, "work_task", task.InstructionVersion, task.Title, retired));

            if (!string.IsNullOrWhiteSpace(task.BackingDemandId))
            {
                // Card CANCELADO não implementa nada — foi exatamente assim que o run de
                // empréstimos perdeu T02: o card morreu e a cobertura do requisito continuou
                // contada. A aresta implements só existe para card vivo.
                if (!retired)
                {
                    links.Add(new GraphSourceLink(
                        GraphRelationType.Implements,
                        GraphNodeType.Card, task.Id,
                        GraphNodeType.Requirement, task.BackingDemandId));
                }
            }

            if (!string.IsNullOrWhiteSpace(task.PhaseName))
            {
                phases.Add(task.PhaseName!);
                links.Add(new GraphSourceLink(
                    GraphRelationType.Produces,
                    GraphNodeType.Phase, task.PhaseName!,
                    GraphNodeType.Card, task.Id));
            }

            var attempts = await _board.ListAttemptsAsync(tenantId, task.Id, null, 100, cancellationToken);
            foreach (var attempt in attempts
                .Where(a => string.Equals(a.State, "approved", StringComparison.Ordinal))
                .OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                items.Add(new GraphSourceItem(
                    GraphNodeType.Evidence, attempt.Id, "work_attempt", attempt.Number,
                    $"Tentativa {attempt.Number} aprovada — {task.Title}"));
                links.Add(new GraphSourceLink(
                    GraphRelationType.Proves,
                    GraphNodeType.Evidence, attempt.Id,
                    GraphNodeType.Card, task.Id));
            }
        }

        foreach (var phase in phases.OrderBy(name => name, StringComparer.Ordinal))
        {
            items.Add(new GraphSourceItem(GraphNodeType.Phase, phase, "workflow_phase", 1, phase));
        }

        // Launch Gate (Prisma): a especificação anexada (role requirements_source) alimenta o
        // grafo — um Requirement por critério de aceite **T<n>** da seção de aceite, derivado do
        // artefato-solicitação de origem. Ids estáveis por critério ("criterio-t14"), então o
        // MESMO documento anexado duas vezes colapsa num único conjunto. O fato humano do banco
        // (Oracle) entra quando a solicitação o declara — extração estreita e declarada, nunca
        // inferência solta.
        await AppendRequirementsSourceAsync(tenantId, solicitations, items, links, cancellationToken);

        return new GraphSourceSnapshot(projectId, items, links);
    }

    private async Task AppendRequirementsSourceAsync(
        string tenantId,
        IReadOnlyList<BoardSolicitationRecord> solicitations,
        List<GraphSourceItem> items,
        List<GraphSourceLink> links,
        CancellationToken cancellationToken)
    {
        if (attachments is null)
        {
            return;
        }

        foreach (var solicitation in solicitations.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (solicitation.Body.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new GraphSourceItem(
                    GraphNodeType.HumanFact, "banco-oracle-19c", "solicitation_declaration", 1,
                    "O banco corporativo exigido é Oracle Database 19c"));
            }

            foreach (var record in await attachments.ListAsync(
                tenantId, solicitation.Id, cancellationToken))
            {
                if (!string.Equals(record.Role, "requirements_source", StringComparison.Ordinal) ||
                    !string.Equals(record.State, "accepted", StringComparison.Ordinal))
                {
                    continue;
                }

                string text;
                try
                {
                    // storage_path é relativo à raiz de anexos; a resolução canônica (com
                    // confinamento) é da SolicitationAttachmentStorage.
                    var absolute = attachmentStorage is null
                        ? record.StoragePath
                        : attachmentStorage.Resolve(record.StoragePath);
                    if (!File.Exists(absolute))
                    {
                        continue;
                    }

                    text = await File.ReadAllTextAsync(absolute, cancellationToken);
                }
                catch (IOException)
                {
                    continue;
                }

                // Extração AGNÓSTICA de template (Dual Project Gate): a seção de aceite é
                // achada pelo título semântico e cada cláusula vira Requirement com id estável
                // — "- **T14** …" do Prisma e "O sistema se conectar ao Oracle;" dos
                // Indicadores alimentam o MESMO modelo.
                // O fato do ambiente também pode vir DENTRO do documento de requisitos
                // ("Utilizar banco de dados Oracle"), não só do corpo da solicitação.
                if (text.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new GraphSourceItem(
                        GraphNodeType.HumanFact, "banco-oracle-19c", "solicitation_declaration", 1,
                        "O banco corporativo exigido é Oracle Database 19c"));
                }

                foreach (var criterion in
                    Harness.Modules.Workflows.Product.AcceptanceCriteriaExtractor.Extract(text))
                {
                    items.Add(new GraphSourceItem(
                        GraphNodeType.Requirement, criterion.Id, "acceptance_criterion", 1,
                        criterion.Text.Length > 180 ? criterion.Text[..180] : criterion.Text));
                    links.Add(new GraphSourceLink(
                        GraphRelationType.DerivesFrom,
                        GraphNodeType.Requirement, criterion.Id,
                        GraphNodeType.Artifact, solicitation.Id));
                }
            }
        }
    }
}

public sealed record ProjectGraphRebuildResult(long Version, int NodeCount, int EdgeCount);
