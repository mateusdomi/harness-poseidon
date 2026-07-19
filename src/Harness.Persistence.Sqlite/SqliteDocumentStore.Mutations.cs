using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDocumentStore
{
    public Task<DocumentMutationReceipt> AppendVersionAsync(
        DocumentVersionAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentVersionAppendValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => AppendVersionCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<DocumentMutationReceipt> AppendVersionCoreAsync(
        SqliteConnection connection,
        DocumentVersionAppendCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        else if (row.State is not ("in_elaboration" or "in_review" or "awaiting_approval"))
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
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO document_versions
                        (id,tenant_id,project_id,document_id,version,catalog_path,content_hash,
                         supersedes_id,author_kind,author_id,created_at)
                    VALUES ($documentVersionId,$tenantId,$projectId,$documentId,$contentVersion,
                            $catalogPath,$contentHash,$supersedesId,$authorKind,$authorId,$occurredAt);

                    UPDATE documents
                    SET current_version=$contentVersion,version=$nextVersion,updated_at=$occurredAt
                    WHERE tenant_id=$tenantId AND id=$documentId AND version=$expectedVersion;

                    UPDATE document_approval_requests
                    SET document_version_id=$documentVersionId,version=version+1
                    WHERE tenant_id=$tenantId AND document_id=$documentId AND state='pending'
                      AND $rebindApproval=1;
                    """;
                Add(insert, "$documentVersionId", command.DocumentVersionId);
                Add(insert, "$tenantId", command.TenantId);
                Add(insert, "$projectId", row.ProjectId);
                Add(insert, "$documentId", command.DocumentId);
                Add(insert, "$contentVersion", nextContentVersion);
                Add(insert, "$catalogPath", command.CatalogPath);
                Add(insert, "$contentHash", command.ContentHash);
                Add(insert, "$supersedesId", row.CurrentDocumentVersionId);
                Add(insert, "$authorKind", command.AuthorKind);
                AddNullable(insert, "$authorId", command.AuthorId);
                Add(insert, "$occurredAt", Store(command.OccurredAt));
                Add(insert, "$nextVersion", nextDocumentVersion);
                Add(insert, "$expectedVersion", command.ExpectedDocumentVersion);
                var rebindApproval = row.State == "awaiting_approval";
                Add(insert, "$rebindApproval", rebindApproval ? 1 : 0);
                var expectedChanges = rebindApproval ? 3 : 2;
                if (await insert.ExecuteNonQueryAsync(cancellationToken) != expectedChanges)
                {
                    throw new InvalidOperationException(
                        "The document changed during serialized version append.");
                }
            }

            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextDocumentVersion,
                row.State,
                nextContentVersion,
                command.DocumentVersionId);
        }

        return await FinalizeVersionAppendAsync(
            connection,
            transaction,
            command,
            hash,
            row?.ProjectId,
            receipt,
            cancellationToken);
    }

    private static async Task<DocumentMutationRow?> ReadDocumentForMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT d.project_id,d.state,d.current_version,d.version,v.id
            FROM documents d
            JOIN document_versions v
              ON v.document_id=d.id AND v.version=d.current_version
            WHERE d.tenant_id=$tenantId AND d.id=$documentId;
            """;
        Add(query, "$tenantId", tenantId);
        Add(query, "$documentId", documentId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DocumentMutationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetString(4))
            : null;
    }

    private static async Task<DocumentMutationReceipt?> ReadMutationInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash,response_json FROM inbox_messages WHERE tenant_id=$tenantId AND idempotency_key=$key;";
        Add(query, "$tenantId", tenantId);
        Add(query, "$key", key);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different document mutation.");
        }

        return JsonSerializer.Deserialize<DocumentMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted document mutation receipt is invalid.");
    }

    private static async Task<DocumentMutationReceipt> FinalizeVersionAppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DocumentVersionAppendCommand command,
        string hash,
        string? projectId,
        DocumentMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == DocumentMutationStatus.Applied)
        {
            var payload = JsonSerializer.Serialize(new
            {
                projectId,
                documentId = receipt.DocumentId,
                from = ApiState(receipt.State),
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
                INSERT INTO audit_ledger
                    (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
                VALUES ($id,$tenantId,$sequence,$previousHash,$eventHash,$eventType,$payload,$occurredAt);
                INSERT INTO outbox_messages
                    (id,tenant_id,event_type,payload_json,occurred_at)
                VALUES ($outboxId,$tenantId,$eventType,$payload,$occurredAt);
                """,
                cancellationToken,
                ("$id", UlidValue.New(command.OccurredAt).ToString()),
                ("$tenantId", command.TenantId),
                ("$sequence", sequence),
                ("$previousHash", previousHash),
                ("$eventHash", eventHash),
                ("$eventType", eventType),
                ("$payload", payload),
                ("$occurredAt", Store(command.OccurredAt)),
                ("$outboxId", outboxId));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($tenantId,$key,$hash,$response,$occurredAt);
            """,
            cancellationToken,
            ("$tenantId", command.TenantId),
            ("$key", command.IdempotencyKey),
            ("$hash", hash),
            ("$response", JsonSerializer.Serialize(final)),
            ("$occurredAt", Store(command.OccurredAt)));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static string? ApiState(string? state) => state switch
    {
        "in_elaboration" => "inElaboration",
        "in_review" => "inReview",
        "awaiting_approval" => "awaitingApproval",
        "not_applicable" => "notApplicable",
        _ => state,
    };

    private static DocumentMutationReceipt Rejected(
        DocumentMutationStatus status,
        string documentId,
        long? version = null,
        string? state = null,
        int? currentVersion = null) =>
        new(status, documentId, version, state, currentVersion);

    private sealed record DocumentMutationRow(
        string ProjectId,
        string State,
        int CurrentVersion,
        long Version,
        string CurrentDocumentVersionId);
}
