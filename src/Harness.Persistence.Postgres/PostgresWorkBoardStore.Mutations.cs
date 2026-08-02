using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkBoardStore
{
    public Task<BoardSolicitationRecord> TransitionSolicitationAsync(
        BoardSolicitationTransitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionSolicitationCoreAsync(command, cancellationToken);
    }

    public Task<BoardTaskRecord> MoveTaskAsync(
        BoardTaskMoveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MoveTaskCoreAsync(command, cancellationToken);
    }

    public Task<BoardTaskRecord> SetTaskPriorityAsync(
        BoardTaskPriorityCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetTaskPriorityCoreAsync(command, cancellationToken);
    }

    public Task<BoardTaskRecord> SetTaskPlanningAsync(
        BoardTaskPlanningCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetTaskPlanningCoreAsync(command, cancellationToken);
    }

    public Task<BoardTaskRecord> SetTaskArchivedAsync(
        BoardTaskArchiveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetTaskArchivedCoreAsync(command, cancellationToken);
    }

    public Task<BoardTaskRecord> DismissTaskAsync(
        BoardTaskDismissCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DismissTaskCoreAsync(command, cancellationToken);
    }

    public Task<BoardInstructionRecord> AppendInstructionAsync(
        BoardInstructionAppendCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AppendInstructionCoreAsync(command, cancellationToken);
    }

    private async Task<BoardSolicitationRecord> TransitionSolicitationCoreAsync(
        BoardSolicitationTransitionCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadSolicitationAsync(
                connection, transaction, command.TenantId, command.SolicitationId,
                includeInternal: false, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("solicitation");
        if (!CanTransitionSolicitation(current.State, command.State))
        {
            throw new WorkBoardInvalidStateException(
                $"Solicitation cannot transition from {current.State} to {command.State}.");
        }

        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.solicitations SET state=$1 WHERE tenant_id=$2 AND id=$3 AND is_internal=false;",
            cancellationToken,
            Text(command.State), Text(command.TenantId), Text(command.SolicitationId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            solicitationId = current.Id,
            from = current.State,
            to = command.State,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "solicitation.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = command.State };
    }

    private async Task<BoardTaskRecord> MoveTaskCoreAsync(
        BoardTaskMoveCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadTaskAsync(
                connection, transaction, command.TenantId, command.TaskId, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (current.ArchivedAt is not null)
        {
            throw new WorkBoardInvalidStateException("An archived task cannot change board state.");
        }
        if (command.ToState == "done" && current.InternalState != "completed")
        {
            throw new WorkBoardInvalidStateException("Only a completed task can move to done.");
        }

        if (command.ToState == "blocked" && string.IsNullOrWhiteSpace(command.Note) &&
            string.IsNullOrWhiteSpace(current.BlockedReason))
        {
            throw new WorkBoardInvalidStateException("Moving a task to blocked requires a note.");
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        var blockedReason = command.ToState == "blocked" ? note ?? current.BlockedReason : null;
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_tasks SET board_state=$1,blocked_reason=$2,version=version+1,updated_at=$3 WHERE tenant_id=$4 AND id=$5;",
            cancellationToken,
            Text(command.ToState), NullableText(blockedReason), Timestamp(command.OccurredAt),
            Text(command.TenantId), Text(command.TaskId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.State,
            to = command.ToState,
            changedByKind = command.ChangedByKind,
            note,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            State = command.ToState,
            BlockedReason = blockedReason,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private async Task<BoardTaskRecord> SetTaskPriorityCoreAsync(
        BoardTaskPriorityCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadTaskAsync(
                connection, transaction, command.TenantId, command.TaskId, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_tasks SET priority=$1,risk_tier=$1,version=version+1,updated_at=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text(command.Priority), Timestamp(command.OccurredAt), Text(command.TenantId),
            Text(command.TaskId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.Priority,
            to = command.Priority,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.priorityChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            Priority = command.Priority,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private async Task<BoardTaskRecord> SetTaskPlanningCoreAsync(
        BoardTaskPlanningCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadTaskAsync(
                connection, transaction, command.TenantId, command.TaskId, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (current.ArchivedAt is not null || current.State == "done")
            throw new WorkBoardInvalidStateException("A completed or archived task cannot be replanned.");

        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_tasks SET assignee_agent_id=$1,due_at=$2,version=version+1,updated_at=$3 WHERE tenant_id=$4 AND id=$5;",
            cancellationToken,
            Text(command.AssigneeAgentId), Timestamp(command.DueAt), Timestamp(command.OccurredAt),
            Text(command.TenantId), Text(command.TaskId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.State,
            to = current.State,
            changedByKind = "user",
            note = "Planejamento da entrega atualizado.",
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            AssigneeAgentId = command.AssigneeAgentId,
            DueAt = command.DueAt,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private async Task<BoardTaskRecord> SetTaskArchivedCoreAsync(
        BoardTaskArchiveCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadTaskAsync(
                connection, transaction, command.TenantId, command.TaskId, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (command.Archived)
        {
            if (current.ArchivedAt is not null)
                throw new WorkBoardInvalidStateException("Task is already archived.");
            if (current.State != "done")
                throw new WorkBoardInvalidStateException("Only a done task can be archived.");
        }
        else if (current.ArchivedAt is null)
        {
            throw new WorkBoardInvalidStateException("Task is not archived.");
        }

        var archivedAt = command.Archived ? command.OccurredAt : (DateTimeOffset?)null;
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_tasks SET archived_at=$1,version=version+1,updated_at=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            NullableTimestamp(archivedAt), Timestamp(command.OccurredAt), Text(command.TenantId),
            Text(command.TaskId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.State,
            to = current.State,
            changedByKind = command.ChangedByKind,
            note = command.Archived ? "archived" : "unarchived",
            archivedAt,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            ArchivedAt = archivedAt,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private async Task<BoardTaskRecord> DismissTaskCoreAsync(
        BoardTaskDismissCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadTaskAsync(
                connection, transaction, command.TenantId, command.TaskId, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (current.ArchivedAt is not null)
            throw new WorkBoardInvalidStateException("Task is already archived.");
        if (!BoardTaskDismissalPolicy.MayDismiss(current, command))
            throw new WorkBoardInvalidStateException(
                "Only inactive work or a system-superseded approved document can be dismissed.");

        var reason = command.Reason.Trim();
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_tasks SET state='cancelled',board_state='done',archived_at=$1," +
            "blocked_reason=$2,version=version+1,updated_at=$1 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Timestamp(command.OccurredAt), Text(reason), Text(command.TenantId), Text(command.TaskId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.State,
            to = "cancelled",
            changedByKind = command.ChangedByKind,
            note = reason,
            archivedAt = command.OccurredAt,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            State = "done",
            InternalState = "cancelled",
            BlockedReason = reason,
            ArchivedAt = command.OccurredAt,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private async Task<BoardInstructionRecord> AppendInstructionCoreAsync(
        BoardInstructionAppendCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await ReadTaskAsync(
                connection, transaction, command.TenantId, command.TaskId, cancellationToken)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        string? attemptState;
        await using (var latestAttempt = connection.CreateCommand())
        {
            latestAttempt.Transaction = transaction;
            latestAttempt.CommandText =
                "SELECT state FROM harness.work_attempts WHERE tenant_id=$1 AND task_id=$2 " +
                "ORDER BY attempt_number DESC LIMIT 1;";
            latestAttempt.Parameters.Add(Text(command.TenantId));
            latestAttempt.Parameters.Add(Text(command.TaskId));
            attemptState = await latestAttempt.ExecuteScalarAsync(cancellationToken) as string;
        }

        var correction = task.InternalState == "running" && attemptState == "rejected";
        var readyContextRefresh = task.InternalState == "ready" && attemptState != "running" &&
            command.AuthorKind is "chief" or "system";
        if (!correction && !readyContextRefresh)
        {
            throw new WorkBoardInvalidStateException(
                "A new instruction version requires either a rejected attempt awaiting correction " +
                "or a ready card being refreshed by the chief/system before dispatch.");
        }

        var nextVersion = task.InstructionVersion + 1;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Body)));
        string supersedes;
        await using (var latestInstruction = connection.CreateCommand())
        {
            latestInstruction.Transaction = transaction;
            latestInstruction.CommandText =
                "SELECT id FROM harness.instruction_versions WHERE tenant_id=$1 AND task_id=$2 " +
                "ORDER BY version DESC LIMIT 1;";
            latestInstruction.Parameters.Add(Text(command.TenantId));
            latestInstruction.Parameters.Add(Text(command.TaskId));
            supersedes = (await latestInstruction.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new WorkBoardReferenceNotFoundException("instruction"))
                .TrimEnd();
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.instruction_versions
                (id,tenant_id,project_id,task_id,version,content,content_hash,supersedes_id,
                 created_at,author_kind,author_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);
            """,
            cancellationToken,
            Text(command.InstructionId), Text(command.TenantId), Text(task.ProjectId),
            Text(command.TaskId), Integer(nextVersion), Text(command.Body), Text(hash),
            Text(supersedes), Timestamp(command.OccurredAt), Text(command.AuthorKind),
            NullableText(command.AuthorId));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_tasks SET state='ready',board_state='ready',blocked_reason=NULL," +
            "version=version+1,updated_at=$1 WHERE tenant_id=$2 AND id=$3;",
            cancellationToken,
            Timestamp(command.OccurredAt), Text(command.TenantId), Text(command.TaskId));
        var payload = JsonSerializer.Serialize(new
        {
            projectId = task.ProjectId,
            taskId = task.Id,
            from = task.State,
            to = "ready",
            changedByKind = command.AuthorKind,
            note = readyContextRefresh
                ? "delegationContextRefreshed"
                : "instructionVersionAppended",
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BoardInstructionRecord(
            command.TenantId, command.InstructionId, command.TaskId, nextVersion, command.Body,
            command.AuthorKind, command.AuthorId, command.OccurredAt);
    }

    private static bool CanTransitionSolicitation(string from, string to) =>
        from == to || (from, to) switch
        {
            ("open", "inAnalysis") => true,
            ("open", "closed") => true,
            ("inAnalysis", "converted") => true,
            ("inAnalysis", "answered") => true,
            ("inAnalysis", "closed") => true,
            ("converted", "closed") => true,
            ("answered", "closed") => true,
            _ => false,
        };
}
