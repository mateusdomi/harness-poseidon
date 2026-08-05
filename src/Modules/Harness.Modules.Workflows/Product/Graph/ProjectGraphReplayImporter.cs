using System.Globalization;
using System.Text.Json;
using Harness.SharedKernel.Graph;

namespace Harness.Modules.Workflows.Product.Graph;

/// <summary>
/// O importador de replay da Onda 4.1 — ferramenta de PLATAFORMA, não script descartável: lê o
/// formato dos arquivos de `Arquivos-Historicos-Projetos/` (o JSON de arquivamento de projeto do
/// próprio Poseidon) e reconstrói o <see cref="GraphSourceSnapshot"/> num instante escolhido
/// (<c>asOf</c>), permitindo reproduzir a linha do tempo do run e provar o que o grafo TERIA
/// dito em cada momento.
///
/// Regras de derivação (determinísticas, as mesmas para qualquer projeto arquivado):
/// 1. Solicitação com título iniciando em "ADR" é Decisão; as demais são Artefato.
///    `supersedes_id` vira aresta <c>supersedes</c>.
/// 2. Demanda é Requisito e deriva da sua solicitação.
/// 3. Card é Card (cancelado = aposentado); implementa a demanda SOMENTE enquanto vivo; a fase
///    produz o card.
/// 4. Card é <c>constrained_by</c> cada Decisão criada ANTES dele — decisões restringem o
///    trabalho posterior. É esta aresta que faz o flip-flop de escopo alcançar o plano de
///    testes.
/// 5. Card de code review (<c>card_type</c> revisão ou título contendo "Code review")
///    <c>depends_on</c> cada card executável (`agent_task`) da MESMA fase — um review sem
///    objeto pronto não tem o que revisar.
/// 6. Tentativa aprovada até o instante é Evidência que prova o card.
/// </summary>
public static class ProjectGraphReplayImporter
{
    public static GraphSourceSnapshot Import(string archiveJson, DateTimeOffset? asOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveJson);
        using var document = JsonDocument.Parse(archiveJson);
        var root = document.RootElement;
        var cutoff = asOf ?? DateTimeOffset.MaxValue;
        var projectId = root.GetProperty("projeto").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("O arquivo não declara o id do projeto.");

        var items = new List<GraphSourceItem>();
        var links = new List<GraphSourceLink>();

        var decisions = new List<(string Id, DateTimeOffset CreatedAt)>();
        foreach (var solicitation in Rows(root, "solicitacoes"))
        {
            var createdAt = At(solicitation, "created_at");
            if (createdAt > cutoff)
            {
                continue;
            }

            var id = Text(solicitation, "id");
            var title = Text(solicitation, "title");
            var isDecision = title.StartsWith("ADR", StringComparison.OrdinalIgnoreCase);
            var type = isDecision ? GraphNodeType.Decision : GraphNodeType.Artifact;
            items.Add(new GraphSourceItem(type, id, "solicitation", 1, title));
            if (isDecision)
            {
                decisions.Add((id, createdAt));
            }

            if (solicitation.TryGetProperty("supersedes_id", out var supersedes) &&
                supersedes.ValueKind == JsonValueKind.String &&
                supersedes.GetString() is { Length: > 0 } supersededId)
            {
                links.Add(new GraphSourceLink(
                    GraphRelationType.Supersedes, type, id, type, supersededId));
            }
        }

        foreach (var demand in Rows(root, "demandas"))
        {
            if (At(demand, "created_at") > cutoff)
            {
                continue;
            }

            var id = Text(demand, "id");
            items.Add(new GraphSourceItem(
                GraphNodeType.Requirement, id, "demand", 1, Text(demand, "title"),
                Retired: Text(demand, "state") == "cancelled"));
            if (demand.TryGetProperty("solicitation_id", out var solicitationId) &&
                solicitationId.ValueKind == JsonValueKind.String &&
                solicitationId.GetString() is { Length: > 0 } source)
            {
                links.Add(new GraphSourceLink(
                    GraphRelationType.DerivesFrom,
                    GraphNodeType.Requirement, id, GraphNodeType.Artifact, source));
            }
        }

