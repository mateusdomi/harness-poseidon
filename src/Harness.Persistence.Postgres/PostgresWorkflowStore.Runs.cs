using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowStore
{
    public Task<WorkflowRunCreateReceipt> CreateRunAsync(
        WorkflowRunCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunCreateValidator.Validate(command);
        return CreateRunCoreAsync(command, cancellationToken);
    }

    public async Task<WorkflowRunStoreSnapshot?> ReadRunAsync(
        string tenantId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(runId, nameof(runId));
        await using var query = _dataSource.CreateCommand(
            """
            SELECT r.tenant_id,r.project_id,r.definition_version_id,r.id,r.state,r.version,
                   (SELECT COUNT(*) FROM harness.workflow_phase_runs p WHERE p.workflow_run_id=r.id),
                   (SELECT COUNT(*) FROM harness.workflow_objective_runs o JOIN harness.workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),
                   (SELECT COUNT(*) FROM harness.workflow_gate_runs g JOIN harness.workflow_phase_runs p ON p.id=g.phase_run_id WHERE p.workflow_run_id=r.id),
                   COALESCE((SELECT ROUND(100.0*SUM(CASE WHEN o.state IN ('executed','validated','approved') THEN d.weight ELSE 0 END)/SUM(d.weight),2) FROM harness.workflow_objective_runs o JOIN harness.workflow_objective_definitions d ON d.id=o.objective_definition_id JOIN harness.workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),0),
                   COALESCE((SELECT ROUND(100.0*SUM(CASE WHEN o.state IN ('validated','approved') THEN d.weight ELSE 0 END)/SUM(d.weight),2) FROM harness.workflow_objective_runs o JOIN harness.workflow_objective_definitions d ON d.id=o.objective_definition_id JOIN harness.workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),0),
                   COALESCE((SELECT ROUND(100.0*SUM(CASE WHEN o.state='approved' THEN d.weight ELSE 0 END)/SUM(d.weight),2) FROM harness.workflow_objective_runs o JOIN harness.workflow_objective_definitions d ON d.id=o.objective_definition_id JOIN harness.workflow_phase_runs p ON p.id=o.phase_run_id WHERE p.workflow_run_id=r.id),0)
            FROM harness.workflow_runs r WHERE r.tenant_id=$1 AND r.id=$2;
            """);
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(runId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorkflowRunStoreSnapshot(
                Trim(reader.GetString(0)), Trim(reader.GetString(1)), Trim(reader.GetString(2)),
                Trim(reader.GetString(3)), reader.GetString(4), reader.GetInt64(5),
                checked((int)reader.GetInt64(6)), checked((int)reader.GetInt64(7)),
                checked((int)reader.GetInt64(8)), reader.GetDecimal(9),
                reader.GetDecimal(10), reader.GetDecimal(11))
            : null;
    }

    private async Task<WorkflowRunCreateReceipt> CreateRunCoreAsync(
        WorkflowRunCreateCommand value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken, Text($"workflow-run:{value.TenantId}:{value.IdempotencyKey}"));
        var commandHash = WorkflowRunCreateHash.Compute(value);
        var replay = await ReadRunInboxAsync(
            connection, transaction, value.TenantId, value.IdempotencyKey, commandHash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        var phases = await ReadPublishedPhasesAsync(connection, transaction, value, cancellationToken);
        if (phases.Count == 0)
        {
            throw new InvalidOperationException("A published workflow definition with phases is required.");
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.workflow_runs
                (id,tenant_id,project_id,definition_version_id,state,version,created_at)
            VALUES ($1,$2,$3,$4,'pending',1,$5);
            """,
            cancellationToken,
            Text(value.RunId), Text(value.TenantId), Text(value.ProjectId),
            Text(value.DefinitionVersionId), Timestamp(value.OccurredAt));
        foreach (var phase in phases)
        {
            var phaseRunId = UlidValue.New(value.OccurredAt).ToString();
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.workflow_phase_runs
                    (id,tenant_id,project_id,workflow_run_id,phase_definition_id,
                     phase_order,state,version)
                VALUES ($1,$2,$3,$4,$5,$6,'pending',1);
                """,
                cancellationToken,
                Text(phaseRunId), Text(value.TenantId), Text(value.ProjectId),
                Text(value.RunId), Text(phase.Id), Integer(phase.Order));
            foreach (var objectiveId in phase.ObjectiveIds)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.workflow_objective_runs
                        (id,tenant_id,project_id,phase_run_id,
                         objective_definition_id,state,version,updated_at)
                    VALUES ($1,$2,$3,$4,$5,'pending',1,$6);
                    """,
                    cancellationToken,
                    Text(UlidValue.New(value.OccurredAt).ToString()), Text(value.TenantId),
                    Text(value.ProjectId), Text(phaseRunId), Text(objectiveId),
                    Timestamp(value.OccurredAt));
            }

            foreach (var gateId in phase.GateIds)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.workflow_gate_runs
                        (id,tenant_id,project_id,phase_run_id,gate_definition_id,state,version)
                    VALUES ($1,$2,$3,$4,$5,'pending',1);
                    """,
                    cancellationToken,
                    Text(UlidValue.New(value.OccurredAt).ToString()), Text(value.TenantId),
                    Text(value.ProjectId), Text(phaseRunId), Text(gateId));
            }
        }

        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken, Text($"audit-ledger:{value.TenantId}"));
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
            INSERT INTO harness.audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """,
            cancellationToken,
            Text(UlidValue.New(value.OccurredAt).ToString()), Text(value.TenantId),
            Bigint(sequence), Text(previousHash), Text(ledgerHash), Text(eventType),
            Json(payload), Timestamp(value.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($1,$2,$3,$4,$5);",
            cancellationToken,
            Text(outboxId), Text(value.TenantId), Text(eventType), Json(payload), Timestamp(value.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(value.TenantId), Text(value.IdempotencyKey), Text(commandHash),
            Json(JsonSerializer.Serialize(receipt)), Timestamp(value.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<List<PhaseProjection>> ReadPublishedPhasesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        WorkflowRunCreateCommand value, CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, int Order)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                """
                SELECT p.id,p.phase_order FROM harness.workflow_phase_definitions p
                JOIN harness.workflow_definition_versions v ON v.id=p.definition_version_id
                JOIN harness.projects x ON x.tenant_id=v.tenant_id
                WHERE v.tenant_id=$1 AND v.id=$2 AND v.status='published'
                  AND x.id=$3 ORDER BY p.phase_order;
                """;
            query.Parameters.Add(Text(value.TenantId));
            query.Parameters.Add(Text(value.DefinitionVersionId));
            query.Parameters.Add(Text(value.ProjectId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((Trim(reader.GetString(0)), reader.GetInt32(1)));
            }
        }

        var result = new List<PhaseProjection>(rows.Count);
        foreach (var row in rows)
        {
            var objectives = await ReadIdsAsync(
                connection, transaction,
                "SELECT id FROM harness.workflow_objective_definitions WHERE phase_definition_id=$1 ORDER BY objective_key;",
                row.Id, cancellationToken);
            var gates = await ReadIdsAsync(
                connection, transaction,
                "SELECT id FROM harness.workflow_gate_definitions WHERE phase_definition_id=$1 ORDER BY gate_key;",
                row.Id, cancellationToken);
            result.Add(new PhaseProjection(row.Id, row.Order, objectives, gates));
        }

        return result;
    }

    private static async Task<List<string>> ReadIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        string id, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = sql;
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Trim(reader.GetString(0)));
        return result;
    }

    private static async Task<WorkflowRunCreateReceipt?> ReadRunInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string key, string hash, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT message_hash,response_json::text FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!string.Equals(Trim(reader.GetString(0)), hash, StringComparison.Ordinal))
            throw new IdempotencyConflictException("The idempotency key belongs to a different workflow run command.");
        return JsonSerializer.Deserialize<WorkflowRunCreateReceipt>(reader.GetString(1));
    }

    private sealed record PhaseProjection(
        string Id, int Order, IReadOnlyList<string> ObjectiveIds, IReadOnlyList<string> GateIds);
}
