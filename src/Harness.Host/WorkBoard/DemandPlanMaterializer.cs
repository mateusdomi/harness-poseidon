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
/// plano em work_tasks reais, e só a PRIMEIRA materialização cria cards (guarda
/// <c>TryMarkMaterializedAsync</c>). Cards nascem em `backlog`; a triagem (DoR + dependências)
/// promove a `ready` em ondas — nunca aqui.
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
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        var proposal = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
            demand.Title, demand.Description, acceptanceCriteria, demand.Priority, hints));
        var command = new DemandPlanSaveCommand(
            tenantId, UlidValue.New(now).ToString(), demand.ProjectId, demand.Id,
            proposal.FeatureId, proposal.Cards.Select(ToCard).ToArray(),
            UlidValue.New(now.AddTicks(1)).ToString(), now);
        return await plans.SaveProposedAsync(command, cancellationToken);
    }

    /// <summary>
    /// Materializa o plano em cards reais do board (estado `backlog`), reusando a criação de task
    /// existente. Idempotente por plano: uma segunda chamada NÃO recria cards.
    /// </summary>
    public async Task<DemandPlanMaterialization> MaterializeAsync(
        string tenantId,
        string actorProfileId,
        DemandPlanRecord plan,
        BoardDemandRecord demand,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(demand);
        var claimed = await plans.TryMarkMaterializedAsync(tenantId, plan.Id, now, cancellationToken);
        if (!claimed)
        {
            return new DemandPlanMaterialization(
                plan,
                AlreadyMaterialized: true,
                [.. plan.Cards.Select(card => new MaterializedPlanCard(
                    card.ProposedTitle, card.CardType, null))]);
        }

        var results = new List<MaterializedPlanCard>(plan.Cards.Count);
        var index = 0;
        foreach (var card in plan.Cards)
        {
            // Uma base de tempo monotônica por card mantém os ULIDs (task, instrução, backing)
            // distintos e cronológicos entre os cards do mesmo plano.
            var cardNow = now.AddMilliseconds(++index * 8);
            var taskId = UlidValue.New(cardNow).ToString();
            var instructionId = UlidValue.New(cardNow.AddMilliseconds(1)).ToString();
            var createRequest = new CreateTaskRequest(
                plan.ProjectId, card.ProposedTitle, ComposeInstruction(card, plan.FeatureId),
                demand.Id, demand.Priority, CardType: card.CardType);
            var values = WorkBoardApplicationService.CreateTask(
                taskId, instructionId, createRequest, cardNow);
            var created = await board.CreateTaskAsync(new(
                tenantId, taskId, values.Task.ProjectId, values.Task.DemandId,
                UlidValue.New(cardNow.AddMilliseconds(2)).ToString(),
                UlidValue.New(cardNow.AddMilliseconds(3)).ToString(), actorProfileId,
                values.Task.Title, values.Task.Priority, values.Task.AssigneeAgentId,
                values.Task.DueAt, instructionId, values.Instruction.Body, cardNow,
                values.Task.PhaseName, values.CardType), cancellationToken);
            results.Add(new MaterializedPlanCard(
                card.ProposedTitle, card.CardType, created.Task.Id));
        }

        return new DemandPlanMaterialization(plan, AlreadyMaterialized: false, results);
    }

    internal static DemandPlanCard ToCard(ProposedCard card) => new(
        card.ProposedTitle, card.CardType, card.RequiredRole, card.Instruction, card.InScope,
        card.OutOfScope, card.AcceptanceCriteria, card.Gates, card.Dependencies);

    // A instrução carrega o PAPEL exigido (nunca uma conta), o escopo in/out, os critérios de aceite,
    // os gates e as dependências — tudo o que o card precisa para virar execução após a triagem.
    internal static string ComposeInstruction(DemandPlanCard card, string featureId)
    {
        var builder = new StringBuilder();
        builder.Append("Feature: ").Append(featureId).Append('\n');
        builder.Append("Papel exigido: ").Append(card.RequiredRole).Append('\n');
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

/// <summary>Resultado da materialização: o plano, se era replay e os cards criados (ou nulos no replay).</summary>
public sealed record DemandPlanMaterialization(
    DemandPlanRecord Plan,
    bool AlreadyMaterialized,
    IReadOnlyList<MaterializedPlanCard> Cards);

public sealed record MaterializedPlanCard(string Title, string CardType, string? TaskId);
