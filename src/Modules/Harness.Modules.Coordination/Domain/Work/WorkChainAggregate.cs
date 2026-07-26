using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Harness.Modules.Coordination.Contracts;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Results;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Coordination.Domain.Work;

public sealed class WorkChainAggregate
{
    private readonly IClock _clock;
    private readonly List<Demand> _demands = [];
    private readonly List<WorkTask> _tasks = [];
    private readonly List<InstructionVersion> _instructions = [];
    private readonly List<WorkAttempt> _attempts = [];
    private readonly List<WorkReview> _reviews = [];
    private readonly WorkReviewCyclePolicy _reviewCyclePolicy;

    private WorkChainAggregate(
        Solicitation solicitation,
        IClock clock,
        WorkReviewCyclePolicy reviewCyclePolicy)
    {
        Solicitation = solicitation;
        _clock = clock;
        _reviewCyclePolicy = reviewCyclePolicy;
        Demands = new ReadOnlyCollection<Demand>(_demands);
        Tasks = new ReadOnlyCollection<WorkTask>(_tasks);
        Instructions = new ReadOnlyCollection<InstructionVersion>(_instructions);
        Attempts = new ReadOnlyCollection<WorkAttempt>(_attempts);
        Reviews = new ReadOnlyCollection<WorkReview>(_reviews);
    }

    public Solicitation Solicitation { get; }

    public IReadOnlyList<Demand> Demands { get; }

    public IReadOnlyList<WorkTask> Tasks { get; }

    public IReadOnlyList<InstructionVersion> Instructions { get; }

    public IReadOnlyList<WorkAttempt> Attempts { get; }

    public IReadOnlyList<WorkReview> Reviews { get; }

    public static WorkChainAggregate Create(
        string tenantId,
        string projectId,
        string userId,
        string content,
        IClock clock,
        int maximumReviewCycles)
    {
        ValidateUlid(tenantId, nameof(tenantId));
        ValidateUlid(projectId, nameof(projectId));
        ValidateUlid(userId, nameof(userId));
        ValidateText(content, nameof(content), 20_000);
        ArgumentNullException.ThrowIfNull(clock);
        return new WorkChainAggregate(
            new Solicitation(
                WorkChainIdFactory.New<SolicitationTag>(clock),
                tenantId,
                projectId,
                userId,
                content,
                clock.UtcNow),
            clock,
            new WorkReviewCyclePolicy(maximumReviewCycles));
    }

    public Demand CreateDemand(
        string title,
        IReadOnlyList<string> acceptanceCriteria)
    {
        ValidateText(title, nameof(title), 500);
        ArgumentNullException.ThrowIfNull(acceptanceCriteria);
        if (acceptanceCriteria.Count == 0 || acceptanceCriteria.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty acceptance criterion is required.", nameof(acceptanceCriteria));
        }

        var demand = new Demand(
            WorkChainIdFactory.New<DemandTag>(_clock),
            Solicitation.Id,
            title,
            acceptanceCriteria.ToArray(),
            _clock.UtcNow);
        _demands.Add(demand);
        return demand;
    }

    public Result<WorkTask> CreateTask(
        EntityId<DemandTag> demandId,
        string title,
        WorkRiskTier riskTier,
        decimal weight)
    {
        ValidateText(title, nameof(title), 500);
        if (!Enum.IsDefined(riskTier))
        {
            throw new ArgumentOutOfRangeException(nameof(riskTier));
        }

        if (weight <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Objective weight must be positive.");
        }

        if (_demands.All(demand => demand.Id != demandId))
        {
            return Result<WorkTask>.Failure(WorkChainErrors.DemandNotFound);
        }

        var task = new WorkTask(
            WorkChainIdFactory.New<WorkTaskTag>(_clock),
            demandId,
            title,
            riskTier,
            weight,
            _clock.UtcNow);
        _tasks.Add(task);
        return Result<WorkTask>.Success(task);
    }

    public Result<InstructionVersion> AddInstructionVersion(
        EntityId<WorkTaskTag> taskId,
        string content)
    {
        ValidateText(content, nameof(content), 100_000);
        var task = _tasks.SingleOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return Result<InstructionVersion>.Failure(WorkChainErrors.TaskNotFound);
        }