        var phases = new HashSet<string>(StringComparer.Ordinal);
        var cardsByPhase = new Dictionary<string, List<(string Id, string CardType)>>(StringComparer.Ordinal);
        var reviewCards = new List<(string Id, string Phase)>();
        foreach (var card in Rows(root, "cards"))
        {
            var createdAt = At(card, "created_at");
            if (createdAt > cutoff)
            {
                continue;
            }

            var id = Text(card, "id");
            var title = Text(card, "title");
            var state = Text(card, "state");
            var retired = state == "cancelled";
            items.Add(new GraphSourceItem(
                GraphNodeType.Card, id, "work_task", (int)Number(card, "version"), title, retired));

            if (!retired &&
                card.TryGetProperty("demand_id", out var demandId) &&
                demandId.ValueKind == JsonValueKind.String &&
                demandId.GetString() is { Length: > 0 } demand)
            {
                links.Add(new GraphSourceLink(
                    GraphRelationType.Implements,
                    GraphNodeType.Card, id, GraphNodeType.Requirement, demand));
            }

            var phase = card.TryGetProperty("phase_name", out var phaseName) &&
                phaseName.ValueKind == JsonValueKind.String
                ? phaseName.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(phase))
            {
                phases.Add(phase);
                links.Add(new GraphSourceLink(
                    GraphRelationType.Produces,
                    GraphNodeType.Phase, phase, GraphNodeType.Card, id));
                var cardType = Text(card, "card_type");
                if (!cardsByPhase.TryGetValue(phase, out var list))
                {
                    cardsByPhase[phase] = list = [];
                }

                list.Add((id, cardType));
                if (cardType is "revisao" ||
                    title.Contains("Code review", StringComparison.OrdinalIgnoreCase))
                {
                    reviewCards.Add((id, phase));
                }
            }

            // Regra 4: decisões restringem o trabalho posterior a elas.
            foreach (var (decisionId, decisionAt) in decisions)
            {
                if (decisionAt <= createdAt)
                {
                    links.Add(new GraphSourceLink(
                        GraphRelationType.ConstrainedBy,
                        GraphNodeType.Card, id, GraphNodeType.Decision, decisionId));
                }
            }
        }

        // Regra 5: o review depende dos cards executáveis da mesma fase.
        foreach (var (reviewId, phase) in reviewCards)
        {
            foreach (var (cardId, cardType) in cardsByPhase.GetValueOrDefault(phase) ?? [])
            {
                if (cardType == "agent_task" && !string.Equals(cardId, reviewId, StringComparison.Ordinal))
                {
                    links.Add(new GraphSourceLink(
                        GraphRelationType.DependsOn,
                        GraphNodeType.Card, reviewId, GraphNodeType.Card, cardId));
                }
            }
        }

        foreach (var phase in phases.OrderBy(name => name, StringComparer.Ordinal))
        {
            items.Add(new GraphSourceItem(GraphNodeType.Phase, phase, "workflow_phase", 1, phase));
        }

        foreach (var attempt in Rows(root, "tentativas"))
        {
            if (Text(attempt, "state") != "approved" || At(attempt, "started_at") > cutoff)
            {
                continue;
            }

            var id = Text(attempt, "id");
            var taskId = Text(attempt, "task_id");
            items.Add(new GraphSourceItem(
                GraphNodeType.Evidence, id, "work_attempt",
                (int)Number(attempt, "attempt_number"), $"Tentativa aprovada {id}"));
            links.Add(new GraphSourceLink(
                GraphRelationType.Proves, GraphNodeType.Evidence, id, GraphNodeType.Card, taskId));
        }

        return new GraphSourceSnapshot(projectId, items, links);
    }

    /// <summary>
    /// O estado ("done"/não) de cada card NO INSTANTE — aproximado pelo par (state, updated_at):
    /// um card só conta como concluído se o arquivamento o registra concluído E a última
    /// atualização é anterior ao corte. Aproximação declarada: o arquivo não versiona estados.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> CardCompletionAsOf(
        string archiveJson, DateTimeOffset asOf)
    {
        using var document = JsonDocument.Parse(archiveJson);
        var completion = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var card in Rows(document.RootElement, "cards"))
        {
            if (At(card, "created_at") > asOf)
            {
                continue;
            }

            completion[Text(card, "id")] =
                Text(card, "state") == "completed" && At(card, "updated_at") <= asOf;
        }

        return completion;
    }

    private static JsonElement.ArrayEnumerator Rows(JsonElement root, string property) =>
        root.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.Array
            ? node.EnumerateArray()
            : default;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 1;

    private static DateTimeOffset At(JsonElement element, string property) =>
        DateTimeOffset.TryParse(
            Text(element, property), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
