using System.Collections.ObjectModel;
using Harness.Modules.Workflows.Contracts;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Results;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Workflows.Domain;

public sealed class WorkflowRunAggregate
{
    private readonly IClock _clock;
    private readonly List<WorkflowPhaseRun> _phases;

    private WorkflowRunAggregate(
        EntityId<WorkflowRunTag> id,
        string tenantId,
        string projectId,
        WorkflowDefinitionVersion definitionVersion,
        IClock clock)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        DefinitionVersionId = definitionVersion.Id;
        DefinitionVersion = definitionVersion.Version;
        DefinitionContentHash = definitionVersion.ContentHash;
        CreatedAt = clock.UtcNow;
        State = WorkflowRunState.Pending;
        _clock = clock;
        _phases = definitionVersion.Phases.Select(phase => new WorkflowPhaseRun(phase)).ToList();
        Phases = new ReadOnlyCollection<WorkflowPhaseRun>(_phases);
    }

    public EntityId<WorkflowRunTag> Id { get; }

    public string TenantId { get; }

    public string ProjectId { get; }

    public EntityId<WorkflowDefinitionVersionTag> DefinitionVersionId { get; }

    public int DefinitionVersion { get; }

    public string DefinitionContentHash { get; }

    public WorkflowRunState State { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<WorkflowPhaseRun> Phases { get; }

    public WorkflowProgress Progress => WorkflowProgressCalculator.Calculate(_phases);

    public static Result<WorkflowRunAggregate> Create(
        string tenantId,
        string projectId,
        WorkflowDefinitionVersion definitionVersion,
        IClock clock)
    {
        WorkflowValidation.ValidateUlid(tenantId, nameof(tenantId));
        WorkflowValidation.ValidateUlid(projectId, nameof(projectId));
        ArgumentNullException.ThrowIfNull(definitionVersion);
        ArgumentNullException.ThrowIfNull(clock);
        if (definitionVersion.Status != WorkflowDefinitionVersionStatus.Published)
        {
            return Result<WorkflowRunAggregate>.Failure(WorkflowErrors.VersionNotPublished);
        }

        return Result<WorkflowRunAggregate>.Success(new WorkflowRunAggregate(
            WorkflowIdFactory.New<WorkflowRunTag>(clock),
            tenantId,
            projectId,
            definitionVersion,
            clock));
    }

    public Result Start()
    {
        if (State != WorkflowRunState.Pending)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        State = WorkflowRunState.Running;
        StartedAt = _clock.UtcNow;
        _phases[0].Activate();
        return Result.Success();
    }

    public Result Pause()
    {
        if (State != WorkflowRunState.Running)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        State = WorkflowRunState.Paused;
        return Result.Success();
    }

    public Result Resume()
    {
        if (State != WorkflowRunState.Paused)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        State = WorkflowRunState.Running;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (State is WorkflowRunState.Completed or WorkflowRunState.Cancelled)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        State = WorkflowRunState.Cancelled;
        CompletedAt = _clock.UtcNow;
        return Result.Success();
    }

    public Result AdvanceObjective(
        string phaseKey,
        string objectiveKey,
        ObjectiveItemState targetState)
    {
        if (State != WorkflowRunState.Running)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        var phase = FindActivePhase(phaseKey);
        if (phase is null)
        {
            return Result.Failure(WorkflowErrors.PhaseNotActive);
        }

        var objective = phase.ObjectiveItems.SingleOrDefault(item => item.Key == objectiveKey);
        if (objective is null)
        {
            return Result.Failure(WorkflowErrors.ObjectiveNotFound);
        }

        if (objective.Kind == ObjectiveItemKind.Gate)
        {
            return Result.Failure(WorkflowErrors.GateMustUseEvaluation);
        }

        if (!Enum.IsDefined(targetState) || (int)targetState != (int)objective.State + 1)
        {
            return Result.Failure(WorkflowErrors.ObjectiveTransitionInvalid);
        }

        objective.Advance(targetState);
        return Result.Success();
    }

    public Result EvaluateGate(string phaseKey, string gateKey, bool passed)
    {
        if (State != WorkflowRunState.Running)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        var phase = FindActivePhase(phaseKey);
        if (phase is null)
        {
            return Result.Failure(WorkflowErrors.PhaseNotActive);
        }

        var gate = phase.Gates.SingleOrDefault(item => item.Key == gateKey);
        if (gate is null)
        {
            return Result.Failure(WorkflowErrors.GateNotFound);
        }

        if (gate.RequiredObjectiveKeys.Any(key =>
            phase.ObjectiveItems.Single(item => item.Key == key).State < gate.MinimumRequiredState))
        {
            return Result.Failure(WorkflowErrors.GateRequirementsNotMet);
        }

        gate.Evaluate(passed);
        if (passed)
        {
            phase.ObjectiveItems.Single(item => item.Key == gate.Key).PassGate();
        }

        return Result.Success();
    }

    public Result CompleteActivePhase(string phaseKey)
    {
        if (State != WorkflowRunState.Running)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        var phase = FindActivePhase(phaseKey);
        if (phase is null)
        {
            return Result.Failure(WorkflowErrors.PhaseNotActive);
        }

        if (phase.Gates.Any(gate => gate.State != WorkflowGateState.Passed) ||
            phase.ObjectiveItems.Any(item => item.State == ObjectiveItemState.Pending))
        {
            return Result.Failure(WorkflowErrors.PhaseCompletionBlocked);
        }

        phase.Complete();
        var nextPhase = _phases.FirstOrDefault(item => item.State == WorkflowPhaseRunState.Pending);
        if (nextPhase is null)
        {
            State = WorkflowRunState.Completed;
            CompletedAt = _clock.UtcNow;
        }
        else
        {
            nextPhase.Activate();
        }

        return Result.Success();
    }

    public Result RollbackActivePhase(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (State != WorkflowRunState.Running)
        {
            return Result.Failure(WorkflowErrors.InvalidRunState);
        }

        var activePhaseIndex = _phases.FindIndex(phase => phase.State == WorkflowPhaseRunState.Active);
        if (activePhaseIndex <= 0)
        {
            return Result.Failure(WorkflowErrors.NoPreviousPhaseToRollback);
        }

        _phases[activePhaseIndex].ResetToPending();
        _phases[activePhaseIndex - 1].Reactivate();

        return Result.Success();
    }

    private WorkflowPhaseRun? FindActivePhase(string phaseKey) =>
        _phases.SingleOrDefault(phase =>
            phase.Key == phaseKey && phase.State == WorkflowPhaseRunState.Active);
}

