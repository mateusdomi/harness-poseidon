using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteFoundationTransactionStore(SqliteWriteDispatcher dispatcher) : IFoundationTransactionStore
{
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ProjectProvisionReceipt> ProvisionProjectAsync(
        ProjectProvisionCommand command,
        CancellationToken cancellationToken = default)
    {
        FoundationCommandValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ProvisionCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<FoundationStoreSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(ReadSnapshotCoreAsync, cancellationToken);

    private static async Task<ProjectProvisionReceipt> ProvisionCoreAsync(
        SqliteConnection connection,
        ProjectProvisionCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = await ReadInboxAsync(connection, transaction, command, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing with { Replay = true };
        }

        var occurredAt = command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.Transaction = transaction;
            stateCommand.CommandText =
                """
                INSERT INTO tenants (id, name, created_at) VALUES ($tenantId, $tenantName, $occurredAt);
                INSERT INTO organizations (id, tenant_id, name, created_at)
                    VALUES ($organizationId, $tenantId, $organizationName, $occurredAt);
                INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                    VALUES ($projectId, $tenantId, $organizationId, $projectName, $occurredAt);
                INSERT INTO local_users (id, tenant_id, display_name, created_at)
                    VALUES ($userId, $tenantId, $userDisplayName, $occurredAt);
                """;
            AddFoundationParameters(stateCommand, command, occurredAt);
            await stateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

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

        await using (var messagingCommand = connection.CreateCommand())
        {
            messagingCommand.Transaction = transaction;
            messagingCommand.CommandText =
                """
                INSERT INTO audit_ledger
                    (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
                VALUES
                    ($ledgerId, $tenantId, $sequence, $previousHash, $eventHash, $eventType, $payloadJson, $occurredAt);
                INSERT INTO outbox_messages
                    (id, tenant_id, event_type, payload_json, occurred_at)
                VALUES
                    ($outboxId, $tenantId, $eventType, $payloadJson, $occurredAt);
                INSERT INTO inbox_messages
                    (tenant_id, idempotency_key, message_hash, response_json, processed_at)
                VALUES
                    ($tenantId, $idempotencyKey, $messageHash, $responseJson, $occurredAt);
                """;
            Add(messagingCommand, "$ledgerId", command.LedgerEventId);
            Add(messagingCommand, "$tenantId", command.TenantId);
            Add(messagingCommand, "$sequence", sequence);
            Add(messagingCommand, "$previousHash", previousHash);
            Add(messagingCommand, "$eventHash", eventHash);
            Add(messagingCommand, "$eventType", command.EventType);
            Add(messagingCommand, "$payloadJson", command.PayloadJson);
            Add(messagingCommand, "$occurredAt", occurredAt);
            Add(messagingCommand, "$outboxId", command.OutboxMessageId);
            Add(messagingCommand, "$idempotencyKey", command.IdempotencyKey);
            Add(messagingCommand, "$messageHash", command.MessageHash);
            Add(messagingCommand, "$responseJson", JsonSerializer.Serialize(receipt));
            await messagingCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<ProjectProvisionReceipt?> ReadInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectProvisionCommand command,
        CancellationToken cancellationToken)
    {
        await using var inbox = connection.CreateCommand();
        inbox.Transaction = transaction;
        inbox.CommandText =
            """
            SELECT message_hash, response_json
            FROM inbox_messages
            WHERE tenant_id = $tenantId AND idempotency_key = $idempotencyKey;
            """;
        Add(inbox, "$tenantId", command.TenantId);
        Add(inbox, "$idempotencyKey", command.IdempotencyKey);
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

    private static async Task<FoundationStoreSnapshot> ReadSnapshotCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT (SELECT COUNT(*) FROM tenants),
                   (SELECT COUNT(*) FROM organizations),
                   (SELECT COUNT(*) FROM projects),
                   (SELECT COUNT(*) FROM local_users),
                   (SELECT COUNT(*) FROM inbox_messages),
                   (SELECT COUNT(*) FROM outbox_messages),
                   (SELECT COUNT(*) FROM audit_ledger);
            """;
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

    private static void AddFoundationParameters(
        SqliteCommand command,
        ProjectProvisionCommand provision,
        string occurredAt)
    {
        Add(command, "$tenantId", provision.TenantId);
        Add(command, "$tenantName", provision.TenantName);
        Add(command, "$organizationId", provision.OrganizationId);
        Add(command, "$organizationName", provision.OrganizationName);
        Add(command, "$projectId", provision.ProjectId);
        Add(command, "$projectName", provision.ProjectName);
        Add(command, "$userId", provision.UserId);
        Add(command, "$userDisplayName", provision.UserDisplayName);
        Add(command, "$occurredAt", occurredAt);
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
