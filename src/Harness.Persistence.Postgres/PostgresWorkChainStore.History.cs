using System.Data;
using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkChainStore
{
    public async Task<WorkChainAggregateSnapshot?> ReadAggregateAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(solicitationId, nameof(solicitationId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await using var header = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT tenant_id, project_id, user_id, id, content, created_at
            FROM harness.solicitations WHERE tenant_id = $1 AND id = $2;
            """,
            Text(tenantId),
            Text(solicitationId));
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var projectId = TrimId(reader.GetString(1));
        var userId = TrimId(reader.GetString(2));
        var content = reader.GetString(4);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(5);
        await reader.DisposeAsync();

        var demands = await ReadDemandsAsync(
            connection, transaction, tenantId, projectId, solicitationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WorkChainAggregateSnapshot(
            tenantId, projectId, userId, solicitationId, content, createdAt, demands);
    }

    private static async Task<IReadOnlyList<WorkDemandSnapshot>> ReadDemandsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string solicitationId,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Title, string Criteria, DateTimeOffset CreatedAt)>();
        await using (var query = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT id, title, acceptance_criteria_json::text, created_at
            FROM harness.demands
            WHERE tenant_id = $1 AND project_id = $2 AND solicitation_id = $3
            ORDER BY created_at, id;
            """,
            Text(tenantId), Text(projectId), Text(solicitationId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    TrimId(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        var demands = new List<WorkDemandSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var criteria = JsonSerializer.Deserialize<string[]>(row.Criteria)
                ?? throw new InvalidOperationException("Persisted acceptance criteria are invalid.");
            var tasks = await ReadTasksAsync(
                connection, transaction, tenantId, projectId, row.Id, cancellationToken);
            demands.Add(new WorkDemandSnapshot(row.Id, row.Title, criteria, row.CreatedAt, tasks));
        }

        return demands;
    }

    private static async Task<IReadOnlyList<WorkTaskAggregateSnapshot>> ReadTasksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string demandId,
        CancellationToken cancellationToken)
    {
        var rows = new List<TaskHistoryRow>();
        await using (var query = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT id, title, risk_tier, weight, state, version, created_at, updated_at
            FROM harness.work_tasks
            WHERE tenant_id = $1 AND project_id = $2 AND demand_id = $3
            ORDER BY created_at, id;
            """,
            Text(tenantId), Text(projectId), Text(demandId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new TaskHistoryRow(
                    TrimId(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetDecimal(3), reader.GetString(4), reader.GetInt64(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        var tasks = new List<WorkTaskAggregateSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var instructions = await ReadInstructionsAsync(
                connection, transaction, row.Id, cancellationToken);
            var attempts = await ReadAttemptsAsync(
                connection, transaction, row.Id, cancellationToken);
            tasks.Add(new WorkTaskAggregateSnapshot(
                row.Id, row.Title, row.RiskTier, row.Weight, row.State, row.Version,
                row.CreatedAt, row.UpdatedAt, instructions, attempts));
        }

        return tasks;
    }

    private static async Task<IReadOnlyList<WorkInstructionVersionSnapshot>> ReadInstructionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkInstructionVersionSnapshot>();
        await using var query = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT id, version, content, content_hash, supersedes_id, created_at
            FROM harness.instruction_versions WHERE task_id = $1 ORDER BY version;
            """,
            Text(taskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkInstructionVersionSnapshot(
                TrimId(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                TrimId(reader.GetString(3)),
                reader.IsDBNull(4) ? null : TrimId(reader.GetString(4)),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<WorkAttemptSnapshot>> ReadAttemptsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        var rows = new List<AttemptHistoryRow>();
        await using (var query = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT id, instruction_version_id, attempt_number, producer_agent_id,
                   CASE
                       WHEN operational_state='cancelled' AND state='rejected'
                            AND failure_reason IS NULL THEN 'abandoned'
                       WHEN operational_state='cancelled' AND state='rejected'
                            THEN 'cancelled'
                       ELSE state
                   END,
                   started_at, completed_at
            FROM harness.work_attempts WHERE task_id = $1 ORDER BY attempt_number;
            """,
            Text(taskId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new AttemptHistoryRow(
                    TrimId(reader.GetString(0)), TrimId(reader.GetString(1)), reader.GetInt32(2),
                    reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        var attempts = new List<WorkAttemptSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var evidence = await ReadEvidenceAsync(
                connection, transaction, row.Id, cancellationToken);
            var review = await ReadReviewAsync(
                connection, transaction, row.Id, cancellationToken);
            attempts.Add(new WorkAttemptSnapshot(
                row.Id, row.InstructionVersionId, row.Number, row.ProducerAgentId, row.State,
                row.StartedAt, row.CompletedAt, evidence, review));
        }

        return attempts;
    }

    private static async Task<IReadOnlyList<WorkEvidenceSnapshot>> ReadEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkEvidenceSnapshot>();
        await using var query = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT id, ordinal, reference, created_at
            FROM harness.work_evidence WHERE attempt_id = $1 ORDER BY ordinal;
            """,
            Text(attemptId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkEvidenceSnapshot(
                TrimId(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return result;
    }

    private static async Task<WorkReviewSnapshot?> ReadReviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var query = CreateHistoryQuery(
            connection,
            transaction,
            """
            SELECT id, reviewer_agent_id, decision, rationale, rejection_cause, created_at
            FROM harness.work_reviews WHERE attempt_id = $1;
            """,
            Text(attemptId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorkReviewSnapshot(
                TrimId(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(5))
            {
                RejectionCause = reader.IsDBNull(4) ? "none" : reader.GetString(4),
            }
            : null;
    }

    private static NpgsqlCommand CreateHistoryQuery(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static string TrimId(string value) => value.TrimEnd();

    private sealed record TaskHistoryRow(
        string Id,
        string Title,
        string RiskTier,
        decimal Weight,
        string State,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record AttemptHistoryRow(
        string Id,
        string InstructionVersionId,
        int Number,
        string ProducerAgentId,
        string State,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt);
}
