using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowStore(NpgsqlDataSource dataSource) : IWorkflowStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<WorkflowDefinitionReceipt> CreatePublishedDefinitionAsync(
        WorkflowDefinitionCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowDefinitionCreateValidator.Validate(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public async Task<WorkflowDefinitionStoreSnapshot?> ReadDefinitionAsync(
        string tenantId,
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(definitionId, nameof(definitionId));
        await using var query = _dataSource.CreateCommand(
            """
            SELECT d.tenant_id, d.id, d.name, v.id, v.version, v.status, v.content_hash,
                   (SELECT COUNT(*) FROM harness.workflow_phase_definitions p WHERE p.definition_version_id=v.id),
                   (SELECT COUNT(*) FROM harness.workflow_objective_definitions o JOIN harness.workflow_phase_definitions p ON p.id=o.phase_definition_id WHERE p.definition_version_id=v.id),
                   (SELECT COUNT(*) FROM harness.workflow_gate_definitions g JOIN harness.workflow_phase_definitions p ON p.id=g.phase_definition_id WHERE p.definition_version_id=v.id),
                   (SELECT COUNT(*) FROM harness.workflow_gate_requirements r JOIN harness.workflow_gate_definitions g ON g.id=r.gate_definition_id JOIN harness.workflow_phase_definitions p ON p.id=g.phase_definition_id WHERE p.definition_version_id=v.id)
            FROM harness.workflow_definitions d
            JOIN harness.workflow_definition_versions v ON v.definition_id=d.id
            WHERE d.tenant_id=$1 AND d.id=$2 ORDER BY v.version DESC LIMIT 1;
            """);
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(definitionId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorkflowDefinitionStoreSnapshot(
                Trim(reader.GetString(0)), Trim(reader.GetString(1)), reader.GetString(2),
                Trim(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5),
                Trim(reader.GetString(6)), checked((int)reader.GetInt64(7)),
                checked((int)reader.GetInt64(8)), checked((int)reader.GetInt64(9)),
                checked((int)reader.GetInt64(10)))
            : null;
    }

    private async Task<WorkflowDefinitionReceipt> CreateCoreAsync(
        WorkflowDefinitionCreateCommand value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken, Text($"workflow:{value.TenantId}:{value.IdempotencyKey}"));
        var commandHash = WorkflowDefinitionCreateHash.Compute(value);
        var replay = await ReadInboxAsync(
            connection, transaction, value.TenantId, value.IdempotencyKey, commandHash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        await ExecuteAsync(
            connection, transaction,
            "INSERT INTO harness.workflow_definitions (id,tenant_id,name,created_at) VALUES ($1,$2,$3,$4);",
            cancellationToken,
            Text(value.DefinitionId), Text(value.TenantId), Text(value.Name), Timestamp(value.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.workflow_definition_versions
                (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,
                 transitions_json)
            VALUES ($1,$2,$3,$4,'published',$5,$6,$6,$7);
            """,
            cancellationToken,
            Text(value.DefinitionVersionId), Text(value.TenantId), Text(value.DefinitionId),
            Integer(value.Version), Text(value.ContentHash), Timestamp(value.OccurredAt),
            Json(value.TransitionsJson));
        foreach (var phase in value.Phases)
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.workflow_phase_definitions
                    (id,tenant_id,definition_version_id,phase_key,name,phase_order)
                VALUES ($1,$2,$3,$4,$5,$6);
                """,
                cancellationToken,
                Text(phase.PhaseDefinitionId), Text(value.TenantId), Text(value.DefinitionVersionId),
                Text(phase.Key), Text(phase.Name), Integer(phase.Order));
            foreach (var objective in phase.Objectives)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.workflow_objective_definitions
                        (id,tenant_id,phase_definition_id,objective_key,name,kind,weight)
                    VALUES ($1,$2,$3,$4,$5,$6,$7);
                    """,
                    cancellationToken,
                    Text(objective.ObjectiveDefinitionId), Text(value.TenantId),
                    Text(phase.PhaseDefinitionId), Text(objective.Key), Text(objective.Name),
                    Text(objective.Kind), Numeric(objective.Weight));
            }

            foreach (var gate in phase.Gates)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.workflow_gate_definitions
                        (id,tenant_id,phase_definition_id,objective_definition_id,
                         gate_key,name,minimum_required_state)
                    VALUES ($1,$2,$3,$4,$5,$6,$7);
                    """,
                    cancellationToken,
                    Text(gate.GateDefinitionId), Text(value.TenantId),
                    Text(phase.PhaseDefinitionId), Text(gate.ObjectiveDefinitionId),
                    Text(gate.Key), Text(gate.Name), Text(gate.MinimumRequiredState));
                for (var index = 0; index < gate.RequiredObjectiveDefinitionIds.Count; index++)
                {
                    await ExecuteAsync(
                        connection, transaction,
                        """
                        INSERT INTO harness.workflow_gate_requirements
                            (phase_definition_id,gate_definition_id,
                             objective_definition_id,requirement_order)
                        VALUES ($1,$2,$3,$4);
                        """,
                        cancellationToken,
                        Text(phase.PhaseDefinitionId), Text(gate.GateDefinitionId),
                        Text(gate.RequiredObjectiveDefinitionIds[index]), Integer(index + 1));
                }
            }
        }

        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken, Text($"audit-ledger:{value.TenantId}"));
        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.DefinitionId,
            versionId = value.DefinitionVersionId,
            version = value.Version,
        });
        var (sequence, previousHash) = await ReadLedgerTailAsync(
            connection, transaction, value.TenantId, cancellationToken);
        const string eventType = "workflow.versionPublished";
        var ledgerHash = AuditLedgerHash.Compute(
            previousHash, value.TenantId, sequence, eventType, payload, value.OccurredAt);
        var outboxId = UlidValue.New(value.OccurredAt).ToString();
        var receipt = new WorkflowDefinitionReceipt(
            value.DefinitionId, value.DefinitionVersionId, value.Version,
            sequence, ledgerHash, outboxId, Replay: false);
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

    private static async Task<WorkflowDefinitionReceipt?> ReadInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string key, string hash, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT message_hash,response_json::text FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(Trim(reader.GetString(0)), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException("The idempotency key belongs to a different workflow command.");
        }

        return JsonSerializer.Deserialize<WorkflowDefinitionReceipt>(reader.GetString(1));
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1;";
        query.Parameters.Add(Text(tenantId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, Trim(reader.GetString(1)))
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };
    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };
    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };
    private static NpgsqlParameter<decimal> Numeric(decimal value) => new() { TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Jsonb };
    private static string Trim(string value) => value.TrimEnd();

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }
}
