using Harness.Modules.Workflows.Contracts;
using Harness.Modules.Workflows.Domain;
using Harness.SharedKernel.Time;

namespace Harness.UnitTests.Workflows;

public sealed class WorkflowAggregatesTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Fact]
    public void DefinitionVersionsAreImmutableOrderedAndPublishedIdempotently()
    {
        var clock = NewClock();
        var definition = WorkflowDefinitionAggregate.Create(TenantId, "Delivery", clock);
        var input = DefinitionInput().ToArray();
        var first = definition.CreateVersion(input);
        input[0] = input[0] with { Name = "Mutated caller input" };
        var second = definition.CreateVersion(DefinitionInput());

        var published = definition.Publish(first.Id);
        var replay = definition.Publish(first.Id);

        Assert.True(published.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal([1, 2], definition.Versions.Select(version => version.Version));
        Assert.Equal("Analysis", first.Phases[0].Name);
        Assert.Equal(WorkflowDefinitionVersionStatus.Published, first.Status);
        Assert.Equal(WorkflowDefinitionVersionStatus.Draft, second.Status);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.NotNull(first.PublishedAt);
    }

    [Fact]
    public void DefinitionRejectsDuplicateAndUnboundGateKeys()
    {
        var definition = WorkflowDefinitionAggregate.Create(TenantId, "Delivery", NewClock());
        var duplicate = DefinitionInput().ToArray();
        duplicate[1] = duplicate[1] with { Key = duplicate[0].Key };
        var unbound = DefinitionInput().ToArray();
        unbound[0] = unbound[0] with { Gates = [] };

        Assert.Throws<ArgumentException>(() => definition.CreateVersion(duplicate));
        Assert.Throws<ArgumentException>(() => definition.CreateVersion(unbound));
    }

    [Fact]
    public void RunRequiresPublishedVersionAndActivatesOnlyFirstPhase()
    {
        var (definition, version, clock) = CreateDefinition(publish: false);
        var rejected = WorkflowRunAggregate.Create(TenantId, ProjectId, version, clock);
        definition.Publish(version.Id);
        var run = WorkflowRunAggregate.Create(TenantId, ProjectId, version, clock).Value;

        var started = run.Start();

        Assert.Equal(WorkflowErrors.VersionNotPublished, rejected.Error);
        Assert.True(started.IsSuccess);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowPhaseRunState.Active, run.Phases[0].State);
        Assert.Equal(WorkflowPhaseRunState.Pending, run.Phases[1].State);
        Assert.Equal(new WorkflowProgress(0m, 0m, 0m), run.Progress);
        Assert.NotNull(run.StartedAt);
    }

    [Fact]
    public void ObjectivesAdvanceSequentiallyAndGateCannotBeBypassed()
    {
        var run = CreateStartedRun();

        var skipped = run.AdvanceObjective("analysis", "requirements", ObjectiveItemState.Validated);
        var gateBypass = run.AdvanceObjective("analysis", "analysis-gate", ObjectiveItemState.Executed);
        var executed = run.AdvanceObjective("analysis", "requirements", ObjectiveItemState.Executed);
        var validated = run.AdvanceObjective("analysis", "requirements", ObjectiveItemState.Validated);

        Assert.Equal(WorkflowErrors.ObjectiveTransitionInvalid, skipped.Error);
        Assert.Equal(WorkflowErrors.GateMustUseEvaluation, gateBypass.Error);
        Assert.True(executed.IsSuccess);
        Assert.True(validated.IsSuccess);
        Assert.Equal(ObjectiveItemState.Validated, run.Phases[0].ObjectiveItems[0].State);
    }

    [Fact]
    public void ProgressIsRecomputedFromObjectiveWeightsAndNeverSupplied()
    {
        var run = CreateStartedRun();
        AdvanceToValidated(run, "requirements");
        AdvanceToValidated(run, "tests");

        Assert.Equal(new WorkflowProgress(30m, 30m, 0m), run.Progress);

        var gate = run.EvaluateGate("analysis", "analysis-gate", passed: true);

        Assert.True(gate.IsSuccess);
        Assert.Equal(new WorkflowProgress(40m, 40m, 10m), run.Progress);
        Assert.Equal(ObjectiveItemState.Approved, run.Phases[0].ObjectiveItems[2].State);
    }

    [Fact]
    public void GateRequiresObjectiveEvidenceAndFailedGateCanBeReevaluated()
    {
        var run = CreateStartedRun();
        var tooEarly = run.EvaluateGate("analysis", "analysis-gate", passed: true);
        AdvanceToValidated(run, "requirements");
        AdvanceToValidated(run, "tests");
        var failed = run.EvaluateGate("analysis", "analysis-gate", passed: false);
        var retried = run.EvaluateGate("analysis", "analysis-gate", passed: true);

        Assert.Equal(WorkflowErrors.GateRequirementsNotMet, tooEarly.Error);
        Assert.True(failed.IsSuccess);
        Assert.True(retried.IsSuccess);
        Assert.Equal(WorkflowGateState.Passed, run.Phases[0].Gates[0].State);
    }

    [Fact]
    public void PhaseCompletionRequiresItemsAndGatesThenActivatesNextPhase()
    {
        var run = CreateStartedRun();
        AdvanceToValidated(run, "requirements");
        AdvanceToValidated(run, "tests");
        var blocked = run.CompleteActivePhase("analysis");
        run.EvaluateGate("analysis", "analysis-gate", passed: true);
        var completed = run.CompleteActivePhase("analysis");

        Assert.Equal(WorkflowErrors.PhaseCompletionBlocked, blocked.Error);
        Assert.True(completed.IsSuccess);
        Assert.Equal(WorkflowPhaseRunState.Completed, run.Phases[0].State);
        Assert.Equal(WorkflowPhaseRunState.Active, run.Phases[1].State);
        Assert.Equal(WorkflowRunState.Running, run.State);
    }

    [Fact]
    public void CompletingAllPhasesClosesRunAndProgressRemainsObjective()
    {
        var run = CreateStartedRun();
        CompleteAnalysis(run);
        AdvanceToApproved(run, "delivery", "implementation");
        AdvanceToApproved(run, "delivery", "human-approval");
        run.EvaluateGate("delivery", "delivery-gate", passed: true);
        var completed = run.CompleteActivePhase("delivery");

        Assert.True(completed.IsSuccess);
        Assert.Equal(WorkflowRunState.Completed, run.State);
        Assert.Equal(new WorkflowProgress(100m, 100m, 100m), run.Progress);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(WorkflowErrors.InvalidRunState, run.Resume().Error);
    }

    [Fact]
    public void PauseResumeAndCancelEnforceRunState()
    {
        var run = CreateStartedRun();
        var paused = run.Pause();
        var mutationWhilePaused = run.AdvanceObjective(
            "analysis", "requirements", ObjectiveItemState.Executed);
        var resumed = run.Resume();
        var cancelled = run.Cancel();

        Assert.True(paused.IsSuccess);
        Assert.Equal(WorkflowErrors.InvalidRunState, mutationWhilePaused.Error);
        Assert.True(resumed.IsSuccess);
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.Equal(WorkflowErrors.InvalidRunState, run.Start().Error);
    }

    private static WorkflowRunAggregate CreateStartedRun()
    {
        var (_, version, clock) = CreateDefinition(publish: true);
        var run = WorkflowRunAggregate.Create(TenantId, ProjectId, version, clock).Value;
        run.Start();
        return run;
    }

    private static (
        WorkflowDefinitionAggregate Definition,
        WorkflowDefinitionVersion Version,
        IncrementingClock Clock) CreateDefinition(bool publish)
    {
        var clock = NewClock();
        var definition = WorkflowDefinitionAggregate.Create(TenantId, "Delivery", clock);
        var version = definition.CreateVersion(DefinitionInput());
        if (publish)
        {
            definition.Publish(version.Id);
        }

        return (definition, version, clock);
    }

    private static IReadOnlyList<WorkflowPhaseDefinitionInput> DefinitionInput() =>
    [
        new(
            "analysis",
            "Analysis",
            [
                new("requirements", "Requirements", ObjectiveItemKind.Document, 2m),
                new("tests", "Tests", ObjectiveItemKind.Test, 1m),
                new("analysis-gate", "Analysis gate", ObjectiveItemKind.Gate, 1m),
            ],
            [
                new(
                    "analysis-gate",
                    "Analysis gate",
                    ["requirements", "tests"],
                    ObjectiveItemState.Validated),
            ]),
        new(
            "delivery",
            "Delivery",
            [
                new("implementation", "Implementation", ObjectiveItemKind.Task, 3m),
                new("human-approval", "Human approval", ObjectiveItemKind.Approval, 1m),
                new("delivery-gate", "Delivery gate", ObjectiveItemKind.Gate, 2m),
            ],
            [
                new(
                    "delivery-gate",
                    "Delivery gate",
                    ["implementation", "human-approval"],
                    ObjectiveItemState.Approved),
            ]),
    ];

    private static void AdvanceToValidated(WorkflowRunAggregate run, string objectiveKey)
    {
        run.AdvanceObjective("analysis", objectiveKey, ObjectiveItemState.Executed);
        run.AdvanceObjective("analysis", objectiveKey, ObjectiveItemState.Validated);
    }

    private static void AdvanceToApproved(
        WorkflowRunAggregate run,
        string phaseKey,
        string objectiveKey)
    {
        run.AdvanceObjective(phaseKey, objectiveKey, ObjectiveItemState.Executed);
        run.AdvanceObjective(phaseKey, objectiveKey, ObjectiveItemState.Validated);
        run.AdvanceObjective(phaseKey, objectiveKey, ObjectiveItemState.Approved);
    }

    private static void CompleteAnalysis(WorkflowRunAggregate run)
    {
        AdvanceToApproved(run, "analysis", "requirements");
        AdvanceToApproved(run, "analysis", "tests");
        run.EvaluateGate("analysis", "analysis-gate", passed: true);
        run.CompleteActivePhase("analysis");
    }

    private static IncrementingClock NewClock() => new(
        new DateTimeOffset(2026, 7, 18, 16, 30, 0, TimeSpan.Zero));

    private sealed class IncrementingClock(DateTimeOffset initial) : IClock
    {
        private DateTimeOffset _now = initial;

        public DateTimeOffset UtcNow
        {
            get
            {
                var current = _now;
                _now = _now.AddSeconds(1);
                return current;
            }
        }
    }
}
