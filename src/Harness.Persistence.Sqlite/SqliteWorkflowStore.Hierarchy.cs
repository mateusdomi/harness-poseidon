using Harness.Persistence.Abstractions.Workflows;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkflowStore
{
    public Task<WorkflowRunAggregateSnapshot?> ReadRunAggregateAsync(
        string tenantId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(runId, nameof(runId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadRunAggregateCoreAsync(connection, tenantId, runId, token),
            cancellationToken);
    }

    private static async Task<WorkflowRunAggregateSnapshot?> ReadRunAggregateCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string runId,
        CancellationToken cancellationToken)
    {
        RunAggregateRow? run;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                """
                SELECT tenant_id,project_id,definition_version_id,id,state,version,
                       created_at,started_at,completed_at
                FROM workflow_runs WHERE tenant_id=$tenantId AND id=$runId;
                """;
            Add(query, "$tenantId", tenantId);
            Add(query, "$runId", runId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            run = await reader.ReadAsync(cancellationToken)
                ? new RunAggregateRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    ParseRunTimestamp(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : ParseRunTimestamp(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : ParseRunTimestamp(reader.GetString(8)))
                : null;
        }

        if (run is null)
        {
            return null;
        }

        var phases = new List<WorkflowPhaseRunSnapshot>();
        var phaseRows = await ReadPhaseRowsAsync(connection, runId, cancellationToken);
        foreach (var phase in phaseRows)
        {
            var objectives = await ReadObjectiveRowsAsync(
                connection, phase.PhaseRunId, cancellationToken);
            var gates = await ReadGateRowsAsync(connection, phase.PhaseRunId, cancellationToken);
            phases.Add(new WorkflowPhaseRunSnapshot(
                phase.PhaseRunId,
                phase.PhaseDefinitionId,
                phase.Key,
                phase.Name,
                phase.Order,
                phase.State,
                phase.Version,
                phase.ActivatedAt,
                phase.CompletedAt,
                objectives,
                gates));
        }

        var progress = CalculateRunProgress(phases);
        return new WorkflowRunAggregateSnapshot(
            run.TenantId,
            run.ProjectId,
            run.DefinitionVersionId,
            run.RunId,
            run.State,
            run.Version,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            progress.Executed,
            progress.Validated,
            progress.Approved,
            phases);
    }

    private static async Task<List<PhaseAggregateRow>> ReadPhaseRowsAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        var result = new List<PhaseAggregateRow>();
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT r.id,r.phase_definition_id,d.phase_key,d.name,r.phase_order,
                   r.state,r.version,r.activated_at,r.completed_at
            FROM workflow_phase_runs r
            JOIN workflow_phase_definitions d ON d.id=r.phase_definition_id
            WHERE r.workflow_run_id=$runId ORDER BY r.phase_order;
            """;
        Add(query, "$runId", runId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PhaseAggregateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : ParseRunTimestamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseRunTimestamp(reader.GetString(8))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<WorkflowObjectiveRunSnapshot>> ReadObjectiveRowsAsync(
        SqliteConnection connection,
        string phaseRunId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowObjectiveRunSnapshot>();
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT r.id,r.objective_definition_id,d.objective_key,d.name,d.kind,d.weight,
                   r.state,r.version,r.updated_at
            FROM workflow_objective_runs r
            JOIN workflow_objective_definitions d ON d.id=r.objective_definition_id
            WHERE r.phase_run_id=$phaseRunId ORDER BY d.objective_key;
            """;
        Add(query, "$phaseRunId", phaseRunId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkflowObjectiveRunSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetString(6),
                reader.GetInt64(7),
                ParseRunTimestamp(reader.GetString(8))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<WorkflowGateRunSnapshot>> ReadGateRowsAsync(
        SqliteConnection connection,
        string phaseRunId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowGateRunSnapshot>();
        var rows = new List<GateAggregateRow>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                """
                SELECT r.id,r.gate_definition_id,d.gate_key,d.name,d.minimum_required_state,
                       r.state,r.version,r.evaluated_at
                FROM workflow_gate_runs r
                JOIN workflow_gate_definitions d ON d.id=r.gate_definition_id
                WHERE r.phase_run_id=$phaseRunId ORDER BY d.gate_key;
                """;
            Add(query, "$phaseRunId", phaseRunId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new GateAggregateRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : ParseRunTimestamp(reader.GetString(7))));
            }
        }

        foreach (var row in rows)
        {
            var requirements = new List<string>();
            await using var query = connection.CreateCommand();
            query.CommandText =
                """
                SELECT d.objective_key FROM workflow_gate_requirements r
                JOIN workflow_objective_definitions d ON d.id=r.objective_definition_id
                WHERE r.gate_definition_id=$gateId ORDER BY r.requirement_order;
                """;
            Add(query, "$gateId", row.GateDefinitionId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                requirements.Add(reader.GetString(0));
            }

            result.Add(new WorkflowGateRunSnapshot(
                row.GateRunId,
                row.GateDefinitionId,
                row.Key,
                row.Name,
                row.MinimumRequiredState,
                row.State,
                row.Version,
                row.EvaluatedAt,
                requirements));
        }

        return result;
    }

    private static (decimal Executed, decimal Validated, decimal Approved) CalculateRunProgress(
        IReadOnlyList<WorkflowPhaseRunSnapshot> phases)
    {
        var objectives = phases.SelectMany(phase => phase.Objectives).ToArray();
        var total = objectives.Sum(objective => objective.Weight);
        if (total <= 0m)
        {
            throw new InvalidOperationException("A workflow run requires positive objective weight.");
        }

        return (
            Percentage(objectives, total, "executed"),
            Percentage(objectives, total, "validated"),
            Percentage(objectives, total, "approved"));
    }

    private static decimal Percentage(
        IReadOnlyList<WorkflowObjectiveRunSnapshot> objectives,
        decimal total,
        string threshold)
    {
        var minimum = ObjectiveStateRank(threshold);
        var achieved = objectives
            .Where(objective => ObjectiveStateRank(objective.State) >= minimum)
            .Sum(objective => objective.Weight);
        return Math.Round(achieved * 100m / total, 2, MidpointRounding.AwayFromZero);
    }

    private static int ObjectiveStateRank(string state) =>
        state switch
        {
            "pending" => 0,
            "executed" => 1,
            "validated" => 2,
            "approved" => 3,
            _ => throw new InvalidOperationException("Persisted objective state is invalid."),
        };

    private sealed record RunAggregateRow(
        string TenantId,
        string ProjectId,
        string DefinitionVersionId,
        string RunId,
        string State,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt);

    private sealed record PhaseAggregateRow(
        string PhaseRunId,
        string PhaseDefinitionId,
        string Key,
        string Name,
        int Order,
        string State,
        long Version,
        DateTimeOffset? ActivatedAt,
        DateTimeOffset? CompletedAt);

    private sealed record GateAggregateRow(
        string GateRunId,
        string GateDefinitionId,
        string Key,
        string Name,
        string MinimumRequiredState,
        string State,
        long Version,
        DateTimeOffset? EvaluatedAt);
}
