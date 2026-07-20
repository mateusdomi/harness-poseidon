using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteAgentCatalogStore(SqliteWriteDispatcher dispatcher) : IAgentCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<AgentDefinitionRecord?> GetDefinitionAsync(string definitionId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{DefinitionSelect} WHERE id=$id;";
            Add(command, "$id", definitionId);
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadDefinition(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsAsync(string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<AgentDefinitionRecord>>(async (connection, token) =>
        {
            var values = new List<AgentDefinitionRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"{DefinitionSelect} WHERE ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(command, "$after", afterId is null ? DBNull.Value : afterId);
            Add(command, "$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadDefinition(reader));
            return values;
        }, cancellationToken);

    public Task<AgentRecord?> GetAgentAsync(string tenantId, string agentId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{AgentSelect} WHERE tenant_id=$tenant AND id=$id AND retired_at IS NULL;";
            Add(command, "$tenant", tenantId);
            Add(command, "$id", agentId);
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadAgent(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<AgentRecord>>(async (connection, token) =>
        {
            var values = new List<AgentRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"{AgentSelect} WHERE tenant_id=$tenant AND retired_at IS NULL AND ($project IS NULL OR project_id=$project) AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(command, "$tenant", tenantId);
            Add(command, "$project", projectId is null ? DBNull.Value : projectId);
            Add(command, "$after", afterId is null ? DBNull.Value : afterId);
            Add(command, "$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadAgent(reader));
            return values;
        }, cancellationToken);

    public Task<AgentDefinitionRecord?> GetDefinitionForTenantAsync(string tenantId, string definitionId, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync(async (connection, token) => { await using var command = connection.CreateCommand(); command.CommandText = $"{DefinitionSelect} WHERE id=$id AND (tenant_id IS NULL OR tenant_id=$tenant);"; Add(command, "$id", definitionId); Add(command, "$tenant", tenantId); await using var reader = await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadDefinition(reader) : null; }, cancellationToken);
    public Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsForTenantAsync(string tenantId, string? afterId, int limit, bool includeArchived, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync<IReadOnlyList<AgentDefinitionRecord>>(async (connection, token) => { var values = new List<AgentDefinitionRecord>(); await using var command = connection.CreateCommand(); command.CommandText = $"{DefinitionSelect} WHERE (tenant_id IS NULL OR tenant_id=$tenant) AND ($archived=1 OR archived_at IS NULL) AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;"; Add(command, "$tenant", tenantId); Add(command, "$archived", includeArchived ? 1 : 0); Add(command, "$after", afterId is null ? DBNull.Value : afterId); Add(command, "$limit", limit); await using var reader = await command.ExecuteReaderAsync(token); while (await reader.ReadAsync(token)) values.Add(ReadDefinition(reader)); return values; }, cancellationToken);

    public Task<IReadOnlyList<AgentDefinitionVersionRecord>> ListDefinitionVersionsAsync(
        string tenantId, string definitionId, int? beforeVersion, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<AgentDefinitionVersionRecord>>(async (connection, token) =>
        {
            var values = new List<AgentDefinitionVersionRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT v.id,v.definition_id,v.version,v.snapshot_json,v.actor_profile_id,v.created_at " +
                "FROM agent_definition_versions v JOIN agent_definitions d ON d.id=v.definition_id " +
                "WHERE d.tenant_id=$tenant AND v.definition_id=$id " +
                "AND ($before IS NULL OR v.version<$before) ORDER BY v.version DESC LIMIT $limit;";
            Add(command, "$tenant", tenantId); Add(command, "$id", definitionId);
            Add(command, "$before", beforeVersion is null ? DBNull.Value : beforeVersion.Value);
            Add(command, "$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadDefinitionVersion(reader));
            return values;
        }, cancellationToken);

    public Task<AgentDefinitionRecord> CreateDefinitionAsync(AgentDefinitionCreateCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync((connection, token) => CreateDefinitionCoreAsync(connection, command, token), cancellationToken);
    public Task<AgentDefinitionRecord> UpdateDefinitionAsync(AgentDefinitionUpdateCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync((connection, token) => UpdateDefinitionCoreAsync(connection, command, token), cancellationToken);
    public Task<AgentDefinitionRecord> DuplicateDefinitionAsync(AgentDefinitionDuplicateCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync(async (connection, token) => { var source = await ReadDefinitionForTenantAsync(connection, command.TenantId, command.SourceId, token) ?? throw new AgentDefinitionAdminException("Source definition was not found."); var content = ToContent(source) with { Key = command.Key, Name = command.Name }; return await CreateDefinitionCoreAsync(connection, new(command.TenantId, command.ActorProfileId, command.Id, content, command.OccurredAt), token); }, cancellationToken);
    public Task<AgentDefinitionRecord> SetDefinitionLifecycleAsync(AgentDefinitionLifecycleCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync(async (connection, token) => { if (command.Action is not ("enable" or "disable" or "archive")) throw new AgentDefinitionAdminException("Definition lifecycle action is invalid."); await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token); await using var update = connection.CreateCommand(); update.Transaction = tx; update.CommandText = command.Action switch { "enable" => "UPDATE agent_definitions SET enabled=1,updated_at=$at WHERE id=$id AND tenant_id=$tenant AND archived_at IS NULL;", "disable" => "UPDATE agent_definitions SET enabled=0,updated_at=$at WHERE id=$id AND tenant_id=$tenant AND archived_at IS NULL;", _ => "UPDATE agent_definitions SET enabled=0,archived_at=$at,updated_at=$at WHERE id=$id AND tenant_id=$tenant AND archived_at IS NULL;" }; Add(update, "$at", Store(command.OccurredAt)); Add(update, "$id", command.Id); Add(update, "$tenant", command.TenantId); if (await update.ExecuteNonQueryAsync(token) != 1) throw new AgentDefinitionAdminException("Definition cannot transition from its current state."); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, $"agentDefinition.{command.Action}d", command.Action, command.OccurredAt, token); await tx.CommitAsync(token); return (await ReadDefinitionForTenantAsync(connection, command.TenantId, command.Id, token))!; }, cancellationToken);
    public Task DeleteDefinitionAsync(AgentDefinitionDeleteCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync(async (connection, token) => { await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token); await using (var check = connection.CreateCommand()) { check.Transaction = tx; check.CommandText = "SELECT EXISTS(SELECT 1 FROM agents WHERE definition_id=$id),EXISTS(SELECT 1 FROM agent_definitions WHERE id=$id AND tenant_id=$tenant)"; Add(check, "$id", command.Id); Add(check, "$tenant", command.TenantId); await using var reader = await check.ExecuteReaderAsync(token); await reader.ReadAsync(token); if (reader.GetInt64(1) == 0) throw new AgentDefinitionAdminException("Definition was not found."); if (reader.GetInt64(0) != 0) throw new AgentDefinitionAdminException("A definition that has been used cannot be deleted."); } await using (var deleteVersions = connection.CreateCommand()) { deleteVersions.Transaction = tx; deleteVersions.CommandText = "DELETE FROM agent_definition_versions WHERE definition_id=$id;"; Add(deleteVersions, "$id", command.Id); await deleteVersions.ExecuteNonQueryAsync(token); } await using (var delete = connection.CreateCommand()) { delete.Transaction = tx; delete.CommandText = "DELETE FROM agent_definitions WHERE id=$id AND tenant_id=$tenant;"; Add(delete, "$id", command.Id); Add(delete, "$tenant", command.TenantId); await delete.ExecuteNonQueryAsync(token); } await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.deleted", "delete", command.OccurredAt, token); await tx.CommitAsync(token); }, cancellationToken);

    private static async Task<AgentDefinitionRecord> CreateDefinitionCoreAsync(SqliteConnection connection, AgentDefinitionCreateCommand command, CancellationToken token) { ValidateDefinition(command.Content); await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token); await ValidateDefinitionReferencesAsync(connection, tx, command.TenantId, command.Content, token); await InsertDefinitionAsync(connection, tx, command.TenantId, command.Id, 1, command.Content, command.OccurredAt, token); await InsertDefinitionVersionAsync(connection, tx, command.Id, 1, command.ActorProfileId, command.Content, command.OccurredAt, token); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.created", "create", command.OccurredAt, token); await tx.CommitAsync(token); return (await ReadDefinitionForTenantAsync(connection, command.TenantId, command.Id, token))!; }
    private static async Task<AgentDefinitionRecord> UpdateDefinitionCoreAsync(SqliteConnection connection, AgentDefinitionUpdateCommand command, CancellationToken token) { ValidateDefinition(command.Content); await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token); await ValidateDefinitionReferencesAsync(connection, tx, command.TenantId, command.Content, token); await using var update = connection.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE agent_definitions SET agent_key=$key,name=$name,role=$role,specialty=$specialty,description=$description,default_model_id=$model,skill_ids_json=$skills,tool_ids_json=$tools,persona=$persona,mission=$mission,operating_principles_json=$principles,deliverables_json=$deliverables,quality_criteria_json=$quality,communication_style=$communication,limitations_json=$limitations,stacks_json=$stacks,default_effort=$effort,preferred_account_id=$account,fallback_model_ids_json=$fallbacks,team=$team,actor_critic=$actorCritic,risk=$risk,version=version+1,updated_at=$at WHERE id=$id AND tenant_id=$tenant AND version=$version AND archived_at IS NULL;"; BindDefinition(update, command.Content); Add(update, "$at", Store(command.OccurredAt)); Add(update, "$id", command.Id); Add(update, "$tenant", command.TenantId); Add(update, "$version", command.ExpectedVersion); if (await update.ExecuteNonQueryAsync(token) != 1) throw new AgentDefinitionAdminException("Definition version conflicted or is archived."); await InsertDefinitionVersionAsync(connection, tx, command.Id, command.ExpectedVersion + 1, command.ActorProfileId, command.Content, command.OccurredAt, token); await AppendDefinitionAuditAsync(connection, tx, command.TenantId, command.ActorProfileId, command.Id, "agentDefinition.updated", "update", command.OccurredAt, token); await tx.CommitAsync(token); return (await ReadDefinitionForTenantAsync(connection, command.TenantId, command.Id, token))!; }
    private static async Task InsertDefinitionAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string id, int version, AgentDefinitionContent content, DateTimeOffset at, CancellationToken token) { await using var insert = connection.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO agent_definitions(id,agent_key,name,role,specialty,description,default_model_id,skill_ids_json,tool_ids_json,tenant_id,persona,mission,operating_principles_json,deliverables_json,quality_criteria_json,communication_style,limitations_json,version,enabled,created_at,updated_at,stacks_json,default_effort,preferred_account_id,fallback_model_ids_json,team,actor_critic,risk) VALUES($id,$key,$name,$role,$specialty,$description,$model,$skills,$tools,$tenant,$persona,$mission,$principles,$deliverables,$quality,$communication,$limitations,$version,1,$at,$at,$stacks,$effort,$account,$fallbacks,$team,$actorCritic,$risk);"; BindDefinition(insert, content); Add(insert, "$id", id); Add(insert, "$tenant", tenant); Add(insert, "$version", version); Add(insert, "$at", Store(at)); await insert.ExecuteNonQueryAsync(token); }
    private static void BindDefinition(SqliteCommand command, AgentDefinitionContent content) { Add(command, "$key", content.Key.Trim()); Add(command, "$name", content.Name.Trim()); Add(command, "$role", content.Role); Add(command, "$specialty", content.Specialty?.Trim() ?? (object)DBNull.Value); Add(command, "$description", content.Description.Trim()); Add(command, "$model", content.DefaultModelId ?? (object)DBNull.Value); Add(command, "$skills", JsonSerializer.Serialize(content.SkillIds, JsonOptions)); Add(command, "$tools", JsonSerializer.Serialize(content.ToolIds, JsonOptions)); Add(command, "$persona", content.Persona?.Trim() ?? (object)DBNull.Value); Add(command, "$mission", content.Mission?.Trim() ?? (object)DBNull.Value); Add(command, "$principles", JsonSerializer.Serialize(content.OperatingPrinciples, JsonOptions)); Add(command, "$deliverables", JsonSerializer.Serialize(content.Deliverables, JsonOptions)); Add(command, "$quality", JsonSerializer.Serialize(content.QualityCriteria, JsonOptions)); Add(command, "$communication", content.CommunicationStyle?.Trim() ?? (object)DBNull.Value); Add(command, "$limitations", JsonSerializer.Serialize(content.Limitations, JsonOptions)); Add(command, "$stacks", JsonSerializer.Serialize(content.Stacks ?? [], JsonOptions)); Add(command, "$effort", content.DefaultEffort ?? (object)DBNull.Value); Add(command, "$account", content.PreferredAccountId ?? (object)DBNull.Value); Add(command, "$fallbacks", JsonSerializer.Serialize(content.FallbackModelIds ?? [], JsonOptions)); Add(command, "$team", content.Team?.Trim() ?? (object)DBNull.Value); Add(command, "$actorCritic", content.ActorCritic ?? (object)DBNull.Value); Add(command, "$risk", content.Risk ?? (object)DBNull.Value); }
    private static async Task InsertDefinitionVersionAsync(SqliteConnection connection, SqliteTransaction tx, string id, int version, string actor, AgentDefinitionContent content, DateTimeOffset at, CancellationToken token) { await using var insert = connection.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO agent_definition_versions(id,definition_id,version,snapshot_json,actor_profile_id,created_at) VALUES($row,$id,$version,$snapshot,$actor,$at);"; Add(insert, "$row", UlidValue.New(at).ToString()); Add(insert, "$id", id); Add(insert, "$version", version); Add(insert, "$snapshot", JsonSerializer.Serialize(content, JsonOptions)); Add(insert, "$actor", actor); Add(insert, "$at", Store(at)); await insert.ExecuteNonQueryAsync(token); }
    private static async Task AppendDefinitionAuditAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string actor, string id, string eventType, string detail, DateTimeOffset at, CancellationToken token) { var payload = JsonSerializer.Serialize(new { auditEvent = new { id = UlidValue.New(at).ToString(), actorKind = "user", actorId = actor, action = eventType, targetType = "agent-definitions", targetId = id, detail, occurredAt = at } }, JsonOptions); await AppendAuditAsync(connection, tx, tenant, eventType, payload, at, token); }
    private static void ValidateDefinition(AgentDefinitionContent content) { var stacks = content.Stacks ?? []; var fallbacks = content.FallbackModelIds ?? []; if (string.IsNullOrWhiteSpace(content.Key) || content.Key.Length > 100 || content.Key.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character == '-')) || string.IsNullOrWhiteSpace(content.Name) || content.Name.Length > 200 || content.Role is not ("chief" or "specialist") || string.IsNullOrWhiteSpace(content.Description) || content.Description.Length > 4000 || content.SkillIds.Concat(content.ToolIds).Concat(fallbacks).Any(value => !UlidValue.TryParse(value, out _)) || content.DefaultModelId is not null && !UlidValue.TryParse(content.DefaultModelId, out _) || content.PreferredAccountId is not null && !UlidValue.TryParse(content.PreferredAccountId, out _) || content.DefaultEffort is not null and not ("low" or "medium" or "high" or "max") || content.ActorCritic is not null and not ("actor" or "critic") || content.Risk is not null and not ("low" or "medium" or "high") || content.Team is not null && (string.IsNullOrWhiteSpace(content.Team) || content.Team.Length > 200) || stacks.Count > 50 || stacks.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 100) || stacks.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stacks.Count || fallbacks.Count > 10 || fallbacks.Distinct(StringComparer.Ordinal).Count() != fallbacks.Count || content.DefaultModelId is not null && fallbacks.Contains(content.DefaultModelId, StringComparer.Ordinal)) throw new AgentDefinitionAdminException("Definition content is invalid."); }
    private static async Task ValidateDefinitionReferencesAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, AgentDefinitionContent content, CancellationToken token) { string? accountProvider = null; if (content.PreferredAccountId is not null) { await using var account = connection.CreateCommand(); account.Transaction = tx; account.CommandText = "SELECT provider_id FROM provider_accounts WHERE tenant_id=$tenant AND id=$id;"; Add(account, "$tenant", tenant); Add(account, "$id", content.PreferredAccountId); accountProvider = (string?)await account.ExecuteScalarAsync(token) ?? throw new AgentDefinitionAdminException("Preferred account was not found."); } var referencedModels = (content.FallbackModelIds ?? []).AsEnumerable(); if (accountProvider is not null && content.DefaultModelId is not null) referencedModels = referencedModels.Prepend(content.DefaultModelId); foreach (var modelId in referencedModels.Distinct(StringComparer.Ordinal)) { await using var model = connection.CreateCommand(); model.Transaction = tx; model.CommandText = "SELECT provider_id FROM provider_models WHERE tenant_id=$tenant AND id=$id AND enabled=1;"; Add(model, "$tenant", tenant); Add(model, "$id", modelId); var provider = (string?)await model.ExecuteScalarAsync(token) ?? throw new AgentDefinitionAdminException("Default or fallback model was not found or is disabled."); if (accountProvider is not null && provider != accountProvider) throw new AgentDefinitionAdminException("Preferred account and models must use the same provider."); } }
    private static AgentDefinitionContent ToContent(AgentDefinitionRecord value) => new(value.Key, value.Name, value.Role, value.Specialty, value.Description, value.DefaultModelId, value.SkillIds, value.ToolIds, value.Persona, value.Mission, value.OperatingPrinciples ?? [], value.Deliverables ?? [], value.QualityCriteria ?? [], value.CommunicationStyle, value.Limitations ?? [], value.Stacks ?? [], value.DefaultEffort, value.PreferredAccountId, value.FallbackModelIds ?? [], value.Team, value.ActorCritic, value.Risk);
    private static async Task<AgentDefinitionRecord?> ReadDefinitionForTenantAsync(SqliteConnection connection, string tenant, string id, CancellationToken token) { await using var command = connection.CreateCommand(); command.CommandText = $"{DefinitionSelect} WHERE id=$id AND (tenant_id IS NULL OR tenant_id=$tenant);"; Add(command, "$id", id); Add(command, "$tenant", tenant); await using var reader = await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadDefinition(reader) : null; }

    public Task<AgentRecord> UpdateSelectionAsync(AgentSelectionCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            if (command.Effort is not ("low" or "medium" or "high" or "max") ||
                string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000 ||
                command.FallbackModelIds.Count > 10 || command.FallbackModelIds.Distinct(StringComparer.Ordinal).Count() != command.FallbackModelIds.Count ||
                command.FallbackModelIds.Contains(command.ModelId, StringComparer.Ordinal))
                throw new AgentSelectionValidationException("Agent selection values are invalid.");
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            string state;
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = tx; query.CommandText = "SELECT state FROM agents WHERE tenant_id=$tenant AND id=$id AND retired_at IS NULL;";
                Add(query, "$tenant", command.TenantId); Add(query, "$id", command.AgentId);
                state = (string?)await query.ExecuteScalarAsync(token) ?? throw new AgentSelectionNotFoundException("agent");
            }
            if (state is not ("idle" or "waiting")) throw new AgentSelectionConflictException("Only an idle or waiting agent can change selection.");
            var (providerId, providerValue) = await ReadModelSelectionAsync(connection, tx, command.TenantId, command.ModelId, command.Effort, token);
            await using (var account = connection.CreateCommand())
            {
                account.Transaction = tx; account.CommandText = "SELECT provider_id,state FROM provider_accounts WHERE tenant_id=$tenant AND id=$id;";
                Add(account, "$tenant", command.TenantId); Add(account, "$id", command.AccountId);
                await using var reader = await account.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) throw new AgentSelectionNotFoundException("account");
                if (reader.GetString(0) != providerId || reader.GetString(1) != "active") throw new AgentSelectionConflictException("Account must be active and belong to the selected model provider.");
            }
            foreach (var fallback in command.FallbackModelIds)
            {
                var (fallbackProvider, _) = await ReadModelSelectionAsync(connection, tx, command.TenantId, fallback, command.Effort, token);
                if (fallbackProvider != providerId) throw new AgentSelectionConflictException("Fallback models must use the selected account provider.");
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = tx; update.CommandText = "UPDATE agents SET account_id=$account,model_id=$model,effort=$effort,provider_effort_value=$value,fallback_model_ids_json=$fallbacks,selection_reason=$reason,selection_updated_at=$at WHERE tenant_id=$tenant AND id=$id;";
                Add(update, "$account", command.AccountId); Add(update, "$model", command.ModelId); Add(update, "$effort", command.Effort); Add(update, "$value", providerValue); Add(update, "$fallbacks", JsonSerializer.Serialize(command.FallbackModelIds, JsonOptions)); Add(update, "$reason", command.Reason.Trim()); Add(update, "$at", Store(command.OccurredAt)); Add(update, "$tenant", command.TenantId); Add(update, "$id", command.AgentId); await update.ExecuteNonQueryAsync(token);
            }
            var payload = JsonSerializer.Serialize(new { auditEvent = new { id = UlidValue.New(command.OccurredAt).ToString(), actorKind = "user", actorId = command.ActorProfileId, action = "agent.selectionUpdated", targetType = "agents", targetId = command.AgentId, detail = command.Reason.Trim(), occurredAt = command.OccurredAt }, modelId = command.ModelId, accountId = command.AccountId, effort = command.Effort, providerEffortValue = providerValue, fallbackModelIds = command.FallbackModelIds }, JsonOptions);
            await AppendAuditAsync(connection, tx, command.TenantId, "agent.selectionUpdated", payload, command.OccurredAt, token);
            await tx.CommitAsync(token);
            return (await GetAgentInConnectionAsync(connection, command.TenantId, command.AgentId, token))!;
        }, cancellationToken);

    private static async Task<(string ProviderId, string ProviderValue)> ReadModelSelectionAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string modelId, string effort, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT provider_id,enabled,effort_mappings_json FROM provider_models WHERE tenant_id=$tenant AND id=$id;";
        Add(query, "$tenant", tenant); Add(query, "$id", modelId); await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new AgentSelectionNotFoundException("model");
        if (reader.GetInt32(1) != 1) throw new AgentSelectionConflictException("Selected models must be enabled.");
        var mapping = (JsonSerializer.Deserialize<EffortMap[]>(reader.GetString(2), JsonOptions) ?? []).SingleOrDefault(value => value.Effort == effort) ?? throw new AgentSelectionValidationException("Effort is not mapped for the selected model.");
        return (reader.GetString(0), mapping.ProviderValue);
    }

    private static async Task AppendAuditAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string eventType, string payload, DateTimeOffset at, CancellationToken token)
    {
        long sequence; string previous; await using (var tail = connection.CreateCommand()) { tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(tail, "$tenant", tenant); await using var reader = await tail.ExecuteReaderAsync(token); if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); } else { sequence = 1; previous = AuditLedgerHash.Genesis; } }
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, eventType, payload, at);
        await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at); INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($outbox,$tenant,'audit.eventAppended',$payload,$at);"; Add(command, "$id", UlidValue.New(at).ToString()); Add(command, "$outbox", UlidValue.New(at).ToString()); Add(command, "$tenant", tenant); Add(command, "$sequence", sequence); Add(command, "$previous", previous); Add(command, "$hash", hash); Add(command, "$type", eventType); Add(command, "$payload", payload); Add(command, "$at", Store(at)); await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<AgentRecord?> GetAgentInConnectionAsync(SqliteConnection connection, string tenant, string id, CancellationToken token) { await using var command = connection.CreateCommand(); command.CommandText = $"{AgentSelect} WHERE tenant_id=$tenant AND id=$id AND retired_at IS NULL;"; Add(command, "$tenant", tenant); Add(command, "$id", id); await using var reader = await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadAgent(reader) : null; }
    private sealed record EffortMap(string Effort, string ProviderValue);

    private static AgentDefinitionRecord ReadDefinition(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions)!,
        JsonSerializer.Deserialize<string[]>(reader.GetString(8), JsonOptions)!,
        reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(12), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(13), JsonOptions) ?? [],
        reader.IsDBNull(14) ? null : reader.GetString(14),
        JsonSerializer.Deserialize<string[]>(reader.GetString(15), JsonOptions) ?? [], reader.GetInt32(16),
        reader.GetInt32(17) == 1, reader.IsDBNull(18) ? null : Parse(reader.GetString(18)),
        JsonSerializer.Deserialize<string[]>(reader.GetString(19), JsonOptions) ?? [],
        reader.IsDBNull(20) ? null : reader.GetString(20),
        reader.IsDBNull(21) ? null : reader.GetString(21),
        JsonSerializer.Deserialize<string[]>(reader.GetString(22), JsonOptions) ?? [],
        reader.IsDBNull(23) ? null : reader.GetString(23),
        reader.IsDBNull(24) ? null : reader.GetString(24),
        reader.IsDBNull(25) ? null : reader.GetString(25));

    private static AgentDefinitionVersionRecord ReadDefinitionVersion(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
        JsonSerializer.Deserialize<AgentDefinitionContent>(reader.GetString(3), JsonOptions)
            ?? throw new InvalidDataException("Agent definition snapshot is invalid."),
        reader.GetString(4), Parse(reader.GetString(5)));

    private static AgentRecord ReadAgent(SqliteDataReader reader)
    {
        var lease = reader.IsDBNull(8)
            ? null
            : new AgentLeaseRecord(reader.GetInt64(8), Parse(reader.GetString(9)));
        return new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            lease,
            new AgentMetricsRecord(reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetDecimal(13), reader.GetInt64(14)),
            reader.IsDBNull(15) ? null : Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18), JsonSerializer.Deserialize<string[]>(reader.GetString(19), JsonOptions) ?? [],
            reader.IsDBNull(20) ? null : reader.GetString(20), reader.IsDBNull(21) ? null : Parse(reader.GetString(21)));
    }

    private const string DefinitionSelect = "SELECT id,agent_key,name,role,specialty,description,default_model_id,skill_ids_json,tool_ids_json,persona,mission,operating_principles_json,deliverables_json,quality_criteria_json,communication_style,limitations_json,version,enabled,archived_at,stacks_json,default_effort,preferred_account_id,fallback_model_ids_json,team,actor_critic,risk FROM agent_definitions";
    private const string AgentSelect = "SELECT tenant_id,id,definition_id,project_id,name,state,current_task_id,model_id,lease_fencing_token,lease_expires_at,tasks_completed,tokens_input,tokens_output,cost_usd,uptime_ms,last_heartbeat_at,account_id,effort,provider_effort_value,fallback_model_ids_json,selection_reason,selection_updated_at FROM agents";
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
}
