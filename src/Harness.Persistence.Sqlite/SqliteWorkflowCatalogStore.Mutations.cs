using System.Text.Json;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkflowCatalogStore
{
    public Task<WorkflowVersionCatalogRecord> CreateDraftAsync(
        WorkflowVersionDraftCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => CreateDraftCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowVersionCatalogRecord> UpdateDraftAsync(
        WorkflowVersionDraftUpdateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => UpdateDraftCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowVersionCatalogRecord> PublishDraftAsync(
        WorkflowVersionDraftPublishCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => PublishDraftCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowTemplateCatalogRecord> ArchiveTemplateAsync(
        WorkflowTemplateArchiveCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => ArchiveTemplateCoreAsync(c, command, token), cancellationToken);

    public Task DeleteTemplateAsync(
        WorkflowTemplateDeleteCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => DeleteTemplateCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowTemplateCatalogRecord> DuplicateTemplateAsync(
        WorkflowTemplateDuplicateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => DuplicateTemplateCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowVersionCatalogRecord> ArchiveVersionAsync(
        WorkflowVersionArchiveCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => ArchiveVersionCoreAsync(c, command, token), cancellationToken);

    public Task DeleteDraftVersionAsync(
        WorkflowVersionDeleteCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => DeleteDraftVersionCoreAsync(c, command, token), cancellationToken);

    public Task<WorkflowBindingCatalogRecord> LinkTemplateAsync(
        WorkflowTemplateLinkCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => LinkTemplateCoreAsync(c, command, token), cancellationToken);

    private static async Task<WorkflowTemplateCatalogRecord> CreateTemplateCoreAsync(
        SqliteConnection c, WorkflowTemplateCreateCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        try
        {
            await ExecuteCatalogAsync(c, tx,
                "INSERT INTO workflow_definitions (id,tenant_id,name,description,created_at) VALUES ($id,$tenant,$name,$description,$at);",
                token, ("$id", value.Id), ("$tenant", value.TenantId), ("$name", value.Name),
                ("$description", value.Description), ("$at", Store(value.OccurredAt)));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new WorkflowTemplateAlreadyExistsException();
        }

        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.Id,
            actorProfileId = value.ActorProfileId,
            state = "draft",
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.templateCreated", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadTemplateAfterMutationAsync(c, value.TenantId, value.Id, token))!;
    }

    private static async Task<WorkflowTemplateCatalogRecord?> ReadTemplateAfterMutationAsync(
        SqliteConnection c, string tenantId, string templateId, CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.CommandText = TemplateSelect + " WHERE d.tenant_id=$tenant AND d.id=$id;";
        Add(q, "$tenant", tenantId); Add(q, "$id", templateId);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadTemplate(r) : null;
    }

    public Task<WorkflowVersionCatalogRecord> PublishVersionAsync(
        WorkflowVersionPublishCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => PublishVersionCoreAsync(c, command, token), cancellationToken);

    private static async Task<WorkflowVersionCatalogRecord> CreateDraftCoreAsync(
        SqliteConnection c, WorkflowVersionDraftCreateCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var nextVersion = await NextVersionAsync(c, tx, value.TenantId, value.TemplateId, token);
        var at = Store(value.OccurredAt);
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "INSERT INTO workflow_definition_versions (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,phase_configs_json,default_operation_mode,transitions_json,changelog) VALUES ($id,$tenant,$template,$version,'draft',$hash,$at,NULL,$configs,$mode,$transitions,$changelog);";
            Add(q, "$id", value.VersionId); Add(q, "$tenant", value.TenantId);
            Add(q, "$template", value.TemplateId); Add(q, "$version", nextVersion);
            Add(q, "$hash", WorkflowDefinitionContentHash.Compute(value.Phases)); Add(q, "$at", at);
            Add(q, "$configs", value.PhaseConfigsJson); AddNullable(q, "$mode", value.DefaultOperationMode);
            Add(q, "$transitions", value.TransitionsJson); AddNullable(q, "$changelog", value.Changelog);
            await q.ExecuteNonQueryAsync(token);
        }
        await InsertHierarchyAsync(c, tx, value.TenantId, value.VersionId, value.Phases, token);
        var payload = JsonSerializer.Serialize(new { templateId = value.TemplateId, versionId = value.VersionId, version = nextVersion }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.draftCreated", payload, value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadVersionAsync(c, value.TenantId, value.VersionId, token))!;
    }

    private static async Task<WorkflowVersionCatalogRecord> UpdateDraftCoreAsync(
        SqliteConnection c, WorkflowVersionDraftUpdateCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await EnsureMutableDraftAsync(c, tx, value.TenantId, value.VersionId, token);
        await DeleteHierarchyAsync(c, tx, value.VersionId, token);
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "UPDATE workflow_definition_versions SET content_hash=$hash,phase_configs_json=$configs,default_operation_mode=$mode,transitions_json=$transitions,changelog=$changelog WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$hash", WorkflowDefinitionContentHash.Compute(value.Phases));
            Add(q, "$configs", value.PhaseConfigsJson); AddNullable(q, "$mode", value.DefaultOperationMode);
            Add(q, "$transitions", value.TransitionsJson); AddNullable(q, "$changelog", value.Changelog);
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.VersionId);
            await q.ExecuteNonQueryAsync(token);
        }
        await InsertHierarchyAsync(c, tx, value.TenantId, value.VersionId, value.Phases, token);
        var payload = JsonSerializer.Serialize(new { versionId = value.VersionId }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.draftUpdated", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadVersionAsync(c, value.TenantId, value.VersionId, token))!;
    }

    private static async Task<WorkflowVersionCatalogRecord> PublishDraftCoreAsync(
        SqliteConnection c, WorkflowVersionDraftPublishCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        string templateId; int version;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT v.definition_id,v.version,v.status,v.archived_at,d.archived_at FROM workflow_definition_versions v JOIN workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$tenant AND v.id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.VersionId);
            await using var r = await q.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (r.GetString(2) != "draft" || !r.IsDBNull(3) || !r.IsDBNull(4))
                throw new WorkflowCatalogLifecycleException("Only an active draft can be published.");
            templateId = r.GetString(0); version = r.GetInt32(1);
        }
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "UPDATE workflow_definition_versions SET status='published',published_at=$at,changelog=$changelog WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$at", Store(value.OccurredAt)); AddNullable(q, "$changelog", value.Changelog);
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.VersionId);
            await q.ExecuteNonQueryAsync(token);
        }
        var payload = JsonSerializer.Serialize(new { templateId, versionId = value.VersionId, version }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.versionPublished", payload,
            value.OccurredAt, token);
        await using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx;
            outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,'workflow.versionPublished',$payload,$at);";
            Add(outbox, "$id", UlidValue.New(value.OccurredAt).ToString()); Add(outbox, "$tenant", value.TenantId);
            Add(outbox, "$payload", payload); Add(outbox, "$at", Store(value.OccurredAt));
            await outbox.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
        return (await ReadVersionAsync(c, value.TenantId, value.VersionId, token))!;
    }

    private static async Task<WorkflowTemplateCatalogRecord> ArchiveTemplateCoreAsync(
        SqliteConnection c, WorkflowTemplateArchiveCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT archived_at FROM workflow_definitions WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.TemplateId);
            var archived = await q.ExecuteScalarAsync(token);
            if (archived is null) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            if (archived is not DBNull)
                throw new WorkflowCatalogLifecycleException("The workflow template is already archived.");
        }
        await ExecuteCatalogAsync(c, tx,
            "UPDATE workflow_definitions SET archived_at=$at WHERE tenant_id=$tenant AND id=$id;",
            token, ("$at", Store(value.OccurredAt)), ("$tenant", value.TenantId),
            ("$id", value.TemplateId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.TemplateId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.templateArchived", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadTemplateAfterMutationAsync(c, value.TenantId, value.TemplateId, token))!;
    }

    private static async Task<WorkflowVersionCatalogRecord> ArchiveVersionCoreAsync(
        SqliteConnection c, WorkflowVersionArchiveCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        string templateId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                "SELECT v.definition_id,v.archived_at,(SELECT p.id FROM workflow_definition_versions p WHERE p.definition_id=v.definition_id AND p.status='published' AND p.archived_at IS NULL ORDER BY p.version DESC LIMIT 1) FROM workflow_definition_versions v WHERE v.tenant_id=$tenant AND v.id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.VersionId);
            await using var r = await q.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (!r.IsDBNull(1))
                throw new WorkflowCatalogLifecycleException("The workflow version is already archived.");
            if (!r.IsDBNull(2) && r.GetString(2) == value.VersionId)
                throw new WorkflowCatalogLifecycleException("The current workflow version cannot be archived.");
            templateId = r.GetString(0);
        }
        await ExecuteCatalogAsync(c, tx,
            "UPDATE workflow_definition_versions SET archived_at=$at WHERE tenant_id=$tenant AND id=$id;",
            token, ("$at", Store(value.OccurredAt)), ("$tenant", value.TenantId),
            ("$id", value.VersionId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId,
            versionId = value.VersionId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.versionArchived", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadVersionAsync(c, value.TenantId, value.VersionId, token))!;
    }

    private static async Task DeleteDraftVersionCoreAsync(
        SqliteConnection c, WorkflowVersionDeleteCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        string templateId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                "SELECT v.definition_id,v.status,v.archived_at,EXISTS(SELECT 1 FROM workflow_bindings b WHERE b.tenant_id=v.tenant_id AND b.active_version_id=v.id),EXISTS(SELECT 1 FROM workflow_runs r WHERE r.tenant_id=v.tenant_id AND r.definition_version_id=v.id) FROM workflow_definition_versions v WHERE v.tenant_id=$tenant AND v.id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.VersionId);
            await using var r = await q.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (r.GetString(1) != "draft" || !r.IsDBNull(2) || r.GetInt64(3) != 0 || r.GetInt64(4) != 0)
                throw new WorkflowCatalogLifecycleException("Only an unused active draft can be deleted.");
            templateId = r.GetString(0);
        }
        await DeleteHierarchyAsync(c, tx, value.VersionId, token);
        await ExecuteCatalogAsync(c, tx,
            "DELETE FROM workflow_definition_versions WHERE tenant_id=$tenant AND id=$id;", token,
            ("$tenant", value.TenantId), ("$id", value.VersionId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId,
            versionId = value.VersionId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.draftDeleted", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
    }

    private static async Task DeleteTemplateCoreAsync(
        SqliteConnection c, WorkflowTemplateDeleteCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                "SELECT d.archived_at,EXISTS(SELECT 1 FROM workflow_definition_versions v WHERE v.definition_id=d.id AND (v.status<>'draft' OR v.archived_at IS NOT NULL)),EXISTS(SELECT 1 FROM workflow_bindings b WHERE b.tenant_id=d.tenant_id AND b.definition_id=d.id) FROM workflow_definitions d WHERE d.tenant_id=$tenant AND d.id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.TemplateId);
            await using var r = await q.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            if (!r.IsDBNull(0) || r.GetInt64(1) != 0 || r.GetInt64(2) != 0)
                throw new WorkflowCatalogLifecycleException("Only an unused draft template can be deleted.");
        }
        var draftIds = new List<string>();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT id FROM workflow_definition_versions WHERE tenant_id=$tenant AND definition_id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.TemplateId);
            await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) draftIds.Add(r.GetString(0));
        }
        foreach (var draftId in draftIds) await DeleteHierarchyAsync(c, tx, draftId, token);
        await ExecuteCatalogAsync(c, tx,
            "DELETE FROM workflow_definition_versions WHERE tenant_id=$tenant AND definition_id=$id; DELETE FROM workflow_definitions WHERE tenant_id=$tenant AND id=$id;",
            token, ("$tenant", value.TenantId), ("$id", value.TemplateId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.TemplateId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.templateDeleted", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
    }

    private static async Task<WorkflowTemplateCatalogRecord> DuplicateTemplateCoreAsync(
        SqliteConnection c, WorkflowTemplateDuplicateCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        string? currentVersionId;
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                "SELECT (SELECT v.id FROM workflow_definition_versions v WHERE v.definition_id=d.id AND v.status='published' AND v.archived_at IS NULL ORDER BY v.version DESC LIMIT 1) FROM workflow_definitions d WHERE d.tenant_id=$tenant AND d.id=$id;";
            Add(q, "$tenant", value.TenantId); Add(q, "$id", value.SourceTemplateId);
            var result = await q.ExecuteScalarAsync(token);
            if (result is null) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            currentVersionId = result is DBNull ? null : (string)result;
        }
        if (currentVersionId != value.SourceVersionId)
            throw new WorkflowCatalogLifecycleException("The source template changed while it was duplicated.");
        await ExecuteCatalogAsync(c, tx,
            "INSERT INTO workflow_definitions (id,tenant_id,name,description,created_at) VALUES ($id,$tenant,$name,$description,$at);",
            token, ("$id", value.TemplateId), ("$tenant", value.TenantId),
            ("$name", value.Name), ("$description", value.Description),
            ("$at", Store(value.OccurredAt)));
        if (value.Draft is not null)
        {
            var draft = value.Draft;
            await using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = "INSERT INTO workflow_definition_versions (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,phase_configs_json,default_operation_mode,transitions_json,changelog) VALUES ($id,$tenant,$template,1,'draft',$hash,$at,NULL,$configs,$mode,$transitions,NULL);";
            Add(q, "$id", draft.VersionId); Add(q, "$tenant", value.TenantId);
            Add(q, "$template", value.TemplateId); Add(q, "$hash", WorkflowDefinitionContentHash.Compute(draft.Phases));
            Add(q, "$at", Store(value.OccurredAt)); Add(q, "$configs", draft.PhaseConfigsJson);
            AddNullable(q, "$mode", draft.DefaultOperationMode); Add(q, "$transitions", draft.TransitionsJson);
            await q.ExecuteNonQueryAsync(token);
            await InsertHierarchyAsync(c, tx, value.TenantId, draft.VersionId, draft.Phases, token);
        }
        var payload = JsonSerializer.Serialize(new
        {
            sourceTemplateId = value.SourceTemplateId,
            templateId = value.TemplateId,
            versionId = value.Draft?.VersionId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.templateDuplicated", payload,
            value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadTemplateAfterMutationAsync(c, value.TenantId, value.TemplateId, token))!;
    }

    private static async Task<WorkflowBindingCatalogRecord> LinkTemplateCoreAsync(
        SqliteConnection c, WorkflowTemplateLinkCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using (var check = c.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText =
                "SELECT EXISTS(SELECT 1 FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL)," +
                "EXISTS(SELECT 1 FROM workflow_definition_versions v JOIN workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$tenant AND v.id=$version AND v.definition_id=$template AND v.status='published' AND v.archived_at IS NULL AND d.archived_at IS NULL)," +
                "EXISTS(SELECT 1 FROM local_users WHERE tenant_id=$tenant AND id=$profile);";
            Add(check, "$tenant", value.TenantId); Add(check, "$project", value.ProjectId);
            Add(check, "$version", value.ActiveVersionId); Add(check, "$template", value.TemplateId);
            Add(check, "$profile", value.ActorProfileId);
            await using var reader = await check.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            if (reader.GetInt64(0) == 0) throw new WorkflowCatalogReferenceNotFoundException("project");
            if (reader.GetInt64(1) == 0) throw new WorkflowCatalogLifecycleException("The workflow version must be active, published, and belong to the template.");
            if (reader.GetInt64(2) == 0) throw new WorkflowCatalogReferenceNotFoundException("profile");
        }
        try
        {
            await ExecuteCatalogAsync(c, tx,
                "INSERT INTO workflow_bindings (id,tenant_id,project_id,definition_id,active_version_id,operation_mode,pause_gates_json,created_at) VALUES ($id,$tenant,$project,$template,$version,$mode,'[]',$at);",
                token, ("$id", value.Id), ("$tenant", value.TenantId),
                ("$project", value.ProjectId), ("$template", value.TemplateId),
                ("$version", value.ActiveVersionId), ("$mode", value.OperationMode),
                ("$at", Store(value.OccurredAt)));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new WorkflowBindingAlreadyExistsException();
        }
        var auditId = UlidValue.New(value.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = value.ProjectId,
            auditEvent = new
            {
                id = auditId,
                actorKind = "user",
                actorId = value.ActorProfileId,
                action = "workflow.templateLinked",
                targetType = "project",
                targetId = value.ProjectId,
                detail = $"Workflow template {value.TemplateId} linked at version {value.ActiveVersionId}.",
                occurredAt = value.OccurredAt,
            },
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "audit.eventAppended", payload,
            value.OccurredAt, token);
        await using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx;
            outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,'audit.eventAppended',$payload,$at);";
            Add(outbox, "$id", UlidValue.New(value.OccurredAt).ToString());
            Add(outbox, "$tenant", value.TenantId); Add(outbox, "$payload", payload);
            Add(outbox, "$at", Store(value.OccurredAt)); await outbox.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
        return (await ReadBindingAsync(c, value.TenantId, value.Id, token))!;
    }

    public Task<WorkflowBindingCatalogRecord> RebindTemplateAsync(
        WorkflowTemplateRebindCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => RebindTemplateCoreAsync(c, command, token), cancellationToken);

    private static async Task<WorkflowBindingCatalogRecord> RebindTemplateCoreAsync(
        SqliteConnection c, WorkflowTemplateRebindCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using (var check = c.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText =
                "SELECT EXISTS(SELECT 1 FROM workflow_bindings WHERE tenant_id=$tenant AND id=$binding AND project_id=$project)," +
                "EXISTS(SELECT 1 FROM workflow_definition_versions v JOIN workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$tenant AND v.id=$version AND v.definition_id=$template AND v.status='published' AND v.archived_at IS NULL AND d.archived_at IS NULL)," +
                "EXISTS(SELECT 1 FROM local_users WHERE tenant_id=$tenant AND id=$profile);";
            Add(check, "$tenant", value.TenantId); Add(check, "$binding", value.BindingId);
            Add(check, "$project", value.ProjectId); Add(check, "$version", value.ActiveVersionId);
            Add(check, "$template", value.TemplateId); Add(check, "$profile", value.ActorProfileId);
            await using var reader = await check.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            if (reader.GetInt64(0) == 0) throw new WorkflowCatalogReferenceNotFoundException("binding");
            if (reader.GetInt64(1) == 0) throw new WorkflowCatalogLifecycleException("The workflow version must be active, published, and belong to the template.");
            if (reader.GetInt64(2) == 0) throw new WorkflowCatalogReferenceNotFoundException("profile");
        }

        await using (var update = c.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText =
                "UPDATE workflow_bindings SET definition_id=$template, active_version_id=$version " +
                "WHERE tenant_id=$tenant AND id=$binding AND project_id=$project;";
            Add(update, "$template", value.TemplateId); Add(update, "$version", value.ActiveVersionId);
            Add(update, "$tenant", value.TenantId); Add(update, "$binding", value.BindingId);
            Add(update, "$project", value.ProjectId);
            await update.ExecuteNonQueryAsync(token);
        }

        var auditId = UlidValue.New(value.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = value.ProjectId,
            auditEvent = new
            {
                id = auditId,
                actorKind = "user",
                actorId = value.ActorProfileId,
                action = "workflow.templateRebound",
                targetType = "project",
                targetId = value.ProjectId,
                detail = $"Workflow binding {value.BindingId} rebound to template {value.TemplateId} at version {value.ActiveVersionId}.",
                occurredAt = value.OccurredAt,
            },
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "audit.eventAppended", payload,
            value.OccurredAt, token);
        await using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx;
            outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,'audit.eventAppended',$payload,$at);";
            Add(outbox, "$id", UlidValue.New(value.OccurredAt).ToString());
            Add(outbox, "$tenant", value.TenantId); Add(outbox, "$payload", payload);
            Add(outbox, "$at", Store(value.OccurredAt)); await outbox.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
        return (await ReadBindingAsync(c, value.TenantId, value.BindingId, token))!;
    }

    public Task<WorkflowBindingCatalogRecord> SetOperationModeAsync(
        WorkflowOperationModeCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => SetOperationModeCoreAsync(c, command, token), cancellationToken);

    private static async Task<WorkflowVersionCatalogRecord> PublishVersionCoreAsync(
        SqliteConnection c, WorkflowVersionPublishCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        var nextVersion = await NextVersionAsync(c, tx, value.TenantId, value.TemplateId, token);
        var at = Store(value.OccurredAt); await using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "INSERT INTO workflow_definition_versions (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,phase_configs_json,default_operation_mode,transitions_json,changelog) VALUES ($id,$tenant,$template,$version,'published',$hash,$at,$at,$configs,$mode,$transitions,$changelog);";
            Add(q, "$id", value.VersionId); Add(q, "$tenant", value.TenantId); Add(q, "$template", value.TemplateId); Add(q, "$version", nextVersion);
            Add(q, "$hash", WorkflowDefinitionContentHash.Compute(value.Phases)); Add(q, "$at", at); Add(q, "$configs", value.PhaseConfigsJson);
            AddNullable(q, "$mode", value.DefaultOperationMode); Add(q, "$transitions", value.TransitionsJson); AddNullable(q, "$changelog", value.Changelog); await q.ExecuteNonQueryAsync(token);
        }
        await InsertHierarchyAsync(c, tx, value.TenantId, value.VersionId, value.Phases, token);
        var payload = JsonSerializer.Serialize(new { templateId = value.TemplateId, versionId = value.VersionId, version = nextVersion }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.versionPublished", payload, value.OccurredAt, token);
        await using (var outbox = c.CreateCommand()) { outbox.Transaction = tx; outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,'workflow.versionPublished',$payload,$at);"; Add(outbox, "$id", UlidValue.New(value.OccurredAt).ToString()); Add(outbox, "$tenant", value.TenantId); Add(outbox, "$payload", payload); Add(outbox, "$at", at); await outbox.ExecuteNonQueryAsync(token); }
        await tx.CommitAsync(token); return (await ReadVersionAsync(c, value.TenantId, value.VersionId, token))!;
    }

    private static async Task<int> NextVersionAsync(SqliteConnection c, SqliteTransaction tx,
        string tenantId, string templateId, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT d.archived_at,COALESCE(MAX(v.version),0) FROM workflow_definitions d LEFT JOIN workflow_definition_versions v ON v.definition_id=d.id WHERE d.tenant_id=$tenant AND d.id=$template GROUP BY d.archived_at;";
        Add(q, "$tenant", tenantId); Add(q, "$template", templateId);
        await using var r = await q.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
        if (!r.IsDBNull(0)) throw new WorkflowCatalogLifecycleException("Archived workflow templates cannot receive versions.");
        return r.GetInt32(1) + 1;
    }

    private static async Task InsertHierarchyAsync(SqliteConnection c, SqliteTransaction tx,
        string tenantId, string versionId, IReadOnlyList<WorkflowPhaseCreateInput> phases,
        CancellationToken token)
    {
        foreach (var phase in phases)
        {
            await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_phase_definitions (id,tenant_id,definition_version_id,phase_key,name,phase_order) VALUES ($id,$tenant,$version,$key,$name,$order);", token,
                ("$id", phase.PhaseDefinitionId), ("$tenant", tenantId), ("$version", versionId), ("$key", phase.Key), ("$name", phase.Name), ("$order", phase.Order));
            foreach (var objective in phase.Objectives) await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_objective_definitions (id,tenant_id,phase_definition_id,objective_key,name,kind,weight) VALUES ($id,$tenant,$phase,$key,$name,$kind,$weight);", token,
                ("$id", objective.ObjectiveDefinitionId), ("$tenant", tenantId), ("$phase", phase.PhaseDefinitionId), ("$key", objective.Key), ("$name", objective.Name), ("$kind", objective.Kind), ("$weight", objective.Weight));
            foreach (var gate in phase.Gates)
            {
                await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_gate_definitions (id,tenant_id,phase_definition_id,objective_definition_id,gate_key,name,minimum_required_state) VALUES ($id,$tenant,$phase,$objective,$key,$name,$minimum);", token,
                    ("$id", gate.GateDefinitionId), ("$tenant", tenantId), ("$phase", phase.PhaseDefinitionId), ("$objective", gate.ObjectiveDefinitionId), ("$key", gate.Key), ("$name", gate.Name), ("$minimum", gate.MinimumRequiredState));
                for (var index = 0; index < gate.RequiredObjectiveDefinitionIds.Count; index++) await ExecuteCatalogAsync(c, tx, "INSERT INTO workflow_gate_requirements (phase_definition_id,gate_definition_id,objective_definition_id,requirement_order) VALUES ($phase,$gate,$objective,$order);", token,
                    ("$phase", phase.PhaseDefinitionId), ("$gate", gate.GateDefinitionId), ("$objective", gate.RequiredObjectiveDefinitionIds[index]), ("$order", index + 1));
            }
        }
    }

    private static async Task EnsureMutableDraftAsync(SqliteConnection c, SqliteTransaction tx,
        string tenantId, string versionId, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT v.status,v.archived_at,d.archived_at FROM workflow_definition_versions v JOIN workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$tenant AND v.id=$id;";
        Add(q, "$tenant", tenantId); Add(q, "$id", versionId);
        await using var r = await q.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
        if (r.GetString(0) != "draft" || !r.IsDBNull(1) || !r.IsDBNull(2))
            throw new WorkflowCatalogLifecycleException("Only an active draft can be edited.");
    }

    private static async Task DeleteHierarchyAsync(SqliteConnection c, SqliteTransaction tx,
        string versionId, CancellationToken token)
    {
        await ExecuteCatalogAsync(c, tx, "DELETE FROM workflow_gate_requirements WHERE phase_definition_id IN (SELECT id FROM workflow_phase_definitions WHERE definition_version_id=$version);", token, ("$version", versionId));
        await ExecuteCatalogAsync(c, tx, "DELETE FROM workflow_gate_definitions WHERE phase_definition_id IN (SELECT id FROM workflow_phase_definitions WHERE definition_version_id=$version);", token, ("$version", versionId));
        await ExecuteCatalogAsync(c, tx, "DELETE FROM workflow_objective_definitions WHERE phase_definition_id IN (SELECT id FROM workflow_phase_definitions WHERE definition_version_id=$version);", token, ("$version", versionId));
        await ExecuteCatalogAsync(c, tx, "DELETE FROM workflow_phase_definitions WHERE definition_version_id=$version;", token, ("$version", versionId));
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
