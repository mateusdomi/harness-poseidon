using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowCatalogStore
{
    public Task<WorkflowVersionCatalogRecord> CreateDraftAsync(
        WorkflowVersionDraftCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateDraftCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowVersionCatalogRecord> UpdateDraftAsync(
        WorkflowVersionDraftUpdateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateDraftCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowVersionCatalogRecord> PublishDraftAsync(
        WorkflowVersionDraftPublishCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return PublishDraftCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowTemplateCatalogRecord> ArchiveTemplateAsync(
        WorkflowTemplateArchiveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ArchiveTemplateCoreAsync(command, cancellationToken);
    }

    public Task DeleteTemplateAsync(
        WorkflowTemplateDeleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DeleteTemplateCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowTemplateCatalogRecord> DuplicateTemplateAsync(
        WorkflowTemplateDuplicateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DuplicateTemplateCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowVersionCatalogRecord> ArchiveVersionAsync(
        WorkflowVersionArchiveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ArchiveVersionCoreAsync(command, cancellationToken);
    }

    public Task DeleteDraftVersionAsync(
        WorkflowVersionDeleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DeleteDraftVersionCoreAsync(command, cancellationToken);
    }

    public Task<WorkflowBindingCatalogRecord> LinkTemplateAsync(
        WorkflowTemplateLinkCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return LinkTemplateCoreAsync(command, cancellationToken);
    }

    private async Task<WorkflowTemplateCatalogRecord> CreateTemplateCoreAsync(
        WorkflowTemplateCreateCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO harness.workflow_definitions (id,tenant_id,name,description,created_at) VALUES ($1,$2,$3,$4,$5);",
                cancellationToken, Text(value.Id), Text(value.TenantId), Text(value.Name),
                Text(value.Description), Timestamp(value.OccurredAt));
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            throw new WorkflowTemplateAlreadyExistsException();
        }

        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.Id,
            actorProfileId = value.ActorProfileId,
            state = "draft",
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.templateCreated",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadTemplateAfterMutationAsync(
            connection, value.TenantId, value.Id, cancellationToken))!;
    }

    private static async Task<WorkflowTemplateCatalogRecord?> ReadTemplateAfterMutationAsync(
        NpgsqlConnection connection, string tenantId, string templateId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{TemplateSelect} WHERE d.tenant_id=$1 AND d.id=$2;";
        query.Parameters.Add(Text(tenantId)); query.Parameters.Add(Text(templateId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTemplate(reader) : null;
    }

    public Task<WorkflowVersionCatalogRecord> PublishVersionAsync(
        WorkflowVersionPublishCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return PublishVersionCoreAsync(command, cancellationToken);
    }

    private async Task<WorkflowVersionCatalogRecord> CreateDraftCoreAsync(
        WorkflowVersionDraftCreateCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));", cancellationToken,
            Text($"workflow-template:{value.TenantId}:{value.TemplateId}"));
        var nextVersion = await NextVersionAsync(connection, transaction, value.TenantId,
            value.TemplateId, cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.workflow_definition_versions
                (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,
                 phase_configs_json,default_operation_mode,transitions_json,changelog)
            VALUES ($1,$2,$3,$4,'draft',$5,$6,NULL,$7,$8,$9,$10);
            """,
            cancellationToken,
            Text(value.VersionId), Text(value.TenantId), Text(value.TemplateId),
            Integer(nextVersion), Text(WorkflowDefinitionContentHash.Compute(value.Phases)),
            Timestamp(value.OccurredAt), Json(value.PhaseConfigsJson),
            NullableText(value.DefaultOperationMode), Json(value.TransitionsJson),
            NullableText(value.Changelog));
        await InsertHierarchyAsync(connection, transaction, value.TenantId, value.VersionId,
            value.Phases, cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.TemplateId,
            versionId = value.VersionId,
            version = nextVersion,
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.draftCreated",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadVersionAsync(connection, value.TenantId, value.VersionId,
            cancellationToken))!;
    }

    private async Task<WorkflowVersionCatalogRecord> UpdateDraftCoreAsync(
        WorkflowVersionDraftUpdateCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockDraftAsync(connection, transaction, value.TenantId, value.VersionId,
            cancellationToken);
        await DeleteHierarchyAsync(connection, transaction, value.VersionId, cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.workflow_definition_versions SET content_hash=$1,phase_configs_json=$2,default_operation_mode=$3,transitions_json=$4,changelog=$5 WHERE tenant_id=$6 AND id=$7;",
            cancellationToken,
            Text(WorkflowDefinitionContentHash.Compute(value.Phases)), Json(value.PhaseConfigsJson),
            NullableText(value.DefaultOperationMode), Json(value.TransitionsJson),
            NullableText(value.Changelog), Text(value.TenantId), Text(value.VersionId));
        await InsertHierarchyAsync(connection, transaction, value.TenantId, value.VersionId,
            value.Phases, cancellationToken);
        var payload = JsonSerializer.Serialize(new { versionId = value.VersionId }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.draftUpdated",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadVersionAsync(connection, value.TenantId, value.VersionId,
            cancellationToken))!;
    }

    private async Task<WorkflowVersionCatalogRecord> PublishDraftCoreAsync(
        WorkflowVersionDraftPublishCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string templateId; int version;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT v.definition_id,v.version,v.status,v.archived_at,d.archived_at FROM harness.workflow_definition_versions v JOIN harness.workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$1 AND v.id=$2 FOR UPDATE OF v,d;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.VersionId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (reader.GetString(2) != "draft" || !reader.IsDBNull(3) || !reader.IsDBNull(4))
                throw new WorkflowCatalogLifecycleException("Only an active draft can be published.");
            templateId = reader.GetString(0).TrimEnd(); version = reader.GetInt32(1);
        }
        await ExecuteAsync(connection, transaction,
            "UPDATE harness.workflow_definition_versions SET status='published',published_at=$1,changelog=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken, Timestamp(value.OccurredAt), NullableText(value.Changelog),
            Text(value.TenantId), Text(value.VersionId));
        var payload = JsonSerializer.Serialize(new { templateId, versionId = value.VersionId, version }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.versionPublished",
            payload, value.OccurredAt, cancellationToken);
        await AppendOutboxAsync(connection, transaction, value.TenantId, "workflow.versionPublished",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadVersionAsync(connection, value.TenantId, value.VersionId,
            cancellationToken))!;
    }

    private async Task<WorkflowTemplateCatalogRecord> ArchiveTemplateCoreAsync(
        WorkflowTemplateArchiveCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT archived_at FROM harness.workflow_definitions WHERE tenant_id=$1 AND id=$2 FOR UPDATE;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.TemplateId));
            var archived = await query.ExecuteScalarAsync(cancellationToken);
            if (archived is null) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            if (archived is not DBNull)
                throw new WorkflowCatalogLifecycleException("The workflow template is already archived.");
        }
        await ExecuteAsync(connection, transaction,
            "UPDATE harness.workflow_definitions SET archived_at=$1 WHERE tenant_id=$2 AND id=$3;",
            cancellationToken, Timestamp(value.OccurredAt), Text(value.TenantId), Text(value.TemplateId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.TemplateId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.templateArchived",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadTemplateAfterMutationAsync(connection, value.TenantId, value.TemplateId,
            cancellationToken))!;
    }

    private async Task<WorkflowVersionCatalogRecord> ArchiveVersionCoreAsync(
        WorkflowVersionArchiveCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string templateId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT v.definition_id,v.archived_at,(SELECT p.id FROM harness.workflow_definition_versions p WHERE p.definition_id=v.definition_id AND p.status='published' AND p.archived_at IS NULL ORDER BY p.version DESC LIMIT 1) FROM harness.workflow_definition_versions v WHERE v.tenant_id=$1 AND v.id=$2 FOR UPDATE;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.VersionId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (!reader.IsDBNull(1))
                throw new WorkflowCatalogLifecycleException("The workflow version is already archived.");
            if (!reader.IsDBNull(2) && reader.GetString(2).TrimEnd() == value.VersionId)
                throw new WorkflowCatalogLifecycleException("The current workflow version cannot be archived.");
            templateId = reader.GetString(0).TrimEnd();
        }
        await ExecuteAsync(connection, transaction,
            "UPDATE harness.workflow_definition_versions SET archived_at=$1 WHERE tenant_id=$2 AND id=$3;",
            cancellationToken, Timestamp(value.OccurredAt), Text(value.TenantId), Text(value.VersionId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId,
            versionId = value.VersionId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.versionArchived",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadVersionAsync(connection, value.TenantId, value.VersionId,
            cancellationToken))!;
    }

    private async Task DeleteDraftVersionCoreAsync(
        WorkflowVersionDeleteCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string templateId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT v.definition_id,v.status,v.archived_at,EXISTS(SELECT 1 FROM harness.workflow_bindings b WHERE b.tenant_id=v.tenant_id AND b.active_version_id=v.id),EXISTS(SELECT 1 FROM harness.workflow_runs r WHERE r.tenant_id=v.tenant_id AND r.definition_version_id=v.id) FROM harness.workflow_definition_versions v WHERE v.tenant_id=$1 AND v.id=$2 FOR UPDATE;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.VersionId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (reader.GetString(1) != "draft" || !reader.IsDBNull(2) ||
                reader.GetBoolean(3) || reader.GetBoolean(4))
                throw new WorkflowCatalogLifecycleException("Only an unused active draft can be deleted.");
            templateId = reader.GetString(0).TrimEnd();
        }
        await DeleteHierarchyAsync(connection, transaction, value.VersionId, cancellationToken);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM harness.workflow_definition_versions WHERE tenant_id=$1 AND id=$2;",
            cancellationToken, Text(value.TenantId), Text(value.VersionId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId,
            versionId = value.VersionId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.draftDeleted",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeleteTemplateCoreAsync(
        WorkflowTemplateDeleteCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT d.archived_at,EXISTS(SELECT 1 FROM harness.workflow_definition_versions v WHERE v.definition_id=d.id AND (v.status<>'draft' OR v.archived_at IS NOT NULL)),EXISTS(SELECT 1 FROM harness.workflow_bindings b WHERE b.tenant_id=d.tenant_id AND b.definition_id=d.id) FROM harness.workflow_definitions d WHERE d.tenant_id=$1 AND d.id=$2 FOR UPDATE;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.TemplateId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            if (!reader.IsDBNull(0) || reader.GetBoolean(1) || reader.GetBoolean(2))
                throw new WorkflowCatalogLifecycleException("Only an unused draft template can be deleted.");
        }
        var draftIds = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT id FROM harness.workflow_definition_versions WHERE tenant_id=$1 AND definition_id=$2;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.TemplateId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) draftIds.Add(reader.GetString(0).TrimEnd());
        }
        foreach (var draftId in draftIds)
            await DeleteHierarchyAsync(connection, transaction, draftId, cancellationToken);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM harness.workflow_definition_versions WHERE tenant_id=$1 AND definition_id=$2;",
            cancellationToken, Text(value.TenantId), Text(value.TemplateId));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM harness.workflow_definitions WHERE tenant_id=$1 AND id=$2;",
            cancellationToken, Text(value.TenantId), Text(value.TemplateId));
        var payload = JsonSerializer.Serialize(new
        {
            templateId = value.TemplateId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.templateDeleted",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<WorkflowTemplateCatalogRecord> DuplicateTemplateCoreAsync(
        WorkflowTemplateDuplicateCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? currentVersionId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT (SELECT v.id FROM harness.workflow_definition_versions v WHERE v.definition_id=d.id AND v.status='published' AND v.archived_at IS NULL ORDER BY v.version DESC LIMIT 1) FROM harness.workflow_definitions d WHERE d.tenant_id=$1 AND d.id=$2 FOR UPDATE;";
            query.Parameters.Add(Text(value.TenantId)); query.Parameters.Add(Text(value.SourceTemplateId));
            var result = await query.ExecuteScalarAsync(cancellationToken);
            if (result is null) throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
            currentVersionId = result is DBNull ? null : ((string)result).TrimEnd();
        }
        if (currentVersionId != value.SourceVersionId)
            throw new WorkflowCatalogLifecycleException("The source template changed while it was duplicated.");
        await ExecuteAsync(connection, transaction,
            "INSERT INTO harness.workflow_definitions (id,tenant_id,name,description,created_at) VALUES ($1,$2,$3,$4,$5);",
            cancellationToken, Text(value.TemplateId), Text(value.TenantId), Text(value.Name),
            Text(value.Description), Timestamp(value.OccurredAt));
        if (value.Draft is not null)
        {
            var draft = value.Draft;
            await ExecuteAsync(connection, transaction,
                "INSERT INTO harness.workflow_definition_versions (id,tenant_id,definition_id,version,status,content_hash,created_at,published_at,phase_configs_json,default_operation_mode,transitions_json,changelog) VALUES ($1,$2,$3,1,'draft',$4,$5,NULL,$6,$7,$8,NULL);",
                cancellationToken, Text(draft.VersionId), Text(value.TenantId), Text(value.TemplateId),
                Text(WorkflowDefinitionContentHash.Compute(draft.Phases)), Timestamp(value.OccurredAt),
                Json(draft.PhaseConfigsJson), NullableText(draft.DefaultOperationMode),
                Json(draft.TransitionsJson));
            await InsertHierarchyAsync(connection, transaction, value.TenantId, draft.VersionId,
                draft.Phases, cancellationToken);
        }
        var payload = JsonSerializer.Serialize(new
        {
            sourceTemplateId = value.SourceTemplateId,
            templateId = value.TemplateId,
            versionId = value.Draft?.VersionId,
            actorProfileId = value.ActorProfileId,
        }, JsonOptions);
        await AppendAuditAsync(connection, transaction, value.TenantId, "workflow.templateDuplicated",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadTemplateAfterMutationAsync(connection, value.TenantId, value.TemplateId,
            cancellationToken))!;
    }

    private async Task<WorkflowBindingCatalogRecord> LinkTemplateCoreAsync(
        WorkflowTemplateLinkCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText =
                "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL)," +
                "EXISTS(SELECT 1 FROM harness.workflow_definition_versions v JOIN harness.workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$1 AND v.id=$3 AND v.definition_id=$4 AND v.status='published' AND v.archived_at IS NULL AND d.archived_at IS NULL)," +
                "EXISTS(SELECT 1 FROM harness.local_users WHERE tenant_id=$1 AND id=$5);";
            check.Parameters.Add(Text(value.TenantId)); check.Parameters.Add(Text(value.ProjectId));
            check.Parameters.Add(Text(value.ActiveVersionId)); check.Parameters.Add(Text(value.TemplateId));
            check.Parameters.Add(Text(value.ActorProfileId));
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.GetBoolean(0)) throw new WorkflowCatalogReferenceNotFoundException("project");
            if (!reader.GetBoolean(1))
                throw new WorkflowCatalogLifecycleException("The workflow version must be active, published, and belong to the template.");
            if (!reader.GetBoolean(2)) throw new WorkflowCatalogReferenceNotFoundException("profile");
        }
        try
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO harness.workflow_bindings (id,tenant_id,project_id,definition_id,active_version_id,operation_mode,pause_gates_json,created_at) VALUES ($1,$2,$3,$4,$5,$6,'[]'::jsonb,$7);",
                cancellationToken, Text(value.Id), Text(value.TenantId), Text(value.ProjectId),
                Text(value.TemplateId), Text(value.ActiveVersionId), Text(value.OperationMode),
                Timestamp(value.OccurredAt));
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
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
        await AppendAuditAsync(connection, transaction, value.TenantId, "audit.eventAppended",
            payload, value.OccurredAt, cancellationToken);
        await AppendOutboxAsync(connection, transaction, value.TenantId, "audit.eventAppended",
            payload, value.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadBindingAsync(connection, value.TenantId, value.Id,
            cancellationToken))!;
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
        var nextVersion = await NextVersionAsync(connection, transaction, value.TenantId,
            value.TemplateId, cancellationToken);

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
        await InsertHierarchyAsync(connection, transaction, value.TenantId, value.VersionId,
            value.Phases, cancellationToken);

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

    private static async Task<int> NextVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string templateId, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT d.archived_at,COALESCE(MAX(v.version),0) FROM harness.workflow_definitions d " +
            "LEFT JOIN harness.workflow_definition_versions v ON v.definition_id=d.id " +
            "WHERE d.tenant_id=$1 AND d.id=$2 GROUP BY d.archived_at;";
        query.Parameters.Add(Text(tenantId)); query.Parameters.Add(Text(templateId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowCatalogReferenceNotFoundException("workflow_template");
        }

        if (!reader.IsDBNull(0))
        {
            throw new WorkflowCatalogLifecycleException(
                "Archived workflow templates cannot receive versions.");
        }

        return reader.GetInt32(1) + 1;
    }

    private static async Task InsertHierarchyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string versionId, IReadOnlyList<WorkflowPhaseCreateInput> phases,
        CancellationToken cancellationToken)
    {
        foreach (var phase in phases)
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.workflow_phase_definitions
                    (id,tenant_id,definition_version_id,phase_key,name,phase_order)
                VALUES ($1,$2,$3,$4,$5,$6);
                """,
                cancellationToken,
                Text(phase.PhaseDefinitionId), Text(tenantId), Text(versionId),
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
                    Text(objective.ObjectiveDefinitionId), Text(tenantId),
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
                    Text(gate.GateDefinitionId), Text(tenantId),
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
    }

    private static async Task LockDraftAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string versionId, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT v.status,v.archived_at,d.archived_at FROM harness.workflow_definition_versions v JOIN harness.workflow_definitions d ON d.tenant_id=v.tenant_id AND d.id=v.definition_id WHERE v.tenant_id=$1 AND v.id=$2 FOR UPDATE OF v,d;";
        query.Parameters.Add(Text(tenantId)); query.Parameters.Add(Text(versionId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
        if (reader.GetString(0) != "draft" || !reader.IsDBNull(1) || !reader.IsDBNull(2))
            throw new WorkflowCatalogLifecycleException("Only an active draft can be edited.");
    }

    private static async Task DeleteHierarchyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string versionId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM harness.workflow_gate_requirements WHERE phase_definition_id IN (SELECT id FROM harness.workflow_phase_definitions WHERE definition_version_id=$1);", cancellationToken, Text(versionId));
        await ExecuteAsync(connection, transaction, "DELETE FROM harness.workflow_gate_definitions WHERE phase_definition_id IN (SELECT id FROM harness.workflow_phase_definitions WHERE definition_version_id=$1);", cancellationToken, Text(versionId));
        await ExecuteAsync(connection, transaction, "DELETE FROM harness.workflow_objective_definitions WHERE phase_definition_id IN (SELECT id FROM harness.workflow_phase_definitions WHERE definition_version_id=$1);", cancellationToken, Text(versionId));
        await ExecuteAsync(connection, transaction, "DELETE FROM harness.workflow_phase_definitions WHERE definition_version_id=$1;", cancellationToken, Text(versionId));
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
