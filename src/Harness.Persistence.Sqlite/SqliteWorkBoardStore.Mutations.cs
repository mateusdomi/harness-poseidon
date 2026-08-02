using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkBoardStore
{
    public Task<BoardSolicitationRecord> TransitionSolicitationAsync(
        BoardSolicitationTransitionCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => TransitionSolicitationCoreAsync(c, command, t), cancellationToken);

    public Task<BoardTaskRecord> MoveTaskAsync(
        BoardTaskMoveCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => MoveTaskCoreAsync(c, command, t), cancellationToken);

    public Task<BoardTaskRecord> SetTaskPriorityAsync(
        BoardTaskPriorityCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => SetTaskPriorityCoreAsync(c, command, t), cancellationToken);

    public Task<BoardTaskRecord> SetTaskPlanningAsync(
        BoardTaskPlanningCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => SetTaskPlanningCoreAsync(c, command, t), cancellationToken);

    public Task<BoardTaskRecord> SetTaskArchivedAsync(
        BoardTaskArchiveCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => SetTaskArchivedCoreAsync(c, command, t), cancellationToken);

    public Task<BoardTaskRecord> DismissTaskAsync(
        BoardTaskDismissCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => DismissTaskCoreAsync(c, command, t), cancellationToken);

    public Task<BoardInstructionRecord> AppendInstructionAsync(
        BoardInstructionAppendCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => AppendInstructionCoreAsync(c, command, t), cancellationToken);

    private static async Task<BoardSolicitationRecord> TransitionSolicitationCoreAsync(
        SqliteConnection c, BoardSolicitationTransitionCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var current = await ReadSolicitationAsync(
            c, tx, command.TenantId, command.SolicitationId, false, token)
            ?? throw new WorkBoardReferenceNotFoundException("solicitation");
        if (!CanTransitionSolicitation(current.State, command.State))
            throw new WorkBoardInvalidStateException(
                $"Solicitation cannot transition from {current.State} to {command.State}.");

        await using var mutation = c.CreateCommand(); mutation.Transaction = tx;
        mutation.CommandText = "UPDATE solicitations SET state=$state WHERE tenant_id=$tenant AND id=$id AND is_internal=0;";
        Add(mutation, "$state", command.State); Add(mutation, "$tenant", command.TenantId);
        Add(mutation, "$id", command.SolicitationId); await mutation.ExecuteNonQueryAsync(token);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            solicitationId = current.Id,
            from = current.State,
            to = command.State,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, command.TenantId, "solicitation.stateChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
        return current with { State = command.State };
    }

    private static async Task<BoardTaskRecord> MoveTaskCoreAsync(
        SqliteConnection c, BoardTaskMoveCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var current = await ReadTaskAsync(c, tx, command.TenantId, command.TaskId, token)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (current.ArchivedAt is not null)
            throw new WorkBoardInvalidStateException("An archived task cannot change board state.");
        if (command.ToState == "done" && current.InternalState != "completed")
            throw new WorkBoardInvalidStateException("Only a completed task can move to done.");
        if (command.ToState == "blocked" && string.IsNullOrWhiteSpace(command.Note) &&
            string.IsNullOrWhiteSpace(current.BlockedReason))
            throw new WorkBoardInvalidStateException("Moving a task to blocked requires a note.");

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        var blockedReason = command.ToState == "blocked" ? note ?? current.BlockedReason : null;
        await using var mutation = c.CreateCommand(); mutation.Transaction = tx;
        mutation.CommandText =
            "UPDATE work_tasks SET board_state=$state,blocked_reason=$reason,version=version+1,updated_at=$at " +
            "WHERE tenant_id=$tenant AND id=$id;";
        Add(mutation, "$state", command.ToState); AddNullable(mutation, "$reason", blockedReason);
        Add(mutation, "$at", Store(command.OccurredAt)); Add(mutation, "$tenant", command.TenantId);
        Add(mutation, "$id", command.TaskId); await mutation.ExecuteNonQueryAsync(token);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.State,
            to = command.ToState,
            changedByKind = command.ChangedByKind,
            note,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
        return current with
        {
            State = command.ToState,
            BlockedReason = blockedReason,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private static async Task<BoardTaskRecord> SetTaskPriorityCoreAsync(
        SqliteConnection c, BoardTaskPriorityCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var current = await ReadTaskAsync(c, tx, command.TenantId, command.TaskId, token)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        await using var mutation = c.CreateCommand(); mutation.Transaction = tx;
        mutation.CommandText =
            "UPDATE work_tasks SET priority=$priority,risk_tier=$priority,version=version+1,updated_at=$at " +
            "WHERE tenant_id=$tenant AND id=$id;";
        Add(mutation, "$priority", command.Priority); Add(mutation, "$at", Store(command.OccurredAt));
        Add(mutation, "$tenant", command.TenantId); Add(mutation, "$id", command.TaskId);
        await mutation.ExecuteNonQueryAsync(token);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.Priority,
            to = command.Priority,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, command.TenantId, "task.priorityChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
        return current with
        {
            Priority = command.Priority,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private static async Task<BoardTaskRecord> SetTaskPlanningCoreAsync(
        SqliteConnection c, BoardTaskPlanningCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var current = await ReadTaskAsync(c, tx, command.TenantId, command.TaskId, token)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (current.ArchivedAt is not null || current.State == "done")
            throw new WorkBoardInvalidStateException("A completed or archived task cannot be replanned.");

        await using var mutation = c.CreateCommand(); mutation.Transaction = tx;
        mutation.CommandText =
            "UPDATE work_tasks SET assignee_agent_id=$agent,due_at=$due,version=version+1,updated_at=$at " +
            "WHERE tenant_id=$tenant AND id=$id;";
        Add(mutation, "$agent", command.AssigneeAgentId);
        Add(mutation, "$due", Store(command.DueAt));
        Add(mutation, "$at", Store(command.OccurredAt));
        Add(mutation, "$tenant", command.TenantId);
        Add(mutation, "$id", command.TaskId);
        await mutation.ExecuteNonQueryAsync(token);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            taskId = current.Id,
            from = current.State,
            to = current.State,
            changedByKind = "user",
            note = "Planejamento da entrega atualizado.",
        }, JsonOptions);
        await AppendAuditAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
        return current with
        {
            AssigneeAgentId = command.AssigneeAgentId,
            DueAt = command.DueAt,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private static async Task<BoardTaskRecord> SetTaskArchivedCoreAsync(
        SqliteConnection c, BoardTaskArchiveCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var current = await ReadTaskAsync(c, tx, command.TenantId, command.TaskId, token)
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
        await using var mutation = c.CreateCommand(); mutation.Transaction = tx;
        mutation.CommandText =
            "UPDATE work_tasks SET archived_at=$archived,version=version+1,updated_at=$at " +
            "WHERE tenant_id=$tenant AND id=$id;";
        AddNullable(mutation, "$archived", archivedAt is null ? null : Store(archivedAt.Value));
        Add(mutation, "$at", Store(command.OccurredAt)); Add(mutation, "$tenant", command.TenantId);
        Add(mutation, "$id", command.TaskId); await mutation.ExecuteNonQueryAsync(token);
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
        await AppendAuditAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
        return current with
        {
            ArchivedAt = archivedAt,
            UpdatedAt = command.OccurredAt,
            Version = current.Version + 1,
        };
    }

    private static async Task<BoardTaskRecord> DismissTaskCoreAsync(
        SqliteConnection c, BoardTaskDismissCommand command, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var current = await ReadTaskAsync(c, tx, command.TenantId, command.TaskId, token)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        if (current.ArchivedAt is not null)
            throw new WorkBoardInvalidStateException("Task is already archived.");
        if (current.State is not ("backlog" or "ready"))
            throw new WorkBoardInvalidStateException("Only inactive backlog or ready tasks can be dismissed.");

        var reason = command.Reason.Trim();
        await using var mutation = c.CreateCommand();
        mutation.Transaction = tx;
        mutation.CommandText =
            "UPDATE work_tasks SET state='cancelled',board_state='done',archived_at=$at," +
            "blocked_reason=$reason,version=version+1,updated_at=$at " +
            "WHERE tenant_id=$tenant AND id=$id;";
        Add(mutation, "$at", Store(command.OccurredAt));
        Add(mutation, "$reason", reason);
        Add(mutation, "$tenant", command.TenantId);
        Add(mutation, "$id", command.TaskId);
        await mutation.ExecuteNonQueryAsync(token);
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
        await AppendAuditAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
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

    private static async Task<BoardInstructionRecord> AppendInstructionCoreAsync(
        SqliteConnection c, BoardInstructionAppendCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var task = await ReadTaskAsync(c, tx, command.TenantId, command.TaskId, token)
            ?? throw new WorkBoardReferenceNotFoundException("task");
        await using var latestAttempt = c.CreateCommand(); latestAttempt.Transaction = tx;
        latestAttempt.CommandText =
            "SELECT state FROM work_attempts WHERE tenant_id=$tenant AND task_id=$task " +
            "ORDER BY attempt_number DESC LIMIT 1;";
        Add(latestAttempt, "$tenant", command.TenantId); Add(latestAttempt, "$task", command.TaskId);
        var attemptState = await latestAttempt.ExecuteScalarAsync(token) as string;
        var correction = task.InternalState == "running" && attemptState == "rejected";
        var readyContextRefresh = task.InternalState == "ready" && attemptState != "running" &&
            command.AuthorKind is "chief" or "system";
        if (!correction && !readyContextRefresh)
            throw new WorkBoardInvalidStateException(
                "A new instruction version requires either a rejected attempt awaiting correction " +
                "or a ready card being refreshed by the chief/system before dispatch.");

        var nextVersion = task.InstructionVersion + 1;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Body)));
        await using var latestInstruction = c.CreateCommand(); latestInstruction.Transaction = tx;
        latestInstruction.CommandText =
            "SELECT id FROM instruction_versions WHERE tenant_id=$tenant AND task_id=$task " +
            "ORDER BY version DESC LIMIT 1;";
        Add(latestInstruction, "$tenant", command.TenantId); Add(latestInstruction, "$task", command.TaskId);
        var supersedes = (string)(await latestInstruction.ExecuteScalarAsync(token)
            ?? throw new WorkBoardReferenceNotFoundException("instruction"));
        await using var mutation = c.CreateCommand(); mutation.Transaction = tx;
        mutation.CommandText =
            "INSERT INTO instruction_versions " +
            "(id,tenant_id,project_id,task_id,version,content,content_hash,supersedes_id,created_at,author_kind,author_id) " +
            "VALUES ($id,$tenant,$project,$task,$version,$body,$hash,$supersedes,$at,$authorKind,$authorId); " +
            "UPDATE work_tasks SET state='ready',board_state='ready',blocked_reason=NULL," +
            "version=version+1,updated_at=$at " +
            "WHERE tenant_id=$tenant AND id=$task;";
        Add(mutation, "$id", command.InstructionId); Add(mutation, "$tenant", command.TenantId);
        Add(mutation, "$project", task.ProjectId); Add(mutation, "$task", command.TaskId);
        Add(mutation, "$version", nextVersion); Add(mutation, "$body", command.Body);
        Add(mutation, "$hash", hash); Add(mutation, "$supersedes", supersedes);
        Add(mutation, "$at", Store(command.OccurredAt)); Add(mutation, "$authorKind", command.AuthorKind);
        AddNullable(mutation, "$authorId", command.AuthorId); await mutation.ExecuteNonQueryAsync(token);
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
        await AppendAuditAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "task.stateChanged", payload,
            command.OccurredAt, token);
        await tx.CommitAsync(token);
        return new BoardInstructionRecord(command.TenantId, command.InstructionId, command.TaskId,
            nextVersion, command.Body, command.AuthorKind, command.AuthorId, command.OccurredAt);
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
