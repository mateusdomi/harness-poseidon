using System.Globalization;
using System.Text;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Security;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

/// <summary>Resultado de um ciclo do condutor de fase, para log e teste.</summary>
public sealed record WorkflowPhaseDriveResult(
    int CardsCreated,
    int ObjectivesAdvanced,
    string? GateAwaitingHuman,
    IReadOnlyList<string> Failures,

    /// <summary>Progresso REAL da fase corrente, por obrigações cumpridas.</summary>
    PhaseProgressSnapshot? Progress = null,

    /// <summary>Fase cujo portão a chefe aprovou por evidência neste ciclo (modo autônomo).</summary>
    string? GateApprovedByChief = null,

    /// <summary>Obrigações materializadas nesta rodada (plano novo ou rebaseline).</summary>
    int ObligationsPlanned = 0);

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
/// 4. materializa o PLANO DE OBRIGAÇÕES da fase (documentos + cards de trabalho) e mede o
///    progresso pelo que foi realmente aceito;
/// 5. decide o portão pelo MODO do projeto: autônomo, a própria chefe aprova por evidência;
///    semiautônomo, só as fases que o dono marcou esperam por ele; manual, toda transição espera.
///
/// A regra que este condutor passou a separar: <b>Default-FAIL não é aprovação humana
/// obrigatória</b>. Sem evidência, o portão reprova nos três modos. Com evidência, quem decide é o
/// modo configurado — e não o produto impondo o dono como gargalo de cada fase de cada projeto.
/// </summary>
public sealed class WorkflowPhaseDriver(
    IWorkflowCatalogStore catalog,
    IWorkflowStore authority,
    IWorkBoardStore board,
    IPhaseObligationStore obligations,
    IWorkflowDocumentTemplateStore documentTemplates,
    IConversationStore conversations,
    IClock clock)
{
    /// <summary>Tipo de objetivo cujo entregável é um documento produzível por agente.</summary>
    private const string DocumentKind = "document";

    /// <summary>
    /// Degraus de um objetivo, em ordem. O motor exige avanço de UM degrau por vez; pular direto
    /// para o fim é recusado como transição inválida.
    /// </summary>
    private static readonly string[] ObjectiveLadder = ["pending", "executed", "validated", "approved"];

    /// <summary>
    /// Degraus que o condutor precisa subir, um a um, até o estado exigido. O limite é explícito:
    /// documentos entregues chegam a `validated`; em modo autônomo a Bruna também pode levar um
    /// objetivo requerido a `approved`, mas somente depois de todas as evidências e gates de
    /// política terem autorizado a decisão.
    /// </summary>
    public static IEnumerable<string> ObjectiveStepsThrough(string currentState, string targetState)
    {
        var index = Array.FindIndex(
            ObjectiveLadder,
            step => string.Equals(step, currentState, StringComparison.OrdinalIgnoreCase));
        var target = Array.FindIndex(
            ObjectiveLadder,
            step => string.Equals(step, targetState, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || target < 0 || index >= target)
        {
            yield break;
        }

        for (var next = index + 1; next <= target; next++)
        {
            yield return ObjectiveLadder[next];
        }
    }

    private readonly List<string> _failures = [];

    private readonly IWorkflowCatalogStore _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IWorkflowStore _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IWorkBoardStore _board = board ?? throw new ArgumentNullException(nameof(board));
    private readonly IPhaseObligationStore _obligations =
        obligations ?? throw new ArgumentNullException(nameof(obligations));
    private readonly IWorkflowDocumentTemplateStore _documentTemplates =
        documentTemplates ?? throw new ArgumentNullException(nameof(documentTemplates));
    private readonly IConversationStore _conversations =
        conversations ?? throw new ArgumentNullException(nameof(conversations));
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
        _failures.Clear();

        var bindings = await _catalog.ListBindingsAsync(tenantId, project.Id, null, 1, cancellationToken);
        if (bindings.Count == 0)
        {
            return new WorkflowPhaseDriveResult(0, 0, null, []);
        }

        var runs = await _catalog.ListRunsAsync(tenantId, bindings[0].Id, null, 20, cancellationToken);
        var running = runs.FirstOrDefault(run => string.Equals(run.State, "running", StringComparison.Ordinal));
        if (running is null)
        {
            return new WorkflowPhaseDriveResult(0, 0, null, []);
        }

        var aggregate = await _authority.ReadRunAggregateAsync(tenantId, running.Id, cancellationToken);
        var phase = aggregate?.Phases.FirstOrDefault(candidate =>
            string.Equals(candidate.State, "active", StringComparison.Ordinal));
        if (aggregate is null || phase is null)
        {
            return new WorkflowPhaseDriveResult(0, 0, null, []);
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
        var solicitations = await _board.ListSolicitationsAsync(
            tenantId, project.Id, null, 500, cancellationToken);
        var demands = await _board.ListDemandsAsync(
            tenantId, project.Id, null, null, 500, cancellationToken);
        var humanMessages = await ListHumanMessagesAsync(
            tenantId, project.Id, cancellationToken);
        var templates = await _documentTemplates.ListAsync(cancellationToken);

        var created = 0;
        var advanced = 0;
        var runVersion = aggregate.Version;

        foreach (var objective in documents)
        {
            var title = CardTitleFor(phase.Name, objective.Name);
            // Objetivo ainda no primeiro degrau: é ele que precisa de um card para produzir o artefato.
            var isPending = string.Equals(objective.State, "pending", StringComparison.OrdinalIgnoreCase);

            if (!byTitle.TryGetValue(title, out var card))
            {
                // Cadastrar um projeto não equivale a autorizar trabalho invisível. A esteira só
                // materializa o primeiro artefato depois de existir um pedido humano autenticado
                // na conversa (qualquer canal) ou uma solicitação explícita no quadro. Assim o
                // profissional nunca recebe um documento pré-fabricado antes de a Bruna ouvir o
                // usuário, e o card já nasce com a fonte primária no pacote executável.
                var hasHumanKickoff = humanMessages.Count > 0 ||
                    solicitations.Any(item => !item.Internal);
                if (isPending && hasHumanKickoff)
                {
                    await CreateObjectiveCardAsync(
                        tenantId, project, actorProfileId, phase.Name, objective.Name,
                        humanMessages, solicitations, demands, templates, cancellationToken);
                    created++;
                }

                continue;
            }

            // O card existe: quando ele FECHA, o objetivo da fase caminha junto. É esta linha que
            // transforma trabalho entregue em progresso REAL da esteira.
            //
            // O objetivo anda um degrau por vez (`pending → executed → validated → approved`) e a
            // semântica de cada degrau já existe no ciclo do card:
            //   `executed`  — o trabalho foi feito (o card produziu o artefato);
            //   `validated` — passou pela revisão independente (chegar a merged exige crítico
            //                 distinto aprovando);
            //   `approved`  — decisão HUMANA. O condutor PARA aqui, de propósito: aprovar sozinho
            //                 o último degrau esvaziaria o sentido da esteira.
            if (isPending && string.Equals(card.InternalState, "completed", StringComparison.Ordinal))
            {
                foreach (var step in ObjectiveStepsThrough(objective.State, "validated"))
                {
                    var occurredAt = _clock.UtcNow;
                    var receipt = await _authority.AdvanceObjectiveAsync(
                        new WorkflowObjectiveAdvanceCommand(
                            tenantId, running.Id, phase.Key, objective.Key, step,
                            runVersion, MutationKey("objective", running.Id, objective.Key, step, occurredAt),
                            occurredAt),
                        cancellationToken);
                    if (receipt.Status is not (WorkflowRunMutationStatus.Applied
                        or WorkflowRunMutationStatus.IdempotentReplay))
                    {
                        // Falha de avanço NUNCA pode passar em silêncio: foi exatamente assim que
                        // um estado-alvo inválido ficou escondido e a esteira parou sem sinal.
                        _failures.Add($"{objective.Key}->{step}:{receipt.Status}");
                        break;
                    }

                    if (receipt.RunVersion is { } next)
                    {
                        runVersion = next;
                    }

                    advanced++;
                }
            }

            // Informação humana nova depois da criação do último card de um documento exige uma
            // nova versão executável. Preservá-la apenas em conversa/demanda deixava o artefato
            // canônico congelado e quebrava a rastreabilidade. A atualização espera a execução
            // anterior estabilizar para evitar duas pessoas editando o mesmo documento em
            // paralelo, e o id da mensagem torna o card idempotente entre ciclos do driver.
            var revisionPrefix = title + " — atualização ";
            var objectiveCards = page.Items
                .Where(item => string.Equals(item.Title, title, StringComparison.Ordinal) ||
                               item.Title.StartsWith(revisionPrefix, StringComparison.Ordinal))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var latestHuman = humanMessages.Count == 0 ? null : humanMessages[^1];
            var latestObjectiveCard = objectiveCards.FirstOrDefault();
            if (latestHuman is not null && latestObjectiveCard is not null &&
                NeedsDocumentRevision(latestHuman.CreatedAt, latestObjectiveCard) &&
                !byTitle.ContainsKey(revisionPrefix + latestHuman.Id))
            {
                await CreateObjectiveCardAsync(
                    tenantId, project, actorProfileId, phase.Name, objective.Name,
                    humanMessages, solicitations, demands, templates, cancellationToken,
                    revisionPrefix + latestHuman.Id, latestHuman.Id);
                created++;
            }
        }

        // ---- PLANO DE OBRIGAÇÕES: o denominador do progresso ----
        //
        // Os cards da fase entram no plano junto com os documentos. Enquanto o progresso vinha só
        // dos documentos, uma fase de Desenvolvimento fechava com briefing, code review e métricas
        // produzidos e ZERO implementação — o indicador media a papelada, não a entrega.
        var phaseCards = page.Items
            .Where(task => string.Equals(task.PhaseName, phase.Name, StringComparison.Ordinal))
            .Where(task => !task.Title.StartsWith($"{phase.Name} —", StringComparison.Ordinal))
            .ToArray();
        var planned = await EnsurePhasePlanAsync(
            tenantId, project.Id, running.Id, phase, documents, phaseCards, cancellationToken);

        var current = await _obligations.ListCurrentAsync(
            tenantId, running.Id, phase.Key, cancellationToken);
        current = await ReconcileObligationStatesAsync(
            tenantId, current, byTitle, page.Items, phase.Name, cancellationToken);
        var progress = PhaseProgressEvaluator.Evaluate([.. current.Select(ToDomain)]);

        // CONSISTÊNCIA: uma obrigação obrigatória sem NENHUM produtor (nem card, nem objetivo) é
        // trabalho que ninguém vai fazer. Antes isso não tinha sintoma: o portão simplesmente
        // esperava para sempre, e de fora parecia backlog normal. Vira achado impeditivo — o
        // portão não fica pronto e o motivo aparece no resultado do ciclo.
        if (PhaseObligationPlanner.HasArtifactWithoutProducer(current))
        {
            _failures.Add($"phase:{phase.Key}:obligation_without_producer");
        }

        // ---- PORTÃO: quem decide depende do MODO configurado pelo dono ----
        var mode = PhaseGatePolicy.ParseMode(bindings[0].OperationMode);
        var gate = phase.Gates.Count > 0 ? phase.Gates[0] : null;
        var blocked = page.Items.Any(task =>
            string.Equals(task.PhaseName, phase.Name, StringComparison.Ordinal) &&
            string.Equals(task.InternalState, "blocked", StringComparison.Ordinal));
        var evidence = new PhaseGateEvidence(
            HasGate: gate is not null,
            AllRequiredObligationsAccepted: progress.TechnicallyComplete,
            HasBlockingFinding: _failures.Count > 0,
            HasOpenBlocker: blocked,
            RequiredObligationCount: progress.RequiredTotal);

        var decision = PhaseGatePolicy.Decide(
            mode, phase.Name, gate?.Name, bindings[0].SemiautonomousPauseGates, evidence);

        // B13 — CONSELHO DE AGENTES na saída do Planejamento.
        //
        // É o último ponto em que corrigir ainda é barato: dali em diante, cada decisão errada
        // custa código escrito, revisado e refeito. O gatilho é de POLÍTICA e não de julgamento da
        // chefe — um conselho que dependesse de ela lembrar de convocá-lo aconteceria nas fases em
        // que menos importa e faltaria justamente onde o erro é caro.
        //
        // O conselho NÃO decide: ele critica, e um único achado impeditivo segura a transição
        // ainda que os demais aprovem. Maioria decide preferência; evidência decide risco.
        CouncilVerdict? councilVerdict = null;
        if (decision != PhaseGateDecision.NotReady &&
            AgentCouncilPolicy.ShouldConvene(phase.Name, progress.RequiredTotal))
        {
            var council = await ConveneCouncilAsync(
                tenantId, project, phase.Name, actorProfileId, _clock.UtcNow, cancellationToken);
            councilVerdict = council;
            if (!council.MayProceed)
            {
                _failures.Add($"phase:{phase.Key}:council:{council.ReasonCode}");
                foreach (var dissent in council.Dissent)
                {
                    _failures.Add($"phase:{phase.Key}:council_dissent:{dissent}");
                }

                decision = PhaseGateDecision.NotReady;
            }
        }

        // O plano de obrigações mede o trabalho real, mas o motor de workflow também mantém
        // objetivos de tarefa/evidência exigidos pelo gate. Eles não têm card próprio (o produtor
        // é o conjunto de cards da fase), portanto deixá-los em `pending` fazia o progresso chegar
        // a 100% enquanto EvaluateGate recusava `GateRequirementsNotMet` para sempre.
        if (decision == PhaseGateDecision.ChiefApproves && gate is not null)
        {
            foreach (var requiredKey in gate.RequiredObjectiveKeys)
            {
                var required = phase.Objectives.FirstOrDefault(objective =>
                    string.Equals(objective.Key, requiredKey, StringComparison.Ordinal));
                if (required is null)
                {
                    _failures.Add($"gate:{gate.Key}:objective_missing:{requiredKey}");
                    decision = PhaseGateDecision.NotReady;
                    break;
                }

                foreach (var step in ObjectiveStepsThrough(required.State, gate.MinimumRequiredState))
                {
                    var occurredAt = _clock.UtcNow;
                    var receipt = await _authority.AdvanceObjectiveAsync(
                        new WorkflowObjectiveAdvanceCommand(
                            tenantId, running.Id, phase.Key, required.Key, step, runVersion,
                            MutationKey("gate-objective", running.Id, required.Key, step, occurredAt),
                            occurredAt),
                        cancellationToken);
                    if (receipt.Status is not (WorkflowRunMutationStatus.Applied
                        or WorkflowRunMutationStatus.IdempotentReplay))
                    {
                        _failures.Add($"{required.Key}->{step}:{receipt.Status}");
                        decision = PhaseGateDecision.NotReady;
                        break;
                    }

                    runVersion = receipt.RunVersion ?? runVersion;
                    advanced++;
                }

                if (decision == PhaseGateDecision.NotReady)
                {
                    break;
                }
            }
        }

        string? gateAwaiting = null;
        string? gateApproved = null;
        if (decision == PhaseGateDecision.AwaitHuman)
        {
            gateAwaiting = phase.Name;
        }
        else if (decision == PhaseGateDecision.ChiefApproves && gate is not null)
        {
            gateApproved = await TryApproveGateAsync(
                tenantId, running.Id, phase, gate, runVersion, actorProfileId, councilVerdict,
                cancellationToken);
        }

        return new WorkflowPhaseDriveResult(
            created, advanced, gateAwaiting, [.. _failures], progress, gateApproved, planned);
    }

    /// <summary>
    /// Materializa (ou reconcilia) o plano da fase. Um plano já existente NÃO é reescrito: quando
    /// aparece obrigação nova e legítima — um card que o dono pediu depois —, o plano ganha uma
    /// VERSÃO nova com todas as obrigações vigentes, e a anterior fica preservada. Mudar o
    /// denominador em silêncio é a forma mais fácil de um indicador mentir.
    /// </summary>
    private async Task<int> EnsurePhasePlanAsync(
        string tenantId,
        string projectId,
        string runId,
        WorkflowPhaseRunSnapshot phase,
        IReadOnlyList<WorkflowObjectiveRunSnapshot> documents,
        IReadOnlyList<BoardTaskRecord> phaseCards,
        CancellationToken cancellationToken)
    {
        var desired = PhaseObligationPlanner.Plan(
            [.. documents.Select(objective => (objective.Key, objective.Name))], phaseCards);
        if (desired.Count == 0)
        {
            return 0;
        }

        var version = await _obligations.CurrentPlanVersionAsync(
            tenantId, runId, phase.Key, cancellationToken);
        if (version == 0)
        {
            return await _obligations.EnsurePlanAsync(
                new PhaseObligationPlanCommand(
                    tenantId, projectId, runId, phase.Key, 1, desired, _clock.UtcNow),
                cancellationToken);
        }

        var existing = await _obligations.ListCurrentAsync(tenantId, runId, phase.Key, cancellationToken);
        var known = existing.Select(item => item.ObligationKey).ToHashSet(StringComparer.Ordinal);
        if (desired.All(item => known.Contains(item.ObligationKey)))
        {
            return 0;
        }

        // REBASELINE: versão nova com o conjunto completo e o motivo registrado. O percentual é
        // recalculado sobre o plano vigente; a versão anterior permanece consultável.
        var rebaselined = desired
            .Select(item => known.Contains(item.ObligationKey)
                ? item
                : item with { Reason = "Obrigação surgida durante a fase (trabalho novo ligado a ela)." })
            .ToArray();
        return await _obligations.EnsurePlanAsync(
            new PhaseObligationPlanCommand(
                tenantId, projectId, runId, phase.Key, version + 1, rebaselined, _clock.UtcNow),
            cancellationToken);
    }

    /// <summary>
    /// Traz o estado das obrigações para o que o board REALMENTE mostra. Card criado, atribuído,
    /// em execução ou em revisão é trabalho em andamento — aparece como situação operacional e
    /// nunca como concluído.
    /// </summary>
    private async Task<IReadOnlyList<PhaseObligationRecord>> ReconcileObligationStatesAsync(
        string tenantId,
        IReadOnlyList<PhaseObligationRecord> obligations,
        Dictionary<string, BoardTaskRecord> byTitle,
        IReadOnlyList<BoardTaskRecord> allTasks,
        string phaseName,
        CancellationToken cancellationToken)
    {
        var byId = allTasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var updated = new List<PhaseObligationRecord>(obligations.Count);
        foreach (var obligation in obligations)
        {
            if (string.Equals(obligation.State, "cancelled", StringComparison.Ordinal))
            {
                updated.Add(obligation);
                continue;
            }

            BoardTaskRecord? card = null;
            if (obligation.CardId is { Length: > 0 } cardId)
            {
                _ = byId.TryGetValue(cardId, out card);
            }
            else if (obligation.ObjectiveKey is { Length: > 0 })
            {
                var name = obligation.Description
                    .Replace("Produzir e aceitar o artefato \"", string.Empty, StringComparison.Ordinal)
                    .TrimEnd('.', '"');
                _ = byTitle.TryGetValue(CardTitleFor(phaseName, name), out card);
            }

            var next = card is null ? "pending" : StateOf(card);
            if (string.Equals(next, obligation.State, StringComparison.Ordinal))
            {
                updated.Add(obligation);
                continue;
            }

            var evidence = card is null
                ? Array.Empty<string>()
                : new[] { $"card:{card.Id}", $"state:{card.State}" };
            _ = await _obligations.UpdateStateAsync(
                new PhaseObligationStateCommand(
                    tenantId, obligation.ObligationId, next, evidence, null, _clock.UtcNow),
                cancellationToken);
            updated.Add(obligation with { State = next, Evidence = evidence });
        }

        return updated;
    }

    /// <summary>
    /// Mapeia o estado do card para o estado da obrigação. Só `completed` conta como aceito: é o
    /// estado que exige revisão independente aprovada.
    /// </summary>
    private static string StateOf(BoardTaskRecord card) => card.InternalState switch
    {
        "completed" => "accepted",
        "awaiting_review" => "in_review",
        "running" or "assigned" => "in_progress",
        "blocked" or "escalated" => "blocked",
        _ => "pending",
    };

    /// <summary>
    /// A chefe aprova o portão POR EVIDÊNCIA e a fase avança. Não é autoaprovação sem prova: a
    /// decisão só chega aqui depois que todas as obrigações obrigatórias foram aceitas, sem achado
    /// impeditivo e sem card bloqueado — e fica registrada com o autor `chief`, auditável como
    /// qualquer decisão humana.
    /// </summary>
    private async Task<string?> TryApproveGateAsync(
        string tenantId,
        string runId,
        WorkflowPhaseRunSnapshot phase,
        WorkflowGateRunSnapshot gate,
        long runVersion,
        string actorProfileId,
        CouncilVerdict? councilVerdict,
        CancellationToken cancellationToken)
    {
        if (string.Equals(gate.State, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(gate.State, "pending", StringComparison.OrdinalIgnoreCase))
        {
            var occurredAt = _clock.UtcNow;
            var receipt = await _authority.EvaluateGateAsync(
                new WorkflowGateEvaluateCommand(
                    tenantId, runId, phase.Key, gate.Key, true, runVersion,
                    MutationKey("gate", runId, gate.Key, "pass", occurredAt), occurredAt,
                    // O ledger referencia a identidade persistida que conduziu a decisão. O literal
                    // histórico `chief` não é ULID, quebrava a mutação no validador e deixava a
                    // esteira autônoma repetindo a mesma exceção a cada ciclo.
                    DecidedByProfileId: actorProfileId,
                    Note: BuildGateRationale(councilVerdict)),
                cancellationToken);
            if (receipt.Status is not (WorkflowRunMutationStatus.Applied
                or WorkflowRunMutationStatus.IdempotentReplay))
            {
                _failures.Add($"gate:{gate.Key}:{receipt.Status}");
                return null;
            }

            runVersion = receipt.RunVersion ?? runVersion;
        }

        var completionAt = _clock.UtcNow;
        var completion = await _authority.CompletePhaseAsync(
            new WorkflowPhaseCompleteCommand(
                tenantId, runId, phase.Key, runVersion,
                MutationKey("complete", runId, phase.Key, "phase", completionAt), completionAt),
            cancellationToken);
        if (completion.Status is not (WorkflowRunMutationStatus.Applied
            or WorkflowRunMutationStatus.IdempotentReplay))
        {
            _failures.Add($"phase:{phase.Key}:{completion.Status}");
            return null;
        }

        return phase.Name;
    }

    private static string BuildGateRationale(CouncilVerdict? council) =>
        council is null
            ? "Portão aprovado pela chefe: todas as obrigações obrigatórias da fase foram aceitas, sem achado impeditivo e sem card bloqueado (modo autônomo)."
            : $"Portão aprovado pela chefe após conselho consultivo. Decisão final: {council.Rationale} " +
              (council.Dissent.Count == 0
                  ? "Nenhuma ressalva permaneceu aberta."
                  : $"Ressalvas preservadas: {string.Join(" | ", council.Dissent)}");

    private static string MutationKey(
        string operation,
        string runId,
        string subject,
        string step,
        DateTimeOffset occurredAt) =>
        $"phase-driver:{operation}:{runId}:{subject}:{step}:{UlidValue.New(occurredAt)}";

    private static PhaseObligation ToDomain(PhaseObligationRecord record) => new(
        record.ObligationKey,
        PhaseProgressEvaluator.ParseKind(record.Kind),
        record.Description,
        record.Required,
        (decimal)record.Weight,
        PhaseProgressEvaluator.ParseState(record.State),
        record.Source,
        record.CardId,
        record.ObjectiveKey,
        record.ArtifactRef);
    /// <summary>
    /// Reúne o conselho: cada assento vira um CARD DE REVISÃO despachado pelo caminho normal.
    ///
    /// Invocar os cinco agentes direto daqui seria mais curto e pior: perderia sandbox atestada,
    /// escopo de path, orçamento de esforço, checkpoint e fencing — todo o maquinário durável que
    /// existe para que uma execução não se perca. Como card, o parecer fica visível no quadro do
    /// dono, é retomável depois de uma queda e entra na contabilidade de custo.
    ///
    /// Enquanto os pareceres não voltam, o conselho está INCOMPLETO e o portão não abre. É o
    /// Default-FAIL aplicado ao conselho: ausência de parecer não é parecer favorável.
    /// </summary>
    private async Task<CouncilVerdict> ConveneCouncilAsync(
        string tenantId,
        ProjectRecord project,
        string phaseName,
        string actorProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var board = await _board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, null, null, null, "active", null, 0, 300),
            cancellationToken);
        var primaryDemand = (await _board.ListDemandsAsync(
                tenantId, project.Id, null, null, 500, cancellationToken))
            .Where(demand => !demand.Internal)
            .OrderBy(demand => demand.CreatedAt)
            .FirstOrDefault();

        var opinions = new List<CouncilOpinion>(AgentCouncilPolicy.Seats.Count);
        var created = 0;
        foreach (var seat in AgentCouncilPolicy.Seats)
        {
            var prefix = CouncilCardTitle(phaseName, seat.PersonaKey);
            var existing = board.Items
                .Where(task => string.Equals(task.Title, prefix, StringComparison.Ordinal) ||
                    task.Title.StartsWith($"{prefix} — ciclo ", StringComparison.Ordinal))
                .Select(task => (Task: task, Cycle: CouncilCycle(task.Title, prefix)))
                .OrderByDescending(value => value.Cycle)
                .ThenByDescending(value => value.Task.CreatedAt)
                .FirstOrDefault();

            if (existing.Task is null)
            {
                var title = CouncilCardTitle(phaseName, seat.PersonaKey, 1);
                await CreateCouncilCardAsync(
                    tenantId, project, phaseName, seat, title, 1, primaryDemand?.Id, actorProfileId,
                    now.AddMilliseconds(created * 6), cancellationToken);
                created++;
                continue;
            }

            var done = string.Equals(existing.Task.State, "done", StringComparison.Ordinal) ||
                string.Equals(existing.Task.InternalState, "completed", StringComparison.Ordinal);
            var blocked = string.Equals(existing.Task.InternalState, "blocked", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(existing.Task.BlockedReason);
            if (!done && !blocked)
            {
                continue;
            }

            var attempts = await _board.ListAttemptsAsync(
                tenantId, existing.Task.Id, null, 100, cancellationToken);
            var attemptSummary = attempts
                .Where(attempt => !string.IsNullOrWhiteSpace(attempt.Summary))
                .OrderByDescending(attempt => attempt.Number)
                .Select(attempt => attempt.Summary)
                .FirstOrDefault();
            var opinion = AgentCouncilPolicy.FromExecution(
                seat, attemptSummary, existing.Task.BlockedReason);
            if (opinion is null)
            {
                continue;
            }

            if (!opinion.IsBlocking)
            {
                opinions.Add(opinion);
                continue;
            }

            // Achado bloqueante vira TRABALHO VISÍVEL, seguido por nova revisão independente.
            // Três ciclos com o mesmo assento ainda bloqueando interrompem o crescimento infinito
            // de cards e devolvem um diagnóstico objetivo à chefe.
            if (existing.Cycle >= AgentCouncilPolicy.MaximumReviewCycles)
            {
                opinions.Add(opinion with
                {
                    Summary = $"Achado persistiu por {existing.Cycle} ciclos: {opinion.Summary}",
                });
                continue;
            }

            var remediationTitle = CouncilRemediationCardTitle(
                phaseName, seat.PersonaKey, existing.Cycle);
            var remediation = board.Items.FirstOrDefault(task =>
                string.Equals(task.Title, remediationTitle, StringComparison.Ordinal));
            if (remediation is null)
            {
                await CreateCouncilRemediationCardAsync(
                    tenantId, project, phaseName, seat, opinion, remediationTitle,
                    existing.Cycle, primaryDemand?.Id, actorProfileId,
                    now.AddMilliseconds(created * 6), cancellationToken);
                created++;
                continue;
            }

            var remediationDone = string.Equals(
                    remediation.InternalState, "completed", StringComparison.Ordinal) ||
                string.Equals(remediation.State, "done", StringComparison.Ordinal);
            if (!remediationDone)
            {
                continue;
            }

            var nextCycle = existing.Cycle + 1;
            var nextTitle = CouncilCardTitle(phaseName, seat.PersonaKey, nextCycle);
            if (!board.Items.Any(task => string.Equals(
                    task.Title, nextTitle, StringComparison.Ordinal)))
            {
                await CreateCouncilCardAsync(
                    tenantId, project, phaseName, seat, nextTitle, nextCycle,
                    primaryDemand?.Id, actorProfileId,
                    now.AddMilliseconds(created * 6), cancellationToken);
                created++;
            }
        }

        return AgentCouncilPolicy.Consolidate(opinions);
    }

    private static string CouncilCardTitle(string phaseName, string personaKey) =>
        $"{phaseName} — Conselho: parecer de {personaKey}";

    private static string CouncilCardTitle(string phaseName, string personaKey, int cycle) =>
        $"{CouncilCardTitle(phaseName, personaKey)} — ciclo {cycle}";

    private static string CouncilRemediationCardTitle(
        string phaseName, string personaKey, int cycle) =>
        $"{phaseName} — Conselho: corrigir achado de {personaKey} — ciclo {cycle}";

    private static int CouncilCycle(string title, string prefix)
    {
        if (string.Equals(title, prefix, StringComparison.Ordinal))
        {
            return 1;
        }

        var suffix = title[$"{prefix} — ciclo ".Length..];
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var cycle)
            ? cycle
            : 1;
    }

    /// <summary>
    /// Cria o card de um assento como `revisao`, o tipo canônico do trabalho. A LENTE e a
    /// especialidade explícita impedem cinco pareceres iguais executados pelo mesmo perfil
    /// genérico; o gate de despacho já reconhece revisão como trabalho delegável.
    /// </summary>
    private async Task CreateCouncilCardAsync(
        string tenantId,
        ProjectRecord project,
        string phaseName,
        CouncilSeat seat,
        string title,
        int cycle,
        string? demandId,
        string actorProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var taskId = UlidValue.New(now).ToString();
        var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        var instruction = ComposeCouncilInstruction(project, phaseName, seat, cycle);

        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId, taskId, project.Id, demandId,
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                actorProfileId, title,
                // Alta: o conselho destrava a fase inteira. Na fila atrás do trabalho comum, ele
                // atrasaria tudo o que vem depois dele.
                "high",
                null, null, instructionId, instruction, now, phaseName, "revisao"),
            cancellationToken);

        _ = await _board.MoveTaskAsync(
            new BoardTaskMoveCommand(
                tenantId, taskId, "ready", $"conselho:{phaseName}", "agent",
                now.AddMilliseconds(4)),
            cancellationToken);
    }

    private async Task CreateCouncilRemediationCardAsync(
        string tenantId,
        ProjectRecord project,
        string phaseName,
        CouncilSeat seat,
        CouncilOpinion opinion,
        string title,
        int cycle,
        string? demandId,
        string actorProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var taskId = UlidValue.New(now).ToString();
        var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        var editor = RemediationPersona(seat.PersonaKey);
        var instruction =
            "Capacidade de execução autorizada: backend-specialist\n" +
            $"Especialidade exigida: {editor}\n" +
            "Tipo de card: documento\n\n" +
            $"Corrigir o achado do Conselho da fase \"{phaseName}\" (ciclo {cycle}) no projeto " +
            $"{project.Name}.\n\nPARECER INDEPENDENTE QUE ORIGINOU ESTE CARD:\n{opinion.Summary}\n\n" +
            "Atualize todos os documentos impactados por nova versão, preserve a versão anterior e " +
            "registre o rationale. Não altere o parecer do conselheiro.\n\n" +
            "Critérios de aceite:\n" +
            "- Cada documento impactado possui versão nova e referência à versão substituída.\n" +
            "- O achado é respondido com evidência verificável, não apenas com concordância textual.\n" +
            "- Escopo, requisitos, arquitetura e plano permanecem consistentes entre si.\n" +
            "- A conclusão lista arquivos, versões, validações e riscos residuais.\n";
        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId, taskId, project.Id, demandId,
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                UlidValue.New(now.AddMilliseconds(3)).ToString(), actorProfileId, title, "critical",
                null, null, instructionId, instruction, now, phaseName, "documento"),
            cancellationToken);
        _ = await _board.MoveTaskAsync(
            new BoardTaskMoveCommand(
                tenantId, taskId, "ready", $"conselho-correcao:{phaseName}", "agent",
                now.AddMilliseconds(4)),
            cancellationToken);
    }

    public static string ComposeCouncilInstruction(
        ProjectRecord project,
        string phaseName,
        CouncilSeat seat,
        int cycle)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(seat);
        return
            "Capacidade de execução autorizada: critic\n" +
            $"Especialidade exigida: {seat.PersonaKey}\n" +
            "Tipo de card: revisao\n\n" +
            $"Você foi convocado ao CONSELHO da fase \"{phaseName}\" do projeto {project.Name} " +
            $"({project.Key}), ciclo {cycle}, como {seat.PersonaKey}.\n\n" +
            $"Sua LENTE — e somente ela: {seat.Lens}\n\n" +
            "Leia as versões mais recentes dos documentos produzidos nas fases 1 a 4 em `docs/` e " +
            "critique o conjunto sob a sua lente. Cite arquivo, versão e trecho de cada evidência. " +
            "Não repita outra lente e não force consenso.\n\n" +
            "Em escopo: parecer independente em `docs/`.\n" +
            "Fora de escopo: código de produção, alteração dos documentos revisados e decisão final.\n\n" +
            "Na conclusão do card, use obrigatoriamente:\n" +
            "VEREDITO: LIBERAR | RESSALVA | BLOQUEAR\n" +
            "RESUMO: <conclusão independente>\n" +
            "EVIDÊNCIAS: <arquivos, versões e trechos>\n" +
            "DOCUMENTOS_IMPACTADOS: <lista ou nenhum>\n\n" +
            "BLOQUEAR somente quando houver violação de requisito, segurança, integridade, critério " +
            "de aceite ou gate obrigatório. Sugestões ficam como RESSALVA. Mesmo quando o veredito " +
            "for BLOQUEAR, conclua o card de parecer: o Control Plane cria cards de correção e uma " +
            "nova rodada. Não invente achado para parecer diligente.\n";
    }

    private static string RemediationPersona(string reviewerPersona) => reviewerPersona switch
    {
        "playbook-arquiteto" => "playbook-tech-lead",
        "playbook-tech-lead" => "playbook-arquiteto",
        "playbook-qa" => "playbook-tech-lead",
        "playbook-security" => "playbook-arquiteto",
        "playbook-dba-dados" => "playbook-arquiteto",
        _ => "playbook-tech-lead",
    };

    private async Task<IReadOnlyList<MessageRecord>> ListHumanMessagesAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var conversations = await _conversations.ListConversationsAsync(
            tenantId, projectId, null, 200, cancellationToken);
        var messages = new Dictionary<string, MessageRecord>(StringComparer.Ordinal);
        foreach (var conversation in conversations)
        {
            // O mandato fundador não pode sumir quando a conversa ultrapassa a janela recente.
            // A store possui consultas próprias para ambos, sem carregar o histórico inteiro.
            var first = await _conversations.GetFirstMessageAsync(
                tenantId, conversation.Id, cancellationToken);
            if (first is not null) messages[first.Id] = first;
            foreach (var message in await _conversations.ListRecentMessagesAsync(
                         tenantId, conversation.Id, 50, cancellationToken))
            {
                messages[message.Id] = message;
            }
        }

        return messages.Values
            .Where(item => string.Equals(item.ProjectId, projectId, StringComparison.Ordinal) &&
                           string.Equals(item.AuthorRole, "user", StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(item.AuthorProfileId))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task CreateObjectiveCardAsync(
        string tenantId,
        ProjectRecord project,
        string actorProfileId,
        string phaseName,
        string objectiveName,
        IReadOnlyList<MessageRecord> humanMessages,
        IReadOnlyList<BoardSolicitationRecord> solicitations,
        IReadOnlyList<BoardDemandRecord> demands,
        IReadOnlyList<WorkflowDocumentTemplateRecord> templates,
        CancellationToken cancellationToken,
        string? titleOverride = null,
        string? revisionSourceMessageId = null)
    {
        var now = _clock.UtcNow;
        var taskId = UlidValue.New(now).ToString();
        var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();

        var personaKey = PersonaForObjective(phaseName, objectiveName);
        var template = MatchTemplate(templates, phaseName, objectiveName);
        var instruction = ComposeObjectiveInstruction(
            project, phaseName, objectiveName, personaKey, template, humanMessages,
            solicitations, demands);
        if (!string.IsNullOrWhiteSpace(revisionSourceMessageId))
        {
            instruction += $"""

                # Atualização versionada obrigatória
                - Este card existe porque a mensagem humana `{revisionSourceMessageId}` chegou depois da versão anterior.
                - Leia a versão mais recente deste mesmo artefato, preserve o que continua válido e produza uma nova versão rastreável.
                - Não sobrescreva a proveniência anterior; registre o delta, a mensagem que o originou e os documentos impactados.
                """;
        }
        var humanDemands = demands.Where(demand => !demand.Internal);
        var primaryDemand = revisionSourceMessageId is null
            ? humanDemands.OrderBy(demand => demand.CreatedAt).FirstOrDefault()
            : humanDemands.OrderByDescending(demand => demand.CreatedAt).FirstOrDefault();

        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId,
                taskId,
                project.Id,
                primaryDemand?.Id,
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                actorProfileId,
                titleOverride ?? CardTitleFor(phaseName, objectiveName),
                // Prioridade BAIXA de propósito: o artefato da fase é obrigatório, mas não pode
                // passar à frente do trabalho que o dono pediu e tomar o slot de despacho dele.
                // Quem quiser antecipá-lo repriorizamos no board — a decisão é humana.
                "low",
                null,
                null,
                instructionId,
                instruction,
                now,
                phaseName,
                "documento"),
            cancellationToken);

        // O card nasce em `backlog`, e quem promove backlog→ready é a triagem por ondas, que
        // trabalha sobre um PLANO de demanda. Este card não vem de demanda: ele vem da esteira, e
        // não tem dependência nenhuma — a fase já está ativa. Sem esta promoção explícita ele
        // ficaria parado para sempre, invisível para o despacho.
        _ = await _board.MoveTaskAsync(
            new BoardTaskMoveCommand(
                tenantId, taskId, "ready", $"esteira:{phaseName}", "agent", now.AddMilliseconds(4)),
            cancellationToken);
    }

    /// <summary>
    /// Determina se informação humana posterior já pode originar uma atualização versionada.
    /// Execução ainda ativa ou em correção permanece serializada no card atual.
    /// </summary>
    public static bool NeedsDocumentRevision(
        DateTimeOffset latestHumanMessageAt,
        BoardTaskRecord latestObjectiveCard)
    {
        if (latestHumanMessageAt <= latestObjectiveCard.CreatedAt)
        {
            return false;
        }

        return latestObjectiveCard.State is "approved" or "merged" or "done" or "completed" ||
               latestObjectiveCard.InternalState is "approved" or "merged" or "done" or "completed";
    }

    /// <summary>
    /// Pacote efetivo do card de documento. Referências substituem o despejo indiscriminado do
    /// repositório, mas fatos do usuário, decisões, lacunas, DoD e evidências permanecem no card.
    /// </summary>
    public static string ComposeObjectiveInstruction(
        ProjectRecord project,
        string phaseName,
        string objectiveName,
        string personaKey,
        WorkflowDocumentTemplateRecord? template,
        IReadOnlyList<MessageRecord> humanMessages,
        IReadOnlyList<BoardSolicitationRecord> solicitations,
        IReadOnlyList<BoardDemandRecord> demands)
    {
        ArgumentNullException.ThrowIfNull(project);
        var builder = new StringBuilder();
        builder.Append("Capacidade de execução autorizada: backend-specialist\n")
            .Append("Especialidade exigida: ").Append(personaKey).Append('\n')
            .Append("Tipo de card: documento\n\n")
            .Append("# Trabalho delegado\n")
            .Append("Produzir o artefato **").Append(objectiveName).Append("** da fase \"")
            .Append(phaseName).Append("\". A Diretora de Engenharia acompanha e revisa; a autoria ")
            .Append("operacional deste documento é sua.\n\n")
            .Append("# Projeto e objetivo de negócio\n")
            .Append("- Projeto: ").Append(project.Name).Append(" (").Append(project.Key).Append(")\n")
            .Append("- Objetivo registrado: ").Append(Clean(project.Description, 2_000)).Append('\n')
            .Append("- Criticidade: ").Append(project.Criticality).Append('\n')
            .Append("- Modo de condução: ").Append(project.OperationMode).Append('\n')
            .Append("- Prazo registrado: ")
            .Append(project.TargetDeadline?.ToString("O", CultureInfo.InvariantCulture) ?? "não informado")
            .Append("\n- Tecnologias já informadas: ")
            .Append(project.Technologies.Count == 0 ? "nenhuma" : string.Join(", ", project.Technologies))
            .Append("\n\n# Proveniência — não confunda fato com inferência\n");

        var orderedConversationInputs = humanMessages
            .Where(item => string.Equals(item.AuthorRole, "user", StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(item.AuthorProfileId))
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        var founders = orderedConversationInputs
            .GroupBy(item => item.ConversationId, StringComparer.Ordinal)
            .Select(group => group.First())
            .TakeLast(20)
            .ToArray();
        var conversationInputs = founders
            .Concat(orderedConversationInputs.TakeLast(Math.Max(0, 20 - founders.Length)))
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        foreach (var item in conversationInputs)
        {
            builder.Append("- FATO EXPLÍCITO DO USUÁRIO [conversa:")
                .Append(item.ConversationId).Append(", mensagem:").Append(item.Id)
                .Append(", em ")
                .Append(item.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append("]: ")
                .Append(Clean(item.Content, 2_000)).Append('\n');
        }

        var boardInputs = solicitations
            .Where(item => !item.Internal)
            .OrderBy(item => item.CreatedAt)
            .TakeLast(20)
            .ToArray();
        if (conversationInputs.Length == 0 && boardInputs.Length == 0)
        {
            builder.Append("- Nenhuma declaração humana autenticada foi localizada. Trate isso como LACUNA; não invente conteúdo.\n");
        }
        foreach (var item in boardInputs)
        {
            builder.Append("- FATO EXPLÍCITO DO USUÁRIO [solicitação:")
                .Append(item.Id).Append(", em ")
                .Append(item.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append("]: ")
                .Append(Clean($"{item.Title}: {item.Body}", 2_000)).Append('\n');
        }

        foreach (var demand in demands.OrderBy(item => item.CreatedAt).TakeLast(30))
        {
            builder.Append("- ")
                .Append(demand.Internal ? "TRABALHO INTERNO DERIVADO" : "DEMANDA DERIVADA DO PEDIDO")
                .Append(" [demanda:").Append(demand.Id);
            if (!string.IsNullOrWhiteSpace(demand.SolicitationId))
                builder.Append(", origem:").Append(demand.SolicitationId);
            builder.Append("]: ").Append(Clean($"{demand.Title}: {demand.Description}", 1_500))
                .Append('\n');
        }

        var playbook = CanonicalWorkflowTemplates.PlaybookStandardTemplate;
        var phaseIndex = Array.FindIndex(
            playbook.Phases.ToArray(),
            value => string.Equals(value, phaseName, StringComparison.Ordinal));
        builder.Append("\n# Fontes canônicas e decisões anteriores\n")
            .Append("- Playbook canônico: template `").Append(playbook.Key).Append("`, fase `")
            .Append(phaseName).Append("`.\n")
            .Append("- Use a versão mais recente aceita dos artefatos abaixo; registre caminho e versão citados.\n");
        if (phaseIndex > 0)
        {
            foreach (var previousPhase in playbook.Phases.Take(phaseIndex))
            {
                foreach (var document in playbook.DocumentsByPhase[previousPhase])
                    builder.Append("  - `docs/**`: ").Append(document).Append(" (")
                        .Append(previousPhase).Append(")\n");
            }
        }
        else
        {
            builder.Append("  - Não há fase anterior; a fonte primária é o pedido humano acima.\n");
        }

        builder.Append("\n# Estrutura canônica deste artefato\n");
        if (template is null)
        {
            builder.Append("- Nenhum template estrutural correspondente foi localizado. Declare a lacuna e siga os critérios do gate; não fabrique uma estrutura canônica.\n");
        }
        else
        {
            builder.Append("- Template: ").Append(template.Code).Append(" — ").Append(template.Name)
                .Append("\n- Campos obrigatórios: `").Append(template.RequiredFieldsJson).Append("`\n")
                .Append("- Formatos de métricas: `").Append(template.MetricFormatsJson).Append("`\n")
                .Append("- O que precisa provar: ").Append(Clean(template.Guidance, 2_000)).Append('\n');
        }

        builder.Append("\n# Escopo, não escopo e dependências\n")
            .Append("- Em escopo: criar ou atualizar a versão de `").Append(objectiveName)
            .Append("` em `docs/`, consolidando as fontes relevantes acima.\n")
            .Append("- Fora de escopo: código de produção, aprovação do próprio documento, alteração de governança e decisões sem evidência.\n")
            .Append("- Dependências: artefatos anteriores citados e decisões já registradas. Informação de fase futura deve ser preservada com sua origem, não descartada.\n")
            .Append("- Considere as declarações humanas citadas uma lista exaustiva do que o usuário afirmou até esta versão. Nome do projeto, tecnologia e governança não viram necessidade de negócio.\n")
            .Append("- Capacidade, valor, problema, restrição ou preferência sem citação humana exata deve ser rotulada PROPOSTA ou PREMISSA INFERIDA em TODA ocorrência; não pode entrar silenciosamente em objetivo, valor ou escopo.\n")
            .Append("- Pedido para criar, tocar ou conduzir o projeto prova intenção de construir. Não pergunte novamente Build x Buy sem evidência nova que torne essa decisão realmente ambígua.\n")
            .Append("- Se faltar decisão de negócio indispensável, registre a pergunta e por que importa. Se houver uma opção responsável inferível, registre-a como PREMISSA INFERIDA, com motivo e risco, e continue.\n\n")
            .Append("# Critérios de aceite e definição de pronto\n")
            .Append("- O arquivo existe em `docs/`, possui versão identificável e não é um esqueleto vazio.\n")
            .Append("- Cada afirmação relevante distingue FATO HUMANO, DECISÃO REGISTRADA e PREMISSA INFERIDA.\n")
            .Append("- Uma classificação na tabela de proveniência não licencia repetir a afirmação sem o mesmo rótulo no corpo, escopo ou conclusão.\n")
            .Append("- O conteúdo cobre o objetivo da fase, os campos obrigatórios do template e os critérios do gate.\n")
            .Append("- Inconsistências com documentos anteriores são resolvidas por nova versão ou registradas como bloqueio; não deixe versões incompatíveis silenciosamente.\n")
            .Append("- Nenhum segredo aparece no documento ou no resumo da execução.\n")
            .Append("- Um revisor diferente do autor consegue verificar o resultado sem redescobrir o projeto.\n\n")
            .Append("# Evidências obrigatórias na conclusão\n")
            .Append("- caminho do arquivo e identificador da versão; o runtime registra o SHA real em `git-commit:<sha>` após a colheita. Não invente hash nem deixe placeholder autorreferente no documento;\n")
            .Append("- lista das fontes e versões consultadas;\n")
            .Append("- campos do template cobertos;\n")
            .Append("- premissas, riscos, lacunas e decisões pendentes;\n")
            .Append("- validação executada e resultado.\n");
        if (playbook.GatesByPhase.TryGetValue(phaseName, out var gates))
        {
            builder.Append("\n# Gate que este trabalho ajuda a provar\n");
            foreach (var gate in gates) builder.Append("- ").Append(gate).Append('\n');
        }

        return builder.ToString();
    }

    public static string PersonaForObjective(string phaseName, string objectiveName)
    {
        var text = NormalizeSearch($"{phaseName} {objectiveName}");
        if (text.Contains("threat", StringComparison.Ordinal) || text.Contains("pentest", StringComparison.Ordinal) || text.Contains("sbom", StringComparison.Ordinal)) return "playbook-security";
        if (text.Contains(" der ", StringComparison.Ordinal) || text.Contains("dados", StringComparison.Ordinal) || text.Contains("dicionario", StringComparison.Ordinal)) return "playbook-dba-dados";
        if (text.Contains("observabilidade", StringComparison.Ordinal) || text.Contains("dora", StringComparison.Ordinal) || text.Contains("gmud", StringComparison.Ordinal) || text.Contains("release", StringComparison.Ordinal) || text.Contains("rollback", StringComparison.Ordinal)) return "playbook-devops";
        if (text.Contains("sustentacao", StringComparison.Ordinal) || text.Contains("runbook", StringComparison.Ordinal) || text.Contains("postmortem", StringComparison.Ordinal) || text.Contains("capacity", StringComparison.Ordinal) || text.Contains("operacao", StringComparison.Ordinal)) return "playbook-sre-sustentacao";
        if (text.Contains("teste", StringComparison.Ordinal) || text.Contains("quality", StringComparison.Ordinal) || text.Contains("performance", StringComparison.Ordinal) || text.Contains("uat", StringComparison.Ordinal) || text.Contains("go/no-go", StringComparison.Ordinal) || text.Contains("defeitos", StringComparison.Ordinal)) return "playbook-qa";
        if (text.Contains("arquitetura", StringComparison.Ordinal) || text.Contains("sad", StringComparison.Ordinal) || text.Contains("adr", StringComparison.Ordinal) || text.Contains("c4", StringComparison.Ordinal) || text.Contains("trade-off", StringComparison.Ordinal)) return "playbook-arquiteto";
        if (text.Contains("planejamento", StringComparison.Ordinal) || text.Contains("dor", StringComparison.Ordinal) || text.Contains("dod", StringComparison.Ordinal) || text.Contains("cronograma", StringComparison.Ordinal) || text.Contains("briefing", StringComparison.Ordinal) || text.Contains("code review", StringComparison.Ordinal)) return "playbook-tech-lead";
        if (text.Contains("desenvolvimento", StringComparison.Ordinal)) return "playbook-dev-executor";
        return "playbook-product-owner";
    }

    private static WorkflowDocumentTemplateRecord? MatchTemplate(
        IReadOnlyList<WorkflowDocumentTemplateRecord> templates,
        string phaseName,
        string objectiveName)
    {
        var normalizedObjective = NormalizeSearch(objectiveName);
        if (normalizedObjective.Contains(" der ", StringComparison.Ordinal))
        {
            return templates.FirstOrDefault(template =>
                string.Equals(template.Phase, phaseName, StringComparison.Ordinal) &&
                string.Equals(template.Code, "09", StringComparison.Ordinal));
        }

        var objectiveTokens = normalizedObjective
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .Select(SingularToken)
            .ToHashSet(StringComparer.Ordinal);
        return templates
            .Where(template => string.Equals(template.Phase, phaseName, StringComparison.Ordinal))
            .Select(template => new
            {
                Template = template,
                Score = NormalizeSearch(template.Name)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(SingularToken)
                    .Count(objectiveTokens.Contains),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Template.Code, StringComparer.Ordinal)
            .Select(candidate => candidate.Template)
            .FirstOrDefault();
    }

    private static string SingularToken(string token) =>
        token.Length > 3 && token.EndsWith('s') ? token[..^1] : token;

    private static string NormalizeSearch(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length + 2).Append(' ');
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.Append(' ').ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Clean(string? value, int limit)
    {
        var redacted = SecretTextProtector.Redact(value).Trim();
        return redacted.Length <= limit ? redacted : $"{redacted[..limit]}…";
    }
}
