using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

/// <summary>Resultado de um ciclo do condutor de fase, para log e teste.</summary>
public sealed record WorkflowPhaseDriveResult(
    int CardsCreated,
    int ObjectivesAdvanced,
    string? GateAwaitingHuman);

/// <summary>
/// O elo que faltava entre o TRABALHO e a ESTEIRA.
///
/// O produto sempre teve as duas metades: a chefe cria cards, delega, revisa e mergeia; e o motor
/// de workflow tem fases, objetivos-documento e portões. Mas nada as ligava — os cards corriam por
/// fora, nenhum agente era encarregado do documento exigido pela fase e o motor nunca era chamado.
/// Na prática a esteira era decorativa: no histórico inteiro do sistema, nenhuma fase avançou,
/// nenhum objetivo saiu de `pending` e nenhum portão foi avaliado.
///
/// Este condutor fecha o ciclo, uma fase por vez:
/// 1. lê a fase ATIVA do run do projeto;
/// 2. para cada objetivo do tipo documento ainda pendente, garante que existe um CARD encarregado
///    de produzi-lo, marcado com o nome da fase (idempotente pelo título do card);
/// 3. quando o card do objetivo chega a `completed`, avança o objetivo no motor;
/// 4. com todos os objetivos-documento da fase satisfeitos, o portão vira decisão HUMANA — o
///    condutor NÃO aprova gate sozinho (Default-FAIL e HITL são regra), apenas reporta que a fase
///    está pronta para o dono decidir.
/// </summary>
public sealed class WorkflowPhaseDriver(
    IWorkflowCatalogStore catalog,
    IWorkflowStore authority,
    IWorkBoardStore board,
    IClock clock)
{
    /// <summary>Tipo de objetivo cujo entregável é um documento produzível por agente.</summary>
    private const string DocumentKind = "document";

    private readonly IWorkflowCatalogStore _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IWorkflowStore _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IWorkBoardStore _board = board ?? throw new ArgumentNullException(nameof(board));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Título ESTÁVEL do card que produz um objetivo de fase. É a chave de idempotência: o mesmo
    /// objetivo nunca gera um segundo card, em nenhum ciclo do loop.
    /// </summary>
    public static string CardTitleFor(string phaseName, string objectiveName) =>
        $"{phaseName} — {objectiveName}";

    public async Task<WorkflowPhaseDriveResult> DriveAsync(
        string tenantId,
        ProjectRecord project,
        string actorProfileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(project);

        var bindings = await _catalog.ListBindingsAsync(tenantId, project.Id, null, 1, cancellationToken);
        if (bindings.Count == 0)
        {
            return new WorkflowPhaseDriveResult(0, 0, null);
        }

        var runs = await _catalog.ListRunsAsync(tenantId, bindings[0].Id, null, 20, cancellationToken);
        var running = runs.FirstOrDefault(run => string.Equals(run.State, "running", StringComparison.Ordinal));
        if (running is null)
        {
            return new WorkflowPhaseDriveResult(0, 0, null);
        }

        var aggregate = await _authority.ReadRunAggregateAsync(tenantId, running.Id, cancellationToken);
        var phase = aggregate?.Phases.FirstOrDefault(candidate =>
            string.Equals(candidate.State, "active", StringComparison.Ordinal));
        if (aggregate is null || phase is null)
        {
            return new WorkflowPhaseDriveResult(0, 0, null);
        }

        // Board inteiro do projeto uma vez só: o casamento card↔objetivo é por título estável.
        var page = await _board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, null, null, null, "active", null, 0, 200),
            cancellationToken);
        var byTitle = new Dictionary<string, BoardTaskRecord>(StringComparer.Ordinal);
        foreach (var task in page.Items)
        {
            byTitle[task.Title] = task;
        }

        var documents = phase.Objectives
            .Where(objective => string.Equals(objective.Kind, DocumentKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var created = 0;
        var advanced = 0;
        var runVersion = aggregate.Version;

        foreach (var objective in documents)
        {
            var title = CardTitleFor(phase.Name, objective.Name);
            var isPending = !string.Equals(objective.State, "completed", StringComparison.OrdinalIgnoreCase);

            if (!byTitle.TryGetValue(title, out var card))
            {
                if (isPending)
                {
                    await CreateObjectiveCardAsync(
                        tenantId, project, actorProfileId, phase.Name, objective.Name, cancellationToken);
                    created++;
                }

                continue;
            }

            // O card existe: quando ele fecha, o objetivo da fase fecha junto. É esta linha que
            // transforma trabalho entregue em progresso REAL da esteira.
            if (isPending && string.Equals(card.InternalState, "completed", StringComparison.Ordinal))
            {
                var receipt = await _authority.AdvanceObjectiveAsync(
                    new WorkflowObjectiveAdvanceCommand(
                        tenantId, running.Id, phase.Key, objective.Key, "completed",
                        runVersion, $"phase-driver:{running.Id}:{objective.Key}", _clock.UtcNow),
                    cancellationToken);
                if (receipt.Status is WorkflowRunMutationStatus.Applied && receipt.RunVersion is { } next)
                {
                    advanced++;
                    runVersion = next;
                }
            }
        }

        // Portão: com todos os documentos entregues, a fase está pronta para a decisão do humano.
        // O condutor NUNCA avalia o gate por conta própria — Default-FAIL e HITL são regra, não
        // preferência, e um gate auto-aprovado destruiria o valor da esteira.
        var allDocumentsDone = documents.Length > 0 && documents.All(objective =>
            string.Equals(objective.State, "completed", StringComparison.OrdinalIgnoreCase) ||
            (byTitle.TryGetValue(CardTitleFor(phase.Name, objective.Name), out var card) &&
                string.Equals(card.InternalState, "completed", StringComparison.Ordinal)));
        var gateAwaiting = allDocumentsDone && phase.Gates.Count > 0 ? phase.Name : null;

        return new WorkflowPhaseDriveResult(created, advanced, gateAwaiting);
    }

    private async Task CreateObjectiveCardAsync(
        string tenantId,
        ProjectRecord project,
        string actorProfileId,
        string phaseName,
        string objectiveName,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var taskId = UlidValue.New(now).ToString();
        var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();

        // O documento de fase vive sob `docs/**`, que pertence ao escopo de backend — por isso o
        // papel exigido é o de backend, e não um papel sem escopo de escrita (que tornaria o card
        // indespachável).
        var instruction =
            $"Papel exigido: backend-specialist\nTipo de card: agent_task\n\n" +
            $"Produzir o artefato **{objectiveName}** exigido pela fase \"{phaseName}\" da esteira do " +
            $"projeto {project.Name}.\n\n" +
            "O documento é o entregável: escreva-o em `docs/` no repositório do projeto, em português, " +
            "com o conteúdo que a fase exige — e não um esqueleto vazio. Baseie-se no que já existe no " +
            "repositório e na demanda do projeto; onde faltar informação, declare a lacuna " +
            "explicitamente em vez de inventar.\n\n" +
            $"Em escopo: o arquivo do artefato e as referências que ele precisa citar.\n" +
            "Fora de escopo: código de produção, mudança de comportamento do sistema.\n\n" +
            "Critérios de aceite:\n" +
            $"- O arquivo do artefato \"{objectiveName}\" existe em `docs/` e está versionado.\n" +
            "- O conteúdo cobre o objetivo da fase e cita as fontes reais que usou.\n";

        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId,
                taskId,
                project.Id,
                null,
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                actorProfileId,
                CardTitleFor(phaseName, objectiveName),
                "medium",
                null,
                null,
                instructionId,
                instruction,
                now,
                phaseName),
            cancellationToken);
    }
}
