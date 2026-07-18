using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDocumentStore
{
    public Task<DocumentMutationReceipt> RequestApprovalAsync(
        DocumentApprovalRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentApprovalMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => RequestApprovalCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DocumentMutationReceipt> ResolveApprovalAsync(
        DocumentApprovalResolveCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentApprovalMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ResolveApprovalCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DocumentMutationReceipt> CancelApprovalAsync(
        DocumentApprovalCancelCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentApprovalMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CancelApprovalCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<DocumentMutationReceipt> RequestApprovalCoreAsync(
        SqliteConnection connection,
        DocumentApprovalRequestCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO document_approval_requests
                    (id,tenant_id,project_id,document_id,document_version_id,title,description,
                     priority,due_at,state,requested_by_agent_id,requested_at,version)
                VALUES ($approvalId,$tenantId,$projectId,$documentId,$documentVersionId,$title,
                        $description,$priority,$dueAt,'pending',$requestedBy,$occurredAt,1);

                UPDATE documents
                SET state='awaiting_approval',version=$nextVersion,updated_at=$occurredAt
                WHERE tenant_id=$tenantId AND id=$documentId AND version=$expectedVersion;

                INSERT INTO document_state_transitions
                    (id,tenant_id,project_id,document_id,document_version,from_state,to_state,
                     note,actor_kind,actor_id,occurred_at)
                VALUES ($transitionId,$tenantId,$projectId,$documentId,$nextVersion,'in_review',
                        'awaiting_approval',NULL,'agent',$requestedBy,$occurredAt);
                """;
            Add(mutation, "$approvalId", command.ApprovalRequestId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$documentId", command.DocumentId);
            Add(mutation, "$documentVersionId", row.CurrentDocumentVersionId);
            Add(mutation, "$title", command.Title);
            Add(mutation, "$description", command.Description);
            Add(mutation, "$priority", command.Priority);
            AddNullable(mutation, "$dueAt", command.DueAt is null ? null : Store(command.DueAt.Value));
            Add(mutation, "$requestedBy", command.RequestedByAgentId);
            Add(mutation, "$occurredAt", Store(command.OccurredAt));
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$expectedVersion", command.ExpectedDocumentVersion);
            Add(mutation, "$transitionId", command.TransitionId);
            if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 3)
            {
                throw new InvalidOperationException(
                    "The approval request did not update all transactional projections.");
            }

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

    private static async Task<DocumentMutationReceipt> ResolveApprovalCoreAsync(
        SqliteConnection connection,
        DocumentApprovalResolveCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE document_approval_requests
                SET state=$decision,resolved_by_profile_id=$resolvedBy,resolved_at=$occurredAt,
                    resolution_note=$note,version=version+1
                WHERE document_id=$documentId AND id=$approvalId AND state='pending';

                UPDATE documents
                SET state=$targetState,version=$nextVersion,updated_at=$occurredAt
                WHERE tenant_id=$tenantId AND id=$documentId AND version=$expectedVersion;

                INSERT INTO document_state_transitions
                    (id,tenant_id,project_id,document_id,document_version,from_state,to_state,
                     note,actor_kind,actor_id,occurred_at)
                VALUES ($transitionId,$tenantId,$projectId,$documentId,$nextVersion,
                        'awaiting_approval',$targetState,$note,'user',$resolvedBy,$occurredAt);
                """;
            Add(mutation, "$decision", command.Decision);
            Add(mutation, "$resolvedBy", command.ResolvedByProfileId);
            Add(mutation, "$occurredAt", Store(command.OccurredAt));
            AddNullable(mutation, "$note", command.Note);
            Add(mutation, "$documentId", command.DocumentId);
            Add(mutation, "$approvalId", command.ApprovalRequestId);
            Add(mutation, "$targetState", targetState);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedDocumentVersion);
            Add(mutation, "$transitionId", command.TransitionId);
            Add(mutation, "$projectId", row.ProjectId);
            if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 3)
            {
                throw new InvalidOperationException(
                    "The approval resolution did not update all transactional projections.");
            }

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

    private static async Task<DocumentMutationReceipt> CancelApprovalCoreAsync(
        SqliteConnection connection,
        DocumentApprovalCancelCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE document_approval_requests
                SET state='cancelled',resolved_at=$occurredAt,resolution_note=$reason,
                    version=version+1
                WHERE document_id=$documentId AND id=$approvalId AND state='pending';

                UPDATE documents
                SET state='in_review',version=$nextVersion,updated_at=$occurredAt
                WHERE tenant_id=$tenantId AND id=$documentId AND version=$expectedVersion;

                INSERT INTO document_state_transitions
                    (id,tenant_id,project_id,document_id,document_version,from_state,to_state,
                     note,actor_kind,actor_id,occurred_at)
                VALUES ($transitionId,$tenantId,$projectId,$documentId,$nextVersion,
                        'awaiting_approval','in_review',$reason,$actorKind,$actorId,$occurredAt);
                """;
            Add(mutation, "$occurredAt", Store(command.OccurredAt));
            Add(mutation, "$reason", command.Reason);
            Add(mutation, "$documentId", command.DocumentId);
            Add(mutation, "$approvalId", command.ApprovalRequestId);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedDocumentVersion);
            Add(mutation, "$transitionId", command.TransitionId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$actorKind", command.ActorKind);
            AddNullable(mutation, "$actorId", command.ActorId);
            if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 3)
            {
                throw new InvalidOperationException(
                    "The approval cancellation did not update all transactional projections.");
            }

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
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM document_approval_requests WHERE document_id=$documentId AND state='pending');";
        Add(query, "$documentId", documentId);
        return Convert.ToInt32(
            await query.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<DocumentApprovalMutationRow?> ReadApprovalForMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        string approvalId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT state,version FROM document_approval_requests WHERE document_id=$documentId AND id=$approvalId;";
        Add(query, "$documentId", documentId);
        Add(query, "$approvalId", approvalId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DocumentApprovalMutationRow(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<DocumentMutationReceipt> FinalizeApprovalMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
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
