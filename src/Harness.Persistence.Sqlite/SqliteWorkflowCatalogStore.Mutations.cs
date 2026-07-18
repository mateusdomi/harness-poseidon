using System.Text.Json;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkflowCatalogStore
{
    public Task<WorkflowVersionCatalogRecord> PublishVersionAsync(
        WorkflowVersionPublishCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => PublishVersionCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowBindingCatalogRecord> SetOperationModeAsync(
        WorkflowOperationModeCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => SetOperationModeCoreAsync(c, command, token), cancellationToken);

    private static async Task<WorkflowVersionCatalogRecord> PublishVersionCoreAsync(
        SqliteConnection c, WorkflowVersionPublishCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        int nextVersion; await using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "SELECT MAX(v.version) FROM workflow_definitions d LEFT JOIN workflow_definition_versions v ON v.definition_id=d.id WHERE d.tenant_id=$tenant AND d.id=$template;";
            Add(q, "$tenant", value.TenantId); Add(q, "$template", value.TemplateId); var scalar = await q.ExecuteScalarAsync(token);
            if (scalar is null || scalar is DBNull) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            nextVersion = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture) + 1;
        }
        var at = Store(value.OccurredAt); await using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "INSERT INTO workflow_definition_versions (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,phase_configs_json,default_operation_mode,transitions_json,changelog) VALUES ($id,$tenant,$template,$version,'published',$hash,$at,$at,$configs,$mode,$transitions,$changelog);";
            Add(q, "$id", value.VersionId); Add(q, "$tenant", value.TenantId); Add(q, "$template", value.TemplateId); Add(q, "$version", nextVersion);
            Add(q, "$hash", WorkflowDefinitionContentHash.Compute(value.Phases)); Add(q, "$at", at); Add(q, "$configs", value.PhaseConfigsJson);
            AddNullable(q, "$mode", value.DefaultOperationMode); Add(q, "$transitions", value.TransitionsJson); AddNullable(q, "$changelog", value.Changelog); await q.ExecuteNonQueryAsync(token);
        }
        foreach (var phase in value.Phases)
        {
            await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_phase_definitions (id,tenant_id,definition_version_id,phase_key,name,phase_order) VALUES ($id,$tenant,$version,$key,$name,$order);", token,
                ("$id", phase.PhaseDefinitionId), ("$tenant", value.TenantId), ("$version", value.VersionId), ("$key", phase.Key), ("$name", phase.Name), ("$order", phase.Order));
            foreach (var objective in phase.Objectives) await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_objective_definitions (id,tenant_id,phase_definition_id,objective_key,name,kind,weight) VALUES ($id,$tenant,$phase,$key,$name,$kind,$weight);", token,
                ("$id", objective.ObjectiveDefinitionId), ("$tenant", value.TenantId), ("$phase", phase.PhaseDefinitionId), ("$key", objective.Key), ("$name", objective.Name), ("$kind", objective.Kind), ("$weight", objective.Weight));
            foreach (var gate in phase.Gates)
            {
                await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_gate_definitions (id,tenant_id,phase_definition_id,objective_definition_id,gate_key,name,minimum_required_state) VALUES ($id,$tenant,$phase,$objective,$key,$name,$minimum);", token,
                    ("$id", gate.GateDefinitionId), ("$tenant", value.TenantId), ("$phase", phase.PhaseDefinitionId), ("$objective", gate.ObjectiveDefinitionId), ("$key", gate.Key), ("$name", gate.Name), ("$minimum", gate.MinimumRequiredState));
                for (var index = 0; index < gate.RequiredObjectiveDefinitionIds.Count; index++) await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_gate_requirements (phase_definition_id,gate_definition_id,objective_definition_id,requirement_order) VALUES ($phase,$gate,$objective,$order);", token,
                    ("$phase", phase.PhaseDefinitionId), ("$gate", gate.GateDefinitionId), ("$objective", gate.RequiredObjectiveDefinitionIds[index]), ("$order", index + 1));
            }
        }
        var payload = JsonSerializer.Serialize(new { templateId = value.TemplateId, versionId = value.VersionId, version = nextVersion }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.versionPublished", payload, value.OccurredAt, token);
        await using (var outbox = c.CreateCommand()) { outbox.Transaction = tx; outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,'workflow.versionPublished',$payload,$at);"; Add(outbox, "$id", UlidValue.New(value.OccurredAt).ToString()); Add(outbox, "$tenant", value.TenantId); Add(outbox, "$payload", payload); Add(outbox, "$at", at); await outbox.ExecuteNonQueryAsync(token); }
        await tx.CommitAsync(token); return (await ReadVersionAsync(c, value.TenantId, value.VersionId, token))!;
    }

    private static async Task<WorkflowBindingCatalogRecord> SetOperationModeCoreAsync(
        SqliteConnection c, WorkflowOperationModeCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token); string project;
        await using (var check = c.CreateCommand()) { check.Transaction = tx; check.CommandText = "SELECT project_id FROM workflow_bindings WHERE tenant_id=$tenant AND id=$id;"; Add(check, "$tenant", value.TenantId); Add(check, "$id", value.WorkflowId); project = await check.ExecuteScalarAsync(token) as string ?? throw new WorkflowCatalogReferenceNotFoundException("workflow"); }
        await using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "UPDATE workflow_bindings SET operation_mode=$mode,pause_gates_json=$gates WHERE tenant_id=$tenant AND id=$id; INSERT INTO workflow_risk_acceptances (id,tenant_id,workflow_id,mode,accepted_by_profile_id,note,accepted_at) VALUES ($acceptance,$tenant,$id,$mode,$profile,$note,$at);"; Add(q, "$mode", value.Mode); Add(q, "$gates", JsonSerializer.Serialize(value.PauseGates, JsonOptions)); Add(q, "$tenant", value.TenantId); Add(q, "$id", value.WorkflowId); Add(q, "$acceptance", value.AcceptanceId); Add(q, "$profile", value.AcceptedByProfileId); Add(q, "$note", value.Note); Add(q, "$at", Store(value.OccurredAt)); await q.ExecuteNonQueryAsync(token); }
        var auditId = UlidValue.New(value.OccurredAt).ToString(); var payload = JsonSerializer.Serialize(new { projectId = project, auditEvent = new { id = auditId, actorKind = "user", actorId = value.AcceptedByProfileId, action = "workflow.operationModeChanged", targetType = "workflow", targetId = value.WorkflowId, detail = $"Mode changed to {value.Mode}. Acceptance: {value.Note}", occurredAt = value.OccurredAt } }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "audit.eventAppended", payload, value.OccurredAt, token);
        await using (var outbox = c.CreateCommand()) { outbox.Transaction = tx; outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,'audit.eventAppended',$payload,$at);"; Add(outbox, "$id", UlidValue.New(value.OccurredAt).ToString()); Add(outbox, "$tenant", value.TenantId); Add(outbox, "$payload", payload); Add(outbox, "$at", Store(value.OccurredAt)); await outbox.ExecuteNonQueryAsync(token); }
        await tx.CommitAsync(token); return (await ReadBindingAsync(c, value.TenantId, value.WorkflowId, token))!;
    }

    private static async Task ExecuteCatalogAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string Name, object Value)[] values)
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var value in values) Add(q, value.Name, value.Value); await q.ExecuteNonQueryAsync(token); }
}
