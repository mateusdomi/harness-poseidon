using Harness.Modules.Coordination.Application;
using Harness.Modules.Workflows.Application;
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
                if (isPending)
                {
                    await CreateObjectiveCardAsync(
                        tenantId, project, actorProfileId, phase.Name, objective.Name, cancellationToken);
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
        if (decision != PhaseGateDecision.NotReady &&
            AgentCouncilPolicy.ShouldConvene(phase.Name, progress.RequiredTotal))
        {
            var council = await ConveneCouncilAsync(
                tenantId, project, phase.Name, actorProfileId, _clock.UtcNow, cancellationToken);
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
                tenantId, running.Id, phase, gate, runVersion, actorProfileId, cancellationToken);
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
                    Note: "Portão aprovado pela chefe: todas as obrigações obrigatórias da fase foram " +
                        "aceitas, sem achado impeditivo e sem card bloqueado (modo autônomo)."),
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

        var opinions = new List<CouncilOpinion>(AgentCouncilPolicy.Seats.Count);
        var created = 0;
        foreach (var seat in AgentCouncilPolicy.Seats)
        {
            var title = CouncilCardTitle(phaseName, seat.PersonaKey);
            var existing = board.Items.FirstOrDefault(task =>
                string.Equals(task.Title, title, StringComparison.Ordinal));

            if (existing is null)
            {
                await CreateCouncilCardAsync(
                    tenantId, project, phaseName, seat, title, actorProfileId,
                    now.AddMilliseconds(created * 6), cancellationToken);
                created++;
                continue;
            }

            // Parecer entregue = card concluído. Card BLOQUEADO é achado impeditivo: o conselheiro
            // encontrou algo que o impediu de aprovar, e isso segura a transição.
            var done = string.Equals(existing.State, "done", StringComparison.Ordinal);
            var blocked = string.Equals(existing.InternalState, "blocked", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(existing.BlockedReason);
            if (!done && !blocked)
            {
                continue;
            }

            opinions.Add(new CouncilOpinion(
                seat.PersonaKey,
                IsBlocking: blocked,
                HasConcern: false,
                Summary: blocked
                    ? $"Achado impeditivo sob a lente: {seat.Lens}"
                    : $"Parecer entregue sob a lente: {seat.Lens}"));
        }

        return AgentCouncilPolicy.Consolidate(opinions);
    }

    private static string CouncilCardTitle(string phaseName, string personaKey) =>
        $"{phaseName} — Conselho: parecer de {personaKey}";

    /// <summary>
    /// Cria o card de um assento. É `agent_task` porque precisa ser despachável; a LENTE vai na
    /// instrução, e é ela que impede cinco pareceres iguais.
    /// </summary>
    private async Task CreateCouncilCardAsync(
        string tenantId,
        ProjectRecord project,
        string phaseName,
        CouncilSeat seat,
        string title,
        string actorProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var taskId = UlidValue.New(now).ToString();
        var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        var instruction =
            "Papel exigido: critic\nTipo de card: agent_task\n\n" +
            $"Você foi convocado ao CONSELHO da fase \"{phaseName}\" do projeto {project.Name}, " +
            $"como {seat.PersonaKey}.\n\n" +
            $"Sua LENTE — e somente ela: {seat.Lens}\n\n" +
            "Leia os documentos que as fases anteriores produziram em `docs/` e critique o " +
            "conjunto SOB A SUA LENTE. Não repita o que outro conselheiro veria: o valor do " +
            "conselho está na diferença entre as perguntas, não na soma das concordâncias.\n\n" +
            "Em escopo: um parecer curto em `docs/`, citando o documento e o trecho de cada " +
            "achado.\nFora de escopo: código de produção e alteração dos documentos revisados.\n\n" +
            "Critérios de aceite:\n" +
            "- O parecer existe, cita fontes reais e separa ACHADO IMPEDITIVO de ressalva.\n" +
            "- Achado impeditivo é o que fica caro corrigir depois de o código existir. Se não " +
            "houver nenhum, diga isso — não invente um para parecer diligente.\n" +
            "- Encontrando impedimento, BLOQUEIE este card com o motivo: é assim que o conselho " +
            "segura a transição.\n";

        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId, taskId, project.Id, null,
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                actorProfileId, title,
                // Alta: o conselho destrava a fase inteira. Na fila atrás do trabalho comum, ele
                // atrasaria tudo o que vem depois dele.
                "high",
                null, null, instructionId, instruction, now, phaseName),
            cancellationToken);

        _ = await _board.MoveTaskAsync(
            new BoardTaskMoveCommand(
                tenantId, taskId, "ready", $"conselho:{phaseName}", "agent",
                now.AddMilliseconds(4)),
            cancellationToken);
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
                // Prioridade BAIXA de propósito: o artefato da fase é obrigatório, mas não pode
                // passar à frente do trabalho que o dono pediu e tomar o slot de despacho dele.
                // Quem quiser antecipá-lo repriorizamos no board — a decisão é humana.
                "low",
                null,
                null,
                instructionId,
                instruction,
                now,
                phaseName),
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
}
