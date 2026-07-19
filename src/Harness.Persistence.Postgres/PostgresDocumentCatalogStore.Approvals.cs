using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDocumentCatalogStore
{
    public Task<ApprovalCatalogRecord> CreateGeneralApprovalAsync(
        GeneralApprovalCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateGeneralCoreAsync(command, cancellationToken);
    }

    public Task<ApprovalCatalogRecord?> ResolveGeneralApprovalAsync(
        GeneralApprovalResolveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ResolveGeneralCoreAsync(command, cancellationToken);
    }

    private async Task<ApprovalCatalogRecord> CreateGeneralCoreAsync(
        GeneralApprovalCreateCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGeneralReferencesAsync(connection, transaction, value, cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO harness.general_approval_requests " +
                "(id,tenant_id,project_id,gate_id,task_id,title,description,priority,due_at,state," +
                "requested_by_agent_id,requested_at,version) VALUES " +
                "($1,$2,$3,$4,$5,$6,$7,$8,$9,'pending',$10,$11,1);";
            insert.Parameters.Add(Text(value.Id));
            insert.Parameters.Add(Text(value.TenantId));
            insert.Parameters.Add(Text(value.ProjectId));
            insert.Parameters.Add(NullableText(value.GateId));
            insert.Parameters.Add(NullableText(value.TaskId));
            insert.Parameters.Add(Text(value.Title));
            insert.Parameters.Add(Text(value.Description));
            insert.Parameters.Add(Text(value.Priority));
            insert.Parameters.Add(NullableTimestamp(value.DueAt));
            insert.Parameters.Add(Text(value.RequestedByAgentId));
            insert.Parameters.Add(Timestamp(value.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var record = new ApprovalCatalogRecord(value.Id, value.ProjectId, value.GateId, value.TaskId,
            null, value.Title, value.Description, value.Priority, value.DueAt, "pending",
            value.RequestedByAgentId, value.OccurredAt, null, null, null, 1);
        var payload = JsonSerializer.Serialize(new { projectId = value.ProjectId, approval = ToPayload(record) });
        await AppendEventAsync(
            connection, transaction, value.TenantId, "approval.requested", payload,
            value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    private async Task<ApprovalCatalogRecord?> ResolveGeneralCoreAsync(
        GeneralApprovalResolveCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        ApprovalCatalogRecord? current;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT * FROM (" + ApprovalSelect +
                ") a WHERE tenant_id=$1 AND id=$2 AND document_id IS NULL;";
            query.Parameters.Add(Text(value.TenantId));
            query.Parameters.Add(Text(value.Id));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken) ? ReadApproval(reader) : null;
        }

        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (current.State != "pending")
        {
            throw new InvalidOperationException("Approval is already resolved.");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE harness.general_approval_requests SET state=$1,resolved_by_profile_id=$2," +
                "resolved_at=$3,resolution_note=$4,version=version+1 WHERE tenant_id=$5 AND id=$6 AND state='pending';";
            update.Parameters.Add(Text(value.Decision));
            update.Parameters.Add(Text(value.ResolvedByProfileId));
            update.Parameters.Add(Timestamp(value.OccurredAt));
            update.Parameters.Add(NullableText(value.Note));
            update.Parameters.Add(Text(value.TenantId));
            update.Parameters.Add(Text(value.Id));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Approval changed during resolution.");
            }
        }

        GateResolutionRow? gateResolution = null;
        if (current.GateId is not null)
        {
            gateResolution = await EnsureGateCanResolveAsync(
                connection, transaction, value.TenantId, current.GateId, cancellationToken);
            await using (var gate = connection.CreateCommand())
            {
                gate.Transaction = transaction;
                gate.CommandText =
                    "UPDATE harness.workflow_gate_runs SET state=$1,decided_by_profile_id=$2," +
                    "evaluated_at=$3,decision_note=$4,version=version+1 WHERE tenant_id=$5 AND id=$6 AND state IN ('pending','failed');";
                gate.Parameters.Add(Text(value.Decision == "approved" ? "passed" : "failed"));
                gate.Parameters.Add(Text(value.ResolvedByProfileId));
                gate.Parameters.Add(Timestamp(value.OccurredAt));
                gate.Parameters.Add(NullableText(value.Note));
                gate.Parameters.Add(Text(value.TenantId));
                gate.Parameters.Add(Text(current.GateId));
                if (await gate.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("Gate cannot be resolved from its current state.");
                }
            }

            if (value.Decision == "approved")
            {
                await using var objective = connection.CreateCommand();
                objective.Transaction = transaction;
                objective.CommandText =
                    "UPDATE harness.workflow_objective_runs SET state='approved',version=version+1 " +
                    "WHERE tenant_id=$1 AND id=$2 AND state<>'approved';";
                objective.Parameters.Add(Text(value.TenantId));
                objective.Parameters.Add(Text(gateResolution.ObjectiveRunId));
                await objective.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var resolved = current with
        {
            State = value.Decision,
            ResolvedByProfileId = value.ResolvedByProfileId,
            ResolvedAt = value.OccurredAt,
            ResolutionNote = value.Note,
            AggregateVersion = current.AggregateVersion + 1,
        };
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            approvalId = value.Id,
            state = value.Decision,
            resolvedByProfileId = value.ResolvedByProfileId,
            note = value.Note,
        });
        await AppendEventAsync(
            connection, transaction, value.TenantId, "approval.resolved", payload,
            value.OccurredAt, cancellationToken);
        if (current.GateId is not null)
        {
            var gatePayload = JsonSerializer.Serialize(new
            {
                projectId = current.ProjectId,
                gateId = current.GateId,
                runId = gateResolution!.RunId,
                from = "pending",
                to = value.Decision,
                decidedByProfileId = value.ResolvedByProfileId,
                note = value.Note,
            });
            await InsertOutboxAsync(
                connection, transaction, value.TenantId, "gate.changed", gatePayload,
                value.OccurredAt.AddTicks(1), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return resolved;
    }

    private static async Task EnsureGeneralReferencesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        GeneralApprovalCreateCommand value, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL)," +
            "($3 IS NULL OR EXISTS(SELECT 1 FROM harness.work_tasks WHERE tenant_id=$1 AND project_id=$2 AND id=$3))," +
            "($4 IS NULL OR EXISTS(SELECT 1 FROM harness.workflow_gate_runs g JOIN harness.workflow_phase_runs p ON p.id=g.phase_run_id " +
            "JOIN harness.workflow_runs r ON r.id=p.workflow_run_id WHERE g.tenant_id=$1 AND g.id=$4 AND r.project_id=$2));";
        query.Parameters.Add(Text(value.TenantId));
        query.Parameters.Add(Text(value.ProjectId));
        query.Parameters.Add(NullableText(value.TaskId));
        query.Parameters.Add(NullableText(value.GateId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (!reader.GetBoolean(0))
        {
            throw new ApprovalReferenceNotFoundException("project");
        }

        if (!reader.GetBoolean(1))
        {
            throw new ApprovalReferenceNotFoundException("task");
        }

        if (!reader.GetBoolean(2))
        {
            throw new ApprovalReferenceNotFoundException("gate");
        }
    }

    private static async Task<GateResolutionRow> EnsureGateCanResolveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string gateId, CancellationToken cancellationToken)
    {
        string runId;
        string objectiveRunId;
        string definitionId;
        string minimum;
        string phaseRunId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT p.workflow_run_id,o.id,d.id,d.minimum_required_state,p.id " +
                "FROM harness.workflow_gate_runs g JOIN harness.workflow_phase_runs p ON p.id=g.phase_run_id " +
                "JOIN harness.workflow_runs w ON w.id=p.workflow_run_id " +
                "JOIN harness.workflow_gate_definitions d ON d.id=g.gate_definition_id " +
                "JOIN harness.workflow_objective_runs o ON o.phase_run_id=p.id " +
                "AND o.objective_definition_id=d.objective_definition_id " +
                "WHERE g.tenant_id=$1 AND g.id=$2 AND g.state IN ('pending','failed') " +
                "AND p.state='active' AND w.state='running';";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(gateId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Gate is not active for resolution.");
            }

            runId = reader.GetString(0).TrimEnd();
            objectiveRunId = reader.GetString(1).TrimEnd();
            definitionId = reader.GetString(2).TrimEnd();
            minimum = reader.GetString(3);
            phaseRunId = reader.GetString(4).TrimEnd();
        }

        await using var requirements = connection.CreateCommand();
        requirements.Transaction = transaction;
        requirements.CommandText =
            "SELECT o.state FROM harness.workflow_gate_requirements r JOIN harness.workflow_objective_runs o " +
            "ON o.objective_definition_id=r.objective_definition_id AND o.phase_run_id=$1 " +
            "WHERE r.gate_definition_id=$2 ORDER BY r.requirement_order;";
        requirements.Parameters.Add(Text(phaseRunId));
        requirements.Parameters.Add(Text(definitionId));
        await using var states = await requirements.ExecuteReaderAsync(cancellationToken);
        while (await states.ReadAsync(cancellationToken))
        {
            if (StateRank(states.GetString(0)) < StateRank(minimum))
            {
                throw new InvalidOperationException("Gate requirements are not met.");
            }
        }

        return new(runId, objectiveRunId);
    }

    private static int StateRank(string state) => state switch
    {
        "pending" => 0,
        "executed" => 1,
        "validated" => 2,
        "approved" => 3,
        _ => throw new InvalidOperationException("Objective state is invalid."),
    };

    private static async Task AppendEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string eventType, string payload, DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using (var advisory = connection.CreateCommand())
        {
            advisory.Transaction = transaction;
            advisory.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));";
            advisory.Parameters.Add(Text($"audit-ledger:{tenantId}"));
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        long sequence;
        string previous;
        await using (var tail = connection.CreateCommand())
        {
            tail.Transaction = transaction;
            tail.CommandText =
                "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;";
            tail.Parameters.Add(Text(tenantId));
            await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                sequence = reader.GetInt64(0) + 1;
                previous = reader.GetString(1).TrimEnd();
            }
            else
            {
                sequence = 1;
                previous = AuditLedgerHash.Genesis;
            }
        }

        var hash = AuditLedgerHash.Compute(previous, tenantId, sequence, eventType, payload, occurredAt);
        await using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText =
                "INSERT INTO harness.audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) " +
                "VALUES ($1,$2,$3,$4,$5,$6,$7,$8);";
            ledger.Parameters.Add(Text(UlidValue.New(occurredAt).ToString()));
            ledger.Parameters.Add(Text(tenantId));
            ledger.Parameters.Add(Bigint(sequence));
            ledger.Parameters.Add(Text(previous));
            ledger.Parameters.Add(Text(hash));
            ledger.Parameters.Add(Text(eventType));
            ledger.Parameters.Add(Json(payload));
            ledger.Parameters.Add(Timestamp(occurredAt));
            await ledger.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertOutboxAsync(
            connection, transaction, tenantId, eventType, payload, occurredAt, cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string eventType, string payload, DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var outbox = connection.CreateCommand();
        outbox.Transaction = transaction;
        outbox.CommandText =
            "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) " +
            "VALUES ($1,$2,$3,$4,$5);";
        outbox.Parameters.Add(Text(UlidValue.New(occurredAt).ToString()));
        outbox.Parameters.Add(Text(tenantId));
        outbox.Parameters.Add(Text(eventType));
        outbox.Parameters.Add(Json(payload));
        outbox.Parameters.Add(Timestamp(occurredAt));
        await outbox.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToPayload(ApprovalCatalogRecord value) => new
    {
        id = value.Id,
        projectId = value.ProjectId,
        gateId = value.GateId,
        taskId = value.TaskId,
        documentId = value.DocumentId,
        title = value.Title,
        description = value.Description,
        priority = value.Priority,
        dueAt = value.DueAt,
        state = value.State,
        requestedByAgentId = value.RequestedByAgentId,
        requestedAt = value.RequestedAt,
        resolvedByProfileId = value.ResolvedByProfileId,
        resolvedAt = value.ResolvedAt,
        resolutionNote = value.ResolutionNote,
    };

    private sealed record GateResolutionRow(string RunId, string ObjectiveRunId);
}
