using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresAgentCatalogStore(NpgsqlDataSource dataSource) : IAgentCatalogStore
{
    private const string DefinitionSelect =
        "SELECT id,agent_key,name,role,specialty,description,default_model_id," +
        "skill_ids_json::text,tool_ids_json::text,persona,mission,operating_principles_json::text," +
        "deliverables_json::text,quality_criteria_json::text,communication_style,limitations_json::text," +
        "version,enabled,archived_at,stacks_json::text,default_effort,preferred_account_id," +
        "fallback_model_ids_json::text,team,actor_critic,risk,owner,origin,lifecycle_state," +
        "scope_project_id,creation_reason,allowed_scopes_json::text,denied_scopes_json::text," +
        "activation_criteria_json::text,non_activation_criteria_json::text " +
        "FROM harness.agent_definitions";

    private const string AgentSelect =
        "SELECT tenant_id,id,definition_id,project_id,name,state,current_task_id,model_id," +
        "lease_fencing_token,lease_expires_at,tasks_completed,tokens_input,tokens_output," +
        "cost_usd,uptime_ms,last_heartbeat_at,account_id,effort,provider_effort_value," +
        "fallback_model_ids_json::text,selection_reason,selection_updated_at FROM harness.agents";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<AgentDefinitionRecord?> GetDefinitionAsync(
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"{DefinitionSelect} WHERE id=$1;");
        command.Parameters.Add(Text(definitionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDefinition(reader) : null;
    }

    public async Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsAsync(
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<AgentDefinitionRecord>();
        await using var command = _dataSource.CreateCommand(
            $"{DefinitionSelect} WHERE ($1::text IS NULL OR id>$1) ORDER BY id LIMIT $2;");
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadDefinition(reader));
        }

        return values;
    }

    public async Task<AgentRecord?> GetAgentAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            $"{AgentSelect} WHERE tenant_id=$1 AND id=$2 AND retired_at IS NULL;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(agentId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAgent(reader) : null;
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(
        string tenantId,
        string? projectId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<AgentRecord>();
        await using var command = _dataSource.CreateCommand(
            $"{AgentSelect} WHERE tenant_id=$1 AND retired_at IS NULL " +
            "AND ($2::text IS NULL OR project_id=$2) AND ($3::text IS NULL OR id>$3) " +
            "ORDER BY id LIMIT $4;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(NullableText(projectId));
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadAgent(reader));
        }

        return values;
    }

    public async Task<AgentDefinitionRecord?> GetDefinitionForTenantAsync(string tenantId, string definitionId, CancellationToken cancellationToken = default) { await using var command = _dataSource.CreateCommand($"{DefinitionSelect} WHERE id=$1 AND (tenant_id IS NULL OR tenant_id=$2);"); command.Parameters.Add(Text(definitionId)); command.Parameters.Add(Text(tenantId)); await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadDefinition(reader) : null; }
    public async Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsForTenantAsync(string tenantId, string? afterId, int limit, bool includeArchived, CancellationToken cancellationToken = default) { var values = new List<AgentDefinitionRecord>(); await using var command = _dataSource.CreateCommand($"{DefinitionSelect} WHERE (tenant_id IS NULL OR tenant_id=$1) AND ($2 OR archived_at IS NULL) AND ($3::text IS NULL OR id>$3) ORDER BY id LIMIT $4;"); command.Parameters.Add(Text(tenantId)); command.Parameters.Add(Boolean(includeArchived)); command.Parameters.Add(NullableText(afterId)); command.Parameters.Add(Integer(limit)); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) values.Add(ReadDefinition(reader)); return values; }
    public async Task<IReadOnlyList<AgentDefinitionVersionRecord>> ListDefinitionVersionsAsync(string tenantId, string definitionId, int? beforeVersion, int limit, CancellationToken cancellationToken = default) { var values = new List<AgentDefinitionVersionRecord>(); await using var command = _dataSource.CreateCommand("SELECT v.id,v.definition_id,v.version,v.snapshot_json::text,v.actor_profile_id,v.created_at FROM harness.agent_definition_versions v JOIN harness.agent_definitions d ON d.id=v.definition_id WHERE d.tenant_id=$1 AND v.definition_id=$2 AND ($3::integer IS NULL OR v.version<$3) ORDER BY v.version DESC LIMIT $4;"); command.Parameters.Add(Text(tenantId)); command.Parameters.Add(Text(definitionId)); command.Parameters.Add(NullableInteger(beforeVersion)); command.Parameters.Add(Integer(limit)); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) values.Add(ReadDefinitionVersion(reader)); return values; }
    public Task<AgentDefinitionRecord> CreateDefinitionAsync(AgentDefinitionCreateCommand command, CancellationToken cancellationToken = default) => CreateDefinitionCoreAsync(command, cancellationToken);
    public async Task<AgentDefinitionRecord> UpdateDefinitionAsync(AgentDefinitionUpdateCommand command, CancellationToken cancellationToken = default) { ValidateDefinition(command.Content); await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken); await using var tx = await connection.BeginTransactionAsync(cancellationToken); await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", cancellationToken, Text($"audit-ledger:{command.TenantId}")); await ValidateDefinitionReferencesAsync(connection, tx, command.TenantId, command.Content, cancellationToken); var parameters = DefinitionParameters(command.Content); await ExecuteDefinitionUpdateAsync(connection, tx, command, parameters, cancellationToken); await InsertDefinitionVersionAsync(connection, tx, command.Id, command.ExpectedVersion + 1, command.ActorProfileId, command.Content, command.OccurredAt, cancellationToken); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.updated", "update", command.OccurredAt, cancellationToken); await tx.CommitAsync(cancellationToken); return (await GetDefinitionForTenantAsync(command.TenantId, command.Id, cancellationToken))!; }
    public async Task<AgentDefinitionRecord> DuplicateDefinitionAsync(AgentDefinitionDuplicateCommand command, CancellationToken cancellationToken = default) { var source = await GetDefinitionForTenantAsync(command.TenantId, command.SourceId, cancellationToken) ?? throw new AgentDefinitionAdminException("Source definition was not found."); return await CreateDefinitionCoreAsync(new(command.TenantId, command.ActorProfileId, command.Id, ToContent(source) with { Key = command.Key, Name = command.Name }, command.OccurredAt), cancellationToken); }
    public async Task<AgentDefinitionRecord> SetDefinitionLifecycleAsync(AgentDefinitionLifecycleCommand command, CancellationToken cancellationToken = default) { if (command.Action is not ("enable" or "disable" or "archive")) throw new AgentDefinitionAdminException("Definition lifecycle action is invalid."); await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken); await using var tx = await connection.BeginTransactionAsync(cancellationToken); await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", cancellationToken, Text($"audit-ledger:{command.TenantId}")); await using var update = connection.CreateCommand(); update.Transaction = tx; update.CommandText = command.Action switch { "enable" => "UPDATE harness.agent_definitions SET enabled=true,updated_at=$1 WHERE id=$2 AND tenant_id=$3 AND archived_at IS NULL;", "disable" => "UPDATE harness.agent_definitions SET enabled=false,updated_at=$1 WHERE id=$2 AND tenant_id=$3 AND archived_at IS NULL;", _ => "UPDATE harness.agent_definitions SET enabled=false,archived_at=$1,updated_at=$1 WHERE id=$2 AND tenant_id=$3 AND archived_at IS NULL;" }; update.Parameters.Add(Timestamp(command.OccurredAt)); update.Parameters.Add(Text(command.Id)); update.Parameters.Add(Text(command.TenantId)); if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new AgentDefinitionAdminException("Definition cannot transition from its current state."); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, $"agentDefinition.{command.Action}d", command.Action, command.OccurredAt, cancellationToken); await tx.CommitAsync(cancellationToken); return (await GetDefinitionForTenantAsync(command.TenantId, command.Id, cancellationToken))!; }
    // CAT-02: enriquece as definições canônicas built-in (tenant_id IS NULL) com o conteúdo
    // completo da persona e o owner, de forma idempotente (guarda `owner IS NULL`). Não gera
    // versão nem trilha de auditoria — é conteúdo de sistema, não uma edição de tenant.
    public async Task<int> EnsureBuiltInDefinitionsAsync(IReadOnlyList<BuiltInAgentDefinitionSeed> definitions, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var seeded = 0;
        foreach (var definition in definitions)
        {
            var c = definition.Content;
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                "UPDATE harness.agent_definitions SET persona=$1,mission=$2,operating_principles_json=$3," +
                "deliverables_json=$4,quality_criteria_json=$5,communication_style=$6,limitations_json=$7," +
                "stacks_json=$8,default_effort=$9,team=$10,actor_critic=$11,risk=$12,owner=$13 " +
                "WHERE id=$14 AND tenant_id IS NULL AND owner IS NULL;";
            command.Parameters.Add(NullableText(c.Persona?.Trim()));
            command.Parameters.Add(NullableText(c.Mission?.Trim()));
            command.Parameters.Add(Json(JsonSerializer.Serialize(c.OperatingPrinciples, JsonOptions)));
            command.Parameters.Add(Json(JsonSerializer.Serialize(c.Deliverables, JsonOptions)));
            command.Parameters.Add(Json(JsonSerializer.Serialize(c.QualityCriteria, JsonOptions)));
            command.Parameters.Add(NullableText(c.CommunicationStyle?.Trim()));
            command.Parameters.Add(Json(JsonSerializer.Serialize(c.Limitations, JsonOptions)));
            command.Parameters.Add(Json(JsonSerializer.Serialize(c.Stacks ?? [], JsonOptions)));
            command.Parameters.Add(NullableText(c.DefaultEffort));
            command.Parameters.Add(NullableText(c.Team?.Trim()));
            command.Parameters.Add(NullableText(c.ActorCritic));
            command.Parameters.Add(NullableText(c.Risk));
            command.Parameters.Add(Text(definition.Owner));
            command.Parameters.Add(Text(definition.Id));
            seeded += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return seeded;
    }

    public async Task DeleteDefinitionAsync(AgentDefinitionDeleteCommand command, CancellationToken cancellationToken = default) { await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken); await using var tx = await connection.BeginTransactionAsync(cancellationToken); await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", cancellationToken, Text($"audit-ledger:{command.TenantId}")); await using (var check = connection.CreateCommand()) { check.Transaction = tx; check.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.agents WHERE definition_id=$1),EXISTS(SELECT 1 FROM harness.agent_definitions WHERE id=$1 AND tenant_id=$2);"; check.Parameters.Add(Text(command.Id)); check.Parameters.Add(Text(command.TenantId)); await using var reader = await check.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken); if (!reader.GetBoolean(1)) throw new AgentDefinitionAdminException("Definition was not found."); if (reader.GetBoolean(0)) throw new AgentDefinitionAdminException("A definition that has been used cannot be deleted."); } await ExecuteAsync(connection, tx, "DELETE FROM harness.agent_definition_versions WHERE definition_id=$1;", cancellationToken, Text(command.Id)); await ExecuteAsync(connection, tx, "DELETE FROM harness.agent_definitions WHERE id=$1 AND tenant_id=$2;", cancellationToken, Text(command.Id), Text(command.TenantId)); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.deleted", "delete", command.OccurredAt, cancellationToken); await tx.CommitAsync(cancellationToken); }

    public async Task<(AgentDefinitionRecord Definition, bool Created)> CreateChiefDefinitionAsync(ChiefDefinitionCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CreationReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ScopeProjectId);
        // Idempotência por chave: duas demandas simultâneas para a mesma especialidade convergem
        // para UMA definição (paridade com o SQLite).
        var existing = await ReadDefinitionByKeyAsync(command.TenantId, command.Content.Key, cancellationToken);
        if (existing is not null)
        {
            return (existing, false);
        }

        var created = await CreateDefinitionCoreAsync(
            new AgentDefinitionCreateCommand(command.TenantId, command.ActorProfileId, command.Id, command.Content, command.OccurredAt),
            cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        // A procedência é carimbada pelo STORE, nunca pelo chamador.
        await ExecuteAsync(
            connection, tx,
            "UPDATE harness.agent_definitions SET origin='chief',lifecycle_state='project_scoped',scope_project_id=$1,creation_reason=$2,owner=COALESCE(owner,'chief') WHERE id=$3 AND tenant_id=$4;",
            cancellationToken, Text(command.ScopeProjectId), Text(command.CreationReason), Text(command.Id), Text(command.TenantId));
        await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.chiefCreated", "create", command.OccurredAt, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ((await GetDefinitionForTenantAsync(command.TenantId, command.Id, cancellationToken))!, true);
    }

    public async Task<AgentDefinitionRecord> SetDefinitionLifecycleStateAsync(AgentDefinitionLifecycleStateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!LifecycleStates.Contains(command.LifecycleState))
        {
            throw new AgentDefinitionAdminException("Definition lifecycle state is invalid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", cancellationToken, Text($"audit-ledger:{command.TenantId}"));
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE harness.agent_definitions SET lifecycle_state=$1,enabled=($1 NOT IN ('quarantined','disabled')),creation_reason=COALESCE(creation_reason,$2),updated_at=$3 WHERE id=$4 AND tenant_id=$5;";
            update.Parameters.Add(Text(command.LifecycleState));
            update.Parameters.Add(Text(command.Reason));
            update.Parameters.Add(Timestamp(command.OccurredAt));
            update.Parameters.Add(Text(command.Id));
            update.Parameters.Add(Text(command.TenantId));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new AgentDefinitionAdminException("Definition was not found for this tenant.");
            }
        }

        await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, $"agentDefinition.lifecycle.{command.LifecycleState}", "lifecycle", command.OccurredAt, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (await GetDefinitionForTenantAsync(command.TenantId, command.Id, cancellationToken))!;
    }

    private static readonly HashSet<string> LifecycleStates = new(
        ["project_scoped", "active", "reusable", "global", "observation", "quarantined", "disabled"],
        StringComparer.Ordinal);

    private async Task<AgentDefinitionRecord?> ReadDefinitionByKeyAsync(string tenantId, string key, CancellationToken token)
    {
        await using var command = _dataSource.CreateCommand($"{DefinitionSelect} WHERE agent_key=$1 AND (tenant_id IS NULL OR tenant_id=$2) LIMIT 1;");
        command.Parameters.Add(Text(key));
        command.Parameters.Add(Text(tenantId));
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadDefinition(reader) : null;
    }

    private async Task<AgentDefinitionRecord> CreateDefinitionCoreAsync(AgentDefinitionCreateCommand command, CancellationToken token) { ValidateDefinition(command.Content); await using var connection = await _dataSource.OpenConnectionAsync(token); await using var tx = await connection.BeginTransactionAsync(token); await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", token, Text($"audit-ledger:{command.TenantId}")); await ValidateDefinitionReferencesAsync(connection, tx, command.TenantId, command.Content, token); var p = DefinitionParameters(command.Content); await ExecuteAsync(connection, tx, "INSERT INTO harness.agent_definitions(id,agent_key,name,role,specialty,description,default_model_id,skill_ids_json,tool_ids_json,tenant_id,persona,mission,operating_principles_json,deliverables_json,quality_criteria_json,communication_style,limitations_json,version,enabled,created_at,updated_at,stacks_json,default_effort,preferred_account_id,fallback_model_ids_json,team,actor_critic,risk,allowed_scopes_json,denied_scopes_json,activation_criteria_json,non_activation_criteria_json) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,1,true,$18,$18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29);", token, Text(command.Id), p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], Text(command.TenantId), p[8], p[9], p[10], p[11], p[12], p[13], p[14], Timestamp(command.OccurredAt), p[15], p[16], p[17], p[18], p[19], p[20], p[21], p[22], p[23], p[24], p[25]); await InsertDefinitionVersionAsync(connection, tx, command.Id, 1, command.ActorProfileId, command.Content, command.OccurredAt, token); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.created", "create", command.OccurredAt, token); await tx.CommitAsync(token); return (await GetDefinitionForTenantAsync(command.TenantId, command.Id, token))!; }
    private static async Task ExecuteDefinitionUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, AgentDefinitionUpdateCommand command, NpgsqlParameter[] p, CancellationToken token) { await using var update = connection.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE harness.agent_definitions SET agent_key=$1,name=$2,role=$3,specialty=$4,description=$5,default_model_id=$6,skill_ids_json=$7,tool_ids_json=$8,persona=$9,mission=$10,operating_principles_json=$11,deliverables_json=$12,quality_criteria_json=$13,communication_style=$14,limitations_json=$15,stacks_json=$16,default_effort=$17,preferred_account_id=$18,fallback_model_ids_json=$19,team=$20,actor_critic=$21,risk=$22,allowed_scopes_json=$23,denied_scopes_json=$24,activation_criteria_json=$25,non_activation_criteria_json=$26,version=version+1,updated_at=$27 WHERE id=$28 AND tenant_id=$29 AND version=$30 AND archived_at IS NULL;"; update.Parameters.AddRange(p); update.Parameters.Add(Timestamp(command.OccurredAt)); update.Parameters.Add(Text(command.Id)); update.Parameters.Add(Text(command.TenantId)); update.Parameters.Add(Integer(command.ExpectedVersion)); if (await update.ExecuteNonQueryAsync(token) != 1) throw new AgentDefinitionAdminException("Definition version conflicted or is archived."); }
    private static NpgsqlParameter[] DefinitionParameters(AgentDefinitionContent c) => [Text(c.Key.Trim()), Text(c.Name.Trim()), Text(c.Role), NullableText(c.Specialty?.Trim()), Text(c.Description.Trim()), NullableText(c.DefaultModelId), Json(JsonSerializer.Serialize(c.SkillIds, JsonOptions)), Json(JsonSerializer.Serialize(c.ToolIds, JsonOptions)), NullableText(c.Persona?.Trim()), NullableText(c.Mission?.Trim()), Json(JsonSerializer.Serialize(c.OperatingPrinciples, JsonOptions)), Json(JsonSerializer.Serialize(c.Deliverables, JsonOptions)), Json(JsonSerializer.Serialize(c.QualityCriteria, JsonOptions)), NullableText(c.CommunicationStyle?.Trim()), Json(JsonSerializer.Serialize(c.Limitations, JsonOptions)), Json(JsonSerializer.Serialize(c.Stacks ?? [], JsonOptions)), NullableText(c.DefaultEffort), NullableText(c.PreferredAccountId), Json(JsonSerializer.Serialize(c.FallbackModelIds ?? [], JsonOptions)), NullableText(c.Team?.Trim()), NullableText(c.ActorCritic), NullableText(c.Risk), Json(JsonSerializer.Serialize(c.AllowedScopes ?? [], JsonOptions)), Json(JsonSerializer.Serialize(c.DeniedScopes ?? [], JsonOptions)), Json(JsonSerializer.Serialize(c.ActivationCriteria ?? [], JsonOptions)), Json(JsonSerializer.Serialize(c.NonActivationCriteria ?? [], JsonOptions))];
    private static async Task InsertDefinitionVersionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string id, int version, string actor, AgentDefinitionContent content, DateTimeOffset at, CancellationToken token) => await ExecuteAsync(connection, tx, "INSERT INTO harness.agent_definition_versions(id,definition_id,version,snapshot_json,actor_profile_id,created_at) VALUES($1,$2,$3,$4,$5,$6);", token, Text(UlidValue.New(at).ToString()), Text(id), Integer(version), Json(JsonSerializer.Serialize(content, JsonOptions)), Text(actor), Timestamp(at));
    private static async Task AppendDefinitionAuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, string actor, string id, string type, string detail, DateTimeOffset at, CancellationToken token) { var payload = JsonSerializer.Serialize(new { auditEvent = new { id = UlidValue.New(at).ToString(), actorKind = "user", actorId = actor, action = type, targetType = "agent-definitions", targetId = id, detail, occurredAt = at } }, JsonOptions); var (sequence, previous) = await ReadLedgerTailAsync(connection, tx, tenant, token); var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at); await ExecuteAsync(connection, tx, "INSERT INTO harness.audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8);", token, Text(UlidValue.New(at).ToString()), Text(tenant), Bigint(sequence), Text(previous), Text(hash), Text(type), Json(payload), Timestamp(at)); await ExecuteAsync(connection, tx, "INSERT INTO harness.outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($1,$2,'audit.eventAppended',$3,$4);", token, Text(UlidValue.New(at).ToString()), Text(tenant), Json(payload), Timestamp(at)); }
    private static void ValidateDefinition(AgentDefinitionContent c) { var stacks = c.Stacks ?? []; var fallbacks = c.FallbackModelIds ?? []; if (string.IsNullOrWhiteSpace(c.Key) || c.Key.Length > 100 || c.Key.Any(ch => !(char.IsLower(ch) || char.IsDigit(ch) || ch == '-')) || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200 || c.Role is not ("chief" or "specialist") || string.IsNullOrWhiteSpace(c.Description) || c.Description.Length > 4000 || c.SkillIds.Concat(c.ToolIds).Concat(fallbacks).Any(value => !UlidValue.TryParse(value, out _)) || c.DefaultModelId is not null && !UlidValue.TryParse(c.DefaultModelId, out _) || c.PreferredAccountId is not null && !UlidValue.TryParse(c.PreferredAccountId, out _) || c.DefaultEffort is not null and not ("low" or "medium" or "high" or "max") || c.ActorCritic is not null and not ("actor" or "critic") || c.Risk is not null and not ("low" or "medium" or "high") || c.Team is not null && (string.IsNullOrWhiteSpace(c.Team) || c.Team.Length > 200) || stacks.Count > 50 || stacks.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 100) || stacks.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stacks.Count || fallbacks.Count > 10 || fallbacks.Distinct(StringComparer.Ordinal).Count() != fallbacks.Count || c.DefaultModelId is not null && fallbacks.Contains(c.DefaultModelId, StringComparer.Ordinal)) throw new AgentDefinitionAdminException("Definition content is invalid."); }
    private static async Task ValidateDefinitionReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, AgentDefinitionContent content, CancellationToken token) { await ValidateCatalogReferenceAsync(connection, tx, "harness.teams", "team", "/api/v1/teams", tenant, content.Team, token); await ValidateCatalogReferenceAsync(connection, tx, "harness.specialties", "specialty", "/api/v1/specialties", tenant, content.Specialty, token); foreach (var skillId in content.SkillIds.Distinct(StringComparer.Ordinal)) await ValidateGlobalComponentAsync(connection, tx, "harness.skills", "skill", "/api/v1/skills", skillId, token); foreach (var toolId in content.ToolIds.Distinct(StringComparer.Ordinal)) await ValidateGlobalComponentAsync(connection, tx, "harness.tools", "tool", "/api/v1/tools", toolId, token); string? accountProvider = null; if (content.PreferredAccountId is not null) { await using var account = connection.CreateCommand(); account.Transaction = tx; account.CommandText = "SELECT provider_id FROM harness.provider_accounts WHERE tenant_id=$1 AND id=$2 FOR SHARE;"; account.Parameters.Add(Text(tenant)); account.Parameters.Add(Text(content.PreferredAccountId)); accountProvider = ((string?)await account.ExecuteScalarAsync(token))?.TrimEnd() ?? throw new AgentDefinitionAdminException("Preferred account was not found."); } var referencedModels = (content.FallbackModelIds ?? []).AsEnumerable(); if (accountProvider is not null && content.DefaultModelId is not null) referencedModels = referencedModels.Prepend(content.DefaultModelId); foreach (var modelId in referencedModels.Distinct(StringComparer.Ordinal)) { await using var model = connection.CreateCommand(); model.Transaction = tx; model.CommandText = "SELECT provider_id FROM harness.provider_models WHERE tenant_id=$1 AND id=$2 AND enabled=true FOR SHARE;"; model.Parameters.Add(Text(tenant)); model.Parameters.Add(Text(modelId)); var provider = ((string?)await model.ExecuteScalarAsync(token))?.TrimEnd() ?? throw new AgentDefinitionCatalogMissingException("model", modelId, "/api/v1/models"); if (accountProvider is not null && provider != accountProvider) throw new AgentDefinitionAdminException("Preferred account and models must use the same provider."); } }
    private static AgentDefinitionContent ToContent(AgentDefinitionRecord v) => new(v.Key, v.Name, v.Role, v.Specialty, v.Description, v.DefaultModelId, v.SkillIds, v.ToolIds, v.Persona, v.Mission, v.OperatingPrinciples ?? [], v.Deliverables ?? [], v.QualityCriteria ?? [], v.CommunicationStyle, v.Limitations ?? [], v.Stacks ?? [], v.DefaultEffort, v.PreferredAccountId, v.FallbackModelIds ?? [], v.Team, v.ActorCritic, v.Risk, v.AllowedScopes ?? [], v.DeniedScopes ?? [], v.ActivationCriteria ?? [], v.NonActivationCriteria ?? []);

    public async Task<AgentRecord> UpdateSelectionAsync(AgentSelectionCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Effort is not ("low" or "medium" or "high" or "max") || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000 || command.FallbackModelIds.Count > 10 || command.FallbackModelIds.Distinct(StringComparer.Ordinal).Count() != command.FallbackModelIds.Count || command.FallbackModelIds.Contains(command.ModelId, StringComparer.Ordinal)) throw new AgentSelectionValidationException("Agent selection values are invalid.");
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken); await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", cancellationToken, Text($"audit-ledger:{command.TenantId}"));
        string state; await using (var agent = connection.CreateCommand()) { agent.Transaction = tx; agent.CommandText = "SELECT state FROM harness.agents WHERE tenant_id=$1 AND id=$2 AND retired_at IS NULL FOR UPDATE;"; agent.Parameters.Add(Text(command.TenantId)); agent.Parameters.Add(Text(command.AgentId)); state = (string?)await agent.ExecuteScalarAsync(cancellationToken) ?? throw new AgentSelectionNotFoundException("agent"); }
        if (state is not ("idle" or "waiting")) throw new AgentSelectionConflictException("Only an idle or waiting agent can change selection.");
        var (providerId, providerValue) = await ReadModelSelectionAsync(connection, tx, command.TenantId, command.ModelId, command.Effort, cancellationToken);
        await using (var account = connection.CreateCommand()) { account.Transaction = tx; account.CommandText = "SELECT provider_id,state FROM harness.provider_accounts WHERE tenant_id=$1 AND id=$2 FOR SHARE;"; account.Parameters.Add(Text(command.TenantId)); account.Parameters.Add(Text(command.AccountId)); await using var reader = await account.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) throw new AgentSelectionNotFoundException("account"); if (reader.GetString(0).TrimEnd() != providerId || reader.GetString(1) != "active") throw new AgentSelectionConflictException("Account must be active and belong to the selected model provider."); }
        foreach (var fallback in command.FallbackModelIds) { var (fallbackProvider, _) = await ReadModelSelectionAsync(connection, tx, command.TenantId, fallback, command.Effort, cancellationToken); if (fallbackProvider != providerId) throw new AgentSelectionConflictException("Fallback models must use the selected account provider."); }
        await ExecuteAsync(connection, tx, "UPDATE harness.agents SET account_id=$1,model_id=$2,effort=$3,provider_effort_value=$4,fallback_model_ids_json=$5,selection_reason=$6,selection_updated_at=$7 WHERE tenant_id=$8 AND id=$9;", cancellationToken, Text(command.AccountId), Text(command.ModelId), Text(command.Effort), Text(providerValue), Json(JsonSerializer.Serialize(command.FallbackModelIds, JsonOptions)), Text(command.Reason.Trim()), Timestamp(command.OccurredAt), Text(command.TenantId), Text(command.AgentId));
        var payload = JsonSerializer.Serialize(new { auditEvent = new { id = UlidValue.New(command.OccurredAt).ToString(), actorKind = "user", actorId = command.ActorProfileId, action = "agent.selectionUpdated", targetType = "agents", targetId = command.AgentId, detail = command.Reason.Trim(), occurredAt = command.OccurredAt }, modelId = command.ModelId, accountId = command.AccountId, effort = command.Effort, providerEffortValue = providerValue, fallbackModelIds = command.FallbackModelIds }, JsonOptions);
        var (sequence, previous) = await ReadLedgerTailAsync(connection, tx, command.TenantId, cancellationToken); var hash = AuditLedgerHash.Compute(previous, command.TenantId, sequence, "agent.selectionUpdated", payload, command.OccurredAt);
        await ExecuteAsync(connection, tx, "INSERT INTO harness.audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($1,$2,$3,$4,$5,'agent.selectionUpdated',$6,$7);", cancellationToken, Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId), Bigint(sequence), Text(previous), Text(hash), Json(payload), Timestamp(command.OccurredAt));
        await ExecuteAsync(connection, tx, "INSERT INTO harness.outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($1,$2,'audit.eventAppended',$3,$4);", cancellationToken, Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId), Json(payload), Timestamp(command.OccurredAt));
        await tx.CommitAsync(cancellationToken); return (await GetAgentAsync(command.TenantId, command.AgentId, cancellationToken))!;
    }

    public async Task<(AgentRecord Agent, bool Created)> EnsureProjectAgentAsync(
        ProjectAgentEnsureCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectAgent(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection, tx,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"project-agent:{command.TenantId}:{command.ProjectId}:{command.DefinitionId}"));

        string? existingId;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText =
                "SELECT id FROM harness.agents WHERE tenant_id=$1 AND project_id=$2 " +
                "AND definition_id=$3 AND retired_at IS NULL ORDER BY id LIMIT 1 FOR UPDATE;";
            existing.Parameters.Add(Text(command.TenantId));
            existing.Parameters.Add(Text(command.ProjectId));
            existing.Parameters.Add(Text(command.DefinitionId));
            existingId = ((string?)await existing.ExecuteScalarAsync(cancellationToken))?.TrimEnd();
        }

        if (existingId is not null)
        {
            await tx.CommitAsync(cancellationToken);
            return ((await GetAgentAsync(command.TenantId, existingId, cancellationToken))!, false);
        }

        await ValidateProjectAgentReferencesAsync(connection, tx, command, cancellationToken);
        await ExecuteAsync(
            connection, tx,
            """
            INSERT INTO harness.agents
                (id,tenant_id,definition_id,project_id,name,state,current_task_id,model_id,
                 account_id,effort,provider_effort_value,fallback_model_ids_json,
                 selection_reason,selection_updated_at,last_heartbeat_at,created_at)
            VALUES ($1,$2,$3,$4,$5,'idle',NULL,$6,$7,$8,$9,$10,$11,$12,$12,$12);
            """,
            cancellationToken,
            Text(command.AgentId), Text(command.TenantId), Text(command.DefinitionId),
            Text(command.ProjectId), Text(command.Name.Trim()), Text(command.ModelId),
            Text(command.AccountId), Text(command.Effort), Text(command.ProviderEffortValue),
            Json(JsonSerializer.Serialize(command.FallbackModelIds, JsonOptions)),
            Text(command.Reason.Trim()), Timestamp(command.OccurredAt));

        var payload = JsonSerializer.Serialize(new
        {
            auditEvent = new
            {
                id = UlidValue.New(command.OccurredAt).ToString(),
                actorKind = "chief",
                actorId = command.ActorProfileId,
                action = "agent.projectSpecialistCreated",
                targetType = "agents",
                targetId = command.AgentId,
                detail = command.Reason.Trim(),
                occurredAt = command.OccurredAt,
            },
            command.ProjectId,
            command.DefinitionId,
            command.ModelId,
            command.AccountId,
        }, JsonOptions);
        await ExecuteAsync(
            connection, tx,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"audit-ledger:{command.TenantId}"));
        var (sequence, previous) = await ReadLedgerTailAsync(
            connection, tx, command.TenantId, cancellationToken);
        var hash = AuditLedgerHash.Compute(
            previous, command.TenantId, sequence, "agent.projectSpecialistCreated", payload,
            command.OccurredAt);
        await ExecuteAsync(
            connection, tx,
            "INSERT INTO harness.audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($1,$2,$3,$4,$5,'agent.projectSpecialistCreated',$6,$7);",
            cancellationToken,
            Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
            Bigint(sequence), Text(previous), Text(hash), Json(payload), Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection, tx,
            "INSERT INTO harness.outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($1,$2,'audit.eventAppended',$3,$4);",
            cancellationToken,
            Text(UlidValue.New(command.OccurredAt.AddTicks(1)).ToString()),
            Text(command.TenantId), Json(payload), Timestamp(command.OccurredAt));

        await tx.CommitAsync(cancellationToken);
        return ((await GetAgentAsync(command.TenantId, command.AgentId, cancellationToken))!, true);
    }

    private static void ValidateProjectAgent(ProjectAgentEnsureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!UlidValue.TryParse(command.AgentId, out _) ||
            !UlidValue.TryParse(command.ProjectId, out _) ||
            !UlidValue.TryParse(command.DefinitionId, out _) ||
            !UlidValue.TryParse(command.AccountId, out _) ||
            !UlidValue.TryParse(command.ModelId, out _) ||
            string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 2000 ||
            command.Effort is not ("low" or "medium" or "high" or "max") ||
            string.IsNullOrWhiteSpace(command.ProviderEffortValue) ||
            command.FallbackModelIds.Count > 10 ||
            command.FallbackModelIds.Contains(command.ModelId, StringComparer.Ordinal) ||
            command.FallbackModelIds.Distinct(StringComparer.Ordinal).Count() !=
                command.FallbackModelIds.Count)
        {
            throw new AgentSelectionValidationException("Project specialist selection is invalid.");
        }
    }

    private static async Task ValidateProjectAgentReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        ProjectAgentEnsureCommand command,
        CancellationToken token)
    {
        await using (var references = connection.CreateCommand())
        {
            references.Transaction = tx;
            references.CommandText =
                "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL)," +
                "EXISTS(SELECT 1 FROM harness.agent_definitions WHERE id=$3 AND " +
                "(tenant_id IS NULL OR tenant_id=$1) AND enabled=true AND archived_at IS NULL);";
            references.Parameters.Add(Text(command.TenantId));
            references.Parameters.Add(Text(command.ProjectId));
            references.Parameters.Add(Text(command.DefinitionId));
            await using var reader = await references.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            if (!reader.GetBoolean(0)) throw new AgentSelectionNotFoundException("project");
            if (!reader.GetBoolean(1)) throw new AgentSelectionNotFoundException("definition");
        }

        var (providerId, providerEffort) = await ReadModelSelectionAsync(
            connection, tx, command.TenantId, command.ModelId, command.Effort, token);
        if (!string.Equals(providerEffort, command.ProviderEffortValue, StringComparison.Ordinal))
            throw new AgentSelectionConflictException("Provider effort mapping changed before specialist creation.");
        await using var account = connection.CreateCommand();
        account.Transaction = tx;
        account.CommandText =
            "SELECT provider_id,state FROM harness.provider_accounts WHERE tenant_id=$1 AND id=$2 FOR SHARE;";
        account.Parameters.Add(Text(command.TenantId));
        account.Parameters.Add(Text(command.AccountId));
        await using var accountReader = await account.ExecuteReaderAsync(token);
        if (!await accountReader.ReadAsync(token)) throw new AgentSelectionNotFoundException("account");
        if (!string.Equals(accountReader.GetString(0).TrimEnd(), providerId, StringComparison.Ordinal) ||
            !string.Equals(accountReader.GetString(1), "active", StringComparison.Ordinal))
            throw new AgentSelectionConflictException("Specialist account is not active for the selected model.");
    }

    // CAT-04: valida que team/specialty referenciados pela definição existem no catálogo
    // tenant-scoped. Valores semeados (migração 0050) continuam válidos; valores novos e
    // inexistentes são recusados com um erro tipado.
    private static async Task ValidateCatalogReferenceAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, string catalog, string createRoute, string tenant, string? value, CancellationToken token) { var trimmed = value?.Trim(); if (string.IsNullOrEmpty(trimmed)) return; await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE tenant_id=$1 AND lower(name)=lower($2));"; command.Parameters.Add(Text(tenant)); command.Parameters.Add(Text(trimmed)); if (!(bool)(await command.ExecuteScalarAsync(token))!) throw new AgentDefinitionCatalogMissingException(catalog, trimmed, createRoute); }
    // CAT-05: skills/tools são catálogos GLOBAIS (sem tenant): valida existência por id e devolve o
    // mesmo problema acionável "criar quando não encontrar".
    private static async Task ValidateGlobalComponentAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, string catalog, string createRoute, string id, CancellationToken token) { await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=$1);"; command.Parameters.Add(Text(id)); if (!(bool)(await command.ExecuteScalarAsync(token))!) throw new AgentDefinitionCatalogMissingException(catalog, id, createRoute); }

    private static async Task<(string ProviderId, string ProviderValue)> ReadModelSelectionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, string modelId, string effort, CancellationToken token) { await using var query = connection.CreateCommand(); query.Transaction = tx; query.CommandText = "SELECT provider_id,enabled,effort_mappings_json::text FROM harness.provider_models WHERE tenant_id=$1 AND id=$2 FOR SHARE;"; query.Parameters.Add(Text(tenant)); query.Parameters.Add(Text(modelId)); await using var reader = await query.ExecuteReaderAsync(token); if (!await reader.ReadAsync(token)) throw new AgentSelectionNotFoundException("model"); if (!reader.GetBoolean(1)) throw new AgentSelectionConflictException("Selected models must be enabled."); var mapping = (JsonSerializer.Deserialize<EffortMap[]>(reader.GetString(2), JsonOptions) ?? []).SingleOrDefault(value => value.Effort == effort) ?? throw new AgentSelectionValidationException("Effort is not mapped for the selected model."); return (reader.GetString(0).TrimEnd(), mapping.ProviderValue); }
    private static async Task<(long Sequence, string Previous)> ReadLedgerTailAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, CancellationToken token) { await using var query = connection.CreateCommand(); query.Transaction = tx; query.CommandText = "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;"; query.Parameters.Add(Text(tenant)); await using var reader = await query.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd()) : (1, AuditLedgerHash.Genesis); }
    private sealed record EffortMap(string Effort, string ProviderValue);

    private static AgentDefinitionRecord ReadDefinition(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6).TrimEnd(),
            Deserialize(reader.GetString(7)),
            Deserialize(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            Deserialize(reader.GetString(11)), Deserialize(reader.GetString(12)), Deserialize(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14), Deserialize(reader.GetString(15)), reader.GetInt32(16),
            reader.GetBoolean(17), reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            Deserialize(reader.GetString(19)), reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21).TrimEnd(), Deserialize(reader.GetString(22)),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26).TrimEnd(),
            reader.GetString(27),
            reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetString(29),
            reader.IsDBNull(30) ? null : reader.GetString(30),
            Deserialize(reader.GetString(31)),
            Deserialize(reader.GetString(32)),
            Deserialize(reader.GetString(33)),
            Deserialize(reader.GetString(34)));

    private static AgentDefinitionVersionRecord ReadDefinitionVersion(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), reader.GetInt32(2),
        JsonSerializer.Deserialize<AgentDefinitionContent>(reader.GetString(3), JsonOptions)
            ?? throw new InvalidDataException("Agent definition snapshot is invalid."),
        reader.GetString(4).TrimEnd(), reader.GetFieldValue<DateTimeOffset>(5));

    private static AgentRecord ReadAgent(NpgsqlDataReader reader)
    {
        var lease = reader.IsDBNull(8)
            ? null
            : new AgentLeaseRecord(reader.GetInt64(8), reader.GetFieldValue<DateTimeOffset>(9));
        return new AgentRecord(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.IsDBNull(3) ? null : reader.GetString(3).TrimEnd(),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6).TrimEnd(),
            reader.IsDBNull(7) ? null : reader.GetString(7).TrimEnd(),
            lease,
            new AgentMetricsRecord(
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetDecimal(13),
                reader.GetInt64(14)),
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
            reader.IsDBNull(16) ? null : reader.GetString(16).TrimEnd(), reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18), Deserialize(reader.GetString(19)),
            reader.IsDBNull(20) ? null : reader.GetString(20), reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21));
    }

    private static string[] Deserialize(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("Persisted agent-definition JSON cannot be null.");

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };
    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };
    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = value };
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params NpgsqlParameter[] parameters) { await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(token); }

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };
    private static NpgsqlParameter NullableInteger(int? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Integer,
        Value = (object?)value ?? DBNull.Value,
    };
}
