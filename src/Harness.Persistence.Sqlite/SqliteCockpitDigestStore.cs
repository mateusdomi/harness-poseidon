using System.Globalization;
using Harness.Persistence.Abstractions.Cockpit;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteCockpitDigestStore(SqliteWriteDispatcher dispatcher) : ICockpitDigestStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<CockpitDigestRecord?> ReadAsync(
        string tenantId,
        string projectId,
        int activityLimit,
        CancellationToken cancellationToken = default)
    {
        if (activityLimit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activityLimit),
                "Activity limit must be between 1 and 100.");
        }

        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadCoreAsync(
                connection,
                tenantId,
                projectId,
                activityLimit,
                token),
            cancellationToken);
    }

    private static async Task<CockpitDigestRecord?> ReadCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string projectId,
        int activityLimit,
        CancellationToken cancellationToken)
    {
        var asOf = await ReadProjectAsOfAsync(
            connection, tenantId, projectId, cancellationToken);
        if (asOf is null)
        {
            return null;
        }

        var tasks = await ReadTaskCountsAsync(
            connection, tenantId, projectId, cancellationToken);
        var pendingApprovals = await ReadPendingApprovalsAsync(
            connection, tenantId, projectId, cancellationToken);
        var (progress, workflow) = await ReadWorkflowAsync(
            connection, tenantId, projectId, cancellationToken);
        var activity = await ReadActivityAsync(
            connection, tenantId, projectId, activityLimit, cancellationToken);

        return new CockpitDigestRecord(
            projectId,
            asOf.Value,
            progress,
            tasks,
            pendingApprovals,
            workflow,
            activity);
    }

    private static async Task<DateTimeOffset?> ReadProjectAsOfAsync(
        SqliteConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1 FROM projects
                WHERE tenant_id=$tenantId AND id=$projectId AND deleted_at IS NULL
            ) THEN MAX(value) END FROM
            (
                SELECT last_activity_at AS value FROM projects
                WHERE tenant_id=$tenantId AND id=$projectId AND deleted_at IS NULL
                UNION ALL
                SELECT MAX(updated_at) FROM work_tasks
                WHERE tenant_id=$tenantId AND project_id=$projectId
                UNION ALL
                SELECT MAX(COALESCE(resolved_at,requested_at)) FROM document_approval_requests
                WHERE tenant_id=$tenantId AND project_id=$projectId
                UNION ALL
                SELECT MAX(COALESCE(completed_at,started_at,created_at)) FROM workflow_runs
                WHERE tenant_id=$tenantId AND project_id=$projectId
                UNION ALL
                SELECT MAX(occurred_at) FROM audit_ledger
                WHERE tenant_id=$tenantId
                  AND json_extract(payload_json,'$.projectId')=$projectId
            );
            """;
        BindScope(query, tenantId, projectId);
        var stored = await query.ExecuteScalarAsync(cancellationToken);
        return stored is null or DBNull
            ? null
            : Parse(Convert.ToString(stored, CultureInfo.InvariantCulture)!);
    }

    private static async Task<CockpitTaskCountsRecord> ReadTaskCountsAsync(
        SqliteConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT
                0,
                COALESCE(SUM(CASE WHEN state='ready' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN state='running' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN state='awaiting_review' THEN 1 ELSE 0 END),0),
                0,
                0,
                0,
                COALESCE(SUM(CASE WHEN state='completed' THEN 1 ELSE 0 END),0)
            FROM work_tasks WHERE tenant_id=$tenantId AND project_id=$projectId;
            """;
        BindScope(query, tenantId, projectId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new CockpitTaskCountsRecord(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    }

    private static async Task<int> ReadPendingApprovalsAsync(
        SqliteConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT COUNT(*) FROM document_approval_requests
            WHERE tenant_id=$tenantId AND project_id=$projectId AND state='pending';
            """;
        BindScope(query, tenantId, projectId);
        return Convert.ToInt32(
            await query.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<(CockpitProgressRecord Progress, CockpitWorkflowRecord? Workflow)>
        ReadWorkflowAsync(
            SqliteConnection connection,
            string tenantId,
            string projectId,
            CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT r.id,r.state,
                   COALESCE(ROUND(100.0*SUM(CASE WHEN o.state IN ('executed','validated','approved') THEN d.weight ELSE 0 END)/NULLIF(SUM(d.weight),0),2),0),
                   COALESCE(ROUND(100.0*SUM(CASE WHEN o.state IN ('validated','approved') THEN d.weight ELSE 0 END)/NULLIF(SUM(d.weight),0),2),0),
                   COALESCE(ROUND(100.0*SUM(CASE WHEN o.state='approved' THEN d.weight ELSE 0 END)/NULLIF(SUM(d.weight),0),2),0),
                   (SELECT pd.name FROM workflow_phase_runs pr
                    JOIN workflow_phase_definitions pd ON pd.id=pr.phase_definition_id
                    WHERE pr.workflow_run_id=r.id AND pr.state='active' LIMIT 1),
                   (SELECT pr.state FROM workflow_phase_runs pr
                    WHERE pr.workflow_run_id=r.id AND pr.state='active' LIMIT 1),
                   (SELECT COUNT(*) FROM workflow_gate_runs g
                    JOIN workflow_phase_runs pr ON pr.id=g.phase_run_id
                    WHERE pr.workflow_run_id=r.id AND g.state='pending')
            FROM workflow_runs r
            LEFT JOIN workflow_phase_runs p ON p.workflow_run_id=r.id
            LEFT JOIN workflow_objective_runs o ON o.phase_run_id=p.id
            LEFT JOIN workflow_objective_definitions d ON d.id=o.objective_definition_id
            WHERE r.tenant_id=$tenantId AND r.project_id=$projectId
            GROUP BY r.id
            ORDER BY CASE r.state WHEN 'running' THEN 0 WHEN 'paused' THEN 1 ELSE 2 END,
                     r.created_at DESC
            LIMIT 1;
            """;
        BindScope(query, tenantId, projectId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (new CockpitProgressRecord(0m, 0m, 0m), null);
        }

        return (
            new CockpitProgressRecord(
                reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)),
            new CockpitWorkflowRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7)));
    }

    private static async Task<IReadOnlyList<CockpitActivityRecord>> ReadActivityAsync(
        SqliteConnection connection,
        string tenantId,
        string projectId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,event_type,payload_json,occurred_at FROM audit_ledger
            WHERE tenant_id=$tenantId
              AND json_extract(payload_json,'$.projectId')=$projectId
            ORDER BY sequence DESC LIMIT $limit;
            """;
        BindScope(query, tenantId, projectId);
        query.Parameters.AddWithValue("$limit", limit);
        var result = new List<CockpitActivityRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CockpitActivityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Parse(reader.GetString(3))));
        }

        return result;
    }

    private static void BindScope(SqliteCommand command, string tenantId, string projectId)
    {
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$projectId", projectId);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
