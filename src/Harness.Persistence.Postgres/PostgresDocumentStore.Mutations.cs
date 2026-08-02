using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDocumentStore
{
    public Task<DocumentMutationReceipt> AppendVersionAsync(
        DocumentVersionAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentVersionAppendValidator.Validate(command);
        return AppendVersionCoreAsync(command, cancellationToken);
    }

    private async Task<DocumentMutationReceipt> AppendVersionCoreAsync(
        DocumentVersionAppendCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"document-mutation:{command.TenantId}:{command.IdempotencyKey}"));
        var hash = DocumentVersionAppendValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = DocumentMutationStatus.IdempotentReplay };
        }

        var row = await ReadDocumentForMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.DocumentId,
            cancellationToken);
        DocumentMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(DocumentMutationStatus.NotFound, command.DocumentId);
        }
        else if (row.Version != command.ExpectedDocumentVersion)
        {
            receipt = Rejected(
                DocumentMutationStatus.VersionConflict,
                command.DocumentId,
                row.Version,
                row.State,
                row.CurrentVersion);
        }
        else if (row.State is not (
                     "in_elaboration" or "in_review" or "awaiting_approval" or
                     "approved" or "outdated"))
        {
            receipt = Rejected(
                DocumentMutationStatus.InvalidState,
                command.DocumentId,
                row.Version,
                row.State,
                row.CurrentVersion);
        }
        else
        {
            var nextDocumentVersion = row.Version + 1;
            var nextContentVersion = row.CurrentVersion + 1;
            var reopensApprovedVersion = row.State is "approved" or "outdated";
            var nextState = reopensApprovedVersion ? "in_elaboration" : row.State;
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.document_versions
                    (id,tenant_id,project_id,document_id,version,catalog_path,content_hash,
                     supersedes_id,author_kind,author_id,created_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);
                """,
                cancellationToken,
                Text(command.DocumentVersionId),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.DocumentId),
                Integer(nextContentVersion),
                Text(command.CatalogPath),
                Text(command.ContentHash),
                Text(row.CurrentDocumentVersionId),
                Text(command.AuthorKind),
                NullableText(command.AuthorId),
                Timestamp(command.OccurredAt));
            var changed = await ExecuteCountAsync(
                connection,
                transaction,
                """
                UPDATE harness.documents
                SET state=$1,current_version=$2,version=$3,updated_at=$4
                WHERE tenant_id=$5 AND id=$6 AND version=$7;
                """,
                cancellationToken,
                Text(nextState),
                Integer(nextContentVersion),
                Bigint(nextDocumentVersion),
                Timestamp(command.OccurredAt),
                Text(command.TenantId),
                Text(command.DocumentId),
                Bigint(command.ExpectedDocumentVersion));
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "The document changed while its locked version was appended.");
            }

            if (row.State == "awaiting_approval")
            {
                var rebound = await ExecuteCountAsync(
                    connection,
                    transaction,
                    """
                    UPDATE harness.document_approval_requests
                    SET document_version_id=$1,version=version+1
                    WHERE tenant_id=$2 AND document_id=$3 AND state='pending';
                    """,
                    cancellationToken,
                    Text(command.DocumentVersionId),
                    Text(command.TenantId),
                    Text(command.DocumentId));
                if (rebound != 1)
                {
                    throw new InvalidOperationException(
                        "The pending approval was not rebound to the appended document version.");
                }
            }

            if (reopensApprovedVersion)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO harness.document_state_transitions
                        (id,tenant_id,project_id,document_id,document_version,from_state,to_state,
                         note,actor_kind,actor_id,occurred_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);
                    """,
                    cancellationToken,
                    Text(command.DocumentVersionId),
                    Text(command.TenantId),
                    Text(row.ProjectId),
                    Text(command.DocumentId),
                    Bigint(nextDocumentVersion),
                    Text(row.State),
                    Text(nextState),
                    Text($"version-appended:{command.DocumentVersionId}"),
                    Text(command.AuthorKind),
                    NullableText(command.AuthorId),
                    Timestamp(command.OccurredAt));
            }

            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextDocumentVersion,
                nextState,
                nextContentVersion,
                command.DocumentVersionId);
        }

        return await FinalizeVersionAppendAsync(
            connection,
            transaction,
            command,
            hash,
            row?.State,
            receipt,
            cancellationToken);
    }

    private static async Task<DocumentMutationRow?> ReadDocumentForMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT d.project_id,d.state,d.current_version,d.version,v.id
            FROM harness.documents d
            JOIN harness.document_versions v
              ON v.document_id=d.id AND v.version=d.current_version
            WHERE d.tenant_id=$1 AND d.id=$2
            FOR UPDATE OF d;
            """;
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(documentId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DocumentMutationRow(
                Trim(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                Trim(reader.GetString(4)))
            : null;
    }

    private static async Task<DocumentMutationReceipt?> ReadMutationInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash,response_json::text FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(Trim(reader.GetString(0)), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different document mutation.");
        }

        return JsonSerializer.Deserialize<DocumentMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted document mutation receipt is invalid.");
    }

    private static async Task<DocumentMutationReceipt> FinalizeVersionAppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentVersionAppendCommand command,
        string hash,
        string? fromState,
        DocumentMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == DocumentMutationStatus.Applied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
                cancellationToken,
                Text($"audit-ledger:{command.TenantId}"));
            var payload = JsonSerializer.Serialize(new
            {
                documentId = receipt.DocumentId,
                from = ApiState(fromState),
                to = ApiState(receipt.State),
                documentVersion = receipt.DocumentVersion,
                currentVersion = receipt.CurrentVersion,
                documentVersionId = receipt.DocumentVersionId,
                change = "versionAppended",
            });
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection,
                transaction,
                command.TenantId,
                cancellationToken);
            const string eventType = "document.stateChanged";
            var eventHash = AuditLedgerHash.Compute(
                previousHash,
                command.TenantId,
                sequence,
                eventType,
                payload,
                command.OccurredAt);
            var outboxId = UlidValue.New(command.OccurredAt).ToString();
            final = receipt with
            {
                LedgerSequence = sequence,
                LedgerHash = eventHash,
                OutboxMessageId = outboxId,
            };
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.audit_ledger
                    (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Bigint(sequence),
                Text(previousHash),
                Text(eventHash),
                Text(eventType),
                Json(payload),
                Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.outbox_messages
                    (id,tenant_id,event_type,payload_json,occurred_at)
                VALUES ($1,$2,$3,$4,$5);
                """,
                cancellationToken,
                Text(outboxId),
                Text(command.TenantId),
                Text(eventType),
                Json(payload),
                Timestamp(command.OccurredAt));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.IdempotencyKey),
            Text(hash),
            Json(JsonSerializer.Serialize(final)),
            Timestamp(command.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static async Task<int> ExecuteCountAsync(
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
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DocumentMutationReceipt Rejected(
        DocumentMutationStatus status,
        string documentId,
        long? version = null,
        string? state = null,
        int? currentVersion = null) =>
        new(status, documentId, version, state, currentVersion);

    private static string? ApiState(string? state) => state switch
    {
        "in_elaboration" => "inElaboration",
        "in_review" => "inReview",
        "awaiting_approval" => "awaitingApproval",
        "not_applicable" => "notApplicable",
        _ => state,
    };

    private sealed record DocumentMutationRow(
        string ProjectId,
        string State,
        int CurrentVersion,
        long Version,
        string CurrentDocumentVersionId);
}
