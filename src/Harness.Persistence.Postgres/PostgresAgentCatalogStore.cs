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
        "skill_ids_json::text,tool_ids_json::text FROM harness.agent_definitions";

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
            Deserialize(reader.GetString(8)));

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
    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = value };
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params NpgsqlParameter[] parameters) { await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(token); }

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };
}