public sealed class WorkflowPhaseRun
{
    private readonly List<ObjectiveItemRun> _objectiveItems;
    private readonly List<WorkflowGateRun> _gates;

    internal WorkflowPhaseRun(WorkflowPhaseDefinition definition)
    {
        Key = definition.Key;
        Name = definition.Name;
        Order = definition.Order;
        State = WorkflowPhaseRunState.Pending;
        _objectiveItems = definition.ObjectiveItems.Select(item => new ObjectiveItemRun(item)).ToList();
        _gates = definition.Gates.Select(gate => new WorkflowGateRun(gate)).ToList();
        ObjectiveItems = new ReadOnlyCollection<ObjectiveItemRun>(_objectiveItems);
        Gates = new ReadOnlyCollection<WorkflowGateRun>(_gates);
    }

    public string Key { get; }

    public string Name { get; }

    public int Order { get; }

    public WorkflowPhaseRunState State { get; private set; }

    public IReadOnlyList<ObjectiveItemRun> ObjectiveItems { get; }

    public IReadOnlyList<WorkflowGateRun> Gates { get; }

    internal void Activate() => State = WorkflowPhaseRunState.Active;

    internal void Complete() => State = WorkflowPhaseRunState.Completed;

    internal void ResetToPending() => State = WorkflowPhaseRunState.Pending;

    internal void Reactivate() => State = WorkflowPhaseRunState.Active;
}

public sealed class ObjectiveItemRun
{
    internal ObjectiveItemRun(ObjectiveItemDefinition definition)
    {
        Key = definition.Key;
        Name = definition.Name;
        Kind = definition.Kind;
        Weight = definition.Weight;
        State = ObjectiveItemState.Pending;
    }

    public string Key { get; }

    public string Name { get; }

    public ObjectiveItemKind Kind { get; }

    public decimal Weight { get; }

    public ObjectiveItemState State { get; private set; }

    internal void Advance(ObjectiveItemState state) => State = state;

    internal void PassGate() => State = ObjectiveItemState.Approved;
}

public sealed class WorkflowGateRun
{
    internal WorkflowGateRun(WorkflowGateDefinition definition)
    {
        Key = definition.Key;
        Name = definition.Name;
        RequiredObjectiveKeys = definition.RequiredObjectiveKeys;
        MinimumRequiredState = definition.MinimumRequiredState;
        State = WorkflowGateState.Pending;
    }

    public string Key { get; }

    public string Name { get; }

    public IReadOnlyList<string> RequiredObjectiveKeys { get; }

    public ObjectiveItemState MinimumRequiredState { get; }

    public WorkflowGateState State { get; private set; }

    internal void Evaluate(bool passed) =>
        State = passed ? WorkflowGateState.Passed : WorkflowGateState.Failed;
}

internal static class WorkflowProgressCalculator
{
    public static WorkflowProgress Calculate(IReadOnlyList<WorkflowPhaseRun> phases)
    {
        var items = phases.SelectMany(phase => phase.ObjectiveItems).ToArray();
        var totalWeight = items.Sum(item => item.Weight);
        return new WorkflowProgress(
            Percentage(items, totalWeight, ObjectiveItemState.Executed),
            Percentage(items, totalWeight, ObjectiveItemState.Validated),
            Percentage(items, totalWeight, ObjectiveItemState.Approved));
    }

    private static decimal Percentage(
        IReadOnlyList<ObjectiveItemRun> items,
        decimal totalWeight,
        ObjectiveItemState threshold)
    {
        var achieved = items.Where(item => item.State >= threshold).Sum(item => item.Weight);
        return Math.Round(achieved * 100m / totalWeight, 2, MidpointRounding.AwayFromZero);
    }
}
