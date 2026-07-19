using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowCatalogStore
{
    public Task<WorkflowVersionCatalogRecord> PublishVersionAsync(
        WorkflowVersionPublishCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return PublishVersionCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowBindingCatalogRecord> SetOperationModeAsync(
        WorkflowOperationModeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetOperationModeCoreAsync(command, cancellationToken);
    }

    private async Task<WorkflowVersionCatalogRecord> PublishVersionCoreAsync(
        WorkflowVersionPublishCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"workflow-template:{value.TenantId}:{value.TemplateId}"));
        int nextVersion;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT MAX(v.version) FROM harness.workflow_definitions d " +
                "LEFT JOIN harness.workflow_definition_versions v ON v.definition_id=d.id " +
                "WHERE d.tenant_id=$1 AND d.id=$2;";
            query.Parameters.Add(Text(value.TenantId));
            query.Parameters.Add(Text(value.TemplateId));
            var scalar = await query.ExecuteScalarAsync(cancellationToken);
            if (scalar is null || scalar is DBNull)
            {
                throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            }

            nextVersion = Convert.ToInt32(scalar, CultureInfo.InvariantCulture) + 1;
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.workflow_definition_versions
                (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,
                 phase_configs_json,default_operation_mode,transitions_json,changelog)
            VALUES ($1,$2,$3,$4,'published',$5,$6,$6,$7,$8,$9,$10);
            """,
            cancellationToken,
            Text(value.VersionId), Text(value.TenantId), Text(value.TemplateId),
            Integer(nextVersion), Text(WorkflowDefinitionContentHash.Compute(value.Phases)),
            Timestamp(value.OccurredAt), Json(value.PhaseConfigsJson),
            NullableText(value.DefaultOperationMode), Json(value.TransitionsJson),
            NullableText(value.Changelog));
        foreach (var phase in value.Phases)
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.workflow_phase_definitions
                    (id,tenant_id,definition_version_id,phase_key,name,phase_order)
                VALUES ($1,$2,$3,$4,$5,$6);
                """,
                cancellationToken,
                Text(phase.PhaseDefinitionId), Text(value.TenantId), Text(value.VersionId),
                Text(phase.Key), Text(phase.Name), Integer(phase.Order));
            foreach (var objective in phase.Objectives)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.workflow_objective_definitions
                        (id,tenant_id,phase_definition_id,objective_key,name,kind,weight)
                    VALUES ($1,$2,$3,$4,$5,$6,$7);
                    """,
                    cancellationToken,
                    Text(objective.ObjectiveDefinitionId), Text(value.TenantId),
                    Text(phase.PhaseDefinitionId), Text(objective.Key), Text(objective.Name),
                    Text(objective.Kind), Numeric(objective.Weight));
            }

            foreach (var gate in phase.Gates)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.workflow_gate_definitions
                        (id,tenant_id,phase_definition_id,objective_definition_id,
                         gate_key,name,minimum_required_state)
                    VALUES ($1,$2,$3,$4,$5,$6,$7);
                    """,
                    cancellationToken,
                    Text(gate.GateDefinitionId), Text(value.TenantId),
                    Text(phase.PhaseDefinitionId), Text(gate.ObjectiveDefinitionId),
                    Text(gate.Key), Text(gate.Name), Text(gate.MinimumRequiredState));
                for (var index = 0; index < gate.RequiredObjectiveDefinitionIds.Count; index++)
                {
                    await ExecuteAsync(
                        connection, transaction,
                        """
                        INSERT INTO harness.workflow_gate_requirements
                            (phase_definition_id,gate_definition_id,
                             objective_definition_id,requirement_order)
                        VALUES ($1,$2,$3,$4);
                        """,
                        cancellationToken,
                        Text(phase.PhaseDefinitionId), Text(gate.GateDefinitionId),
                        Text(gate.RequiredObjectiveDefinitionIds[index]), Integer(index + 1));
                }
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.TemplateId,
            versionId = value.VersionId,
            version = nextVersion,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, value.TenantId, "workflow.versionPublished", payload,
            value.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, value.TenantId, "workflow.versionPublished", payload,
            value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadVersionAsync(connection, value.TenantId, value.VersionId, cancellationToken))!;
    }

    private async Task<WorkflowBindingCatalogRecord> SetOperationModeCoreAsync(
        WorkflowOperationModeCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string project;
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText =
                "SELECT project_id FROM harness.workflow_bindings WHERE tenant_id=$1 AND id=$2 FOR UPDATE;";
            check.Parameters.Add(Text(value.TenantId));
            check.Parameters.Add(Text(value.WorkflowId));
            project = (await check.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new WorkflowCatalogReferenceNotFoundException("workflow"))
                .TrimEnd();
        }

        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.workflow_bindings SET operation_mode=$1,pause_gates_json=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text(value.Mode), Json(JsonSerializer.Serialize(value.PauseGates, JsonOptions)),
            Text(value.TenantId), Text(value.WorkflowId));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.workflow_risk_acceptances
                (id,tenant_id,workflow_id,mode,accepted_by_profile_id,note,accepted_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7);
            """,
            cancellationToken,
            Text(value.AcceptanceId), Text(value.TenantId), Text(value.WorkflowId),
            Text(value.Mode), Text(value.AcceptedByProfileId), Text(value.Note),
            Timestamp(value.OccurredAt));
        var auditId = UlidValue.New(value.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = project,
            auditEvent = new
            {
                id = auditId,
                actorKind = "user",
                actorId = value.AcceptedByProfileId,
                action = "workflow.operationModeChanged",
                targetType = "workflow",
                targetId = value.WorkflowId,
                detail = $"Mode changed to {value.Mode}. Acceptance: {value.Note}",
                occurredAt = value.OccurredAt,
            },
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, value.TenantId, "audit.eventAppended", payload,
            value.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, value.TenantId, "audit.eventAppended", payload,
            value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadBindingAsync(connection, value.TenantId, value.WorkflowId, cancellationToken))!;
    }
}
