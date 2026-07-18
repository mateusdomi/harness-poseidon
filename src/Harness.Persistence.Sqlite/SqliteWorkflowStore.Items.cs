using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkflowStore
{
    public Task<WorkflowRunMutationReceipt> AdvanceObjectiveAsync(
        WorkflowObjectiveAdvanceCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => AdvanceObjectiveCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkflowRunMutationReceipt> EvaluateGateAsync(
        WorkflowGateEvaluateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => EvaluateGateCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkflowRunMutationReceipt> CompletePhaseAsync(
        WorkflowPhaseCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CompletePhaseCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<WorkflowRunMutationReceipt> AdvanceObjectiveCoreAsync(
        SqliteConnection connection,
        WorkflowObjectiveAdvanceCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkflowRunMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkflowRunMutationStatus.IdempotentReplay };
        }

        var run = await ReadRunForMutationAsync(
            connection, transaction, command.TenantId, command.RunId, cancellationToken);
        WorkflowRunMutationReceipt receipt;
        if (run is null)
        {
            receipt = ItemRejected(WorkflowRunMutationStatus.NotFound, command.RunId);
        }
        else if (run.Version != command.ExpectedRunVersion)
        {
            receipt = ItemRejected(
                WorkflowRunMutationStatus.VersionConflict, command.RunId, run.Version, run.State);
        }
        else if (run.State != "running")
        {
            receipt = ItemRejected(
                WorkflowRunMutationStatus.InvalidState, command.RunId, run.Version, run.State);
        }
        else
        {
            var phase = await ReadActivePhaseAsync(
                connection, transaction, command.RunId, command.PhaseKey, cancellationToken);
            if (phase is null)
            {
                receipt = ItemRejected(
                    WorkflowRunMutationStatus.PhaseNotActive, command.RunId, run.Version, run.State);
            }
            else
            {
                var objective = await ReadObjectiveAsync(
                    connection, transaction, phase.PhaseRunId, command.ObjectiveKey, cancellationToken);
                if (objective is null)
                {
                    receipt = ItemRejected(
                        WorkflowRunMutationStatus.ObjectiveNotFound,
                        command.RunId,
                        run.Version,
                        run.State);
                }
                else if (objective.Kind == "gate")
                {
                    receipt = ItemRejected(
                        WorkflowRunMutationStatus.GateEvaluationRequired,
                        command.RunId,
                        run.Version,
                        run.State);
                }
                else if (ObjectiveStateRank(command.TargetState) !=
                    ObjectiveStateRank(objective.State) + 1)
                {
                    receipt = ItemRejected(
                        WorkflowRunMutationStatus.ObjectiveTransitionInvalid,
                        command.RunId,
                        run.Version,
                        run.State);
                }
                else
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE workflow_objective_runs
                        SET state=$state,version=version+1,updated_at=$occurredAt
                        WHERE id=$id AND version=$version;
                        """,
                        cancellationToken,
                        ("$state", command.TargetState),
                        ("$occurredAt", Store(command.OccurredAt)),
                        ("$id", objective.ObjectiveRunId),
                        ("$version", objective.Version));
                    var nextVersion = run.Version + 1;
                    await UpdateRunVersionAsync(
                        connection, transaction, command.TenantId, command.RunId,
                        run.Version, nextVersion, state: null, completedAt: null, cancellationToken);
                    receipt = new WorkflowRunMutationReceipt(
                        WorkflowRunMutationStatus.Applied,
                        command.RunId,
                        nextVersion,
                        run.State);
                }
            }
        }

        return await FinalizeItemMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            "progress.updated",
            "advanceObjective",
            command.PhaseKey,
            command.ObjectiveKey,
            receipt,
            cancellationToken);
    }

    private static async Task<WorkflowRunMutationReceipt> EvaluateGateCoreAsync(
        SqliteConnection connection,
        WorkflowGateEvaluateCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkflowRunMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkflowRunMutationStatus.IdempotentReplay };
        }

        var run = await ReadRunForMutationAsync(
            connection, transaction, command.TenantId, command.RunId, cancellationToken);
        string? eventPayload = null;
        WorkflowRunMutationReceipt receipt;
        if (run is null)
        {
            receipt = ItemRejected(WorkflowRunMutationStatus.NotFound, command.RunId);
        }
        else if (run.Version != command.ExpectedRunVersion)
        {
            receipt = ItemRejected(
                WorkflowRunMutationStatus.VersionConflict, command.RunId, run.Version, run.State);
        }
        else if (run.State != "running")
        {
            receipt = ItemRejected(
                WorkflowRunMutationStatus.InvalidState, command.RunId, run.Version, run.State);
        }
        else
        {
            var phase = await ReadActivePhaseAsync(
                connection, transaction, command.RunId, command.PhaseKey, cancellationToken);
            if (phase is null)
            {
                receipt = ItemRejected(
                    WorkflowRunMutationStatus.PhaseNotActive, command.RunId, run.Version, run.State);
            }
            else
            {
                var gate = await ReadGateAsync(
                    connection, transaction, phase.PhaseRunId, command.GateKey, cancellationToken);
                if (gate is null)
                {
                    receipt = ItemRejected(
                        WorkflowRunMutationStatus.GateNotFound,
                        command.RunId,
                        run.Version,
                        run.State);
                }
                else if (gate.State == "passed")
                {
                    receipt = ItemRejected(
                        WorkflowRunMutationStatus.InvalidState,
                        command.RunId,
                        run.Version,
                        run.State);
                }
                else if (!await GateRequirementsMetAsync(
                    connection, transaction, phase.PhaseRunId,
                    gate.GateDefinitionId, gate.MinimumRequiredState, cancellationToken))
                {
                    receipt = ItemRejected(
                        WorkflowRunMutationStatus.GateRequirementsNotMet,
                        command.RunId,
                        run.Version,
                        run.State);
                }
                else
                {
                    var gateState = command.Passed ? "passed" : "failed";
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE workflow_gate_runs
                        SET state=$state,version=version+1,evaluated_at=$occurredAt,
                            decided_by_profile_id=$profile,decision_note=$note
                        WHERE id=$id AND version=$version;
                        """,
                        cancellationToken,
                        ("$state", gateState),
                        ("$occurredAt", Store(command.OccurredAt)),
                        ("$profile", (object?)command.DecidedByProfileId ?? DBNull.Value),
                        ("$note", (object?)command.Note ?? DBNull.Value),
                        ("$id", gate.GateRunId),
                        ("$version", gate.Version));
                    if (command.Passed)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            UPDATE workflow_objective_runs
                            SET state='approved',version=version+1,updated_at=$occurredAt
                            WHERE id=$id;
                            """,
                            cancellationToken,
                            ("$occurredAt", Store(command.OccurredAt)),
                            ("$id", gate.ObjectiveRunId));
                    }

                    var nextVersion = run.Version + 1;
                    await UpdateRunVersionAsync(
                        connection, transaction, command.TenantId, command.RunId,
                        run.Version, nextVersion, state: null, completedAt: null, cancellationToken);
                    receipt = new WorkflowRunMutationReceipt(
                        WorkflowRunMutationStatus.Applied,
                        command.RunId,
                        nextVersion,
                        run.State);
                    eventPayload = JsonSerializer.Serialize(new
                    {
                        projectId = run.ProjectId,
                        gateId = gate.GateRunId,
                        runId = command.RunId,
                        from = gate.State,
                        to = gateState,
                        decidedByProfileId = command.DecidedByProfileId,
                        note = command.Note,
                    });
                }
            }
        }

        return await FinalizeItemMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            "gate.changed",
            command.Passed ? "passGate" : "failGate",
            command.PhaseKey,
            command.GateKey,
            receipt,
            cancellationToken,
            eventPayload);
    }

    private static async Task<WorkflowRunMutationReceipt> CompletePhaseCoreAsync(
        SqliteConnection connection,
        WorkflowPhaseCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var hash = WorkflowRunMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkflowRunMutationStatus.IdempotentReplay };
        }

        var run = await ReadRunForMutationAsync(
            connection, transaction, command.TenantId, command.RunId, cancellationToken);
        WorkflowRunMutationReceipt receipt;
        if (run is null)
        {
            receipt = ItemRejected(WorkflowRunMutationStatus.NotFound, command.RunId);
        }
        else if (run.Version != command.ExpectedRunVersion)
        {
            receipt = ItemRejected(
                WorkflowRunMutationStatus.VersionConflict, command.RunId, run.Version, run.State);
        }
        else if (run.State != "running")
        {
            receipt = ItemRejected(
                WorkflowRunMutationStatus.InvalidState, command.RunId, run.Version, run.State);
        }
        else
        {
            var phase = await ReadActivePhaseAsync(
                connection, transaction, command.RunId, command.PhaseKey, cancellationToken);
            if (phase is null)
            {
                receipt = ItemRejected(
                    WorkflowRunMutationStatus.PhaseNotActive, command.RunId, run.Version, run.State);
            }
            else if (!await PhaseCanCompleteAsync(
                connection, transaction, phase.PhaseRunId, cancellationToken))
            {
                receipt = ItemRejected(
                    WorkflowRunMutationStatus.PhaseCompletionBlocked,
                    command.RunId,
                    run.Version,
                    run.State);
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE workflow_phase_runs
                    SET state='completed',version=version+1,completed_at=$occurredAt
                    WHERE id=$id AND version=$version;
                    """,
                    cancellationToken,
                    ("$occurredAt", Store(command.OccurredAt)),
                    ("$id", phase.PhaseRunId),
                    ("$version", phase.Version));
                var activated = await ActivateNextPhaseAsync(
                    connection, transaction, command.RunId, command.OccurredAt, cancellationToken);
                var nextState = activated ? "running" : "completed";
                var nextVersion = run.Version + 1;
                await UpdateRunVersionAsync(
                    connection,
                    transaction,
                    command.TenantId,
                    command.RunId,
                    run.Version,
                    nextVersion,
                    activated ? null : nextState,
                    activated ? null : command.OccurredAt,
                    cancellationToken);
                receipt = new WorkflowRunMutationReceipt(
                    WorkflowRunMutationStatus.Applied,
                    command.RunId,
                    nextVersion,
                    nextState);
            }
        }

        return await FinalizeItemMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            command.OccurredAt,
            "progress.updated",
            "completePhase",
            command.PhaseKey,
            itemKey: null,
            receipt,
            cancellationToken);
    }

    private static async Task<ActivePhaseRow?> ReadActivePhaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string phaseKey,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT r.id,r.version,r.phase_order FROM workflow_phase_runs r
            JOIN workflow_phase_definitions d ON d.id=r.phase_definition_id
            WHERE r.workflow_run_id=$runId AND r.state='active' AND d.phase_key=$phaseKey;
            """;
        Add(query, "$runId", runId);
        Add(query, "$phaseKey", phaseKey);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ActivePhaseRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2))
            : null;
    }

    private static async Task<ObjectiveMutationRow?> ReadObjectiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string phaseRunId,
        string objectiveKey,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT r.id,r.state,r.version,d.kind FROM workflow_objective_runs r
            JOIN workflow_objective_definitions d ON d.id=r.objective_definition_id
            WHERE r.phase_run_id=$phaseRunId AND d.objective_key=$key;
            """;
        Add(query, "$phaseRunId", phaseRunId);
        Add(query, "$key", objectiveKey);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ObjectiveMutationRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private static async Task<GateMutationRow?> ReadGateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string phaseRunId,
        string gateKey,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT r.id,r.gate_definition_id,r.state,r.version,d.minimum_required_state,o.id
            FROM workflow_gate_runs r
            JOIN workflow_gate_definitions d ON d.id=r.gate_definition_id
            JOIN workflow_objective_runs o
              ON o.phase_run_id=r.phase_run_id
             AND o.objective_definition_id=d.objective_definition_id
            WHERE r.phase_run_id=$phaseRunId AND d.gate_key=$key;
            """;
        Add(query, "$phaseRunId", phaseRunId);
        Add(query, "$key", gateKey);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GateMutationRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    private static async Task<bool> GateRequirementsMetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string phaseRunId,
        string gateDefinitionId,
        string minimumState,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT o.state FROM workflow_gate_requirements r
            JOIN workflow_objective_runs o
              ON o.phase_run_id=$phaseRunId
             AND o.objective_definition_id=r.objective_definition_id
            WHERE r.gate_definition_id=$gateId ORDER BY r.requirement_order;
            """;
        Add(query, "$phaseRunId", phaseRunId);
        Add(query, "$gateId", gateDefinitionId);
        var found = false;
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found = true;
            if (ObjectiveStateRank(reader.GetString(0)) < ObjectiveStateRank(minimumState))
            {
                return false;
            }
        }

        return found;
    }

    private static async Task<bool> PhaseCanCompleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string phaseRunId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT
              (SELECT COUNT(*) FROM workflow_objective_runs
               WHERE phase_run_id=$phaseRunId AND state='pending'),
              (SELECT COUNT(*) FROM workflow_gate_runs
               WHERE phase_run_id=$phaseRunId AND state<>'passed');
            """;
        Add(query, "$phaseRunId", phaseRunId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
            reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0;
    }

    private static async Task<bool> ActivateNextPhaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText =
            """
            UPDATE workflow_phase_runs
            SET state='active',version=version+1,activated_at=$occurredAt
            WHERE id=(SELECT id FROM workflow_phase_runs
                      WHERE workflow_run_id=$runId AND state='pending'
                      ORDER BY phase_order LIMIT 1);
            """;
        Add(mutation, "$occurredAt", Store(occurredAt));
        Add(mutation, "$runId", runId);
        return await mutation.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task UpdateRunVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string runId,
        long expectedVersion,
        long nextVersion,
        string? state,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText =
            """
            UPDATE workflow_runs
            SET version=$nextVersion,state=COALESCE($state,state),
                completed_at=COALESCE($completedAt,completed_at)
            WHERE tenant_id=$tenantId AND id=$runId AND version=$expectedVersion;
            """;
        Add(mutation, "$nextVersion", nextVersion);
        mutation.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        mutation.Parameters.AddWithValue(
            "$completedAt",
            completedAt is null ? DBNull.Value : Store(completedAt.Value));
        Add(mutation, "$tenantId", tenantId);
        Add(mutation, "$runId", runId);
        Add(mutation, "$expectedVersion", expectedVersion);
        if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The workflow run changed during serialized item mutation.");
        }
    }

    private static async Task<WorkflowRunMutationReceipt> FinalizeItemMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string key,
        string hash,
        DateTimeOffset occurredAt,
        string eventType,
        string operation,
        string phaseKey,
        string? itemKey,
        WorkflowRunMutationReceipt receipt,
        CancellationToken cancellationToken,
        string? appliedPayload = null)
    {
        var final = receipt;
        if (receipt.Status == WorkflowRunMutationStatus.Applied)
        {
            var payload = appliedPayload ?? JsonSerializer.Serialize(new
            {
                runId = receipt.RunId,
                state = receipt.RunState,
                version = receipt.RunVersion,
                operation,
                phaseKey,
                itemKey,
            });
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection, transaction, tenantId, cancellationToken);
            var eventHash = AuditLedgerHash.Compute(
                previousHash, tenantId, sequence, eventType, payload, occurredAt);
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
                INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at)
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
            ("$key", key),
            ("$hash", hash),
            ("$response", JsonSerializer.Serialize(final)),
            ("$occurredAt", Store(occurredAt)));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static WorkflowRunMutationReceipt ItemRejected(
        WorkflowRunMutationStatus status,
        string runId,
        long? version = null,
        string? state = null) => new(status, runId, version, state);

    private sealed record ActivePhaseRow(string PhaseRunId, long Version, int Order);

    private sealed record ObjectiveMutationRow(
        string ObjectiveRunId,
        string State,
        long Version,
        string Kind);

    private sealed record GateMutationRow(
        string GateRunId,
        string GateDefinitionId,
        string State,
        long Version,
        string MinimumRequiredState,
        string ObjectiveRunId);
}
