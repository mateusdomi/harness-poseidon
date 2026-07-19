using Harness.Persistence.Abstractions.Workflows;

namespace Harness.UnitTests.Workflows;

public sealed class WorkflowConsistencyVerifierTests
{
    [Fact]
    public void HealthyRunningRunPassesDeterministicLayer()
    {
        var report = WorkflowConsistencyVerifier.VerifyDeterministic(HealthyRun());
        Assert.Equal(ConsistencyLayerState.Passed, report.State);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void TamperedStoredProgressIsDetected()
    {
        var run = HealthyRun() with { Approved = 100m };
        var report = WorkflowConsistencyVerifier.VerifyDeterministic(run);
        Assert.Equal(ConsistencyLayerState.Failed, report.State);
        Assert.Contains(report.Findings, finding => finding.Code == "progress_mismatch");
    }

    [Fact]
    public void TwoActivePhasesViolateTheInvariant()
    {
        var run = HealthyRun();
        var tampered = run with
        {
            Phases =
            [
                run.Phases[0],
                run.Phases[1] with { State = "active" },
            ],
        };
        var report = WorkflowConsistencyVerifier.VerifyDeterministic(tampered);
        Assert.Contains(report.Findings, finding => finding.Code == "active_phase_invariant");
    }

    [Fact]
    public void PassedGateWithoutRequirementsIsDetected()
    {
        var run = HealthyRun();
        var phase = run.Phases[0];
        var tampered = run with
        {
            Phases =
            [
                phase with
                {
                    Gates = [phase.Gates[0] with { State = "passed" }],
                },
                run.Phases[1],
            ],
        };
        var report = WorkflowConsistencyVerifier.VerifyDeterministic(tampered);
        Assert.Contains(report.Findings, finding => finding.Code == "gate_requirements_violated");
    }

    [Fact]
    public void StructuralLayerDetectsProjectionDrift()
    {
        var run = HealthyRun();
        var healthyProjection = Projection(run);
        Assert.Equal(
            ConsistencyLayerState.Passed,
            WorkflowConsistencyVerifier.VerifyStructural(
                run,
                healthyProjection.Run,
                healthyProjection.Phases,
                healthyProjection.Gates).State);

        var drifted = WorkflowConsistencyVerifier.VerifyStructural(
            run,
            healthyProjection.Run with { State = "completed" },
            healthyProjection.Phases,
            healthyProjection.Gates);
        Assert.Contains(drifted.Findings, finding => finding.Code == "run_state_drift");

        var missingPhase = WorkflowConsistencyVerifier.VerifyStructural(
            run,
            healthyProjection.Run,
            [healthyProjection.Phases[0]],
            healthyProjection.Gates);
        Assert.Contains(
            missingPhase.Findings,
            finding => finding.Code is "phase_count_drift" or "phase_missing_in_projection");

        var missingProjection = WorkflowConsistencyVerifier.VerifyStructural(run, null, [], []);
        Assert.Contains(
            missingProjection.Findings,
            finding => finding.Code == "projection_missing");
    }

    [Fact]
    public async Task SemanticLayerOnlyRunsWhenAuthorityLayersPass()
    {
        var run = HealthyRun();
        var projection = Projection(run);
        var reviewer = new DeterministicWorkflowConsistencyReviewer();
        var healthy = await WorkflowConsistencyVerifier.VerifyAsync(
            run,
            projection.Run,
            projection.Phases,
            projection.Gates,
            reviewer,
            CancellationToken.None);
        Assert.True(healthy.Consistent);
        Assert.Equal(
            ["passed", "passed", "passed"],
            healthy.Layers.Select(layer => layer.State.ToString().ToLowerInvariant()));

        var broken = await WorkflowConsistencyVerifier.VerifyAsync(
            run with { Approved = 100m },
            projection.Run,
            projection.Phases,
            projection.Gates,
            reviewer,
            CancellationToken.None);
        Assert.False(broken.Consistent);
        Assert.Equal(
            ConsistencyLayerState.Skipped,
            broken.Layers.Single(layer => layer.Layer == "semantic").State);
    }

    private static WorkflowRunAggregateSnapshot HealthyRun()
    {
        var now = DateTimeOffset.Parse("2026-07-19T00:00:00Z", null);
        WorkflowObjectiveRunSnapshot Objective(string key, string kind, string state) => new(
            $"or-{key}",
            $"od-{key}",
            key,
            key,
            kind,
            1m,
            state,
            1,
            now);
        return new WorkflowRunAggregateSnapshot(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            "running",
            3,
            now,
            now,
            null,
            25m,
            0m,
            0m,
            [
                new WorkflowPhaseRunSnapshot(
                    "pr-1",
                    "pd-1",
                    "phase-1",
                    "Fase 1",
                    1,
                    "active",
                    2,
                    now,
                    null,
                    [
                        Objective("work-1", "task", "executed"),
                        Objective("gate-objective-1", "gate", "pending"),
                    ],
                    [
                        new WorkflowGateRunSnapshot(
                            "gr-1",
                            "gd-1",
                            "gate-1",
                            "Gate 1",
                            "validated",
                            "pending",
                            1,
                            null,
                            ["work-1"]),
                    ]),
                new WorkflowPhaseRunSnapshot(
                    "pr-2",
                    "pd-2",
                    "phase-2",
                    "Fase 2",
                    2,
                    "pending",
                    1,
                    null,
                    null,
                    [
                        Objective("work-2", "task", "pending"),
                        Objective("gate-objective-2", "gate", "pending"),
                    ],
                    [
                        new WorkflowGateRunSnapshot(
                            "gr-2",
                            "gd-2",
                            "gate-2",
                            "Gate 2",
                            "validated",
                            "pending",
                            1,
                            null,
                            ["work-2"]),
                    ]),
            ]);
    }

    private static (
        WorkflowRunCatalogRecord Run,
        IReadOnlyList<WorkflowPhaseCatalogRecord> Phases,
        IReadOnlyList<WorkflowGateCatalogRecord> Gates) Projection(
        WorkflowRunAggregateSnapshot run) => (
        new WorkflowRunCatalogRecord(
            run.TenantId,
            run.RunId,
            "wf-1",
            run.DefinitionVersionId,
            run.State,
            run.StartedAt ?? run.CreatedAt,
            run.CompletedAt,
            run.Version),
        run.Phases
            .Select(phase => new WorkflowPhaseCatalogRecord(
                run.TenantId,
                phase.PhaseRunId,
                run.RunId,
                phase.Name,
                phase.Order,
                phase.State,
                phase.ActivatedAt,
                phase.CompletedAt))
            .ToArray(),
        run.Phases
            .SelectMany(phase => phase.Gates.Select(gate => new WorkflowGateCatalogRecord(
                run.TenantId,
                gate.GateRunId,
                phase.PhaseRunId,
                run.RunId,
                gate.Name,
                gate.State,
                RequiresApproval: true,
                null,
                gate.EvaluatedAt,
                null)))
            .ToArray());
}
