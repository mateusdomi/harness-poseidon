using Harness.Persistence.Abstractions.Cockpit;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresCockpitDigestStore(NpgsqlDataSource dataSource) : ICockpitDigestStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

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

        return ReadCoreAsync(tenantId, projectId, activityLimit, cancellationToken);
    }

    private async Task<CockpitDigestRecord?> ReadCoreAsync(
        string tenantId,
        string projectId,
        int activityLimit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var asOf = await ReadProjectAsOfAsync(connection, tenantId, projectId, cancellationToken);
        if (asOf is null)
        {
            return null;
        }

        var tasks = await ReadTaskCountsAsync(connection, tenantId, projectId, cancellationToken);
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
        NpgsqlConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1 FROM harness.projects
                WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL
            ) THEN MAX(value) END FROM
            (
                SELECT COALESCE(last_activity_at,created_at) AS value FROM harness.projects
                WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL
                UNION ALL
                SELECT MAX(updated_at) FROM harness.work_tasks
                WHERE tenant_id=$1 AND project_id=$2
                UNION ALL
                SELECT MAX(COALESCE(resolved_at,requested_at)) FROM harness.document_approval_requests
                WHERE tenant_id=$1 AND project_id=$2
                UNION ALL
                SELECT MAX(COALESCE(completed_at,started_at,created_at)) FROM harness.workflow_runs
                WHERE tenant_id=$1 AND project_id=$2
                UNION ALL
                SELECT MAX(occurred_at) FROM harness.audit_ledger
                WHERE tenant_id=$1
                  AND payload_json->>'projectId'=$2
            ) AS candidates;
            """;
        BindScope(query, tenantId, projectId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : null;
    }

    private static async Task<CockpitTaskCountsRecord> ReadTaskCountsAsync(
        NpgsqlConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT
                COALESCE(SUM(CASE WHEN board_state='backlog' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='ready' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='development' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='review' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='corrections' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='testsGates' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='blocked' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN board_state='done' THEN 1 ELSE 0 END),0)
            FROM harness.work_tasks WHERE tenant_id=$1 AND project_id=$2;
            """;
        BindScope(query, tenantId, projectId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new CockpitTaskCountsRecord(
            checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)), checked((int)reader.GetInt64(3)),
            checked((int)reader.GetInt64(4)), checked((int)reader.GetInt64(5)),
            checked((int)reader.GetInt64(6)), checked((int)reader.GetInt64(7)));
    }

    private static async Task<int> ReadPendingApprovalsAsync(
        NpgsqlConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT COUNT(*) FROM harness.document_approval_requests
            WHERE tenant_id=$1 AND project_id=$2 AND state='pending';
            """;
        BindScope(query, tenantId, projectId);
        var stored = await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return an approval count.");
        return checked((int)(long)stored);
    }

    private static async Task<(CockpitProgressRecord Progress, CockpitWorkflowRecord? Workflow)>
        ReadWorkflowAsync(
            NpgsqlConnection connection,
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
                   (SELECT pd.name FROM harness.workflow_phase_runs pr
                    JOIN harness.workflow_phase_definitions pd ON pd.id=pr.phase_definition_id
                    WHERE pr.workflow_run_id=r.id AND pr.state='active' LIMIT 1),
                   (SELECT pr.state FROM harness.workflow_phase_runs pr
                    WHERE pr.workflow_run_id=r.id AND pr.state='active' LIMIT 1),
                   (SELECT COUNT(*) FROM harness.workflow_gate_runs g
                    JOIN harness.workflow_phase_runs pr ON pr.id=g.phase_run_id
                    WHERE pr.workflow_run_id=r.id AND g.state='pending')
            FROM harness.workflow_runs r
            LEFT JOIN harness.workflow_phase_runs p ON p.workflow_run_id=r.id
            LEFT JOIN harness.workflow_objective_runs o ON o.phase_run_id=p.id
            LEFT JOIN harness.workflow_objective_definitions d ON d.id=o.objective_definition_id
            WHERE r.tenant_id=$1 AND r.project_id=$2
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
                reader.GetString(0).TrimEnd(),
                reader.GetString(1),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                checked((int)reader.GetInt64(7))));
    }

    private static async Task<IReadOnlyList<CockpitActivityRecord>> ReadActivityAsync(
        NpgsqlConnection connection,
        string tenantId,
        string projectId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,event_type,payload_json::text,occurred_at FROM harness.audit_ledger
            WHERE tenant_id=$1
              AND payload_json->>'projectId'=$2
            ORDER BY sequence DESC LIMIT $3;
            """;
        BindScope(query, tenantId, projectId);
        query.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        var result = new List<CockpitActivityRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CockpitActivityRecord(
                reader.GetString(0).TrimEnd(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return result;
    }

    private static void BindScope(NpgsqlCommand command, string tenantId, string projectId)
    {
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenantId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = projectId });
    }
}
