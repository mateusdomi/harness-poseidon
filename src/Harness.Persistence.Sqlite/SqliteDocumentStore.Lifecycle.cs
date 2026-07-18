using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDocumentStore
{
    public Task<DocumentMutationReceipt> UpdateMetadataAsync(
        DocumentMetadataUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentLifecycleMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => UpdateMetadataCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DocumentMutationReceipt> TransitionAsync(
        DocumentTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentLifecycleMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => TransitionCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<DocumentMutationReceipt> UpdateMetadataCoreAsync(
        SqliteConnection connection,
        DocumentMetadataUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = DocumentLifecycleMutationValidator.Hash(command);
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
        else
        {
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM document_classifications WHERE document_id=$documentId;",
                cancellationToken,
                ("$documentId", command.DocumentId));
            for (var index = 0; index < command.Classifications.Count; index++)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO document_classifications
                        (tenant_id,project_id,document_id,label,ordinal)
                    VALUES ($tenantId,$projectId,$documentId,$label,$ordinal);
                    """,
                    cancellationToken,
                    ("$tenantId", command.TenantId),
                    ("$projectId", row.ProjectId),
                    ("$documentId", command.DocumentId),
                    ("$label", command.Classifications[index]),
                    ("$ordinal", index + 1));
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE documents
                    SET phase_name=$phaseName,inconsistent=$inconsistent,
                        version=$nextVersion,updated_at=$occurredAt
                    WHERE tenant_id=$tenantId AND id=$documentId AND version=$expectedVersion;
                    """;
                AddNullable(update, "$phaseName", command.PhaseName);
                Add(update, "$inconsistent", command.Inconsistent ? 1 : 0);
                Add(update, "$nextVersion", nextVersion);
                Add(update, "$occurredAt", Store(command.OccurredAt));
                Add(update, "$tenantId", command.TenantId);
                Add(update, "$documentId", command.DocumentId);
                Add(update, "$expectedVersion", command.ExpectedDocumentVersion);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        "The document changed during serialized metadata update.");
                }
            }

            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextVersion,
                row.State,
                row.CurrentVersion,
                row.CurrentDocumentVersionId);
        }

        var payload = JsonSerializer.Serialize(new
        {
            projectId = row?.ProjectId,
            documentId = receipt.DocumentId,
            from = ApiState(receipt.State),
            to = ApiState(receipt.State),
            documentVersion = receipt.DocumentVersion,
            currentVersion = receipt.CurrentVersion,
            phaseName = command.PhaseName,
            inconsistent = command.Inconsistent,
            change = "metadataUpdated",
        });
        return await FinalizeLifecycleMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            payload,
            receipt,
            cancellationToken);
    }

    private static async Task<DocumentMutationReceipt> TransitionCoreAsync(
        SqliteConnection connection,
        DocumentTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = DocumentLifecycleMutationValidator.Hash(command);
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
        else if (!CanTransition(row.State, command.TargetState))
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
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE documents
                SET state=$targetState,version=$nextVersion,updated_at=$occurredAt
                WHERE tenant_id=$tenantId AND id=$documentId AND version=$expectedVersion;

                INSERT INTO document_state_transitions
                    (id,tenant_id,project_id,document_id,document_version,from_state,to_state,
                     note,actor_kind,actor_id,occurred_at)
                VALUES ($transitionId,$tenantId,$projectId,$documentId,$nextVersion,$fromState,
                        $targetState,$note,$actorKind,$actorId,$occurredAt);
                """;
            Add(mutation, "$targetState", command.TargetState);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", Store(command.OccurredAt));
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$documentId", command.DocumentId);
            Add(mutation, "$expectedVersion", command.ExpectedDocumentVersion);
            Add(mutation, "$transitionId", command.TransitionId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$fromState", row.State);
            AddNullable(mutation, "$note", command.Note);
            Add(mutation, "$actorKind", command.ActorKind);
            AddNullable(mutation, "$actorId", command.ActorId);
            if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 2)
            {
                throw new InvalidOperationException(
                    "The document changed during serialized lifecycle transition.");
            }

            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextVersion,
                command.TargetState,
                row.CurrentVersion,
                row.CurrentDocumentVersionId);
        }

        var payload = JsonSerializer.Serialize(new
        {
            projectId = row?.ProjectId,
            documentId = receipt.DocumentId,
            from = ApiState(row?.State),
            to = ApiState(receipt.State),
            documentVersion = receipt.DocumentVersion,
            currentVersion = receipt.CurrentVersion,
            transitionId = command.TransitionId,
            fromState = row?.State,
            change = "stateTransitioned",
        });
        return await FinalizeLifecycleMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            payload,
            receipt,
            cancellationToken);
    }

    private static async Task<DocumentMutationReceipt> FinalizeLifecycleMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string hash,
        DateTimeOffset occurredAt,
        string payload,
        DocumentMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == DocumentMutationStatus.Applied)
        {
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection,
                transaction,
                tenantId,
                cancellationToken);
            const string eventType = "document.stateChanged";
            var eventHash = AuditLedgerHash.Compute(
                previousHash,
                tenantId,
                sequence,
                eventType,
                payload,
                occurredAt);
            var outboxId = UlidValue.New(occurredAt).ToString();
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
                ("$id", UlidValue.New(occurredAt).ToString()),
                ("$tenantId", tenantId),
                ("$sequence", sequence),
                ("$previousHash", previousHash),
                ("$eventHash", eventHash),
                ("$eventType", eventType),
                ("$payload", payload),
                ("$occurredAt", Store(occurredAt)),
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
            ("$tenantId", tenantId),
            ("$key", idempotencyKey),
            ("$hash", hash),
            ("$response", JsonSerializer.Serialize(final)),
            ("$occurredAt", Store(occurredAt)));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static bool CanTransition(string current, string target) =>
        (current, target) switch
        {
            ("planned", "in_elaboration" or "not_applicable") => true,
            ("in_elaboration", "in_review" or "not_applicable") => true,
            ("in_review", "in_elaboration") => true,
            ("approved", "outdated" or "superseded") => true,
            _ => false,
        };

}
