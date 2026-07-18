using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDocumentStore
{
    public Task<DocumentMutationReceipt> RequestApprovalAsync(
        DocumentApprovalRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentApprovalMutationValidator.Validate(command);
        return RequestApprovalCoreAsync(command, cancellationToken);
    }

    public Task<DocumentMutationReceipt> ResolveApprovalAsync(
        DocumentApprovalResolveCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentApprovalMutationValidator.Validate(command);
        return ResolveApprovalCoreAsync(command, cancellationToken);
    }

    public Task<DocumentMutationReceipt> CancelApprovalAsync(
        DocumentApprovalCancelCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentApprovalMutationValidator.Validate(command);
        return CancelApprovalCoreAsync(command, cancellationToken);
    }

    private async Task<DocumentMutationReceipt> RequestApprovalCoreAsync(
        DocumentApprovalRequestCommand command,
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
        var hash = DocumentApprovalMutationValidator.Hash(command);
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
        else if (await HasPendingApprovalAsync(
                     connection,
                     transaction,
                     command.DocumentId,
                     cancellationToken))
        {
            receipt = Rejected(
                DocumentMutationStatus.ApprovalAlreadyPending,
                command.DocumentId,
                row.Version,
                row.State,
                row.CurrentVersion);
        }
        else if (row.State != "in_review")
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
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.document_approval_requests
                    (id,tenant_id,project_id,document_id,document_version_id,title,description,
                     priority,due_at,state,requested_by_agent_id,requested_at,version)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,'pending',$10,$11,1);
                """,
                cancellationToken,
                Text(command.ApprovalRequestId),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.DocumentId),
                Text(row.CurrentDocumentVersionId),
                Text(command.Title),
                Text(command.Description),
                Text(command.Priority),
                NullableTimestamp(command.DueAt),
                Text(command.RequestedByAgentId),
                Timestamp(command.OccurredAt));
            await UpdateDocumentStateAsync(
                connection,
                transaction,
                command.TenantId,
                command.DocumentId,
                command.ExpectedDocumentVersion,
                nextVersion,
                "awaiting_approval",
                command.OccurredAt,
                cancellationToken);
            await InsertApprovalTransitionAsync(
                connection,
                transaction,
                command.TransitionId,
                command.TenantId,
                row.ProjectId,
                command.DocumentId,
                nextVersion,
                "in_review",
                "awaiting_approval",
                note: null,
                "agent",
                command.RequestedByAgentId,
                command.OccurredAt,
                cancellationToken);
            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextVersion,
                "awaiting_approval",
                row.CurrentVersion,
                row.CurrentDocumentVersionId,
                ApprovalRequestId: command.ApprovalRequestId,
                ApprovalState: "pending");
        }

        return await FinalizeApprovalMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            "approval.requested",
            receipt,
            cancellationToken);
    }

    private async Task<DocumentMutationReceipt> ResolveApprovalCoreAsync(
        DocumentApprovalResolveCommand command,
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
        var hash = DocumentApprovalMutationValidator.Hash(command);
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
        var approval = row is null
            ? null
            : await ReadApprovalForMutationAsync(
                connection,
                transaction,
                command.DocumentId,
                command.ApprovalRequestId,
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
        else if (approval is null)
        {
            receipt = Rejected(
                DocumentMutationStatus.ApprovalNotFound,
                command.DocumentId,
                row.Version,
                row.State,
                row.CurrentVersion);
        }
        else if (approval.State != "pending")
        {
            receipt = RejectedApproval(
                DocumentMutationStatus.ApprovalAlreadyResolved,
                command.DocumentId,
                row,
                command.ApprovalRequestId,
                approval.State);
        }
        else if (command.Decision == "rejected" && command.Note is null)
        {
            receipt = RejectedApproval(
                DocumentMutationStatus.RejectionNoteRequired,
                command.DocumentId,
                row,
                command.ApprovalRequestId,
                approval.State);
        }
        else if (row.State != "awaiting_approval")
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
            var targetState = command.Decision == "approved" ? "approved" : "in_elaboration";
            var changed = await ExecuteCountAsync(
                connection,
                transaction,
                """
                UPDATE harness.document_approval_requests
                SET state=$1,resolved_by_profile_id=$2,resolved_at=$3,
                    resolution_note=$4,version=version+1
                WHERE document_id=$5 AND id=$6 AND state='pending';
                """,
                cancellationToken,
                Text(command.Decision),
                Text(command.ResolvedByProfileId),
                Timestamp(command.OccurredAt),
                NullableText(command.Note),
                Text(command.DocumentId),
                Text(command.ApprovalRequestId));
            if (changed != 1)
            {
                throw new InvalidOperationException("The locked approval request was not pending.");
            }

            await UpdateDocumentStateAsync(
                connection,
                transaction,
                command.TenantId,
                command.DocumentId,
                command.ExpectedDocumentVersion,
                nextVersion,
                targetState,
                command.OccurredAt,
                cancellationToken);
            await InsertApprovalTransitionAsync(
                connection,
                transaction,
                command.TransitionId,
                command.TenantId,
                row.ProjectId,
                command.DocumentId,
                nextVersion,
                "awaiting_approval",
                targetState,
                command.Note,
                "user",
                command.ResolvedByProfileId,
                command.OccurredAt,
                cancellationToken);
            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextVersion,
                targetState,
                row.CurrentVersion,
                row.CurrentDocumentVersionId,
                ApprovalRequestId: command.ApprovalRequestId,
                ApprovalState: command.Decision);
        }

        return await FinalizeApprovalMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            "approval.resolved",
            receipt,
            cancellationToken);
    }

    private async Task<DocumentMutationReceipt> CancelApprovalCoreAsync(
        DocumentApprovalCancelCommand command,
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
        var hash = DocumentApprovalMutationValidator.Hash(command);
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
        var approval = row is null
            ? null
            : await ReadApprovalForMutationAsync(
                connection,
                transaction,
                command.DocumentId,
                command.ApprovalRequestId,
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
        else if (approval is null)
        {
            receipt = Rejected(
                DocumentMutationStatus.ApprovalNotFound,
                command.DocumentId,
                row.Version,
                row.State,
                row.CurrentVersion);
        }
        else if (approval.State != "pending")
        {
            receipt = RejectedApproval(
                DocumentMutationStatus.ApprovalAlreadyResolved,
                command.DocumentId,
                row,
                command.ApprovalRequestId,
                approval.State);
        }
        else if (row.State != "awaiting_approval")
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
                UPDATE harness.document_approval_requests
                SET state='cancelled',resolved_at=$1,resolution_note=$2,version=version+1
                WHERE document_id=$3 AND id=$4 AND state='pending';
                """,
                cancellationToken,
                Timestamp(command.OccurredAt),
                Text(command.Reason),
                Text(command.DocumentId),
                Text(command.ApprovalRequestId));
            if (changed != 1)
            {
                throw new InvalidOperationException("The locked approval request was not pending.");
            }

            await UpdateDocumentStateAsync(
                connection,
                transaction,
                command.TenantId,
                command.DocumentId,
                command.ExpectedDocumentVersion,
                nextVersion,
                "in_review",
                command.OccurredAt,
                cancellationToken);
            await InsertApprovalTransitionAsync(
                connection,
                transaction,
                command.TransitionId,
                command.TenantId,
                row.ProjectId,
                command.DocumentId,
                nextVersion,
                "awaiting_approval",
                "in_review",
                command.Reason,
                command.ActorKind,
                command.ActorId,
                command.OccurredAt,
                cancellationToken);
            receipt = new DocumentMutationReceipt(
                DocumentMutationStatus.Applied,
                command.DocumentId,
                nextVersion,
                "in_review",
                row.CurrentVersion,
                row.CurrentDocumentVersionId,
                ApprovalRequestId: command.ApprovalRequestId,
                ApprovalState: "cancelled");
        }

        return await FinalizeApprovalMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            "approval.resolved",
            receipt,
            cancellationToken);
    }

    private static async Task<bool> HasPendingApprovalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM harness.document_approval_requests WHERE document_id=$1 AND state='pending');";
        query.Parameters.Add(Text(documentId));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Approval existence query returned no value."));
    }

    private static async Task<DocumentApprovalMutationRow?> ReadApprovalForMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string documentId,
        string approvalId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT state,version FROM harness.document_approval_requests WHERE document_id=$1 AND id=$2;";
        query.Parameters.Add(Text(documentId));
        query.Parameters.Add(Text(approvalId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DocumentApprovalMutationRow(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static async Task UpdateDocumentStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string documentId,
        long expectedVersion,
        long nextVersion,
        string state,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var changed = await ExecuteCountAsync(
            connection,
            transaction,
            """
            UPDATE harness.documents
            SET state=$1,version=$2,updated_at=$3
            WHERE tenant_id=$4 AND id=$5 AND version=$6;
            """,
            cancellationToken,
            Text(state),
            Bigint(nextVersion),
            Timestamp(occurredAt),
            Text(tenantId),
            Text(documentId),
            Bigint(expectedVersion));
        if (changed != 1)
        {
            throw new InvalidOperationException("The locked document version changed unexpectedly.");
        }
    }

    private static async Task InsertApprovalTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string transitionId,
        string tenantId,
        string projectId,
        string documentId,
        long documentVersion,
        string fromState,
        string toState,
        string? note,
        string actorKind,
        string? actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
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
            Text(transitionId),
            Text(tenantId),
            Text(projectId),
            Text(documentId),
            Bigint(documentVersion),
            Text(fromState),
            Text(toState),
            NullableText(note),
            Text(actorKind),
            NullableText(actorId),
            Timestamp(occurredAt));

    private static async Task<DocumentMutationReceipt> FinalizeApprovalMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string hash,
        DateTimeOffset occurredAt,
        string eventType,
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
            var payload = JsonSerializer.Serialize(new
            {
                documentId = receipt.DocumentId,
                documentVersion = receipt.DocumentVersion,
                currentVersion = receipt.CurrentVersion,
                state = receipt.State,
                approvalRequestId = receipt.ApprovalRequestId,
                approvalState = receipt.ApprovalState,
            });
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection,
                transaction,
                tenantId,
                cancellationToken);
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

    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
        Value = (object?)value ?? DBNull.Value,
    };

    private static DocumentMutationReceipt RejectedApproval(
        DocumentMutationStatus status,
        string documentId,
        DocumentMutationRow row,
        string approvalId,
        string approvalState) =>
        new(
            status,
            documentId,
            row.Version,
            row.State,
            row.CurrentVersion,
            row.CurrentDocumentVersionId,
            ApprovalRequestId: approvalId,
            ApprovalState: approvalState);

    private sealed record DocumentApprovalMutationRow(string State, long Version);
}
