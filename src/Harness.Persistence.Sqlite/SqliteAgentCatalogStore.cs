using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
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
            reader.IsDBNull(15) ? null : Parse(reader.GetString(15)));
    }

    private const string DefinitionSelect = "SELECT id,agent_key,name,role,specialty,description,default_model_id,skill_ids_json,tool_ids_json FROM agent_definitions";
    private const string AgentSelect = "SELECT tenant_id,id,definition_id,project_id,name,state,current_task_id,model_id,lease_fencing_token,lease_expires_at,tasks_completed,tokens_input,tokens_output,cost_usd,uptime_ms,last_heartbeat_at FROM agents";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
}
