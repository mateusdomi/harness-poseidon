using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkflowStore
{
    public Task<WorkflowRunCreateReceipt> CreateRunAsync(
        WorkflowRunCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunCreateValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateRunCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkflowRunStoreSnapshot?> ReadRunAsync(
        string tenantId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(runId, nameof(runId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadRunCoreAsync(connection, tenantId, runId, token),
            cancellationToken);
    }

    private static async Task<WorkflowRunCreateReceipt> CreateRunCoreAsync(
        SqliteConnection connection,
        WorkflowRunCreateCommand value,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var commandHash = WorkflowRunCreateHash.Compute(value);
        var replay = await ReadRunInboxAsync(
            connection, transaction, value.TenantId, value.IdempotencyKey, commandHash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        var phases = await ReadPublishedPhasesAsync(
            connection, transaction, value, cancellationToken);
        if (phases.Count == 0)
        {
            throw new InvalidOperationException("A published workflow definition with phases is required.");
        }

        var occurredAt = Store(value.OccurredAt);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO workflow_runs
                (id,tenant_id,project_id,definition_version_id,state,version,created_at,workflow_id)
            VALUES ($runId,$tenantId,$projectId,$versionId,'pending',1,$occurredAt,$workflowId);
            """,
            cancellationToken,
            ("$runId", value.RunId), ("$tenantId", value.TenantId),
            ("$projectId", value.ProjectId), ("$versionId", value.DefinitionVersionId),
            ("$workflowId", (object?)value.WorkflowId ?? DBNull.Value),
            ("$occurredAt", occurredAt));
        foreach (var phase in phases)
        {
            var phaseRunId = UlidValue.New(value.OccurredAt).ToString();
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO workflow_phase_runs
                    (id,tenant_id,project_id,workflow_run_id,phase_definition_id,
                     phase_order,state,version)
                VALUES ($id,$tenantId,$projectId,$runId,$phaseId,$order,'pending',1);
                """,
                cancellationToken,
                ("$id", phaseRunId), ("$tenantId", value.TenantId),
                ("$projectId", value.ProjectId), ("$runId", value.RunId),
                ("$phaseId", phase.Id), ("$order", phase.Order));
            foreach (var objectiveId in phase.ObjectiveIds)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO workflow_objective_runs
                        (id,tenant_id,project_id,phase_run_id,
                         objective_definition_id,state,version,updated_at)
                    VALUES ($id,$tenantId,$projectId,$phaseRunId,$objectiveId,'pending',1,$occurredAt);
                    """,
                    cancellationToken,
                    ("$id", UlidValue.New(value.OccurredAt).ToString()),
                    ("$tenantId", value.TenantId), ("$projectId", value.ProjectId),
                    ("$phaseRunId", phaseRunId), ("$objectiveId", objectiveId),
                    ("$occurredAt", occurredAt));
            }

            foreach (var gateId in phase.GateIds)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO workflow_gate_runs
                        (id,tenant_id,project_id,phase_run_id,gate_definition_id,state,version)
                    VALUES ($id,$tenantId,$projectId,$phaseRunId,$gateId,'pending',1);
                    """,
                    cancellationToken,
                    ("$id", UlidValue.New(value.OccurredAt).ToString()),
                    ("$tenantId", value.TenantId), ("$projectId", value.ProjectId),
                    ("$phaseRunId", phaseRunId), ("$gateId", gateId));
            }
        }

        var payload = JsonSerializer.Serialize(new { runId = value.RunId, state = "pending", version = 1 });
        var (sequence, previousHash) = await ReadLedgerTailAsync(
            connection, transaction, value.TenantId, cancellationToken);
        const string eventType = "progress.updated";
        var ledgerHash = AuditLedgerHash.Compute(
            previousHash, value.TenantId, sequence, eventType, payload, value.OccurredAt);
        var outboxId = UlidValue.New(value.OccurredAt).ToString();
        var receipt = new WorkflowRunCreateReceipt(
            value.RunId, 1, sequence, ledgerHash, outboxId, Replay: false);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($ledgerId,$tenantId,$sequence,$previousHash,$ledgerHash,$eventType,$payload,$occurredAt);
            INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at)
            VALUES ($outboxId,$tenantId,$eventType,$payload,$occurredAt);
            INSERT INTO inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($tenantId,$key,$commandHash,$response,$occurredAt);
            """,
            cancellationToken,
            ("$ledgerId", UlidValue.New(value.OccurredAt).ToString()),
            ("$tenantId", value.TenantId), ("$sequence", sequence),
            ("$previousHash", previousHash), ("$ledgerHash", ledgerHash),
            ("$eventType", eventType), ("$payload", payload), ("$occurredAt", occurredAt),
            ("$outboxId", outboxId), ("$key", value.IdempotencyKey),
            ("$commandHash", commandHash), ("$response", JsonSerializer.Serialize(receipt)));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<List<PhaseProjection>> ReadPublishedPhasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowRunCreateCommand value,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, int Order)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                """
                SELECT p.id,p.phase_order FROM workflow_phase_definitions p
                JOIN workflow_definition_versions v ON v.id=p.definition_version_id
                JOIN projects x ON x.tenant_id=v.tenant_id
                WHERE v.tenant_id=$tenantId AND v.id=$versionId AND v.status='published'
                  AND x.id=$projectId ORDER BY p.phase_order;
                """;
            Add(query, "$tenantId", value.TenantId);
            Add(query, "$versionId", value.DefinitionVersionId);
            Add(query, "$projectId", value.ProjectId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        var result = new List<PhaseProjection>(rows.Count);
        foreach (var row in rows)
        {
            var objectives = await ReadIdsAsync(
                connection, transaction,
                "SELECT id FROM workflow_objective_definitions WHERE phase_definition_id=$id ORDER BY objective_key;",
                row.Id, cancellationToken);
            var gates = await ReadIdsAsync(
                connection, transaction,
                "SELECT id FROM workflow_gate_definitions WHERE phase_definition_id=$id ORDER BY gate_key;",
                row.Id, cancellationToken);
            result.Add(new PhaseProjection(row.Id, row.Order, objectives, gates));
        }

        return result;
    }

    private static async Task<List<string>> ReadIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        string id, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = sql;
        Add(query, "$id", id);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<WorkflowRunCreateReceipt?> ReadRunInboxAsync(
        SqliteConnection connection, SqliteTransaction transaction, string tenantId,
        string key, string hash, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT message_hash,response_json FROM inbox_messages WHERE tenant_id=$tenantId AND idempotency_key=$key;";
        Add(query, "$tenantId", tenantId);
        Add(query, "$key", key);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
            throw new IdempotencyConflictException("The idempotency key belongs to a different workflow run command.");
        return JsonSerializer.Deserialize<WorkflowRunCreateReceipt>(reader.GetString(1));
    }

    private static async Task<WorkflowRunStoreSnapshot?> ReadRunCoreAsync(
        SqliteConnection connection, string tenantId, string runId, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT r.tenant_id,r.project_id,r.definition_version_id,r.id,r.state,r.version,
                   r.created_at,r.started_at,r.completed_at,
                   (SELECT COUNT(*) FROM workflow_phase_runs p WHERE p.workflow_run_id=r.id),
                   (SELECT COUNT(*) FROM workflow_phase_runs p WHERE p.workflow_run_id=r.id AND p.state='active'),
                   (SELECT COUNT(*) FROM workflow_objective_runs o JOIN workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),
                   (SELECT COUNT(*) FROM workflow_gate_runs g JOIN workflow_phase_runs p ON p.id=g.phase_run_id WHERE p.workflow_run_id=r.id),
                   COALESCE((SELECT ROUND(100.0*SUM(CASE WHEN o.state IN ('executed','validated','approved') THEN d.weight ELSE 0 END)/SUM(d.weight),2) FROM workflow_objective_runs o JOIN workflow_objective_definitions d ON d.id=o.objective_definition_id JOIN workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),0),
                   COALESCE((SELECT ROUND(100.0*SUM(CASE WHEN o.state IN ('validated','approved') THEN d.weight ELSE 0 END)/SUM(d.weight),2) FROM workflow_objective_runs o JOIN workflow_objective_definitions d ON d.id=o.objective_definition_id JOIN workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),0),
                   COALESCE((SELECT ROUND(100.0*SUM(CASE WHEN o.state='approved' THEN d.weight ELSE 0 END)/SUM(d.weight),2) FROM workflow_objective_runs o JOIN workflow_objective_definitions d ON d.id=o.objective_definition_id JOIN workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),0)
            FROM workflow_runs r WHERE r.tenant_id=$tenantId AND r.id=$runId;
            """;
        Add(query, "$tenantId", tenantId);
        Add(query, "$runId", runId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorkflowRunStoreSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), ParseRunTimestamp(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseRunTimestamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseRunTimestamp(reader.GetString(8)),
                reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15))
            : null;
    }

    private static DateTimeOffset ParseRunTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record PhaseProjection(
        string Id, int Order, IReadOnlyList<string> ObjectiveIds, IReadOnlyList<string> GateIds);
}
