using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkChainStore(NpgsqlDataSource dataSource) : IWorkChainStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<WorkChainCreateReceipt> CreateAsync(
        WorkChainCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainCreateValidator.Validate(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public async Task<WorkChainSnapshot?> ReadAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(solicitationId, nameof(solicitationId));
        await using var command = _dataSource.CreateCommand(
            """
            SELECT s.tenant_id, s.project_id, s.id, s.content, d.id, t.id, t.state,
                   t.version, t.risk_tier, t.weight, i.id, i.version, i.content_hash,
                   (SELECT COUNT(*) FROM harness.work_attempts a WHERE a.task_id = t.id),
                   (SELECT COUNT(*) FROM harness.work_evidence e
                    JOIN harness.work_attempts a ON a.id = e.attempt_id WHERE a.task_id = t.id),
                   (SELECT COUNT(*) FROM harness.work_reviews r
                    JOIN harness.work_attempts a ON a.id = r.attempt_id WHERE a.task_id = t.id)
            FROM harness.solicitations s
            JOIN harness.demands d ON d.solicitation_id = s.id
            JOIN harness.work_tasks t ON t.demand_id = d.id
            JOIN harness.instruction_versions i ON i.task_id = t.id
            WHERE s.tenant_id = $1 AND s.id = $2
            ORDER BY i.version DESC LIMIT 1;
            """);
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(solicitationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkChainSnapshot(
            reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), reader.GetString(2).TrimEnd(),
            reader.GetString(3), reader.GetString(4).TrimEnd(), reader.GetString(5).TrimEnd(),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.GetDecimal(9),
            reader.GetString(10).TrimEnd(), reader.GetInt32(11), reader.GetString(12).TrimEnd(),
            checked((int)reader.GetInt64(13)), checked((int)reader.GetInt64(14)),
            checked((int)reader.GetInt64(15)));
    }

    private async Task<WorkChainCreateReceipt> CreateCoreAsync(
        WorkChainCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"work-chain:{command.TenantId}:{command.IdempotencyKey}"));
        var commandHash = WorkChainCreateHash.Compute(command);
        var replay = await ReadInboxAsync(
            connection,
            transaction,
            command,
            commandHash,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.solicitations (id, tenant_id, project_id, user_id, content, created_at) VALUES ($1, $2, $3, $4, $5, $6);",
            cancellationToken,
            Text(command.SolicitationId),
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.UserId),
            Text(command.SolicitationContent),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.demands
                (id, tenant_id, project_id, solicitation_id, title, acceptance_criteria_json, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            cancellationToken,
            Text(command.DemandId),
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.SolicitationId),
            Text(command.DemandTitle),
            Json(command.AcceptanceCriteriaJson),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.work_tasks
                (id, tenant_id, project_id, demand_id, title, risk_tier, weight,
                 state, version, created_at, updated_at, source_demand_id, board_state, priority)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 'draft', 1, $8, $8, $4, 'backlog', $6);
            """,
            cancellationToken,
            Text(command.TaskId),
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.DemandId),
            Text(command.TaskTitle),
            Text(command.RiskTier),
            Numeric(command.Weight),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.instruction_versions
                (id, tenant_id, project_id, task_id, version, content, content_hash, created_at)
            VALUES ($1, $2, $3, $4, 1, $5, $6, $7);
            """,
            cancellationToken,
            Text(command.InstructionVersionId),
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.TaskId),
            Text(command.InstructionContent),
            Text(command.InstructionContentHash),
            Timestamp(command.OccurredAt));

        var payload = JsonSerializer.Serialize(new
        {
            projectId = command.ProjectId,
            solicitationId = command.SolicitationId,
            demandId = command.DemandId,
            taskId = command.TaskId,
            instructionVersionId = command.InstructionVersionId,
        });
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{command.TenantId}"));
        var (ledgerSequence, previousHash) = await ReadLedgerTailAsync(
            connection,
            transaction,
            command.TenantId,
            cancellationToken);
        const string eventType = "task.created";
        var ledgerHash = AuditLedgerHash.Compute(
            previousHash,
            command.TenantId,
            ledgerSequence,
            eventType,
            payload,
            command.OccurredAt);
        var ledgerId = UlidValue.New(command.OccurredAt).ToString();
        var outboxId = UlidValue.New(command.OccurredAt).ToString();
        var receipt = new WorkChainCreateReceipt(
            command.SolicitationId,
            command.DemandId,
            command.TaskId,
            command.InstructionVersionId,
            ledgerSequence,
            ledgerHash,
            outboxId,
            Replay: false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(ledgerId),
            Text(command.TenantId),
            Bigint(ledgerSequence),
            Text(previousHash),
            Text(ledgerHash),
            Text(eventType),
            Json(payload),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
            cancellationToken,
            Text(outboxId),
            Text(command.TenantId),
            Text(eventType),
            Json(payload),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.IdempotencyKey),
            Text(commandHash),
            Json(JsonSerializer.Serialize(receipt)),
            Timestamp(command.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<WorkChainCreateReceipt?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkChainCreateCommand command,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var inbox = connection.CreateCommand();
        inbox.Transaction = transaction;
        inbox.CommandText =
            "SELECT message_hash, response_json::text FROM harness.inbox_messages WHERE tenant_id = $1 AND idempotency_key = $2;";
        inbox.Parameters.Add(Text(command.TenantId));
        inbox.Parameters.Add(Text(command.IdempotencyKey));
        await using var reader = await inbox.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0).TrimEnd(), commandHash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different work-chain command.");
        }

        return JsonSerializer.Deserialize<WorkChainCreateReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted work-chain receipt is invalid.");
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<decimal> Numeric(decimal value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
