using System.Text;
using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

/// <summary>
/// PLAT-01: endpoints do artefato de PLANO por demanda. Gerar/ler um plano é INERTE (não cria nada
/// na esteira); a materialização é o único ponto que decompõe o plano em work_tasks reais, reusando
/// a criação de task existente (<see cref="WorkBoardApplicationService.CreateTask"/> +
/// <c>IWorkBoardStore.CreateTaskAsync</c>) e sendo idempotente por plano.
/// </summary>
public static class DemandPlanEndpoints
{
    public static IEndpointRouteBuilder MapDemandPlans(this IEndpointRouteBuilder endpoints)
    {
        var plans = endpoints.MapGroup("/api/v1/demands").WithTags("demand-plans");
        plans.MapPost("/{demandId}/plan", GeneratePlanAsync)
            .Produces<DemandPlanContract>(201).Produces<DemandPlanContract>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        plans.MapGet("/{demandId}/plan", GetPlanAsync)
            .Produces<DemandPlanContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        plans.MapPost("/{demandId}/plan/materialize", MaterializePlanAsync)
            .Produces<MaterializePlanResult>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GeneratePlanAsync(
        string demandId, GeneratePlanRequest? input, HttpRequest request, ILocalProfileStore profiles,
        IWorkBoardStore board, IDemandPlanStore plans, IClock clock, CancellationToken token)
    {
        if (!Valid(demandId)) return InvalidId("demand");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var demand = await board.GetDemandAsync(profile.TenantId, demandId, token);
        if (demand is null) return NotFound("demand");

        try
        {
            var criteria = NormalizeCriteria(input?.AcceptanceCriteria);
            var hints = input?.Hints is null
                ? null
                : new DemandDecompositionHints(
                    input.Hints.HasFrontendSurface, input.Hints.RequiresExternalCredential,
                    input.Hints.HasTechnicalUncertainty, input.Hints.RequiresDecision);
            var proposal = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
                demand.Title, demand.Description, criteria, demand.Priority, hints));

            var now = clock.UtcNow;
            var command = new DemandPlanSaveCommand(
                profile.TenantId, UlidValue.New(now).ToString(), demand.ProjectId, demand.Id,
                proposal.FeatureId, proposal.Cards.Select(ToCard).ToArray(),
                UlidValue.New(now.AddTicks(1)).ToString(), now);
            var result = await plans.SaveProposedAsync(command, token);
            var contract = ToContract(result.Plan);
            return result.Created
                ? Results.Created($"/api/v1/demands/{demand.Id}/plan", contract)
                : Results.Ok(contract);
        }
        catch (ArgumentException e) { return Invalid("demand_plan", e.Message); }
    }

    private static async Task<IResult> GetPlanAsync(
        string demandId, HttpRequest request, ILocalProfileStore profiles, IDemandPlanStore plans,
        CancellationToken token)
    {
        if (!Valid(demandId)) return InvalidId("demand");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var plan = await plans.GetByDemandAsync(profile.TenantId, demandId, token);
        return plan is null ? NotFound("demand_plan") : Results.Ok(ToContract(plan));
    }

    private static async Task<IResult> MaterializePlanAsync(
        string demandId, HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore board,
        IDemandPlanStore plans, IClock clock, CancellationToken token)
    {
        if (!Valid(demandId)) return InvalidId("demand");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var plan = await plans.GetByDemandAsync(profile.TenantId, demandId, token);
        if (plan is null) return NotFound("demand_plan");
        var demand = await board.GetDemandAsync(profile.TenantId, demandId, token);
        if (demand is null) return NotFound("demand");

        var now = clock.UtcNow;
        // Guarda de idempotência: só a PRIMEIRA materialização transiciona 'proposed'→'materialized'.
        // Uma segunda chamada NÃO recria os cards.
        var claimed = await plans.TryMarkMaterializedAsync(profile.TenantId, plan.Id, now, token);
        if (!claimed)
        {
            return Results.Ok(new MaterializePlanResult(
                plan.Id, "materialized", AlreadyMaterialized: true,
                plan.Cards.Select(card => new MaterializedCardResult(
                    card.ProposedTitle, card.CardType, null)).ToArray()));
        }

        var results = new List<MaterializedCardResult>(plan.Cards.Count);
        var index = 0;
        try
        {
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
                    profile.TenantId, taskId, values.Task.ProjectId, values.Task.DemandId,
                    UlidValue.New(cardNow.AddMilliseconds(2)).ToString(),
                    UlidValue.New(cardNow.AddMilliseconds(3)).ToString(), profile.Id,
                    values.Task.Title, values.Task.Priority, values.Task.AssigneeAgentId,
                    values.Task.DueAt, instructionId, values.Instruction.Body, cardNow,
                    values.Task.PhaseName, values.CardType), token);
                results.Add(new MaterializedCardResult(
                    card.ProposedTitle, card.CardType, created.Task.Id));
            }
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (ArgumentException e) { return Invalid("demand_plan", e.Message); }

        return Results.Ok(new MaterializePlanResult(
            plan.Id, "materialized", AlreadyMaterialized: false, results));
    }

    // A instrução carrega o PAPEL exigido (nunca uma conta), o escopo in/out, os critérios de aceite,
    // os gates e as dependências — tudo o que o card precisa para virar execução após a triagem.
    private static string ComposeInstruction(DemandPlanCard card, string featureId)
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

    private static string[] NormalizeCriteria(IReadOnlyList<string>? criteria)
    {
        if (criteria is null) return [];
        if (criteria.Count > 100)
            throw new ArgumentException("At most 100 acceptance criteria are supported.", nameof(criteria));
        return criteria
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().Length <= 2_000
                ? value.Trim()
                : throw new ArgumentException("An acceptance criterion exceeds 2000 characters.", nameof(criteria)))
            .ToArray();
    }

    private static DemandPlanCard ToCard(ProposedCard card) => new(
        card.ProposedTitle, card.CardType, card.RequiredRole, card.Instruction, card.InScope,
        card.OutOfScope, card.AcceptanceCriteria, card.Gates, card.Dependencies);

    private static DemandPlanContract ToContract(DemandPlanRecord plan)
    {
        // Fase 10 — o grafo provides/consumes REAL do plano: cada card provê o próprio código
        // estável e consome os códigos declarados em Dependencies. As ondas de despacho e as
        // barreiras de fan-in são derivadas deterministicamente — é o insumo do paralelismo
        // (quem pode rodar junto, quem espera quem) e a trava contra ciclo/dependência fantasma.
        var dependencyPlan = CardDependencyGraph.Build(
            plan.Cards.Select(card => new CardDependencyNode(
                DemandDecompositionPlanner.CodeOf(card.ProposedTitle),
                [DemandDecompositionPlanner.CodeOf(card.ProposedTitle)],
                card.Dependencies)).ToArray());

        return new DemandPlanContract(
            plan.Id, plan.ProjectId, plan.DemandId, plan.FeatureId, plan.Status,
            plan.Cards.Select(card => new ProposedCardContract(
                card.ProposedTitle, card.CardType, card.RequiredRole, card.Instruction, card.InScope,
                card.OutOfScope, card.AcceptanceCriteria, card.Gates, card.Dependencies)).ToArray(),
            plan.CreatedAt, plan.MaterializedAt,
            dependencyPlan.DispatchWaves,
            dependencyPlan.FanInBarriers.Select(barrier => new PlanFanInBarrierContract(
                barrier.ConsumerCardId, barrier.ProviderCardIds)).ToArray(),
            dependencyPlan.Issues.Select(issue => new PlanDependencyIssueContract(
                issue.Code, issue.CardId, issue.Resource, issue.RelatedCardIds)).ToArray());
    }

    private static bool Valid(string id) => UlidValue.TryParse(id, out _);
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", "ID must be a ULID.");
    private static IResult Invalid(string resource, string detail) => Problem(400, $"invalid_{resource}", detail);
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult ReferenceNotFound(string reference) => Problem(404, $"{reference}_not_found", "The referenced resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratePlanRequest(
    IReadOnlyList<string>? AcceptanceCriteria = null, PlanHintsPayload? Hints = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanHintsPayload(
    bool? HasFrontendSurface = null, bool? RequiresExternalCredential = null,
    bool? HasTechnicalUncertainty = null, bool? RequiresDecision = null);

public sealed record ProposedCardContract(
    string ProposedTitle, string CardType, string RequiredRole, string Instruction, string InScope,
    string OutOfScope, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<string> Gates,
    IReadOnlyList<string> Dependencies);

public sealed record PlanFanInBarrierContract(
    string ConsumerCard, IReadOnlyList<string> ProviderCards);

public sealed record PlanDependencyIssueContract(
    string Code, string? CardId, string? Resource, IReadOnlyList<string> RelatedCardIds);

public sealed record DemandPlanContract(
    string Id, string ProjectId, string DemandId, string FeatureId, string Status,
    IReadOnlyList<ProposedCardContract> Cards, DateTimeOffset CreatedAt,
    DateTimeOffset? MaterializedAt,
    IReadOnlyList<IReadOnlyList<string>> DispatchWaves,
    IReadOnlyList<PlanFanInBarrierContract> FanInBarriers,
    IReadOnlyList<PlanDependencyIssueContract> DependencyIssues);

public sealed record MaterializedCardResult(string ProposedTitle, string CardType, string? TaskId);

public sealed record MaterializePlanResult(
    string PlanId, string Status, bool AlreadyMaterialized, IReadOnlyList<MaterializedCardResult> Cards);
