using Harness.Modules.Workflows.Contracts;
using Harness.Modules.Workflows.Domain;
using Harness.SharedKernel.Time;

namespace Harness.UnitTests.Workflows;

public sealed class WorkflowRollbackTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Fact]
    public void RollbackActivePhaseRollsBackToPreviousPhaseWhenInPhaseTwo()
    {
        var clock = new IncrementingClock(DateTimeOffset.UtcNow);
        var definition = WorkflowDefinitionAggregate.Create(TenantId, "Delivery", clock);
        var phases = new List<WorkflowPhaseDefinitionInput>
        {
            new("p1", "Phase 1", [new("req1", "Req", ObjectiveItemKind.Document, 1m)], []),
            new("p2", "Phase 2", [new("req2", "Req", ObjectiveItemKind.Document, 1m)], [])
        };

        var version = definition.CreateVersion(phases);
        definition.Publish(version.Id);

        var run = WorkflowRunAggregate.Create(TenantId, ProjectId, version, clock).Value;
        run.Start();

        // Advance objective in phase 1 & complete phase 1 -> phase 2 becomes active
        run.AdvanceObjective("p1", "req1", ObjectiveItemState.Executed);
        run.CompleteActivePhase("p1");
        Assert.Equal(WorkflowPhaseRunState.Active, run.Phases[1].State);

        // Rollback phase 2 -> phase 1 becomes active again
        var rollbackResult = run.RollbackActivePhase("Critic failed audit");
        Assert.True(rollbackResult.IsSuccess);
        Assert.Equal(WorkflowPhaseRunState.Active, run.Phases[0].State);
        Assert.Equal(WorkflowPhaseRunState.Pending, run.Phases[1].State);
    }

    [Fact]
    public void RollbackActivePhaseFailsWhenInFirstPhase()
    {
        var clock = new IncrementingClock(DateTimeOffset.UtcNow);
        var definition = WorkflowDefinitionAggregate.Create(TenantId, "Delivery", clock);
        var phases = new List<WorkflowPhaseDefinitionInput>
        {
            new("p1", "Phase 1", [new("req1", "Req", ObjectiveItemKind.Document, 1m)], [])
        };

        var version = definition.CreateVersion(phases);
        definition.Publish(version.Id);

        var run = WorkflowRunAggregate.Create(TenantId, ProjectId, version, clock).Value;
        run.Start();

        var result = run.RollbackActivePhase("Cannot rollback phase 1");
        Assert.False(result.IsSuccess);
        Assert.Equal("workflows.phase.noPreviousPhase", result.Error.Code);
    }

    private sealed class IncrementingClock(DateTimeOffset initial) : IClock
    {
        private DateTimeOffset _current = initial;

        public DateTimeOffset UtcNow
        {
            get
            {
                var now = _current;
                _current = _current.AddMinutes(1);
                return now;
            }
        }
    }
}