        if (task.State == WorkTaskState.Escalated)
        {
            return Result<InstructionVersion>.Failure(WorkChainErrors.ReplanningRequired);
        }

        if (_attempts.Any(attempt =>
            attempt.TaskId == taskId && attempt.State == WorkAttemptState.Running))
        {
            return Result<InstructionVersion>.Failure(WorkChainErrors.ActiveAttemptExists);
        }

        return Result<InstructionVersion>.Success(CreateInstructionVersion(task, content));
    }

    public Result<InstructionVersion> ReplanEscalatedTask(
        EntityId<WorkTaskTag> taskId,
        string content)
    {
        ValidateText(content, nameof(content), 100_000);
        var task = _tasks.SingleOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return Result<InstructionVersion>.Failure(WorkChainErrors.TaskNotFound);
        }

        if (task.State != WorkTaskState.Escalated)
        {
            return Result<InstructionVersion>.Failure(WorkChainErrors.TaskIsNotEscalated);
        }

        return Result<InstructionVersion>.Success(CreateInstructionVersion(task, content));
    }

    private InstructionVersion CreateInstructionVersion(WorkTask task, string content)
    {
        var previous = _instructions.LastOrDefault(instruction => instruction.TaskId == task.Id);
        var instruction = new InstructionVersion(
            WorkChainIdFactory.New<InstructionVersionTag>(_clock),
            task.Id,
            (previous?.Version ?? 0) + 1,
            content,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            previous?.Id,
            _clock.UtcNow);
        _instructions.Add(instruction);
        task.State = WorkTaskState.Ready;
        return instruction;
    }

    public Result<WorkAttempt> StartAttempt(
        EntityId<WorkTaskTag> taskId,
        EntityId<InstructionVersionTag> instructionVersionId,
        string producerAgentId)
    {
        ValidateText(producerAgentId, nameof(producerAgentId), 200);
        var task = _tasks.SingleOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return Result<WorkAttempt>.Failure(WorkChainErrors.TaskNotFound);
        }

        var instruction = _instructions.SingleOrDefault(candidate => candidate.Id == instructionVersionId);
        if (instruction is null || instruction.TaskId != taskId)
        {
            return Result<WorkAttempt>.Failure(WorkChainErrors.InstructionNotFound);
        }

        var latest = _instructions.Last(candidate => candidate.TaskId == taskId);
        if (latest.Id != instructionVersionId)
        {
            return Result<WorkAttempt>.Failure(WorkChainErrors.InstructionIsNotLatest);
        }

        if (_attempts.Any(candidate =>
            candidate.TaskId == taskId && candidate.State == WorkAttemptState.Running))
        {
            return Result<WorkAttempt>.Failure(WorkChainErrors.ActiveAttemptExists);
        }

        var previous = _attempts.LastOrDefault(candidate => candidate.TaskId == taskId);
        if (previous is { State: WorkAttemptState.Rejected } &&
            previous.InstructionVersionId == instructionVersionId)
        {
            return Result<WorkAttempt>.Failure(WorkChainErrors.CorrectionRequired);
        }

        var attempt = new WorkAttempt(
            WorkChainIdFactory.New<WorkAttemptTag>(_clock),
            taskId,
            instructionVersionId,
            (previous?.Number ?? 0) + 1,
            producerAgentId,
            _clock.UtcNow);
        _attempts.Add(attempt);
        task.State = WorkTaskState.Running;
        return Result<WorkAttempt>.Success(attempt);
    }

    public Result CompleteAttempt(
        EntityId<WorkAttemptTag> attemptId,
        IReadOnlyList<string> evidenceReferences)
    {
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        var attempt = _attempts.SingleOrDefault(candidate => candidate.Id == attemptId);
        if (attempt is null)
        {
            return Result.Failure(WorkChainErrors.AttemptNotFound);
        }

        if (attempt.State != WorkAttemptState.Running)
        {
            return Result.Failure(WorkChainErrors.InvalidAttemptState);
        }

        if (evidenceReferences.Count == 0 || evidenceReferences.Any(string.IsNullOrWhiteSpace))
        {
            return Result.Failure(WorkChainErrors.EvidenceRequired);
        }

        attempt.EvidenceReferences = evidenceReferences.ToArray();
        attempt.CompletedAt = _clock.UtcNow;
        attempt.State = WorkAttemptState.AwaitingReview;
        _tasks.Single(task => task.Id == attempt.TaskId).State = WorkTaskState.AwaitingReview;
        return Result.Success();
    }

    public Result ExpireAttemptLease(EntityId<WorkAttemptTag> attemptId)
    {
        var attempt = _attempts.SingleOrDefault(candidate => candidate.Id == attemptId);
        if (attempt is null)
        {
            return Result.Failure(WorkChainErrors.AttemptNotFound);
        }

        if (attempt.State != WorkAttemptState.Running)
        {
            return Result.Failure(WorkChainErrors.InvalidAttemptState);
        }

        attempt.State = WorkAttemptState.Abandoned;
        attempt.CompletedAt = _clock.UtcNow;
        _tasks.Single(task => task.Id == attempt.TaskId).State = WorkTaskState.Ready;
        return Result.Success();
    }

    public Result<WorkReview> ReviewAttempt(
        EntityId<WorkAttemptTag> attemptId,
        string reviewerAgentId,
        ReviewDecision decision,
        string rationale)
    {
        ValidateText(reviewerAgentId, nameof(reviewerAgentId), 200);
        ValidateText(rationale, nameof(rationale), 10_000);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        var attempt = _attempts.SingleOrDefault(candidate => candidate.Id == attemptId);
        if (attempt is null)
        {
            return Result<WorkReview>.Failure(WorkChainErrors.AttemptNotFound);
        }

        if (attempt.State != WorkAttemptState.AwaitingReview)
        {
            return Result<WorkReview>.Failure(
                _reviews.Any(review => review.AttemptId == attemptId)
                    ? WorkChainErrors.ReviewAlreadyRecorded
                    : WorkChainErrors.InvalidAttemptState);
        }

        var task = _tasks.Single(candidate => candidate.Id == attempt.TaskId);
        if (task.RiskTier >= WorkRiskTier.Medium &&
            string.Equals(attempt.ProducerAgentId, reviewerAgentId, StringComparison.Ordinal))
        {
            return Result<WorkReview>.Failure(WorkChainErrors.IndependentReviewerRequired);
        }

        var review = new WorkReview(
            WorkChainIdFactory.New<WorkReviewTag>(_clock),
            attemptId,
            reviewerAgentId,
            decision,
            rationale,
            _clock.UtcNow);
        _reviews.Add(review);
        if (decision == ReviewDecision.Approved)
        {
            attempt.State = WorkAttemptState.Approved;
            task.State = WorkTaskState.Approved;
        }
        else
        {
            attempt.State = WorkAttemptState.Rejected;
            var completedReviewCycles = _reviews.Count(candidate =>
                candidate.Decision == ReviewDecision.Rejected &&
                _attempts.Any(reviewedAttempt =>
                    reviewedAttempt.Id == candidate.AttemptId &&
                    reviewedAttempt.TaskId == task.Id));
            task.State = _reviewCyclePolicy
                .EvaluateRejectedReview(completedReviewCycles)
                .TargetState;
        }

        return Result<WorkReview>.Success(review);
    }

    public Result MergeApprovedTask(EntityId<WorkTaskTag> taskId)
    {
        var task = _tasks.SingleOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return Result.Failure(WorkChainErrors.TaskNotFound);
        }

        if (task.State != WorkTaskState.Approved)
        {
            return Result.Failure(WorkChainErrors.TaskIsNotApproved);
        }

        task.State = WorkTaskState.Merged;
        return Result.Success();
    }

    public Result CompleteMergedTask(EntityId<WorkTaskTag> taskId)
    {
        var task = _tasks.SingleOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return Result.Failure(WorkChainErrors.TaskNotFound);
        }

        if (task.State != WorkTaskState.Merged)
        {
            return Result.Failure(WorkChainErrors.TaskIsNotMerged);
        }

        task.State = WorkTaskState.Done;
        return Result.Success();
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }
    }

    private static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }
}
