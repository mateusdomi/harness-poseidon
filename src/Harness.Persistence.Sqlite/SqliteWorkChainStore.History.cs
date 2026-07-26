using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkChainStore
{
    public Task<WorkChainAggregateSnapshot?> ReadAggregateAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(solicitationId, nameof(solicitationId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadAggregateCoreAsync(
                connection, tenantId, solicitationId, token),
            cancellationToken);
    }

    private static async Task<WorkChainAggregateSnapshot?> ReadAggregateCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var header = CreateQuery(
            connection,
            transaction,
            """
            SELECT tenant_id, project_id, user_id, id, content, created_at
            FROM solicitations WHERE tenant_id = $tenantId AND id = $solicitationId;
            """,
            ("$tenantId", tenantId),
            ("$solicitationId", solicitationId));
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var projectId = reader.GetString(1);
        var userId = reader.GetString(2);
        var content = reader.GetString(4);
        var createdAt = ParseHistoryTimestamp(reader.GetString(5));
        await reader.DisposeAsync();

        var demands = await ReadDemandsAsync(
            connection, transaction, tenantId, projectId, solicitationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WorkChainAggregateSnapshot(
            tenantId, projectId, userId, solicitationId, content, createdAt, demands);
    }

    private static async Task<IReadOnlyList<WorkDemandSnapshot>> ReadDemandsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string projectId,
        string solicitationId,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Title, string Criteria, DateTimeOffset CreatedAt)>();
        await using (var query = CreateQuery(
            connection,
            transaction,
            """
            SELECT id, title, acceptance_criteria_json, created_at
            FROM demands
            WHERE tenant_id = $tenantId AND project_id = $projectId
              AND solicitation_id = $solicitationId
            ORDER BY created_at, id;
            """,
            ("$tenantId", tenantId),
            ("$projectId", projectId),
            ("$solicitationId", solicitationId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    ParseHistoryTimestamp(reader.GetString(3))));
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string projectId,
        string demandId,
        CancellationToken cancellationToken)
    {
        var rows = new List<TaskHistoryRow>();
        await using (var query = CreateQuery(
            connection,
            transaction,
            """
            SELECT id, title, risk_tier, weight, state, version, created_at, updated_at
            FROM work_tasks
            WHERE tenant_id = $tenantId AND project_id = $projectId AND demand_id = $demandId
            ORDER BY created_at, id;
            """,
            ("$tenantId", tenantId),
            ("$projectId", projectId),
            ("$demandId", demandId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new TaskHistoryRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetDecimal(3), reader.GetString(4), reader.GetInt64(5),
                    ParseHistoryTimestamp(reader.GetString(6)),
                    ParseHistoryTimestamp(reader.GetString(7))));
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkInstructionVersionSnapshot>();
        await using var query = CreateQuery(
            connection,
            transaction,
            """
            SELECT id, version, content, content_hash, supersedes_id, created_at
            FROM instruction_versions WHERE task_id = $taskId ORDER BY version;
            """,
            ("$taskId", taskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkInstructionVersionSnapshot(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseHistoryTimestamp(reader.GetString(5))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<WorkAttemptSnapshot>> ReadAttemptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        var rows = new List<AttemptHistoryRow>();
        await using (var query = CreateQuery(
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
            FROM work_attempts WHERE task_id = $taskId ORDER BY attempt_number;
            """,
            ("$taskId", taskId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new AttemptHistoryRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetString(4),
                    ParseHistoryTimestamp(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : ParseHistoryTimestamp(reader.GetString(6))));
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkEvidenceSnapshot>();
        await using var query = CreateQuery(
            connection,
            transaction,
            """
            SELECT id, ordinal, reference, created_at
            FROM work_evidence WHERE attempt_id = $attemptId ORDER BY ordinal;
            """,
            ("$attemptId", attemptId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkEvidenceSnapshot(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                ParseHistoryTimestamp(reader.GetString(3))));
        }

        return result;
    }

    private static async Task<WorkReviewSnapshot?> ReadReviewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var query = CreateQuery(
            connection,
            transaction,
            """
            SELECT id, reviewer_agent_id, decision, rationale, created_at
            FROM work_reviews WHERE attempt_id = $attemptId;
            """,
            ("$attemptId", attemptId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorkReviewSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseHistoryTimestamp(reader.GetString(4)))
            : null;
    }

    private static SqliteCommand CreateQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        return command;
    }

    private static DateTimeOffset ParseHistoryTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
