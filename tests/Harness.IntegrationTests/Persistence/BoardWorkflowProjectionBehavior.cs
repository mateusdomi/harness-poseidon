using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// Cenário provider-neutro das projeções do quadro (solicitação→demanda→tarefa,
/// movimento com matriz de estados, instrução imutável) e do catálogo de workflows
/// (binding com aceite de risco e troca de modo auditada).
/// </summary>
public static class BoardWorkflowProjectionBehavior
{
    public static async Task AssertAsync(
        IWorkBoardStore board,
        IWorkflowStore workflowAuthority,
        IWorkflowCatalogStore workflowCatalog,
        string tenantId,
        string projectId,
        string profileId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Solicitação → demanda → tarefa com backing interno preservado.
        var solicitationId = UlidValue.New(now).ToString();
        var solicitation = await board.CreateSolicitationAsync(
            new BoardSolicitationCreateCommand(
                tenantId, solicitationId, projectId, profileId, "request",
                "Paridade do quadro", "Comportamento idêntico nos dois providers.",
                null, now),
            cancellationToken);
        Assert.Equal("open", solicitation.State);
        var demandId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        var demand = await board.CreateDemandAsync(
            new BoardDemandCreateCommand(
                tenantId, demandId, projectId, solicitationId,
                UlidValue.New(now.AddMilliseconds(2)).ToString(), profileId,
                "Demanda do quadro", "Descrição da demanda.", "medium",
                now.AddMilliseconds(2)),
            cancellationToken);
        Assert.Equal(solicitationId, demand.SolicitationId);
        var taskId = UlidValue.New(now.AddMilliseconds(3)).ToString();
        var instructionId = UlidValue.New(now.AddMilliseconds(4)).ToString();
        var task = await board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                tenantId, taskId, projectId, demandId,
                UlidValue.New(now.AddMilliseconds(5)).ToString(),
                UlidValue.New(now.AddMilliseconds(6)).ToString(),
                profileId, "Tarefa do quadro", "medium", null, null,
                instructionId, "Instrução imutável v1.", now.AddMilliseconds(6)),
            cancellationToken);
        Assert.Equal("backlog", task.Task.State);
        Assert.Equal(1, task.Instruction.Version);

        // Movimento respeita a matriz; instrução única listável.
        var moved = await board.MoveTaskAsync(
            new BoardTaskMoveCommand(
                tenantId, taskId, "ready", "Priorizada.", "user",
                now.AddMilliseconds(7)),
            cancellationToken);
        Assert.Equal("ready", moved.State);
        Assert.Single(await board.ListInstructionsAsync(
            tenantId, taskId, null, 10, cancellationToken));

        // Catálogo de workflows: definição publicada pela autoridade projeta como template;
        // binding registra aceite; troca de modo grava nova aceitação.
        var draftTemplateId = UlidValue.New(now.AddMilliseconds(19)).ToString();
        var draft = await workflowCatalog.CreateTemplateAsync(
            new WorkflowTemplateCreateCommand(
                tenantId, draftTemplateId, "Rascunho provider-neutral", string.Empty,
                profileId, now.AddMilliseconds(19)),
            cancellationToken);
        Assert.Equal("draft", draft.State);
        Assert.Null(draft.CurrentVersionId);
        Assert.Equal("draft", (await workflowCatalog.GetTemplateAsync(
            tenantId, draftTemplateId, cancellationToken))!.State);

        var templateId = UlidValue.New(now.AddMilliseconds(20)).ToString();
        var versionId = UlidValue.New(now.AddMilliseconds(21)).ToString();
        var creation = Harness.Modules.Workflows.Application.WorkflowCatalogApplicationService
            .CreateTemplate(
                templateId,
                versionId,
                new Harness.Modules.Workflows.Contracts.CreateWorkflowTemplateRequest(
                    $"Paridade {templateId[^6..]}",
                    "Template do cenário dual-provider.",
                    ["Fase A", "Fase B"],
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                    {
                        ["Fase B"] = ["Aprovação de Homologação"],
                    }),
                now.AddMilliseconds(22));
        var phases = creation.Phases
            .Select(phase => new WorkflowPhaseCreateInput(
                phase.Id, phase.Key, phase.Name, phase.Order,
                phase.Objectives
                    .Select(objective => new WorkflowObjectiveCreateInput(
                        objective.Id, objective.Key, objective.Name, objective.Kind, objective.Weight))
                    .ToArray(),
                phase.Gates
                    .Select(gate => new WorkflowGateCreateInput(
                        gate.Id, gate.ObjectiveId, gate.Key, gate.Name,
                        gate.MinimumRequiredState, gate.RequiredObjectiveIds))
                    .ToArray()))
            .ToArray();
        await workflowAuthority.CreatePublishedDefinitionAsync(
            new(
                tenantId, creation.TemplateId, creation.Name, creation.VersionId, 1,
                WorkflowDefinitionContentHash.Compute(phases), phases,
                $"parity:workflow-template:{creation.TemplateId}",
                now.AddMilliseconds(22), creation.Description),
            cancellationToken);
        var template = await workflowCatalog.GetTemplateAsync(
            tenantId, creation.TemplateId, cancellationToken);
        Assert.NotNull(template);
        Assert.NotNull(template!.CurrentVersionId);
        var draftVersionId = UlidValue.New(now.AddMilliseconds(23)).ToString();
        var draftCreation = Harness.Modules.Workflows.Application.WorkflowCatalogApplicationService
            .CreateDraftVersion(
                template.Id,
                draftVersionId,
                new Harness.Modules.Workflows.Contracts.WorkflowDraftRequest(
                    ["Fase A", "Fase B"],
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                    {
                        ["Fase B"] = ["Aprovação de Homologação"],
                    }),
                now.AddMilliseconds(23));
        var draftPhases = draftCreation.Hierarchy.Phases
            .Select(phase => new WorkflowPhaseCreateInput(
                phase.Id, phase.Key, phase.Name, phase.Order,
                phase.Objectives
                    .Select(objective => new WorkflowObjectiveCreateInput(
                        objective.Id, objective.Key, objective.Name, objective.Kind, objective.Weight))
                    .ToArray(),
                phase.Gates
                    .Select(gate => new WorkflowGateCreateInput(
                        gate.Id, gate.ObjectiveId, gate.Key, gate.Name,
                        gate.MinimumRequiredState, gate.RequiredObjectiveIds))
                    .ToArray()))
            .ToArray();
        var draftVersion = await workflowCatalog.CreateDraftAsync(
            new WorkflowVersionDraftCreateCommand(
                tenantId, template.Id, draftVersionId, draftPhases, "{}", null, "{}", null,
                now.AddMilliseconds(23)),
            cancellationToken);
        Assert.Equal(2, draftVersion.Version);
        Assert.Equal("draft", draftVersion.State);
        Assert.Null(draftVersion.PublishedAt);
        Assert.Equal(template.CurrentVersionId, (await workflowCatalog.GetTemplateAsync(
            tenantId, template.Id, cancellationToken))!.CurrentVersionId);
        var workflowId = UlidValue.New(now.AddMilliseconds(8)).ToString();
        var binding = await workflowCatalog.CreateBindingAsync(
            new WorkflowBindingCreateCommand(
                tenantId, workflowId, projectId, template.Id, template.CurrentVersionId!,
                "semiautonomous", ["Aprovação de Homologação"], profileId,
                UlidValue.New(now.AddMilliseconds(9)).ToString(),
                "Aceite de risco da paridade dual.", now.AddMilliseconds(9)),
            cancellationToken);
        Assert.Equal("semiautonomous", binding.OperationMode);
        var autonomous = await workflowCatalog.SetOperationModeAsync(
            new WorkflowOperationModeCommand(
                tenantId, workflowId, "autonomous", [],
                UlidValue.New(now.AddMilliseconds(10)).ToString(), profileId,
                "Aceite para modo autônomo.", now.AddMilliseconds(10)),
            cancellationToken);
        Assert.Equal("autonomous", autonomous.OperationMode);
    }
}
