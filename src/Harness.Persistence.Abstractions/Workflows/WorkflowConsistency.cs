namespace Harness.Persistence.Abstractions.Workflows;

public enum ConsistencyLayerState
{
    Passed,
    Failed,
    Skipped,
}

public sealed record ConsistencyFinding(string Code, string Detail);

public sealed record ConsistencyLayerReport(
    string Layer,
    ConsistencyLayerState State,
    IReadOnlyList<ConsistencyFinding> Findings);

public sealed record WorkflowConsistencyReport(
    string RunId,
    bool Consistent,
    IReadOnlyList<ConsistencyLayerReport> Layers);

public interface IWorkflowConsistencyReviewer
{
    Task<ConsistencyLayerReport> ReviewAsync(
        WorkflowRunAggregateSnapshot run,
        CancellationToken cancellationToken);
}

/// <summary>
/// Revisor semântico determinístico usado enquanto a camada LLM real não é
/// habilitada por configuração de provider: confirma heurísticas simples e
/// nunca contradiz as camadas determinística e estrutural, que são a autoridade.
/// </summary>
public sealed class DeterministicWorkflowConsistencyReviewer : IWorkflowConsistencyReviewer
{
    public Task<ConsistencyLayerReport> ReviewAsync(
        WorkflowRunAggregateSnapshot run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var findings = new List<ConsistencyFinding>();
        if (run.State == "completed" && run.Approved < 100m)
        {
            findings.Add(new ConsistencyFinding(
                "semantic_completion_without_full_approval",
                "O run está concluído sem aprovação integral dos itens ponderados."));
        }

        return Task.FromResult(new ConsistencyLayerReport(
            "semantic",
            findings.Count == 0 ? ConsistencyLayerState.Passed : ConsistencyLayerState.Failed,
            findings));
    }
}

public static class WorkflowConsistencyVerifier
{
    private static readonly Dictionary<string, int> ObjectiveRank =
        new(StringComparer.Ordinal)
        {
            ["pending"] = 0,
            ["executed"] = 1,
            ["validated"] = 2,
            ["approved"] = 3,
        };

    private static readonly HashSet<string> RunStates =
        new(["pending", "running", "paused", "completed", "cancelled"], StringComparer.Ordinal);

    private static readonly HashSet<string> PhaseStates =
        new(["pending", "active", "completed"], StringComparer.Ordinal);

    private static readonly HashSet<string> GateStates =
        new(["pending", "failed", "passed"], StringComparer.Ordinal);

    public static ConsistencyLayerReport VerifyDeterministic(WorkflowRunAggregateSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var findings = new List<ConsistencyFinding>();
        if (!RunStates.Contains(run.State))
        {
            findings.Add(new ConsistencyFinding(
                "invalid_run_state",
                $"O estado de run '{run.State}' está fora do conjunto fechado."));
        }

        foreach (var phase in run.Phases)
        {
            if (!PhaseStates.Contains(phase.State))
            {
                findings.Add(new ConsistencyFinding(
                    "invalid_phase_state",
                    $"A fase {phase.Key} carrega estado '{phase.State}' fora do conjunto fechado."));
            }

            foreach (var objective in phase.Objectives)
            {
                if (!ObjectiveRank.ContainsKey(objective.State))
                {
                    findings.Add(new ConsistencyFinding(
                        "invalid_objective_state",
                        $"O objetivo {objective.Key} carrega estado '{objective.State}' fora do conjunto fechado."));
                }
            }

            foreach (var gate in phase.Gates)
            {
                if (!GateStates.Contains(gate.State))
                {
                    findings.Add(new ConsistencyFinding(
                        "invalid_gate_state",
                        $"O gate {gate.Key} carrega estado '{gate.State}' fora do conjunto fechado."));
                }
            }
        }

        var activePhases = run.Phases.Count(phase => phase.State == "active");
        if (run.State is "running" or "paused" && activePhases != 1)
        {
            findings.Add(new ConsistencyFinding(
                "active_phase_invariant",
                $"Um run '{run.State}' exige exatamente uma fase ativa; foram encontradas {activePhases}."));
        }

        if (run.State == "completed" &&
            run.Phases.Any(phase => phase.State != "completed"))
        {
            findings.Add(new ConsistencyFinding(
                "completed_run_with_open_phase",
                "O run concluído ainda possui fase não concluída."));
        }

        foreach (var phase in run.Phases.Where(phase => phase.State == "completed"))
        {
            if (phase.Objectives.Any(objective => objective.State == "pending") ||
                phase.Gates.Any(gate => gate.State != "passed"))
            {
                findings.Add(new ConsistencyFinding(
                    "phase_completion_invariant",
                    $"A fase {phase.Key} está concluída com pendências ou gates não aprovados."));
            }
        }

        foreach (var phase in run.Phases)
        {
            foreach (var gate in phase.Gates.Where(gate => gate.State == "passed"))
            {
                var objectivesByKey = phase.Objectives.ToDictionary(
                    objective => objective.Key,
                    StringComparer.Ordinal);
                var minimum = ObjectiveRank.GetValueOrDefault(gate.MinimumRequiredState, int.MaxValue);
                var unmet = gate.RequiredObjectiveKeys.Where(key =>
                    !objectivesByKey.TryGetValue(key, out var objective) ||
                    ObjectiveRank.GetValueOrDefault(objective.State, -1) < minimum);
                if (unmet.Any())
                {
                    findings.Add(new ConsistencyFinding(
                        "gate_requirements_violated",
                        $"O gate {gate.Key} está aprovado sem os requisitos mínimos atendidos."));
                }
            }
        }

        var recomputed = RecomputeProgress(run);
        if (Math.Abs(recomputed.Executed - run.Executed) > 0.01m ||
            Math.Abs(recomputed.Validated - run.Validated) > 0.01m ||
            Math.Abs(recomputed.Approved - run.Approved) > 0.01m)
        {
            findings.Add(new ConsistencyFinding(
                "progress_mismatch",
                $"Progresso armazenado ({run.Executed}/{run.Validated}/{run.Approved}) diverge do " +
                $"recomputado ({recomputed.Executed}/{recomputed.Validated}/{recomputed.Approved})."));
        }

        return new ConsistencyLayerReport(
            "deterministic",
            findings.Count == 0 ? ConsistencyLayerState.Passed : ConsistencyLayerState.Failed,
            findings);
    }

