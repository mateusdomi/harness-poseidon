using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowStore
{
    public Task<WorkflowRunMutationReceipt> AdvanceObjectiveAsync(
        WorkflowObjectiveAdvanceCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return AdvanceObjectiveCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowRunMutationReceipt> EvaluateGateAsync(
        WorkflowGateEvaluateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return EvaluateGateCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowRunMutationReceipt> CompletePhaseAsync(
        WorkflowPhaseCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return CompletePhaseCoreAsync(command, cancellationToken);
    }

    private async Task<WorkflowRunMutationReceipt> AdvanceObjectiveCoreAsync(
        WorkflowObjectiveAdvanceCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockItemMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey,
            command.RunId, cancellationToken);
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
                        UPDATE harness.workflow_objective_runs
                        SET state=$1,version=version+1,updated_at=$2
                        WHERE id=$3 AND version=$4;
                        """,
                        cancellationToken,
                        Text(command.TargetState),
                        Timestamp(command.OccurredAt),
                        Text(objective.ObjectiveRunId),
                        Bigint(objective.Version));
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

    private async Task<WorkflowRunMutationReceipt> EvaluateGateCoreAsync(
        WorkflowGateEvaluateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockItemMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey,
            command.RunId, cancellationToken);
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
                        UPDATE harness.workflow_gate_runs
                        SET state=$1,version=version+1,evaluated_at=$2
                        WHERE id=$3 AND version=$4;
                        """,
                        cancellationToken,
                        Text(gateState),
                        Timestamp(command.OccurredAt),
                        Text(gate.GateRunId),
                        Bigint(gate.Version));
                    if (command.Passed)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            UPDATE harness.workflow_objective_runs
                            SET state='approved',version=version+1,updated_at=$1
                            WHERE id=$2;
                            """,
                            cancellationToken,
                            Timestamp(command.OccurredAt),
                            Text(gate.ObjectiveRunId));
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
            cancellationToken);
    }

    private async Task<WorkflowRunMutationReceipt> CompletePhaseCoreAsync(
        WorkflowPhaseCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockItemMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey,
            command.RunId, cancellationToken);
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
                    UPDATE harness.workflow_phase_runs
                    SET state='completed',version=version+1,completed_at=$1
                    WHERE id=$2 AND version=$3;
                    """,
                    cancellationToken,
                    Timestamp(command.OccurredAt),
                    Text(phase.PhaseRunId),
                    Bigint(phase.Version));
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        string phaseKey,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT r.id,r.version,r.phase_order FROM harness.workflow_phase_runs r
            JOIN harness.workflow_phase_definitions d ON d.id=r.phase_definition_id
            WHERE r.workflow_run_id=$1 AND r.state='active' AND d.phase_key=$2;
            """;
        query.Parameters.Add(Text(runId));
        query.Parameters.Add(Text(phaseKey));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ActivePhaseRow(Trim(reader.GetString(0)), reader.GetInt64(1), reader.GetInt32(2))
            : null;
    }

    private static async Task<ObjectiveMutationRow?> ReadObjectiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string phaseRunId,
        string objectiveKey,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT r.id,r.state,r.version,d.kind FROM harness.workflow_objective_runs r
            JOIN harness.workflow_objective_definitions d ON d.id=r.objective_definition_id
            WHERE r.phase_run_id=$1 AND d.objective_key=$2;
            """;
        query.Parameters.Add(Text(phaseRunId));
        query.Parameters.Add(Text(objectiveKey));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ObjectiveMutationRow(
                Trim(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private static async Task<GateMutationRow?> ReadGateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string phaseRunId,
        string gateKey,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT r.id,r.gate_definition_id,r.state,r.version,d.minimum_required_state,o.id
            FROM harness.workflow_gate_runs r
            JOIN harness.workflow_gate_definitions d ON d.id=r.gate_definition_id
            JOIN harness.workflow_objective_runs o
              ON o.phase_run_id=r.phase_run_id
             AND o.objective_definition_id=d.objective_definition_id
            WHERE r.phase_run_id=$1 AND d.gate_key=$2;
            """;
        query.Parameters.Add(Text(phaseRunId));
        query.Parameters.Add(Text(gateKey));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GateMutationRow(
                Trim(reader.GetString(0)), Trim(reader.GetString(1)), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), Trim(reader.GetString(5)))
            : null;
    }

    private static async Task<bool> GateRequirementsMetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string phaseRunId,
        string gateDefinitionId,
        string minimumState,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT o.state FROM harness.workflow_gate_requirements r
            JOIN harness.workflow_objective_runs o
              ON o.phase_run_id=$1
             AND o.objective_definition_id=r.objective_definition_id
            WHERE r.gate_definition_id=$2 ORDER BY r.requirement_order;
            """;
        query.Parameters.Add(Text(phaseRunId));
        query.Parameters.Add(Text(gateDefinitionId));
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string phaseRunId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT
              (SELECT COUNT(*) FROM harness.workflow_objective_runs
               WHERE phase_run_id=$1 AND state='pending'),
              (SELECT COUNT(*) FROM harness.workflow_gate_runs
               WHERE phase_run_id=$1 AND state<>'passed');
            """;
        query.Parameters.Add(Text(phaseRunId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
            reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0;
    }

    private static async Task<bool> ActivateNextPhaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText =
            """
            UPDATE harness.workflow_phase_runs
            SET state='active',version=version+1,activated_at=$1
            WHERE id=(SELECT id FROM harness.workflow_phase_runs
                      WHERE workflow_run_id=$2 AND state='pending'
                      ORDER BY phase_order LIMIT 1);
            """;
        mutation.Parameters.Add(Timestamp(occurredAt));
        mutation.Parameters.Add(Text(runId));
        return await mutation.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task UpdateRunVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
        mutation.CommandText = state is null
            ? "UPDATE harness.workflow_runs SET version=$1 WHERE tenant_id=$2 AND id=$3 AND version=$4;"
            : "UPDATE harness.workflow_runs SET version=$1,state=$2,completed_at=$3 WHERE tenant_id=$4 AND id=$5 AND version=$6;";
        mutation.Parameters.Add(Bigint(nextVersion));
        if (state is not null)
        {
            mutation.Parameters.Add(Text(state));
            mutation.Parameters.Add(Timestamp(completedAt ??
                throw new InvalidOperationException("Completed run requires a timestamp.")));
        }

        mutation.Parameters.Add(Text(tenantId));
        mutation.Parameters.Add(Text(runId));
        mutation.Parameters.Add(Bigint(expectedVersion));
        if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The workflow run changed while its item lock was held.");
        }
    }

    private static async Task<WorkflowRunMutationReceipt> FinalizeItemMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        DateTimeOffset occurredAt,
        string eventType,
        string operation,
        string phaseKey,
        string? itemKey,
        WorkflowRunMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == WorkflowRunMutationStatus.Applied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
                cancellationToken,
                Text($"audit-ledger:{tenantId}"));
            var payload = JsonSerializer.Serialize(new
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
                "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($1,$2,$3,$4,$5);",
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
            Text(key),
            Text(hash),
            Json(JsonSerializer.Serialize(final)),
            Timestamp(occurredAt));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static async Task LockItemMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string runId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"workflow-mutation:{tenantId}:{key}"));
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"workflow-run:{runId}"));
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
