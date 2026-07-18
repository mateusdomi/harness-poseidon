using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteWorkflowStore(SqliteWriteDispatcher dispatcher) : IWorkflowStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<WorkflowDefinitionReceipt> CreatePublishedDefinitionAsync(
        WorkflowDefinitionCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowDefinitionCreateValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkflowDefinitionStoreSnapshot?> ReadDefinitionAsync(
        string tenantId,
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(definitionId, nameof(definitionId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadCoreAsync(connection, tenantId, definitionId, token),
            cancellationToken);
    }

    private static async Task<WorkflowDefinitionReceipt> CreateCoreAsync(
        SqliteConnection connection,
        WorkflowDefinitionCreateCommand value,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var commandHash = WorkflowDefinitionCreateHash.Compute(value);
        var replay = await ReadInboxAsync(
            connection, transaction, value.TenantId, value.IdempotencyKey, commandHash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        var occurredAt = Store(value.OccurredAt);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO workflow_definitions (id, tenant_id, name, created_at)
            VALUES ($definitionId, $tenantId, $name, $occurredAt);
            INSERT INTO workflow_definition_versions
                (id, tenant_id, definition_id, version, status, content_hash, created_at, published_at)
            VALUES ($versionId, $tenantId, $definitionId, $version, 'published',
                    $contentHash, $occurredAt, $occurredAt);
            """,
            cancellationToken,
            ("$definitionId", value.DefinitionId), ("$tenantId", value.TenantId),
            ("$name", value.Name), ("$occurredAt", occurredAt),
            ("$versionId", value.DefinitionVersionId), ("$version", value.Version),
            ("$contentHash", value.ContentHash));

        foreach (var phase in value.Phases)
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO workflow_phase_definitions
                    (id, tenant_id, definition_version_id, phase_key, name, phase_order)
                VALUES ($id, $tenantId, $versionId, $key, $name, $order);
                """,
                cancellationToken,
                ("$id", phase.PhaseDefinitionId), ("$tenantId", value.TenantId),
                ("$versionId", value.DefinitionVersionId), ("$key", phase.Key),
                ("$name", phase.Name), ("$order", phase.Order));
            foreach (var objective in phase.Objectives)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO workflow_objective_definitions
                        (id, tenant_id, phase_definition_id, objective_key, name, kind, weight)
                    VALUES ($id, $tenantId, $phaseId, $key, $name, $kind, $weight);
                    """,
                    cancellationToken,
                    ("$id", objective.ObjectiveDefinitionId), ("$tenantId", value.TenantId),
                    ("$phaseId", phase.PhaseDefinitionId), ("$key", objective.Key),
                    ("$name", objective.Name), ("$kind", objective.Kind), ("$weight", objective.Weight));
            }

            foreach (var gate in phase.Gates)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO workflow_gate_definitions
                        (id, tenant_id, phase_definition_id, objective_definition_id,
                         gate_key, name, minimum_required_state)
                    VALUES ($id, $tenantId, $phaseId, $objectiveId, $key, $name, $minimumState);
                    """,
                    cancellationToken,
                    ("$id", gate.GateDefinitionId), ("$tenantId", value.TenantId),
                    ("$phaseId", phase.PhaseDefinitionId),
                    ("$objectiveId", gate.ObjectiveDefinitionId), ("$key", gate.Key),
                    ("$name", gate.Name), ("$minimumState", gate.MinimumRequiredState));
                for (var index = 0; index < gate.RequiredObjectiveDefinitionIds.Count; index++)
                {
                    await ExecuteAsync(
                        connection, transaction,
                        """
                        INSERT INTO workflow_gate_requirements
                            (phase_definition_id, gate_definition_id,
                             objective_definition_id, requirement_order)
                        VALUES ($phaseId, $gateId, $objectiveId, $order);
                        """,
                        cancellationToken,
                        ("$phaseId", phase.PhaseDefinitionId), ("$gateId", gate.GateDefinitionId),
                        ("$objectiveId", gate.RequiredObjectiveDefinitionIds[index]),
                        ("$order", index + 1));
                }
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            definitionId = value.DefinitionId,
            definitionVersionId = value.DefinitionVersionId,
            version = value.Version,
        });
        var (sequence, previousHash) = await ReadLedgerTailAsync(
            connection, transaction, value.TenantId, cancellationToken);
        const string eventType = "workflow.definitionPublished";
        var ledgerHash = AuditLedgerHash.Compute(
            previousHash, value.TenantId, sequence, eventType, payload, value.OccurredAt);
        var outboxId = UlidValue.New(value.OccurredAt).ToString();
        var receipt = new WorkflowDefinitionReceipt(
            value.DefinitionId, value.DefinitionVersionId, value.Version,
            sequence, ledgerHash, outboxId, Replay: false);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($ledgerId, $tenantId, $sequence, $previousHash, $ledgerHash,
                    $eventType, $payload, $occurredAt);
            INSERT INTO outbox_messages (id, tenant_id, event_type, payload_json, occurred_at)
            VALUES ($outboxId, $tenantId, $eventType, $payload, $occurredAt);
            INSERT INTO inbox_messages
                (tenant_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($tenantId, $key, $commandHash, $response, $occurredAt);
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

    private static async Task<WorkflowDefinitionStoreSnapshot?> ReadCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string definitionId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT d.tenant_id, d.id, d.name, v.id, v.version, v.status, v.content_hash,
                   (SELECT COUNT(*) FROM workflow_phase_definitions p WHERE p.definition_version_id = v.id),
                   (SELECT COUNT(*) FROM workflow_objective_definitions o JOIN workflow_phase_definitions p ON p.id=o.phase_definition_id WHERE p.definition_version_id = v.id),
                   (SELECT COUNT(*) FROM workflow_gate_definitions g JOIN workflow_phase_definitions p ON p.id=g.phase_definition_id WHERE p.definition_version_id = v.id),
                   (SELECT COUNT(*) FROM workflow_gate_requirements r JOIN workflow_gate_definitions g ON g.id=r.gate_definition_id JOIN workflow_phase_definitions p ON p.id=g.phase_definition_id WHERE p.definition_version_id = v.id)
            FROM workflow_definitions d JOIN workflow_definition_versions v ON v.definition_id=d.id
            WHERE d.tenant_id=$tenantId AND d.id=$definitionId
            ORDER BY v.version DESC LIMIT 1;
            """;
        Add(query, "$tenantId", tenantId);
        Add(query, "$definitionId", definitionId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorkflowDefinitionStoreSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10))
            : null;
    }

    private static async Task<WorkflowDefinitionReceipt?> ReadInboxAsync(
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
            throw new IdempotencyConflictException("The idempotency key belongs to a different workflow command.");
        return JsonSerializer.Deserialize<WorkflowDefinitionReceipt>(reader.GetString(1));
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        SqliteConnection connection, SqliteTransaction transaction, string tenantId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenantId ORDER BY sequence DESC LIMIT 1;";
        Add(query, "$tenantId", tenantId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1))
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _)) throw new ArgumentException("Value must be a canonical ULID.", parameterName);
    }
}
