using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkChainStore
{
    public Task<WorkChainMutationReceipt> TriageTaskAsync(
        WorkTaskLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => TransitionInitialTaskCoreAsync(
                connection,
                command,
                "draft",
                "triaged",
                "triaged",
                "backlog",
                token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> MarkTaskReadyAsync(
        WorkTaskLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => TransitionInitialTaskCoreAsync(
                connection,
                command,
                "triaged",
                "ready",
                "requirementsCompleted",
                "ready",
                token),
            cancellationToken);
    }

    private static async Task<WorkChainMutationReceipt> TransitionInitialTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskLifecycleCommand command,
        string expectedState,
        string targetState,
        string transitionEvent,
        string targetBoardState,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks
                SET state=$targetState,version=$nextVersion,updated_at=$occurredAt,
                    board_state=$targetBoardState,blocked_reason=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$targetState", targetState);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$targetBoardState", targetBoardState);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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
        return _dispatcher.ExecuteAsync(
            (connection, token) => AssignTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<WorkChainMutationReceipt> AssignTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskAssignmentCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks
                SET state='assigned',version=$nextVersion,updated_at=$occurredAt,
                    board_state='development',assignee_agent_id=$assignee,blocked_reason=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$assignee", command.AssigneeAgentId);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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
        return _dispatcher.ExecuteAsync(
            (connection, token) => AddInstructionVersionCoreAsync(connection, command, token),
            cancellationToken);
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

        return await _dispatcher.ExecuteAsync(
            (connection, token) => StartAttemptCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CompleteAttemptAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CompleteAttemptCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CompleteAndApproveAttemptAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CompleteAndApproveAttemptCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ExpireAttemptLeaseAsync(
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ExpireAttemptLeaseCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> BlockRunningTaskAsync(
        WorkTaskBlockCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => BlockRunningTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> UnblockTaskAsync(
        WorkTaskUnblockCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => UnblockTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> EscalateUnreviewableTaskAsync(
        WorkTaskReviewUnavailableCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => EscalateUnreviewableTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> EscalateUndispatchableTaskAsync(
        WorkTaskUndispatchableCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => EscalateUndispatchableTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ReplanEscalatedTaskAsync(
        WorkTaskReplanCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReplanEscalatedTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ReviewAttemptAsync(
        WorkAttemptReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReviewAttemptCoreAsync(
                connection,
                command,
                _maximumReviewCycles,
                token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> MergeApprovedTaskAsync(
        WorkTaskMergeCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => MergeApprovedTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CompleteMergedTaskAsync(
        WorkTaskDeliveryCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CompleteMergedTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CancelRunningTaskAsync(
        WorkTaskCancellationCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CancelRunningTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainMutationReceipt> SupersedeTaskAsync(
        WorkTaskSupersessionCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => SupersedeTaskCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<WorkChainMutationReceipt> AddInstructionVersionCoreAsync(
        SqliteConnection connection,
        WorkInstructionVersionCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO instruction_versions
                    (id, tenant_id, project_id, task_id, version, content, content_hash,
                     supersedes_id, created_at)
                VALUES
                    ($instructionId, $tenantId, $projectId, $taskId, $instructionVersion,
                     $content, $contentHash, $supersedesId, $occurredAt);
                UPDATE work_tasks SET state='ready',version=$nextTaskVersion,
                    updated_at=$occurredAt,board_state='ready',blocked_reason=NULL
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedTaskVersion;
                """;
            Add(mutation, "$instructionId", command.InstructionVersionId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$instructionVersion", instructionVersion);
            Add(mutation, "$content", command.Content);
            Add(mutation, "$contentHash", command.ContentHash);
            Add(mutation, "$supersedesId", row.LatestInstructionId);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$nextTaskVersion", nextTaskVersion);
            Add(mutation, "$expectedTaskVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> ReplanEscalatedTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskReplanCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        // Um card pode escalar ANTES de qualquer tentativa — despacho impossível, circuito do card
        // aberto. Exigir uma tentativa reprovada deixava justamente esses presos: escalados para
        // sempre, e o replanejamento, que é o ÚNICO caminho de volta, recusado como estado
        // inválido. Replanejar um card que nunca rodou é o caso mais simples de todos: só a
        // instrução muda.
        //
        // Uma tentativa ENCERRADA sem aprovação também é caminho de volta, não só a reprovada.
        // A projeção acima traduz uma expiração de lease em `cancelled` (falha com motivo) ou
        // `abandoned` (sem motivo) — nunca em `rejected`. Exigir literalmente `rejected` prendia
        // todo card cujas tentativas MORRERAM em vez de terem sido reprovadas por um crítico:
        // ele escalava, o replanejamento era recusado como estado inválido e a Bruna só podia
        // chamar o dono. O que precisa ser barrado é tentativa AINDA VIVA (`running`) ou já
        // APROVADA — nesses casos não há o que replanejar.
        else if (row.TaskState != "escalated" ||
                 (row.LatestAttemptState is not null &&
                  !WorkChainMutationValidator.WorkAttemptIsReplannable(row.LatestAttemptState)))
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO instruction_versions
                    (id,tenant_id,project_id,task_id,version,content,content_hash,
                     supersedes_id,created_at,author_kind,author_id)
                VALUES
                    ($instructionId,$tenantId,$projectId,$taskId,$instructionVersion,
                     $content,$contentHash,$supersedesId,$occurredAt,'chief',$chiefAgentId);
                UPDATE work_tasks
                SET state='ready',version=$nextTaskVersion,updated_at=$occurredAt,
                    board_state='ready',blocked_reason=NULL,assignee_agent_id=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedTaskVersion;
                -- O replanejamento é o ÚNICO caminho que reabre o circuito do card — e ele vivia
                -- desligado: o card voltava a 'ready', o circuito era rederivado do MESMO
                -- histórico de falhas no ciclo seguinte e o card re-escalava, desfazendo a
                -- decisão do dono minutos depois de anunciada. O fechamento mora na mutação,
                -- na mesma transação, porque os dois lados são um só ato.
                INSERT INTO card_circuit_breakers
                    (tenant_id,task_id,project_id,state,consecutive_failures,
                     last_failure_reason_code,last_failure_at,opened_at,replanned_at,
                     replan_note,updated_at)
                VALUES
                    ($tenantId,$taskId,$projectId,'closed',0,NULL,NULL,NULL,
                     $occurredAt,$circuitNote,$occurredAt)
                ON CONFLICT(tenant_id,task_id) DO UPDATE SET
                    state='closed',consecutive_failures=0,last_failure_reason_code=NULL,
                    last_failure_at=NULL,opened_at=NULL,replanned_at=$occurredAt,
                    replan_note=$circuitNote,updated_at=$occurredAt;
                """;
            Add(mutation, "$instructionId", command.InstructionVersionId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$instructionVersion", instructionVersion);
            Add(mutation, "$content", command.Content);
            Add(mutation, "$contentHash", command.ContentHash);
            Add(mutation, "$supersedesId", row.LatestInstructionId);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$chiefAgentId", command.ChiefAgentId);
            Add(mutation, "$nextTaskVersion", nextTaskVersion);
            Add(mutation, "$expectedTaskVersion", command.ExpectedTaskVersion);
            Add(mutation, "$circuitNote", WorkChainMutationValidator.TruncateCircuitReplanNote(command.Reason));
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> StartAttemptCoreAsync(
        SqliteConnection connection,
        WorkAttemptStartCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            attemptId: null, cancellationToken);
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
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO work_attempts
                    (id, tenant_id, project_id, task_id, instruction_version_id, attempt_number,
                     producer_agent_id, state, started_at, operational_state)
                VALUES
                    ($attemptId, $tenantId, $projectId, $taskId, $instructionId, $attemptNumber,
                     $producer, 'running', $occurredAt, 'running');
                INSERT INTO attempt_events (id,tenant_id,project_id,attempt_id,kind,content,occurred_at)
                VALUES ($attemptEventId,$tenantId,$projectId,$attemptId,'log','Attempt started.',$occurredAt);
                UPDATE work_tasks SET state = 'running', version = $nextVersion,
                    updated_at = $occurredAt,board_state='development',blocked_reason=NULL
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedVersion;
                """;
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$attemptEventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$instructionId", command.InstructionVersionId);
            Add(mutation, "$attemptNumber", row.AttemptCount + 1);
            Add(mutation, "$producer", command.ProducerAgentId);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> CompleteAttemptCoreAsync(
        SqliteConnection connection,
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            command.AttemptId, cancellationToken);
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
                await using var evidence = connection.CreateCommand();
                evidence.Transaction = transaction;
                evidence.CommandText =
                    """
                    INSERT INTO work_evidence
                        (id, tenant_id, project_id, attempt_id, ordinal, reference, created_at)
                    VALUES ($id, $tenantId, $projectId, $attemptId, $ordinal, $reference, $occurredAt);
                    """;
                Add(evidence, "$id", command.Evidence[index].EvidenceId);
                Add(evidence, "$tenantId", command.TenantId);
                Add(evidence, "$projectId", row.ProjectId);
                Add(evidence, "$attemptId", command.AttemptId);
                Add(evidence, "$ordinal", index + 1);
                Add(evidence, "$reference", command.Evidence[index].Reference);
                Add(evidence, "$occurredAt", ToStorage(command.OccurredAt));
                await evidence.ExecuteNonQueryAsync(cancellationToken);
            }

            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_attempts SET state = 'awaiting_review', completed_at = $occurredAt,
                    operational_state = 'completed',
                    duration_ms = COALESCE($durationMs, duration_ms),
                    tokens_input = COALESCE($tokensInput, tokens_input),
                    tokens_output = COALESCE($tokensOutput, tokens_output),
                    cost_usd = COALESCE($costUsd, cost_usd)
                WHERE id = $attemptId AND state = 'running';
                INSERT INTO attempt_events (id,tenant_id,project_id,attempt_id,kind,content,occurred_at)
                VALUES ($attemptEventId,$tenantId,$projectId,$attemptId,'log','Attempt submitted for review.',$occurredAt);
                UPDATE work_tasks SET state = 'awaiting_review', version = $nextVersion,
                    updated_at = $occurredAt,board_state='review',blocked_reason=NULL
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedVersion;
                """;
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$attemptEventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            // COALESCE no SQL + DBNull aqui: métrica não exposta pelo executor preserva o valor
            // anterior em vez de gravar zero, para não confundir "zero medido" com "desconhecido".
            AddNullable(mutation, "$durationMs", command.Usage?.DurationMs);
            AddNullable(mutation, "$tokensInput", command.Usage?.TokensInput);
            AddNullable(mutation, "$tokensOutput", command.Usage?.TokensOutput);
            AddNullable(mutation, "$costUsd", command.Usage?.CostUsd);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "awaiting_review", "awaiting_review");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "attempt.completed", command.OccurredAt, receipt, cancellationToken);
    }

    /// <summary>
    /// F-03: completa e aprova uma tentativa isenta de revisão independente (parecer do Conselho).
    /// A consolidação do Conselho continua sendo o controle real; o parecer é, por construção, uma
    /// opinião crítica sobre trabalho de terceiro.
    /// </summary>
    private static async Task<WorkChainMutationReceipt> CompleteAndApproveAttemptCoreAsync(
        SqliteConnection connection,
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            command.AttemptId, cancellationToken);
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
                await using var evidence = connection.CreateCommand();
                evidence.Transaction = transaction;
                evidence.CommandText =
                    """
                    INSERT INTO work_evidence
                        (id, tenant_id, project_id, attempt_id, ordinal, reference, created_at)
                    VALUES ($id, $tenantId, $projectId, $attemptId, $ordinal, $reference, $occurredAt);
                    """;
                Add(evidence, "$id", command.Evidence[index].EvidenceId);
                Add(evidence, "$tenantId", command.TenantId);
                Add(evidence, "$projectId", row.ProjectId);
                Add(evidence, "$attemptId", command.AttemptId);
                Add(evidence, "$ordinal", index + 1);
                Add(evidence, "$reference", command.Evidence[index].Reference);
                Add(evidence, "$occurredAt", ToStorage(command.OccurredAt));
                await evidence.ExecuteNonQueryAsync(cancellationToken);
            }

            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_attempts SET state = 'approved', completed_at = $occurredAt,
                    operational_state = 'completed',
                    duration_ms = COALESCE($durationMs, duration_ms),
                    tokens_input = COALESCE($tokensInput, tokens_input),
                    tokens_output = COALESCE($tokensOutput, tokens_output),
                    cost_usd = COALESCE($costUsd, cost_usd)
                WHERE id = $attemptId AND state = 'running';
                INSERT INTO attempt_events (id,tenant_id,project_id,attempt_id,kind,content,occurred_at)
                VALUES ($attemptEventId,$tenantId,$projectId,$attemptId,'log','Council opinion approved without independent review.',$occurredAt);
                UPDATE work_tasks SET state = 'approved', version = $nextVersion,
                    updated_at = $occurredAt,board_state='review',blocked_reason=NULL
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedVersion;
                """;
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$attemptEventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            AddNullable(mutation, "$durationMs", command.Usage?.DurationMs);
            AddNullable(mutation, "$tokensInput", command.Usage?.TokensInput);
            AddNullable(mutation, "$tokensOutput", command.Usage?.TokensOutput);
            AddNullable(mutation, "$costUsd", command.Usage?.CostUsd);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "approved", "approved");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "attempt.completed", command.OccurredAt, receipt, cancellationToken);
    }

    /// <summary>
    /// Escala um card PRONTO que o despacho não consegue assumir. Não há tentativa: nada foi
    /// executado, então nada é reprovado — o card apenas sai da fila e vira impedimento visível.
    /// </summary>
    private static async Task<WorkChainMutationReceipt> EscalateUndispatchableTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskUndispatchableCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            attemptId: null, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict, row, command.TaskId, attemptId: null);
        }
        else if (row.TaskState is not ("ready" or "backlog" or "triaged"))
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState, row, command.TaskId, attemptId: null);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks SET state = 'escalated', version = $nextVersion,
                    updated_at = $occurredAt, board_state = 'blocked', blocked_reason = $reason
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedVersion;
                """;
            Add(mutation, "$reason", command.Reason);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, AttemptId: null,
                nextVersion, "escalated", null);
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "task.stateChanged", command.OccurredAt, receipt,
            new TransitionAudit(
                "ready",
                receipt.TaskState ?? "ready",
                "dispatchImpossible",
                "dispatch",
                "system",
                "chief-backlog-loop",
                command.Reason,
                command.EvidenceReference),
            cancellationToken);
    }

    /// <summary>
    /// Escala um card cuja revisão é impossível. Diferente de <c>ReviewAttemptCoreAsync</c>, NÃO
    /// grava veredito e NÃO muda o estado da tentativa: o trabalho do ator continua íntegro em
    /// `awaiting_review` e pode ser revisado assim que houver crítico. O que muda é o card, que
    /// sai da fila de retry e vira impedimento anunciável.
    /// </summary>
    private static async Task<WorkChainMutationReceipt> EscalateUnreviewableTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskReviewUnavailableCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            command.AttemptId, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "awaiting_review" || row.AttemptState != "awaiting_review")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else
        {
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO attempt_events (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES ($attemptEventId,$tenantId,$projectId,$attemptId,'note',$reason,$occurredAt,'error');
                UPDATE work_tasks SET state = 'escalated', version = $nextVersion,
                    updated_at = $occurredAt, board_state = 'blocked', blocked_reason = $reason
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedVersion;
                """;
            Add(mutation, "$attemptEventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$reason", command.Reason);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "escalated", "awaiting_review");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "task.stateChanged", command.OccurredAt, receipt,
            new TransitionAudit(
                "awaiting_review",
                receipt.TaskState ?? "awaiting_review",
                "reviewUnavailable",
                "review",
                "system",
                "chief-backlog-loop",
                command.Reason,
                command.EvidenceReference),
            cancellationToken);
    }

    private static async Task<WorkChainMutationReceipt> ReviewAttemptCoreAsync(
        SqliteConnection connection,
        WorkAttemptReviewCommand command,
        int maximumReviewCycles,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            command.AttemptId, cancellationToken);
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
            var rejectedReviewCount = row.RejectedReviewCount +
                (command.Decision == "rejected" ? 1 : 0);
            var taskState = command.Decision == "approved"
                ? "approved"
                : rejectedReviewCount > maximumReviewCycles
                    ? "escalated"
                    : "running";
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO work_reviews
                    (id, tenant_id, project_id, attempt_id, reviewer_agent_id, decision, rationale, rejection_cause, created_at)
                VALUES
                    ($reviewId, $tenantId, $projectId, $attemptId, $reviewer, $decision, $rationale, $rejectionCause, $occurredAt);
                UPDATE work_attempts SET state = $decision,
                    operational_state = CASE WHEN $decision='rejected' THEN 'failed' ELSE 'completed' END
                WHERE id = $attemptId AND state = 'awaiting_review';
                INSERT INTO attempt_events (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES ($attemptEventId,$tenantId,$projectId,$attemptId,'note',$rationale,$occurredAt,
                        CASE WHEN $decision='rejected' THEN 'error' ELSE 'info' END);
                UPDATE work_tasks SET state = $taskState, version = $nextVersion,
                    updated_at = $occurredAt,
                    board_state=CASE
                        WHEN $taskState='approved' THEN 'review'
                        WHEN $taskState='escalated' THEN 'blocked'
                        ELSE 'corrections'
                    END,
                    blocked_reason=CASE
                        WHEN $taskState='escalated' THEN $rationale
                        ELSE NULL
                    END
                WHERE id = $taskId AND tenant_id = $tenantId AND version = $expectedVersion;
                """;
            Add(mutation, "$reviewId", command.ReviewId);
            Add(mutation, "$attemptEventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$reviewer", command.ReviewerAgentId);
            Add(mutation, "$decision", command.Decision);
            Add(mutation, "$rationale", command.Rationale);
            Add(mutation, "$rejectionCause", command.RejectionCause);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$taskState", taskState);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> MergeApprovedTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskMergeCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks
                SET state='merged',version=$nextVersion,updated_at=$occurredAt,
                    board_state='review',blocked_reason=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> CompleteMergedTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskDeliveryCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks
                SET state='completed',version=$nextVersion,updated_at=$occurredAt,
                    board_state='done',blocked_reason=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> CancelRunningTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskCancellationCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_attempts
                SET state='rejected',operational_state='cancelled',
                    completed_at=$occurredAt,failure_reason=$reason
                WHERE id=$attemptId AND tenant_id=$tenantId
                  AND state='running' AND operational_state='running';
                INSERT INTO attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES
                    ($eventId,$tenantId,$projectId,$attemptId,'note',$reason,
                     $occurredAt,'warning');
                UPDATE work_tasks
                SET state='cancelled',version=$nextVersion,updated_at=$occurredAt,
                    board_state='done',blocked_reason=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$reason", command.Reason);
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$eventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    /// <summary>
    /// Superssessão de card escalado. Precondições:
    ///
    /// - o card precisa estar `escalated` (mesma exigência de <see cref="ReplanEscalatedTaskCoreAsync"/>
    ///   — se estivesse `running`, seria <see cref="CancelRunningTaskCoreAsync"/> quem se aplica);
    /// - todo id de substituto precisa existir no mesmo tenant/projeto;
    /// - nenhuma tentativa do card pode ter workspace com claim ainda viva (<c>released_at</c> nulo)
    ///   — em vez de tentar liberar por conta própria (a fencing token e o dono da concessão
    ///   pertencem a <c>IAttemptWorkspaceStore</c>, uma store diferente), a superssessão recusa e
    ///   pede para o caminho de liberação normal (lease/rejeição) rodar primeiro.
    ///
    /// Se, depois de terminal, NENHUMA outra tarefa viva restar sob a mesma demanda, a demanda
    /// também vira `superseded` — sem isso, <c>RequirementCoverageAnalyzer</c> relataria o
    /// requisito como órfão (`Unplanned`), o mesmo defeito do run de 2026-08-04. Se um irmão sob a
    /// mesma demanda ainda está vivo (ex.: o card gêmeo de frontend), a demanda fica como está — o
    /// irmão continua contando a verdade sobre o requisito.
    /// </summary>
    private static async Task<WorkChainMutationReceipt> SupersedeTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskSupersessionCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId, command.TaskId,
            attemptId: null, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, null);
        }
        else if (row.TaskState != "escalated")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, null);
        }
        else
        {
            await using var demandQuery = connection.CreateCommand();
            demandQuery.Transaction = transaction;
            demandQuery.CommandText =
                "SELECT demand_id FROM work_tasks WHERE id=$taskId AND tenant_id=$tenantId;";
            Add(demandQuery, "$taskId", command.TaskId);
            Add(demandQuery, "$tenantId", command.TenantId);
            var demandId = (string)(await demandQuery.ExecuteScalarAsync(cancellationToken))!;

            await using var replacementCheck = connection.CreateCommand();
            replacementCheck.Transaction = transaction;
            replacementCheck.CommandText =
                $"""
                SELECT COUNT(*) FROM work_tasks
                WHERE tenant_id=$tenantId AND project_id=$projectId
                  AND id IN ({string.Join(',', command.ReplacementTaskIds.Select((_, i) => $"$r{i}"))});
                """;
            Add(replacementCheck, "$tenantId", command.TenantId);
            Add(replacementCheck, "$projectId", row.ProjectId);
            for (var i = 0; i < command.ReplacementTaskIds.Count; i++)
            {
                Add(replacementCheck, $"$r{i}", command.ReplacementTaskIds[i]);
            }

            var existingReplacements = Convert.ToInt32(
                await replacementCheck.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (existingReplacements != command.ReplacementTaskIds.Count)
            {
                receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, null);
                return await FinalizeMutationAsync(
                    connection, transaction, command.TenantId, command.IdempotencyKey, hash,
                    "task.stateChanged", command.OccurredAt, receipt, cancellationToken);
            }

            await using var liveClaimCheck = connection.CreateCommand();
            liveClaimCheck.Transaction = transaction;
            liveClaimCheck.CommandText =
                "SELECT COUNT(*) FROM attempt_workspaces WHERE task_id=$taskId AND released_at IS NULL;";
            Add(liveClaimCheck, "$taskId", command.TaskId);
            var liveClaims = Convert.ToInt32(
                await liveClaimCheck.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (liveClaims > 0)
            {
                receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, null);
                return await FinalizeMutationAsync(
                    connection, transaction, command.TenantId, command.IdempotencyKey, hash,
                    "task.stateChanged", command.OccurredAt, receipt, cancellationToken);
            }

            await using var siblingCheck = connection.CreateCommand();
            siblingCheck.Transaction = transaction;
            siblingCheck.CommandText =
                """
                SELECT COUNT(*) FROM work_tasks
                WHERE tenant_id=$tenantId AND demand_id=$demandId AND id != $taskId
                  AND archived_at IS NULL
                  AND state NOT IN ('cancelled','done','completed');
                """;
            Add(siblingCheck, "$tenantId", command.TenantId);
            Add(siblingCheck, "$demandId", demandId);
            Add(siblingCheck, "$taskId", command.TaskId);
            var aliveSiblings = Convert.ToInt32(
                await siblingCheck.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            var supersedeDemand = aliveSiblings == 0;

            var nextVersion = row.Version + 1;
            var supersessionId = UlidValue.New(command.OccurredAt).ToString();
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks
                SET state='cancelled',version=$nextVersion,updated_at=$occurredAt,
                    board_state='done',blocked_reason=NULL,assignee_agent_id=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                INSERT INTO task_supersessions
                    (id,tenant_id,project_id,original_task_id,replacement_task_ids_json,reason,
                     actor_kind,actor_id,demand_superseded,occurred_at)
                VALUES
                    ($supersessionId,$tenantId,$projectId,$taskId,$replacementIdsJson,$reason,
                     $actorKind,$actorId,$demandSuperseded,$occurredAt);
                """;
            if (supersedeDemand)
            {
                mutation.CommandText +=
                    "UPDATE demands SET state='superseded' WHERE id=$demandId AND tenant_id=$tenantId;";
                Add(mutation, "$demandId", demandId);
            }

            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            Add(mutation, "$supersessionId", supersessionId);
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$replacementIdsJson", JsonSerializer.Serialize(command.ReplacementTaskIds));
            Add(mutation, "$reason", command.Reason);
            Add(mutation, "$actorKind", command.ActorKind);
            Add(mutation, "$actorId", command.ActorId);
            Add(mutation, "$demandSuperseded", supersedeDemand ? 1 : 0);
            await mutation.ExecuteNonQueryAsync(cancellationToken);

            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, null, nextVersion, "cancelled", null);
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
                "cancelled",
                "superseded",
                "blocked",
                command.ActorKind,
                command.ActorId,
                command.Reason,
                string.Join(',', command.ReplacementTaskIds)),
            cancellationToken);
    }

    private static async Task<WorkChainMutationReceipt> ExpireAttemptLeaseCoreAsync(
        SqliteConnection connection,
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_attempts
                SET state='rejected',operational_state=$operationalState,
                    completed_at=$occurredAt,failure_reason=$failureReason
                WHERE id=$attemptId AND tenant_id=$tenantId
                  AND state='running' AND operational_state='running';
                INSERT INTO attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES
                    ($eventId,$tenantId,$projectId,$attemptId,'log',$eventContent,
                     $occurredAt,'warning');
                UPDATE work_tasks
                SET state='ready',version=$nextVersion,updated_at=$occurredAt,
                    board_state='ready',blocked_reason=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$operationalState",
                command.CountsTowardRoundBudget ? "failed" : "cancelled");
            // O motivo é gravado SEMPRE que existe, independente do orçamento de rodadas: é ele
            // que distingue uma falha real de um cancelamento por infraestrutura para o circuito
            // do card (`CardCircuitBreakerService.IsFailure`). Descartá-lo aqui tornava invisível
            // toda falha transitória e reabria o redespacho infinito.
            Add(mutation, "$failureReason",
                command.FailureReason is not null
                    ? command.FailureReason
                    : DBNull.Value);
            Add(mutation, "$eventContent", command.CountsTowardRoundBudget
                ? "Attempt failed; card returned to ready."
                : "Attempt abandoned; card returned to ready.");
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$eventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                command.AttemptId,
                nextVersion,
                "ready",
                command.CountsTowardRoundBudget ? "failed" : "abandoned");
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

    private static async Task<WorkChainMutationReceipt> BlockRunningTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskBlockCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            var nextVersion = row.Version + 1;
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_attempts
                SET state='rejected',operational_state='cancelled',
                    completed_at=$occurredAt,failure_reason=NULL
                WHERE id=$attemptId AND tenant_id=$tenantId
                  AND state='running' AND operational_state='running';
                INSERT INTO attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES
                    ($eventId,$tenantId,$projectId,$attemptId,'note',$reason,
                     $occurredAt,'error');
                UPDATE work_tasks
                SET state='blocked',version=$nextVersion,updated_at=$occurredAt,
                    board_state='blocked',blocked_reason=$reason
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$reason", command.Reason);
            Add(mutation, "$attemptId", command.AttemptId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$eventId", UlidValue.New(command.OccurredAt).ToString());
            Add(mutation, "$projectId", row.ProjectId);
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<WorkChainMutationReceipt> UnblockTaskCoreAsync(
        SqliteConnection connection,
        WorkTaskUnblockCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                UPDATE work_tasks
                SET state='ready',version=$nextVersion,updated_at=$occurredAt,
                    board_state='ready',blocked_reason=NULL,assignee_agent_id=NULL
                WHERE id=$taskId AND tenant_id=$tenantId AND version=$expectedVersion;
                """;
            Add(mutation, "$nextVersion", nextVersion);
            Add(mutation, "$occurredAt", ToStorage(command.OccurredAt));
            Add(mutation, "$taskId", command.TaskId);
            Add(mutation, "$tenantId", command.TenantId);
            Add(mutation, "$expectedVersion", command.ExpectedTaskVersion);
            await mutation.ExecuteNonQueryAsync(cancellationToken);
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
        SqliteConnection connection,
        SqliteTransaction transaction,
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
                   (SELECT id FROM instruction_versions i WHERE i.task_id = t.id ORDER BY version DESC LIMIT 1),
                   (SELECT version FROM instruction_versions i WHERE i.task_id = t.id ORDER BY version DESC LIMIT 1),
                   (SELECT COUNT(*) FROM work_attempts a WHERE a.task_id = t.id),
                   (SELECT COUNT(*)
                    FROM work_reviews r
                    JOIN work_attempts reviewed ON reviewed.id = r.attempt_id
                    WHERE reviewed.task_id = t.id AND r.decision = 'rejected'),
                   (SELECT CASE
                                WHEN operational_state='cancelled' AND state='rejected'
                                     AND failure_reason IS NULL THEN 'abandoned'
                                WHEN operational_state='cancelled' AND state='rejected'
                                     THEN 'cancelled'
                                ELSE state
                            END
                    FROM work_attempts a WHERE a.task_id = t.id
                    ORDER BY attempt_number DESC LIMIT 1),
                   (SELECT instruction_version_id FROM work_attempts a WHERE a.task_id = t.id ORDER BY attempt_number DESC LIMIT 1),
                   CASE
                       WHEN a.operational_state='cancelled' AND a.state='rejected'
                            AND a.failure_reason IS NULL THEN 'abandoned'
                       WHEN a.operational_state='cancelled' AND a.state='rejected'
                            THEN 'cancelled'
                       ELSE a.state
                   END,
                   a.producer_agent_id,
                   t.assignee_agent_id
            FROM work_tasks t
            JOIN demands d ON d.id = t.demand_id
            JOIN solicitations s ON s.id = d.solicitation_id
            LEFT JOIN work_attempts a ON a.id = $attemptId AND a.task_id = t.id
            WHERE t.tenant_id = $tenantId AND s.id = $solicitationId AND t.id = $taskId;
            """;
        Add(query, "$attemptId", attemptId is null ? DBNull.Value : attemptId);
        Add(query, "$tenantId", tenantId);
        Add(query, "$solicitationId", solicitationId);
        Add(query, "$taskId", taskId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TaskRow(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12))
            : null;
    }

    private static async Task<WorkChainMutationReceipt?> ReadMutationInboxAsync(
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
            "SELECT message_hash, response_json FROM inbox_messages WHERE tenant_id = $tenantId AND idempotency_key = $key;";
        Add(query, "$tenantId", tenantId);
        Add(query, "$key", key);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException("The idempotency key belongs to a different work-chain mutation.");
        }

        return JsonSerializer.Deserialize<WorkChainMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted mutation receipt is invalid.");
    }

    private static Task<WorkChainMutationReceipt> FinalizeMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
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
        SqliteConnection connection,
        SqliteTransaction transaction,
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
            var outboxPayload = await BuildMutationPayloadAsync(
                connection,
                transaction,
                eventType,
                receipt,
                occurredAt,
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
            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText =
                """
                INSERT INTO audit_ledger
                    (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
                VALUES ($ledgerId, $tenantId, $sequence, $previousHash, $eventHash, $eventType, $auditPayload, $occurredAt);
                INSERT INTO outbox_messages (id, tenant_id, event_type, payload_json, occurred_at)
                VALUES ($outboxId, $tenantId, $eventType, $outboxPayload, $occurredAt);
                """;
            Add(audit, "$ledgerId", UlidValue.New(occurredAt).ToString());
            Add(audit, "$tenantId", tenantId);
            Add(audit, "$sequence", sequence);
            Add(audit, "$previousHash", previousHash);
            Add(audit, "$eventHash", eventHash);
            Add(audit, "$eventType", eventType);
            Add(audit, "$auditPayload", auditPayload);
            Add(audit, "$outboxPayload", outboxPayload);
            Add(audit, "$occurredAt", ToStorage(occurredAt));
            Add(audit, "$outboxId", outboxId);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var inbox = connection.CreateCommand();
        inbox.Transaction = transaction;
        inbox.CommandText =
            """
            INSERT INTO inbox_messages
                (tenant_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($tenantId, $key, $hash, $response, $occurredAt);
            """;
        Add(inbox, "$tenantId", tenantId);
        Add(inbox, "$key", key);
        Add(inbox, "$hash", hash);
        Add(inbox, "$response", JsonSerializer.Serialize(final));
        Add(inbox, "$occurredAt", ToStorage(occurredAt));
        await inbox.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static async Task<string> BuildMutationPayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        WorkChainMutationReceipt receipt,
        DateTimeOffset occurredAt,
        TransitionAudit? transitionAudit,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT t.project_id,t.board_state,t.title,t.priority,t.source_demand_id,
                   t.assignee_agent_id,t.blocked_reason,t.created_at,t.updated_at,t.due_at,
                   (SELECT MAX(version) FROM instruction_versions i WHERE i.task_id=t.id)
            FROM work_tasks t WHERE t.id=$taskId;
            """;
        Add(query, "$taskId", receipt.TaskId);
        await using var taskReader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await taskReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Applied work mutation lost its task projection.");
        }

        var projectId = taskReader.GetString(0);
        var boardState = taskReader.GetString(1);
        await taskReader.DisposeAsync();
        if (eventType == "task.stateChanged")
        {
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

        if (receipt.AttemptId is null)
        {
            throw new InvalidOperationException("Attempt event has no attempt identifier.");
        }

        await using var attempt = connection.CreateCommand();
        attempt.Transaction = transaction;
        attempt.CommandText =
            """
            SELECT a.attempt_number,a.state,a.producer_agent_id,a.started_at,a.completed_at,
                   a.duration_ms,a.cost_usd,a.tokens_input,a.tokens_output,a.summary,a.failure_reason,
                   COALESCE((SELECT json_group_array(reference) FROM work_evidence e
                             WHERE e.attempt_id=a.id),'[]')
            FROM work_attempts a WHERE a.id=$attemptId;
            """;
        Add(attempt, "$attemptId", receipt.AttemptId);
        await using var reader = await attempt.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Applied work mutation lost its attempt projection.");
        }

        var startedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
        var finishedAt = reader.IsDBNull(4)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
        var durationMs = reader.IsDBNull(5)
            ? finishedAt is null ? 0L : Math.Max(0L, (long)(finishedAt.Value - startedAt).TotalMilliseconds)
            : reader.GetInt64(5);
        var commitRefs = JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? [];
        if (eventType == "attempt.started")
        {
            return JsonSerializer.Serialize(new
            {
                projectId,
                attempt = new
                {
                    id = receipt.AttemptId,
                    taskId = receipt.TaskId,
                    number = reader.GetInt32(0),
                    state = "running",
                    agentId = reader.GetString(2),
                    startedAt,
                    finishedAt = (DateTimeOffset?)null,
                    durationMs = (long?)null,
                    costUsd = reader.GetDecimal(6),
                    tokensInput = reader.GetInt64(7),
                    tokensOutput = reader.GetInt64(8),
                    commitRefs,
                    summary = reader.IsDBNull(9) ? null : reader.GetString(9),
                    failureReason = (string?)null,
                },
            });
        }

        if (eventType == "attempt.completed")
        {
            return JsonSerializer.Serialize(new
            {
                projectId,
                attemptId = receipt.AttemptId,
                taskId = receipt.TaskId,
                durationMs,
                tokensInput = reader.GetInt64(7),
                tokensOutput = reader.GetInt64(8),
                costUsd = reader.GetDecimal(6),
                commitRefs,
                summary = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        throw new InvalidOperationException($"Unsupported work event type: {eventType} at {occurredAt:O}.");
    }

    private static WorkChainMutationReceipt Rejected(
        WorkChainMutationStatus status,
        string taskId,
        string? attemptId) => new(status, taskId, attemptId, null, null, null);

    private static WorkChainMutationReceipt Rejected(
        WorkChainMutationStatus status,
        TaskRow row,
        string taskId,
        string? attemptId) => new(
            status, taskId, attemptId,
            row.Version, row.TaskState, row.AttemptState);

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
