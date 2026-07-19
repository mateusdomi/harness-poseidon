using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
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
        "cost_usd,uptime_ms,last_heartbeat_at FROM harness.agents";

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
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15));
    }

    private static string[] Deserialize(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("Persisted agent-definition JSON cannot be null.");

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };
}
