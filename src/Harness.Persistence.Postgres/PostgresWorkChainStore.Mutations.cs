using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkChainStore
{
    public Task<WorkChainMutationReceipt> TriageTaskAsync(
        WorkTaskLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return TransitionInitialTaskCoreAsync(
            command,
            "draft",
            "triaged",
            "triaged",
            "backlog",
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> MarkTaskReadyAsync(
        WorkTaskLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return TransitionInitialTaskCoreAsync(
            command,
            "triaged",
            "ready",
            "requirementsCompleted",
            "ready",
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> TransitionInitialTaskCoreAsync(
        WorkTaskLifecycleCommand command,
        string expectedState,
        string targetState,
        string transitionEvent,
        string targetBoardState,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            attemptId: null,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(
                WorkChainMutationStatus.NotFound,
                command.TaskId,
                attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                attemptId: null);
        }
        else if (row.TaskState != expectedState)
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                attemptId: null);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state=$1,version=$2,updated_at=$3,board_state=$4,blocked_reason=NULL
                WHERE id=$5 AND tenant_id=$6 AND version=$7;
                """,
                cancellationToken,
                Text(targetState),
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(targetBoardState),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                null,
                nextVersion,
                targetState,
                row.LatestAttemptState);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                expectedState,
                targetState,
                transitionEvent,
                "backlog",
                command.ActorKind,
                command.ActorId,
                command.Reason,
                command.EvidenceReference),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> AssignTaskAsync(
        WorkTaskAssignmentCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return AssignTaskCoreAsync(command, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> AssignTaskCoreAsync(
        WorkTaskAssignmentCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            attemptId: null,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(
                WorkChainMutationStatus.NotFound,
                command.TaskId,
                attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                attemptId: null);
        }
        else if (row.TaskState != "ready" ||
            row.LatestInstructionId != command.InstructionVersionId ||
            (row.LatestAttemptState == "rejected" &&
                row.LatestAttemptInstructionId == command.InstructionVersionId))
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                attemptId: null);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='assigned',version=$1,updated_at=$2,board_state='development',
                    assignee_agent_id=$3,blocked_reason=NULL
                WHERE id=$4 AND tenant_id=$5 AND version=$6;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.AssigneeAgentId),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                null,
                nextVersion,
                "assigned",
                row.LatestAttemptState);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "ready",
                "assigned",
                "leaseAcquired",
                "ready",
                command.ActorKind,
                command.ActorId,
                "Task lease acquired for the assigned agent.",
                command.LeaseReference),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> AddInstructionVersionAsync(
        WorkInstructionVersionCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return AddInstructionVersionCoreAsync(command, cancellationToken);
    }

    public async Task<WorkChainMutationReceipt> StartAttemptAsync(
        WorkAttemptStartCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        var assignment = await AssignTaskAsync(
            new WorkTaskAssignmentCommand(
                command.TenantId,
                command.SolicitationId,
                command.TaskId,
                command.InstructionVersionId,
                command.ProducerAgentId,
                "agent",
                command.ProducerAgentId,
                $"attempt:{command.AttemptId}",
                command.ExpectedTaskVersion,
                $"{command.IdempotencyKey}:lease",
                command.OccurredAt),
            cancellationToken);
        if (assignment.Status == WorkChainMutationStatus.Applied ||
            (assignment.Status == WorkChainMutationStatus.IdempotentReplay &&
                assignment.TaskState == "assigned"))
        {
            command = command with
            {
                ExpectedTaskVersion = assignment.TaskVersion
                    ?? throw new InvalidOperationException(
                        "Applied assignment has no task version."),
            };
        }
        else if (assignment.Status != WorkChainMutationStatus.InvalidState ||
            assignment.TaskState != "assigned" ||
            assignment.TaskVersion != command.ExpectedTaskVersion)
        {
            return assignment with { AttemptId = command.AttemptId };
        }

        return await StartAttemptCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CompleteAttemptAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return CompleteAttemptCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ExpireAttemptLeaseAsync(
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return ExpireAttemptLeaseCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> BlockRunningTaskAsync(
        WorkTaskBlockCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return BlockRunningTaskCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> UnblockTaskAsync(
        WorkTaskUnblockCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return UnblockTaskCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ReplanEscalatedTaskAsync(
        WorkTaskReplanCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return ReplanEscalatedTaskCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ReviewAttemptAsync(
        WorkAttemptReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return ReviewAttemptCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> MergeApprovedTaskAsync(
        WorkTaskMergeCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return MergeApprovedTaskCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CompleteMergedTaskAsync(
        WorkTaskDeliveryCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return CompleteMergedTaskCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CancelRunningTaskAsync(
        WorkTaskCancellationCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return CancelRunningTaskCoreAsync(command, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> AddInstructionVersionCoreAsync(
        WorkInstructionVersionCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, attemptId: null, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, attemptId: null);
        }
        else if (row.TaskState != "running" || row.LatestAttemptState != "rejected")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, attemptId: null);
        }
        else
        {
            var instructionVersion = row.LatestInstructionVersion + 1;
            var nextTaskVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.instruction_versions
                    (id, tenant_id, project_id, task_id, version, content, content_hash,
                     supersedes_id, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9);
                """,
                cancellationToken,
                Text(command.InstructionVersionId), Text(command.TenantId), Text(row.ProjectId),
                Text(command.TaskId), Integer(instructionVersion), Text(command.Content),
                Text(command.ContentHash), Text(row.LatestInstructionId), Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = 'ready', version = $1, updated_at = $2,
                    board_state = 'ready', blocked_reason = NULL
                WHERE id = $3 AND tenant_id = $4 AND version = $5;
                """,
                cancellationToken,
                Bigint(nextTaskVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, null,
                nextTaskVersion, "ready", row.LatestAttemptState,
                InstructionVersionId: command.InstructionVersionId,
                InstructionVersion: instructionVersion);
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "task.stateChanged", command.OccurredAt, receipt,
            new TransitionAudit(
                "running",
                "ready",
                "correctionPrepared",
                "corrections",
                "system",
                "work-chain",
                "A new immutable correction instruction was prepared.",
                $"instruction:{command.InstructionVersionId}"),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> ReplanEscalatedTaskCoreAsync(
        WorkTaskReplanCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            attemptId: null,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                null);
        }
        else if (row.TaskState != "escalated" || row.LatestAttemptState != "rejected")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                null);
        }
        else
        {
            var instructionVersion = row.LatestInstructionVersion + 1;
            var nextTaskVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.instruction_versions
                    (id,tenant_id,project_id,task_id,version,content,content_hash,
                     supersedes_id,created_at,author_kind,author_id)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,'chief',$10);
                """,
                cancellationToken,
                Text(command.InstructionVersionId),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.TaskId),
                Integer(instructionVersion),
                Text(command.Content),
                Text(command.ContentHash),
                Text(row.LatestInstructionId),
                Timestamp(command.OccurredAt),
                Text(command.ChiefAgentId));
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='ready',version=$1,updated_at=$2,
                    board_state='ready',blocked_reason=NULL,assignee_agent_id=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextTaskVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                null,
                nextTaskVersion,
                "ready",
                row.LatestAttemptState,
                InstructionVersionId: command.InstructionVersionId,
                InstructionVersion: instructionVersion);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "escalated",
                "ready",
                "replanned",
                "blocked",
                "chief",
                command.ChiefAgentId,
                command.Reason,
                command.EvidenceReference),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> StartAttemptCoreAsync(
        WorkAttemptStartCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, attemptId: null, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "assigned" ||
            row.AssigneeAgentId != command.ProducerAgentId ||
            row.LatestInstructionId != command.InstructionVersionId ||
            (row.LatestAttemptState == "rejected" && row.LatestAttemptInstructionId == command.InstructionVersionId))
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.work_attempts
                    (id, tenant_id, project_id, task_id, instruction_version_id, attempt_number,
                     producer_agent_id, state, started_at, operational_state)
                VALUES ($1, $2, $3, $4, $5, $6, $7, 'running', $8, 'running');
                """,
                cancellationToken,
                Text(command.AttemptId), Text(command.TenantId), Text(row.ProjectId), Text(command.TaskId),
                Text(command.InstructionVersionId), Integer(row.AttemptCount + 1),
                Text(command.ProducerAgentId), Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.attempt_events (id, tenant_id, project_id, attempt_id, kind, content, occurred_at)
                VALUES ($1, $2, $3, $4, 'log', 'Attempt started.', $5);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
                Text(row.ProjectId), Text(command.AttemptId), Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = 'running', version = $1, updated_at = $2,
                    board_state = 'development', blocked_reason = NULL
                WHERE id = $3 AND tenant_id = $4 AND version = $5;
                """,
                cancellationToken,
                Bigint(nextVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "running", "running");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "attempt.started", command.OccurredAt, receipt,
            new TransitionAudit(
                "assigned",
                "running",
                "heartbeatConfirmed",
                "development",
                "agent",
                command.ProducerAgentId,
                "Assigned agent confirmed the execution heartbeat.",
                $"attempt:{command.AttemptId}"),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> CompleteAttemptCoreAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, command.AttemptId, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "running" || row.AttemptState != "running")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else
        {
            for (var index = 0; index < command.Evidence.Count; index++)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.work_evidence
                        (id, tenant_id, project_id, attempt_id, ordinal, reference, created_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7);
                    """,
                    cancellationToken,
                    Text(command.Evidence[index].EvidenceId), Text(command.TenantId), Text(row.ProjectId),
                    Text(command.AttemptId), Integer(index + 1), Text(command.Evidence[index].Reference),
                    Timestamp(command.OccurredAt));
            }

            await ExecuteAsync(
                connection, transaction,
                "UPDATE harness.work_attempts SET state = 'awaiting_review', completed_at = $1, operational_state = 'completed' WHERE id = $2 AND state = 'running';",
                cancellationToken,
                Timestamp(command.OccurredAt), Text(command.AttemptId));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.attempt_events (id, tenant_id, project_id, attempt_id, kind, content, occurred_at)
                VALUES ($1, $2, $3, $4, 'log', 'Attempt submitted for review.', $5);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
                Text(row.ProjectId), Text(command.AttemptId), Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = 'awaiting_review', version = $1, updated_at = $2,
                    board_state = 'review', blocked_reason = NULL
                WHERE id = $3 AND tenant_id = $4 AND version = $5;
                """,
                cancellationToken,
                Bigint(nextVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "awaiting_review", "awaiting_review");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "attempt.completed", command.OccurredAt, receipt, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> ReviewAttemptCoreAsync(
        WorkAttemptReviewCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, command.AttemptId, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "awaiting_review" || row.AttemptState != "awaiting_review")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else if (string.Equals(
            row.ProducerAgentId,
            command.ReviewerAgentId,
            StringComparison.Ordinal))
        {
            receipt = Rejected(
                WorkChainMutationStatus.IndependentReviewerRequired,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.work_reviews
                    (id, tenant_id, project_id, attempt_id, reviewer_agent_id, decision, rationale, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                """,
                cancellationToken,
                Text(command.ReviewId), Text(command.TenantId), Text(row.ProjectId), Text(command.AttemptId),
                Text(command.ReviewerAgentId), Text(command.Decision), Text(command.Rationale),
                Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_attempts SET state = $1,
                    operational_state = CASE WHEN $1 = 'rejected' THEN 'failed' ELSE 'completed' END
                WHERE id = $2 AND state = 'awaiting_review';
                """,
                cancellationToken,
                Text(command.Decision), Text(command.AttemptId));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.attempt_events (id, tenant_id, project_id, attempt_id, kind, content, occurred_at, severity)
                VALUES ($1, $2, $3, $4, 'note', $5, $6, $7);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
                Text(row.ProjectId), Text(command.AttemptId), Text(command.Rationale),
                Timestamp(command.OccurredAt),
                Text(command.Decision == "rejected" ? "error" : "info"));
            var rejectedReviewCount = row.RejectedReviewCount +
                (command.Decision == "rejected" ? 1 : 0);
            var taskState = command.Decision == "approved"
                ? "approved"
                : rejectedReviewCount > _maximumReviewCycles
                    ? "escalated"
                    : "running";
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = $1, version = $2, updated_at = $3,
                    board_state = CASE
                        WHEN $1 = 'approved' THEN 'review'
                        WHEN $1 = 'escalated' THEN 'blocked'
                        ELSE 'corrections'
                    END,
                    blocked_reason = CASE WHEN $1 = 'escalated' THEN $4 ELSE NULL END
                WHERE id = $5 AND tenant_id = $6 AND version = $7;
                """,
                cancellationToken,
                Text(taskState), Bigint(nextVersion), Timestamp(command.OccurredAt),
                Text(command.Rationale), Text(command.TaskId), Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, taskState, command.Decision);
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "task.stateChanged", command.OccurredAt, receipt,
            new TransitionAudit(
                "awaiting_review",
                receipt.TaskState ?? "awaiting_review",
                command.Decision == "approved"
                    ? "reviewApproved"
                    : receipt.TaskState == "escalated"
                        ? "reviewLimitExceeded"
                        : "reviewRejected",
                "review",
                "agent",
                command.ReviewerAgentId,
                command.Rationale,
                $"review:{command.ReviewId}"),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> MergeApprovedTaskCoreAsync(
        WorkTaskMergeCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            attemptId: null,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(
                WorkChainMutationStatus.NotFound,
                command.TaskId,
                attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                attemptId: null);
        }
        else if (row.TaskState != "approved" || row.LatestAttemptState != "approved")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                attemptId: null);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='merged',version=$1,updated_at=$2,
                    board_state='review',blocked_reason=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                null,
                nextVersion,
                "merged",
                row.LatestAttemptState);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "approved",
                "merged",
                "mergeCompleted",
                "review",
                "agent",
                command.CoordinatorAgentId,
                "Approved submission merged by the coordinator.",
                command.SubmissionReference),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> CompleteMergedTaskCoreAsync(
        WorkTaskDeliveryCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            attemptId: null,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(
                WorkChainMutationStatus.NotFound,
                command.TaskId,
                attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                attemptId: null);
        }
        else if (row.TaskState != "merged" || row.LatestAttemptState != "approved")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                attemptId: null);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='completed',version=$1,updated_at=$2,
                    board_state='done',blocked_reason=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                null,
                nextVersion,
                "completed",
                row.LatestAttemptState);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "merged",
                "completed",
                "deliveryCompleted",
                "review",
                "agent",
                command.ActorId,
                "Merged delivery reconciled and completed.",
                command.EvidenceReference),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> CancelRunningTaskCoreAsync(
        WorkTaskCancellationCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(
                WorkChainMutationStatus.NotFound,
                command.TaskId,
                command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else if (row.TaskState != "running" || row.AttemptState != "running")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_attempts
                SET state='rejected',operational_state='cancelled',
                    completed_at=$1,failure_reason=$2
                WHERE id=$3 AND tenant_id=$4
                  AND state='running' AND operational_state='running';
                """,
                cancellationToken,
                Timestamp(command.OccurredAt),
                Text(command.Reason),
                Text(command.AttemptId),
                Text(command.TenantId));
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES ($1,$2,$3,$4,'note',$5,$6,'warning');
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.AttemptId),
                Text(command.Reason),
                Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='cancelled',version=$1,updated_at=$2,
                    board_state='done',blocked_reason=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                command.AttemptId,
                nextVersion,
                "cancelled",
                "cancelled");
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "running",
                "cancelled",
                "cancelled",
                "development",
                command.ActorKind,
                command.ActorId,
                command.Reason,
                command.EvidenceReference),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> ExpireAttemptLeaseCoreAsync(
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else if (row.TaskState != "running" || row.AttemptState != "running")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_attempts
                SET state='rejected',operational_state='cancelled',completed_at=$1
                WHERE id=$2 AND tenant_id=$3
                  AND state='running' AND operational_state='running';
                """,
                cancellationToken,
                Timestamp(command.OccurredAt),
                Text(command.AttemptId),
                Text(command.TenantId));
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES
                    ($1,$2,$3,$4,'log','Attempt abandoned; card returned to ready.',$5,'warning');
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.AttemptId),
                Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='ready',version=$1,updated_at=$2,
                    board_state='ready',blocked_reason=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                command.AttemptId,
                nextVersion,
                "ready",
                "abandoned");
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> BlockRunningTaskCoreAsync(
        WorkTaskBlockCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(
                WorkChainMutationStatus.NotFound,
                command.TaskId,
                command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else if (row.TaskState != "running" || row.AttemptState != "running")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_attempts
                SET state='rejected',operational_state='cancelled',
                    completed_at=$1,failure_reason=NULL
                WHERE id=$2 AND tenant_id=$3
                  AND state='running' AND operational_state='running';
                """,
                cancellationToken,
                Timestamp(command.OccurredAt),
                Text(command.AttemptId),
                Text(command.TenantId));
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES ($1,$2,$3,$4,'note',$5,$6,'error');
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.AttemptId),
                Text(command.Reason),
                Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='blocked',version=$1,updated_at=$2,
                    board_state='blocked',blocked_reason=$3
                WHERE id=$4 AND tenant_id=$5 AND version=$6;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.Reason),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                command.AttemptId,
                nextVersion,
                "blocked",
                "abandoned");
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "running",
                "blocked",
                "blocked",
                "development",
                command.ActorKind,
                command.ActorId,
                command.Reason,
                command.EvidenceReference),
            cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> UnblockTaskCoreAsync(
        WorkTaskUnblockCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
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
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            attemptId: null,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                null);
        }
        else if (row.TaskState != "blocked" || row.LatestAttemptState != "abandoned")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                null);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='ready',version=$1,updated_at=$2,
                    board_state='ready',blocked_reason=NULL,assignee_agent_id=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                null,
                nextVersion,
                "ready",
                row.LatestAttemptState);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            new TransitionAudit(
                "blocked",
                "ready",
                "unblocked",
                "blocked",
                command.ActorKind,
                command.ActorId,
                command.Resolution,
                command.EvidenceReference),
            cancellationToken);
    }

    private static async Task<TaskRow?> ReadTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string solicitationId,
        string taskId,
        string? attemptId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT t.project_id, t.version, t.state, t.risk_tier,
                   (SELECT id FROM harness.instruction_versions i WHERE i.task_id = t.id ORDER BY version DESC LIMIT 1),
                   (SELECT version FROM harness.instruction_versions i WHERE i.task_id = t.id ORDER BY version DESC LIMIT 1),
                   (SELECT COUNT(*) FROM harness.work_attempts x WHERE x.task_id = t.id),
                   (SELECT COUNT(*)
                    FROM harness.work_reviews r
                    JOIN harness.work_attempts reviewed ON reviewed.id = r.attempt_id
                    WHERE reviewed.task_id = t.id AND r.decision = 'rejected'),
                   (SELECT CASE
                                WHEN operational_state='cancelled' AND state='rejected'
                                     AND failure_reason IS NULL THEN 'abandoned'
                                WHEN operational_state='cancelled' AND state='rejected'
                                     THEN 'cancelled'
                                ELSE state
                            END
                    FROM harness.work_attempts x WHERE x.task_id = t.id
                    ORDER BY attempt_number DESC LIMIT 1),
                   (SELECT instruction_version_id FROM harness.work_attempts x WHERE x.task_id = t.id ORDER BY attempt_number DESC LIMIT 1),
                   CASE
                       WHEN a.operational_state='cancelled' AND a.state='rejected'
                            AND a.failure_reason IS NULL THEN 'abandoned'
                       WHEN a.operational_state='cancelled' AND a.state='rejected'
                            THEN 'cancelled'
                       ELSE a.state
                   END,
                   a.producer_agent_id,
                   t.assignee_agent_id
            FROM harness.work_tasks t
            JOIN harness.demands d ON d.id = t.demand_id
            JOIN harness.solicitations s ON s.id = d.solicitation_id
            LEFT JOIN harness.work_attempts a ON a.id = $1 AND a.task_id = t.id
            WHERE t.tenant_id = $2 AND s.id = $3 AND t.id = $4
            FOR UPDATE OF t;
            """;
        query.Parameters.Add(NullableText(attemptId));
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(solicitationId));
        query.Parameters.Add(Text(taskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TaskRow(
                reader.GetString(0).TrimEnd(), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4).TrimEnd(), reader.GetInt32(5), checked((int)reader.GetInt64(6)),
                checked((int)reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9).TrimEnd(),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12))
            : null;
    }

    private static async Task<WorkChainMutationReceipt?> ReadMutationInboxAsync(
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
            "SELECT message_hash, response_json::text FROM harness.inbox_messages WHERE tenant_id = $1 AND idempotency_key = $2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0).TrimEnd(), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException("The idempotency key belongs to a different work-chain mutation.");
        }

        return JsonSerializer.Deserialize<WorkChainMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted mutation receipt is invalid.");
    }

    private static Task<WorkChainMutationReceipt> FinalizeMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        string eventType,
        DateTimeOffset occurredAt,
        WorkChainMutationReceipt receipt,
        CancellationToken cancellationToken) =>
        FinalizeMutationAsync(
            connection,
            transaction,
            tenantId,
            key,
            hash,
            eventType,
            occurredAt,
            receipt,
            transitionAudit: null,
            cancellationToken);

    private static async Task<WorkChainMutationReceipt> FinalizeMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        string eventType,
        DateTimeOffset occurredAt,
        WorkChainMutationReceipt receipt,
        TransitionAudit? transitionAudit,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == WorkChainMutationStatus.Applied)
        {
            await ExecuteAsync(
                connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
                cancellationToken,
                Text($"audit-ledger:{tenantId}"));
            var outboxPayload = await BuildOutboxPayloadAsync(
                connection,
                transaction,
                eventType,
                receipt,
                transitionAudit,
                cancellationToken);
            var auditPayload = transitionAudit is null
                ? outboxPayload
                : JsonSerializer.Serialize(new
                {
                    cardId = receipt.TaskId,
                    fromState = transitionAudit.FromState,
                    toState = transitionAudit.ToState,
                    @event = transitionAudit.Event,
                    actorKind = transitionAudit.ActorKind,
                    actorId = transitionAudit.ActorId,
                    timestamp = occurredAt,
                    reason = transitionAudit.Reason,
                    evidenceRef = transitionAudit.EvidenceReference,
                    version = receipt.TaskVersion,
                });
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection, transaction, tenantId, cancellationToken);
            var eventHash = AuditLedgerHash.Compute(
                previousHash, tenantId, sequence, eventType, auditPayload, occurredAt);
            var outboxId = UlidValue.New(occurredAt).ToString();
            final = receipt with
            {
                LedgerSequence = sequence,
                LedgerHash = eventHash,
                OutboxMessageId = outboxId,
            };
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.audit_ledger
                    (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                """,
                cancellationToken,
                Text(UlidValue.New(occurredAt).ToString()), Text(tenantId), Bigint(sequence),
                Text(previousHash), Text(eventHash), Text(eventType), Json(auditPayload), Timestamp(occurredAt));
            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
                cancellationToken,
                Text(outboxId), Text(tenantId), Text(eventType), Json(outboxPayload), Timestamp(occurredAt));
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(tenantId), Text(key), Text(hash), Json(JsonSerializer.Serialize(final)), Timestamp(occurredAt));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static async Task<string> BuildOutboxPayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        WorkChainMutationReceipt receipt,
        TransitionAudit? transitionAudit,
        CancellationToken cancellationToken)
    {
        if (eventType != "task.stateChanged")
        {
            return JsonSerializer.Serialize(new
            {
                taskId = receipt.TaskId,
                attemptId = receipt.AttemptId,
                taskState = receipt.TaskState,
                attemptState = receipt.AttemptState,
                instructionVersionId = receipt.InstructionVersionId,
                instructionVersion = receipt.InstructionVersion,
                version = receipt.TaskVersion,
            });
        }

        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT project_id,board_state FROM harness.work_tasks WHERE id=$1;";
        query.Parameters.Add(Text(receipt.TaskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Applied work mutation lost its task projection.");
        }

        var projectId = reader.GetString(0).TrimEnd();
        var boardState = reader.GetString(1);
        var from = transitionAudit?.FromBoardState ??
            (receipt.AttemptState == "abandoned"
                ? "development"
                : receipt.InstructionVersionId is not null
                    ? "corrections"
                    : "review");
        return JsonSerializer.Serialize(new
        {
            projectId,
            taskId = receipt.TaskId,
            from,
            to = boardState,
            changedByKind = transitionAudit?.ActorKind ?? "system",
            note = transitionAudit?.Reason,
        });
    }

    private static Task LockTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string taskId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"work-task:{taskId}"));

    private static WorkChainMutationReceipt Rejected(
        WorkChainMutationStatus status,
        string taskId,
        string? attemptId) => new(status, taskId, attemptId, null, null, null);

    private static WorkChainMutationReceipt Rejected(
        WorkChainMutationStatus status,
        TaskRow row,
        string taskId,
        string? attemptId) => new(
            status, taskId, attemptId, row.Version, row.TaskState, row.AttemptState);

    private static NpgsqlParameter<string?> NullableText(string? value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private sealed record TaskRow(
        string ProjectId,
        long Version,
        string TaskState,
        string RiskTier,
        string LatestInstructionId,
        int LatestInstructionVersion,
        int AttemptCount,
        int RejectedReviewCount,
        string? LatestAttemptState,
        string? LatestAttemptInstructionId,
        string? AttemptState,
        string? ProducerAgentId,
        string? AssigneeAgentId);

    private sealed record TransitionAudit(
        string FromState,
        string ToState,
        string Event,
        string FromBoardState,
        string ActorKind,
        string ActorId,
        string Reason,
        string EvidenceReference);
}
