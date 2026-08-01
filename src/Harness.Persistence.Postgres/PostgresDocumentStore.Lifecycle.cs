using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDocumentStore
{
    public Task<DocumentMutationReceipt> UpdateMetadataAsync(
        DocumentMetadataUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentLifecycleMutationValidator.Validate(command);
        return UpdateMetadataCoreAsync(command, cancellationToken);
    }

    public Task<DocumentMutationReceipt> TransitionAsync(
        DocumentTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentLifecycleMutationValidator.Validate(command);
        return TransitionCoreAsync(command, cancellationToken);
    }

    private async Task<DocumentMutationReceipt> UpdateMetadataCoreAsync(
        DocumentMetadataUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockMutationKeyAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            cancellationToken);
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
                "DELETE FROM harness.document_classifications WHERE document_id=$1;",
                cancellationToken,
                Text(command.DocumentId));
            for (var index = 0; index < command.Classifications.Count; index++)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO harness.document_classifications
                        (tenant_id,project_id,document_id,label,ordinal)
                    VALUES ($1,$2,$3,$4,$5);
                    """,
                    cancellationToken,
                    Text(command.TenantId),
                    Text(row.ProjectId),
                    Text(command.DocumentId),
                    Text(command.Classifications[index]),
                    Integer(index + 1));
            }

            var changed = await ExecuteCountAsync(
                connection,
                transaction,
                """
                UPDATE harness.documents
                SET phase_name=$1,inconsistent=$2,version=$3,updated_at=$4
                WHERE tenant_id=$5 AND id=$6 AND version=$7;
                """,
                cancellationToken,
                NullableText(command.PhaseName),
                Boolean(command.Inconsistent),
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TenantId),
                Text(command.DocumentId),
                Bigint(command.ExpectedDocumentVersion));
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "The document changed during its locked metadata update.");
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
            documentId = receipt.DocumentId,
            state = receipt.State,
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

    private async Task<DocumentMutationReceipt> TransitionCoreAsync(
        DocumentTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockMutationKeyAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            cancellationToken);
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
        else if (!DocumentLifecycleMutationValidator.CanTransition(
                     row.State, command.TargetState, command.ActorKind))
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
            var changed = await ExecuteCountAsync(
                connection,
                transaction,
                """
                UPDATE harness.documents
                SET state=$1,version=$2,updated_at=$3
                WHERE tenant_id=$4 AND id=$5 AND version=$6;
                """,
                cancellationToken,
                Text(command.TargetState),
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TenantId),
                Text(command.DocumentId),
                Bigint(command.ExpectedDocumentVersion));
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "The document changed during its locked lifecycle transition.");
            }

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
                Text(command.TransitionId),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.DocumentId),
                Bigint(nextVersion),
                Text(row.State),
                Text(command.TargetState),
                NullableText(command.Note),
                Text(command.ActorKind),
                NullableText(command.ActorId),
                Timestamp(command.OccurredAt));
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
            documentId = receipt.DocumentId,
            state = receipt.State,
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

    private static async Task LockMutationKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"document-mutation:{tenantId}:{idempotencyKey}"));

    private static async Task<DocumentMutationReceipt> FinalizeLifecycleMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
            await ExecuteAsync(
                connection,
                transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
                cancellationToken,
                Text($"audit-ledger:{tenantId}"));
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
                INSERT INTO harness.audit_ledger
                    (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """,
                cancellationToken,
                Text(UlidValue.New(occurredAt).ToString()),
                Text(tenantId),
                Bigint(sequence),
                Text(previousHash),
                Text(eventHash),
                Text(eventType),
                Json(payload),
                Timestamp(occurredAt));
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
                Text(tenantId),
                Text(eventType),
                Json(payload),
                Timestamp(occurredAt));
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
            Text(tenantId),
            Text(idempotencyKey),
            Text(hash),
            Json(JsonSerializer.Serialize(final)),
            Timestamp(occurredAt));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

}