    public static ConsistencyLayerReport VerifyStructural(
        WorkflowRunAggregateSnapshot run,
        WorkflowRunCatalogRecord? projectedRun,
        IReadOnlyList<WorkflowPhaseCatalogRecord> projectedPhases,
        IReadOnlyList<WorkflowGateCatalogRecord> projectedGates)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(projectedPhases);
        ArgumentNullException.ThrowIfNull(projectedGates);
        var findings = new List<ConsistencyFinding>();
        if (projectedRun is null)
        {
            findings.Add(new ConsistencyFinding(
                "projection_missing",
                "A projeção de catálogo do run não existe."));
            return new ConsistencyLayerReport("structural", ConsistencyLayerState.Failed, findings);
        }

        if (!string.Equals(projectedRun.State, run.State, StringComparison.Ordinal))
        {
            findings.Add(new ConsistencyFinding(
                "run_state_drift",
                $"A projeção reporta '{projectedRun.State}' e a autoridade '{run.State}'."));
        }

        if (projectedPhases.Count != run.Phases.Count)
        {
            findings.Add(new ConsistencyFinding(
                "phase_count_drift",
                $"A projeção possui {projectedPhases.Count} fases e a autoridade {run.Phases.Count}."));
        }

        var projectedByOrder = projectedPhases.ToDictionary(phase => phase.Order);
        foreach (var phase in run.Phases)
        {
            if (!projectedByOrder.TryGetValue(phase.Order, out var projected))
            {
                findings.Add(new ConsistencyFinding(
                    "phase_missing_in_projection",
                    $"A fase {phase.Key} (ordem {phase.Order}) não existe na projeção."));
                continue;
            }

            if (!string.Equals(projected.State, phase.State, StringComparison.Ordinal))
            {
                findings.Add(new ConsistencyFinding(
                    "phase_state_drift",
                    $"A fase {phase.Key} está '{phase.State}' na autoridade e '{projected.State}' na projeção."));
            }
        }

        var authorityGates = run.Phases.SelectMany(phase => phase.Gates).ToArray();
        if (projectedGates.Count != authorityGates.Length)
        {
            findings.Add(new ConsistencyFinding(
                "gate_count_drift",
                $"A projeção possui {projectedGates.Count} gates e a autoridade {authorityGates.Length}."));
        }

        var projectedGateStates = projectedGates
            .GroupBy(gate => gate.State, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var authorityGateStates = authorityGates
            .GroupBy(gate => gate.State, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (projectedGateStates.Count != authorityGateStates.Count ||
            authorityGateStates.Any(pair =>
                projectedGateStates.GetValueOrDefault(pair.Key) != pair.Value))
        {
            findings.Add(new ConsistencyFinding(
                "gate_state_drift",
                "A distribuição de estados de gate diverge entre autoridade e projeção."));
        }

        return new ConsistencyLayerReport(
            "structural",
            findings.Count == 0 ? ConsistencyLayerState.Passed : ConsistencyLayerState.Failed,
            findings);
    }

    public static async Task<WorkflowConsistencyReport> VerifyAsync(
        WorkflowRunAggregateSnapshot run,
        WorkflowRunCatalogRecord? projectedRun,
        IReadOnlyList<WorkflowPhaseCatalogRecord> projectedPhases,
        IReadOnlyList<WorkflowGateCatalogRecord> projectedGates,
        IWorkflowConsistencyReviewer reviewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        var deterministic = VerifyDeterministic(run);
        var structural = VerifyStructural(run, projectedRun, projectedPhases, projectedGates);
        var semantic = deterministic.State == ConsistencyLayerState.Passed &&
            structural.State == ConsistencyLayerState.Passed
            ? await reviewer.ReviewAsync(run, cancellationToken)
            : new ConsistencyLayerReport(
                "semantic",
                ConsistencyLayerState.Skipped,
                [new ConsistencyFinding(
                    "semantic_skipped",
                    "A revisão semântica só executa com as camadas determinística e estrutural verdes.")]);
        var layers = new[] { deterministic, structural, semantic };
        return new WorkflowConsistencyReport(
            run.RunId,
            layers.All(layer => layer.State != ConsistencyLayerState.Failed),
            layers);
    }

    private static (decimal Executed, decimal Validated, decimal Approved) RecomputeProgress(
        WorkflowRunAggregateSnapshot run)
    {
        var objectives = run.Phases.SelectMany(phase => phase.Objectives).ToArray();
        var totalWeight = objectives.Sum(objective => objective.Weight);
        if (totalWeight <= 0)
        {
            return (0m, 0m, 0m);
        }

        decimal Share(int minimumRank) => Math.Round(
            objectives
                .Where(objective =>
                    ObjectiveRank.GetValueOrDefault(objective.State, -1) >= minimumRank)
                .Sum(objective => objective.Weight) / totalWeight * 100m,
            2);
        return (Share(1), Share(2), Share(3));
    }
}
