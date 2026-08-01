using System.Text;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Contracts;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.WorkBoard;

/// <summary>
/// Núcleo COMPARTILHADO do caminho demanda → plano → cards. É o mesmo efeito nos dois pontos de
/// entrada do produto: o endpoint HTTP (<see cref="DemandPlanEndpoints"/>, acionado pelo humano) e
/// o turno do Chefe (<c>ChiefTurnBackgroundService</c>, quando a Bruna delega uma demanda no chat).
/// Gerar o plano é INERTE e idempotente por demanda; materializar é o ÚNICO ponto que decompõe o
/// plano em work_tasks reais. Cards nascem em `backlog`; a triagem (DoR + dependências) promove a
/// `ready` em ondas — nunca aqui.
///
/// Fase 0A1 (BR-001): materializar é IDEMPOTENTE POR FATIA e o marker é o ÚLTIMO passo. Antes, o
/// plano era carimbado como materializado ANTES de criar os cards e uma queda no meio deixava um
/// plano permanentemente "concluído" com cards faltando; e uma segunda chamada saía cedo pelo
/// marker, sem nunca olhar a realidade do board. Agora a identidade do card é a fatia do plano
/// (chave única no banco), o retry compara o conjunto real com o previsto e só carimba quando o
/// conjunto está completo — inclusive com todas as dependências declaradas resolvíveis.
/// </summary>
public sealed class DemandPlanMaterializer(IWorkBoardStore board, IDemandPlanStore plans)
{
    /// <summary>
    /// Gera (ou lê, se já existir) o plano proposto da demanda. Nada é criado na esteira.
    /// </summary>
    public async Task<DemandPlanSaveResult> EnsurePlanAsync(
        string tenantId,
        BoardDemandRecord demand,
        IReadOnlyList<string> acceptanceCriteria,
        DemandDecompositionHints? hints,
        string? specialty,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        var proposal = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
            demand.Title, demand.Description, acceptanceCriteria, demand.Priority, hints, specialty));
        var command = new DemandPlanSaveCommand(
            tenantId, UlidValue.New(now).ToString(), demand.ProjectId, demand.Id,
            proposal.FeatureId, proposal.Cards.Select(ToCard).ToArray(),
            UlidValue.New(now.AddTicks(1)).ToString(), now);
        return await plans.SaveProposedAsync(command, cancellationToken);
    }

    /// <summary>
    /// Materializa o plano em cards reais do board (estado `backlog`). Convergente: cria apenas as
    /// fatias ausentes, nunca duplica uma fatia existente e só carimba o marker depois de comprovar
    /// que o conjunto materializado é exatamente o previsto. Uma execução repetida sobre um plano
    /// íntegro não altera nada.
    /// </summary>
    public async Task<DemandPlanMaterialization> MaterializeAsync(
        string tenantId,
        string actorProfileId,
        DemandPlanRecord plan,
        BoardDemandRecord demand,
        DateTimeOffset now,
        IPlanMaterializationFaultInjector? faults = null,
        string? activePhaseName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(demand);
        var slices = SlicePlan(plan);
        var injector = faults ?? NullPlanMaterializationFaultInjector.Instance;

        var materialized = await board.ListPlanCardsAsync(tenantId, plan.Id, cancellationToken);
        if (materialized.Count < slices.Count)
        {
            // Planos materializados ANTES da chave lógica existir têm cards sem carimbo: sem
            // adotá-los, o plano pareceria vazio e o board ganharia uma segunda cópia de tudo.
            var adopted = await board.AdoptPlanCardsAsync(
                new BoardPlanCardAdoptCommand(
                    tenantId, plan.Id, plan.DemandId,
                    [.. slices.Select(slice => new BoardPlanSlice(slice.Key, slice.Card.ProposedTitle))]),
                cancellationToken);
            if (adopted > 0)
            {
                materialized = await board.ListPlanCardsAsync(tenantId, plan.Id, cancellationToken);
            }
        }

        var byKey = materialized.ToDictionary(card => card.SliceKey, StringComparer.Ordinal);
        var results = new List<MaterializedPlanCard>(slices.Count);
        var created = 0;
        var index = 0;
        foreach (var slice in slices)
        {
            index++;
            if (byKey.TryGetValue(slice.Key, out var existing))
            {
                results.Add(new MaterializedPlanCard(
                    slice.Card.ProposedTitle, slice.Card.CardType, existing.TaskId));
                continue;
            }

            // Uma base de tempo monotônica por card mantém os ULIDs (task, instrução, backing)
            // distintos e cronológicos entre os cards do mesmo plano.
            var cardNow = now.AddMilliseconds(index * 8);
            var taskId = UlidValue.New(cardNow).ToString();
            var instructionId = UlidValue.New(cardNow.AddMilliseconds(1)).ToString();
            var createRequest = new CreateTaskRequest(
                plan.ProjectId, slice.Card.ProposedTitle,
                ComposeInstruction(slice.Card, plan.FeatureId),
                demand.Id, demand.Priority, CardType: slice.Card.CardType,
                // O card de TRABALHO nasce carimbado com a fase ativa. Sem isso o progresso da
                // fase media só documentos: uma fase de Desenvolvimento exibia "100%" com o
                // briefing e o code review escritos e nenhuma linha implementada.
                PhaseName: activePhaseName);
            var values = WorkBoardApplicationService.CreateTask(
                taskId, instructionId, createRequest, cardNow);
            var outcome = await board.CreatePlanCardAsync(
                new BoardPlanCardCreateCommand(
                    new(tenantId, taskId, values.Task.ProjectId, values.Task.DemandId,
                        UlidValue.New(cardNow.AddMilliseconds(2)).ToString(),
                        UlidValue.New(cardNow.AddMilliseconds(3)).ToString(), actorProfileId,
                        values.Task.Title, values.Task.Priority, values.Task.AssigneeAgentId,
                        values.Task.DueAt, instructionId, values.Instruction.Body, cardNow,
                        values.Task.PhaseName, values.CardType),
                    plan.Id,
                    slice.Key),
                cancellationToken);
            results.Add(new MaterializedPlanCard(
                slice.Card.ProposedTitle, slice.Card.CardType, outcome.Task.Id));
            if (outcome.Created)
            {
                created++;
                await injector.SignalAsync(
                    created == 1
                        ? PlanMaterializationStage.AfterFirstCard
                        : PlanMaterializationStage.BetweenCards,
                    cancellationToken);
            }
        }

        await injector.SignalAsync(PlanMaterializationStage.AfterCards, cancellationToken);

        // O conjunto REAL é medido de novo no banco: o que garante a completude é o board, não a
        // lista que este método acabou de montar.
        var settled = await board.ListPlanCardsAsync(tenantId, plan.Id, cancellationToken);
        AssertComplete(plan, slices, settled);
        await injector.SignalAsync(PlanMaterializationStage.AfterDependencies, cancellationToken);

        await injector.SignalAsync(PlanMaterializationStage.BeforeMarker, cancellationToken);
        var claimed = await plans.TryMarkMaterializedAsync(tenantId, plan.Id, now, cancellationToken);
        await injector.SignalAsync(PlanMaterializationStage.AfterMarker, cancellationToken);
        return new DemandPlanMaterialization(
            plan with { Status = "materialized", MaterializedAt = plan.MaterializedAt ?? now },
            AlreadyMaterialized: !claimed && created == 0,
            results,
            CreatedCards: created);
    }

    /// <summary>
    /// Fatias previstas do plano, na ordem do plano. A chave é o código estável do card
    /// (<c>F.../Tnn</c>) — a mesma identidade que a triagem por ondas usa para resolver
    /// dependências, e por isso a única chave de idempotência tecnicamente defensável aqui.
    /// </summary>
    internal static IReadOnlyList<PlanSlice> SlicePlan(DemandPlanRecord plan)
    {
        var slices = new List<PlanSlice>(plan.Cards.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in plan.Cards)
        {
            var key = DemandDecompositionPlanner.CodeOf(card.ProposedTitle);
            if (!seen.Add(key))
            {
                // Dois cards com o mesmo código tornariam a materialização ambígua e a triagem
                // por ondas indeterminada. Falhar aqui é visível; adivinhar não seria.
                throw new PlanMaterializationIntegrityException(
                    "plan_slice_key_duplicated",
                    $"The plan declares the slice '{key}' more than once.");
            }

            slices.Add(new PlanSlice(key, card));
        }

        return slices;
    }

    /// <summary>
    /// Invariante do bloco: cardinalidade exata, nenhuma fatia extra e toda dependência declarada
    /// resolvendo para uma fatia REAL do mesmo plano. Uma dependência que não resolve deixaria o
    /// card preso no backlog para sempre — trabalho invisível, que é exatamente o que este bloco
    /// existe para impedir.
    /// </summary>
    private static void AssertComplete(
        DemandPlanRecord plan,
        IReadOnlyList<PlanSlice> slices,
        IReadOnlyList<BoardPlanCardRecord> materialized)
    {
        var actual = new HashSet<string>(
            materialized.Select(card => card.SliceKey), StringComparer.Ordinal);
        if (actual.Count != materialized.Count)
        {
            throw new PlanMaterializationIntegrityException(
                "plan_card_duplicated",
                $"The board holds duplicate slices for plan '{plan.Id}'.");
        }

        var expected = new HashSet<string>(slices.Select(slice => slice.Key), StringComparer.Ordinal);
        var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new PlanMaterializationIntegrityException(
                "plan_cards_missing",
                $"The plan '{plan.Id}' is missing the slices {string.Join(", ", missing)}.");
        }

        var unexpected = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unexpected.Length > 0)
        {
            throw new PlanMaterializationIntegrityException(
                "plan_cards_unexpected",
                $"The plan '{plan.Id}' materialized unknown slices {string.Join(", ", unexpected)}.");
        }

        foreach (var slice in slices)
        {
            foreach (var dependency in slice.Card.Dependencies)
            {
                if (!expected.Contains(dependency))
                {
                    throw new PlanMaterializationIntegrityException(
                        "plan_dependency_unresolved",
                        $"The slice '{slice.Key}' depends on '{dependency}', which the plan does not declare.");
                }
            }
        }
    }

    internal static DemandPlanCard ToCard(ProposedCard card) => new(
        card.ProposedTitle, card.CardType, card.RequiredRole, card.Instruction, card.InScope,
        card.OutOfScope, card.AcceptanceCriteria, card.Gates, card.Dependencies, card.Specialty,
        // Fase 1B: o orçamento decidido no planejamento viaja com o card. Recalcular no despacho
        // deixaria o card sujeito a mudanças de política feitas depois — e o plano deixaria de ser
        // reproduzível, que é a única razão de ele ser determinístico.
        card.Budget is null
            ? null
            : new DemandCardBudget(
                card.Budget.Agents, card.Budget.TokenBudget, card.Budget.MaxRounds,
                card.Budget.ReviewDepth, card.Budget.FanOutAllowed, card.Budget.ReasonCode));

    // A instrução carrega a CAPACIDADE operacional exigida (nunca uma conta), o escopo in/out, os
    // critérios de aceite, gates e dependências. Ela não é confundida com o papel humano, que vem
    // da especialidade/persona declarada no mesmo pacote.
    internal static string ComposeInstruction(DemandPlanCard card, string featureId)
    {
        var builder = new StringBuilder();
        builder.Append("Feature: ").Append(featureId).Append('\n');
        builder.Append("Capacidade de execução autorizada: ").Append(card.RequiredRole).Append('\n');
        if (!string.IsNullOrWhiteSpace(card.Specialty))
        {
            // O julgamento do Chefe sobre QUEM é o profissional qualificado chega ao card e, dali,
            // ao despacho. Uma chave que não exista no catálogo é descartada no despacho — o texto
            // do modelo nunca define autoridade, apenas propõe.
            builder.Append("Especialidade exigida: ").Append(card.Specialty!.Trim()).Append('\n');
        }

        builder.Append("Tipo de card: ").Append(card.CardType).Append("\n\n");
        builder.Append(card.Instruction).Append("\n\n");
        builder.Append("Em escopo: ").Append(card.InScope).Append('\n');
        builder.Append("Fora de escopo: ").Append(card.OutOfScope).Append('\n');
        if (card.AcceptanceCriteria.Count > 0)
        {
            builder.Append("\nCritérios de aceite:\n");
            foreach (var criterion in card.AcceptanceCriteria)
            {
                builder.Append("- ").Append(criterion).Append('\n');
            }
        }

        if (card.Gates.Count > 0)
        {
            builder.Append("\nGates: ").Append(string.Join(", ", card.Gates)).Append('\n');
        }

        if (card.Dependencies.Count > 0)
        {
            builder.Append("Depende de: ").Append(string.Join(", ", card.Dependencies)).Append('\n');
        }

        return builder.ToString().Trim();
    }
}

/// <summary>Uma fatia do plano: a chave lógica estável e o card proposto que ela materializa.</summary>
internal sealed record PlanSlice(string Key, DemandPlanCard Card);

/// <summary>
/// O plano e o board divergiram de um jeito que a materialização não pode corrigir sozinha. É
/// sempre um FATO verificado (fatia faltando, duplicada, desconhecida ou dependência que não
/// resolve), nunca uma suspeita — e nunca é engolido: vira estado `failed` visível com este código.
/// </summary>
public sealed class PlanMaterializationIntegrityException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Resultado da materialização: o plano, se nada mudou e os cards da vez.</summary>
public sealed record DemandPlanMaterialization(
    DemandPlanRecord Plan,
    bool AlreadyMaterialized,
    IReadOnlyList<MaterializedPlanCard> Cards,
    int CreatedCards = 0);

public sealed record MaterializedPlanCard(string Title, string CardType, string? TaskId);
