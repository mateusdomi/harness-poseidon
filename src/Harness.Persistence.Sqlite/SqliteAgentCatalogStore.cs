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
            await AppendAuditAsync(connection, tx, command.TenantId, payload, command.OccurredAt, token);
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

    private static async Task AppendAuditAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string payload, DateTimeOffset at, CancellationToken token)
    {
        long sequence; string previous; await using (var tail = connection.CreateCommand()) { tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(tail, "$tenant", tenant); await using var reader = await tail.ExecuteReaderAsync(token); if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); } else { sequence = 1; previous = AuditLedgerHash.Genesis; } }
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, "agent.selectionUpdated", payload, at);
        await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,'agent.selectionUpdated',$payload,$at); INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($outbox,$tenant,'audit.eventAppended',$payload,$at);"; Add(command, "$id", UlidValue.New(at).ToString()); Add(command, "$outbox", UlidValue.New(at).ToString()); Add(command, "$tenant", tenant); Add(command, "$sequence", sequence); Add(command, "$previous", previous); Add(command, "$hash", hash); Add(command, "$payload", payload); Add(command, "$at", Store(at)); await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<AgentRecord?> GetAgentInConnectionAsync(SqliteConnection connection, string tenant, string id, CancellationToken token) { await using var command = connection.CreateCommand(); command.CommandText = $"{AgentSelect} WHERE tenant_id=$tenant AND id=$id AND retired_at IS NULL;"; Add(command, "$tenant", tenant); Add(command, "$id", id); await using var reader = await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadAgent(reader) : null; }
    private sealed record EffortMap(string Effort, string ProviderValue);

    private static AgentDefinitionRecord ReadDefinition(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions)!,
        JsonSerializer.Deserialize<string[]>(reader.GetString(8), JsonOptions)!);

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

    private const string DefinitionSelect = "SELECT id,agent_key,name,role,specialty,description,default_model_id,skill_ids_json,tool_ids_json FROM agent_definitions";
    private const string AgentSelect = "SELECT tenant_id,id,definition_id,project_id,name,state,current_task_id,model_id,lease_fencing_token,lease_expires_at,tasks_completed,tokens_input,tokens_output,cost_usd,uptime_ms,last_heartbeat_at,account_id,effort,provider_effort_value,fallback_model_ids_json,selection_reason,selection_updated_at FROM agents";
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
}
