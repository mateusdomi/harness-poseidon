using Harness.Modules.Coordination.Contracts;
using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Coordination.Domain.Work;

public sealed record Solicitation(
    EntityId<SolicitationTag> Id,
    string TenantId,
    string ProjectId,
    string UserId,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record Demand(
    EntityId<DemandTag> Id,
    EntityId<SolicitationTag> SolicitationId,
    string Title,
    IReadOnlyList<string> AcceptanceCriteria,
    DateTimeOffset CreatedAt);

public sealed class WorkTask
{
    internal WorkTask(
        EntityId<WorkTaskTag> id,
        EntityId<DemandTag> demandId,
        string title,
        WorkRiskTier riskTier,
        decimal weight,
        DateTimeOffset createdAt)
    {
        Id = id;
        DemandId = demandId;
        Title = title;
        RiskTier = riskTier;
        Weight = weight;
        CreatedAt = createdAt;
        State = WorkTaskState.Draft;
    }

    public EntityId<WorkTaskTag> Id { get; }

    public EntityId<DemandTag> DemandId { get; }

    public string Title { get; }

    public WorkRiskTier RiskTier { get; }

    public decimal Weight { get; }

    public DateTimeOffset CreatedAt { get; }

    public WorkTaskState State { get; internal set; }
}

public sealed record InstructionVersion(
    EntityId<InstructionVersionTag> Id,
    EntityId<WorkTaskTag> TaskId,
    int Version,
    string Content,
    string ContentHash,
    EntityId<InstructionVersionTag>? SupersedesId,
    DateTimeOffset CreatedAt);

public sealed class WorkAttempt
{
    internal WorkAttempt(
        EntityId<WorkAttemptTag> id,
        EntityId<WorkTaskTag> taskId,
        EntityId<InstructionVersionTag> instructionVersionId,
        int number,
        string producerAgentId,
        DateTimeOffset startedAt)
    {
        Id = id;
        TaskId = taskId;
        InstructionVersionId = instructionVersionId;
        Number = number;
        ProducerAgentId = producerAgentId;
        StartedAt = startedAt;
        State = WorkAttemptState.Running;
        EvidenceReferences = [];
    }

    public EntityId<WorkAttemptTag> Id { get; }

    public EntityId<WorkTaskTag> TaskId { get; }

    public EntityId<InstructionVersionTag> InstructionVersionId { get; }

    public int Number { get; }

    public string ProducerAgentId { get; }

    public DateTimeOffset StartedAt { get; }

    public WorkAttemptState State { get; internal set; }

    public IReadOnlyList<string> EvidenceReferences { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }
}

public sealed record WorkReview(
    EntityId<WorkReviewTag> Id,
    EntityId<WorkAttemptTag> AttemptId,
    string ReviewerAgentId,
    ReviewDecision Decision,
    string Rationale,
    DateTimeOffset CreatedAt);
