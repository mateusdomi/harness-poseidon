using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresFoundationTransactionStore(NpgsqlDataSource dataSource) : IFoundationTransactionStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<ProjectProvisionReceipt> ProvisionProjectAsync(
        ProjectProvisionCommand command,
        CancellationToken cancellationToken = default)
    {
        FoundationCommandValidator.Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));";
            lockCommand.Parameters.Add(new NpgsqlParameter<string>
            {
                TypedValue = $"{command.TenantId}:{command.IdempotencyKey}",
            });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = await ReadInboxAsync(connection, transaction, command, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing with { Replay = true };
        }

        await InsertStateAsync(connection, transaction, command, cancellationToken);
        var previousHash = AuditLedgerHash.Genesis;
        const long sequence = 1;
        var eventHash = AuditLedgerHash.Compute(
            previousHash,
            command.TenantId,
            sequence,
            command.EventType,
            command.PayloadJson,
            command.OccurredAt);
        var receipt = new ProjectProvisionReceipt(
            command.ProjectId,
            sequence,
            eventHash,
            command.OutboxMessageId,
            Replay: false);
        await InsertMessagingAsync(connection, transaction, command, receipt, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    public async Task<FoundationStoreSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT (SELECT COUNT(*) FROM harness.tenants),
                   (SELECT COUNT(*) FROM harness.organizations),
                   (SELECT COUNT(*) FROM harness.projects),
                   (SELECT COUNT(*) FROM harness.local_users),
                   (SELECT COUNT(*) FROM harness.inbox_messages),
                   (SELECT COUNT(*) FROM harness.outbox_messages),
                   (SELECT COUNT(*) FROM harness.audit_ledger);
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new FoundationStoreSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    private static async Task<ProjectProvisionReceipt?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectProvisionCommand command,
        CancellationToken cancellationToken)
    {
        await using var inbox = connection.CreateCommand();
        inbox.Transaction = transaction;
        inbox.CommandText =
            """
            SELECT message_hash, response_json::text
            FROM harness.inbox_messages
            WHERE tenant_id = $1 AND idempotency_key = $2;
            """;
        inbox.Parameters.Add(new NpgsqlParameter<string> { TypedValue = command.TenantId });
        inbox.Parameters.Add(new NpgsqlParameter<string> { TypedValue = command.IdempotencyKey });
        await using var reader = await inbox.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), command.MessageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IdempotencyConflictException("The idempotency key belongs to a different command hash.");
        }

        return JsonSerializer.Deserialize<ProjectProvisionReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted foundation receipt is invalid.");
    }

    private static async Task InsertStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectProvisionCommand command,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.tenants (id, name, created_at) VALUES ($1, $2, $3);",
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<string> { TypedValue = command.TenantName },
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.organizations (id, tenant_id, name, created_at) VALUES ($1, $2, $3, $4);",
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.OrganizationId },
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<string> { TypedValue = command.OrganizationName },
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.projects (id, tenant_id, organization_id, name, created_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.ProjectId },
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<string> { TypedValue = command.OrganizationId },
            new NpgsqlParameter<string> { TypedValue = command.ProjectName },
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.local_users (id, tenant_id, display_name, created_at) VALUES ($1, $2, $3, $4);",
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.UserId },
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<string> { TypedValue = command.UserDisplayName },
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
    }

    private static async Task InsertMessagingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectProvisionCommand command,
        ProjectProvisionReceipt receipt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.LedgerEventId },
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<long> { TypedValue = receipt.LedgerSequence },
            new NpgsqlParameter<string> { TypedValue = AuditLedgerHash.Genesis },
            new NpgsqlParameter<string> { TypedValue = receipt.LedgerHash },
            new NpgsqlParameter<string> { TypedValue = command.EventType },
            JsonParameter(command.PayloadJson),
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.OutboxMessageId },
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<string> { TypedValue = command.EventType },
            JsonParameter(command.PayloadJson),
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            new NpgsqlParameter<string> { TypedValue = command.TenantId },
            new NpgsqlParameter<string> { TypedValue = command.IdempotencyKey },
            new NpgsqlParameter<string> { TypedValue = command.MessageHash },
            JsonParameter(JsonSerializer.Serialize(receipt)),
            new NpgsqlParameter<DateTimeOffset> { TypedValue = command.OccurredAt });
    }

    private static NpgsqlParameter<string> JsonParameter(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };

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
}
