using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Contracts;

namespace Harness.UnitTests.Coordination;

public sealed class WorkBoardApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateContractsApplyPublishedDefaultsAndKeepThreeProgressTracks()
    {
        var solicitation = WorkBoardApplicationService.CreateSolicitation(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new CreateSolicitationRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX", "request", " Pedido ", " Corpo "), Now);
        var demand = WorkBoardApplicationService.CreateDemand(
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            new CreateDemandRequest(solicitation.ProjectId, "Entrega", "Descrição", solicitation.Id,
                PhaseName: "  Execução  "), Now);
        var (task, instruction, cardType) = WorkBoardApplicationService.CreateTask(
            "01ARZ3NDEKTSV4RRFFQ69G5FAZ", "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            new CreateTaskRequest(demand.ProjectId, "Implementar", "Faça com testes", demand.Id,
                PhaseName: " Execução "), Now);

        Assert.Equal("agent_task", cardType);

        Assert.Equal("open", solicitation.State);
        Assert.Equal("medium", demand.Priority);
        Assert.Equal("Execução", demand.PhaseName);
        Assert.Equal("backlog", task.State);
        Assert.Equal("Execução", task.PhaseName);
        Assert.Equal((0m, 0m, 0m),
            (task.Progress.Executed, task.Progress.Validated, task.Progress.Approved));
        Assert.Equal(1, task.InstructionVersion);
        Assert.Equal("chief", instruction.AuthorKind);
        Assert.Null(instruction.AuthorId);
    }

    [Fact]
    public void InvalidEnumsIdsAndNonUtcDueDateAreRejected()
    {
        Assert.Throws<ArgumentException>(() => WorkBoardApplicationService.CreateSolicitation(
            "bad", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new CreateSolicitationRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX", "request", "Pedido", "Corpo"), Now));
        Assert.Throws<ArgumentException>(() => WorkBoardApplicationService.CreateDemand(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateDemandRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX", "Entrega", "Descrição", Priority: "urgent"), Now));
        Assert.Throws<ArgumentException>(() => WorkBoardApplicationService.CreateTask(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new CreateTaskRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX", "Implementar", "Instrução",
                DueAt: new DateTimeOffset(2026, 7, 18, 19, 0, 0, TimeSpan.FromHours(-3))), Now));
    }

    [Fact]
    public void BoardCommandsNormalizePublishedEnumsAndImmutableInstructionBody()
    {
        var move = WorkBoardApplicationService.MoveTask(
            new MoveTaskRequest("blocked", "  aguardando token  "));
        Assert.Equal(("blocked", "aguardando token"), move);
        Assert.Equal("critical", WorkBoardApplicationService.SetTaskPriority(
            new SetTaskPriorityRequest("critical")));
        Assert.Equal("correção", WorkBoardApplicationService.AppendInstruction(
            new AppendTaskInstructionRequest("  correção  ")));
        Assert.Equal("inAnalysis", WorkBoardApplicationService.TransitionSolicitation(
            new TransitionSolicitationRequest("inAnalysis")));
        Assert.Throws<ArgumentException>(() => WorkBoardApplicationService.MoveTask(
            new MoveTaskRequest("doing")));
        Assert.Throws<ArgumentException>(() => WorkBoardApplicationService.TransitionSolicitation(
            new TransitionSolicitationRequest("pending")));
    }

    [Fact]
    public void SolicitationAnalysisProducesFiveDeterministicPanelsAndValidatesAttachments()
    {
        var result = WorkBoardApplicationService.AnalyzeSolicitation(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new AnalyzeSolicitationRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX",
                "A API deve responder rápido. Não salvar segredo. Salvar segredo. Qual é o SLA?",
                ["contrato.pdf", "CONTRATO.pdf"]), Now);

        Assert.Equal("request", result.Solicitation.Kind);
        Assert.Equal("A API deve responder rápido.", result.Solicitation.Title);
        Assert.Contains(result.Requirements, value => value.Contains("contrato.pdf", StringComparison.Ordinal));
        Assert.Single(result.Ambiguities);
        Assert.Single(result.Contradictions);
        Assert.Contains("Qual é o SLA?", result.Questions);
        Assert.Equal(result.Requirements.Count, result.AcceptanceCriteria.Count);
        Assert.Throws<ArgumentException>(() => WorkBoardApplicationService.AnalyzeSolicitation(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new AnalyzeSolicitationRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX", "Pedido", ["../segredo.txt"]), Now));
    }
}
