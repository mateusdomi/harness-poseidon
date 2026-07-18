using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Tools;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteToolCatalogStore(SqliteWriteDispatcher dispatcher) : IToolCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<SkillCatalogRecord>> ListSkillsAsync(string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(SkillSelect, afterId, limit, ReadSkill, cancellationToken);
    public Task<SkillCatalogRecord?> GetSkillAsync(string id, CancellationToken cancellationToken = default) =>
        GetAsync(SkillSelect, id, ReadSkill, cancellationToken);
    public Task<IReadOnlyList<ToolCatalogRecord>> ListToolsAsync(string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(ToolSelect, afterId, limit, ReadTool, cancellationToken);
    public Task<ToolCatalogRecord?> GetToolAsync(string id, CancellationToken cancellationToken = default) =>
        GetAsync(ToolSelect, id, ReadTool, cancellationToken);
    public Task<IReadOnlyList<PluginCatalogRecord>> ListPluginsAsync(string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(PluginSelect, afterId, limit, ReadPlugin, cancellationToken);
    public Task<PluginCatalogRecord?> GetPluginAsync(string id, CancellationToken cancellationToken = default) =>
        GetAsync(PluginSelect, id, ReadPlugin, cancellationToken);
    public Task<IReadOnlyList<McpServerCatalogRecord>> ListMcpServersAsync(string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(McpSelect, afterId, limit, ReadMcp, cancellationToken);
    public Task<McpServerCatalogRecord?> GetMcpServerAsync(string id, CancellationToken cancellationToken = default) =>
        GetAsync(McpSelect, id, ReadMcp, cancellationToken);

    public Task<ComponentCatalogRecord> UpdateAsync(ComponentCatalogUpdateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => UpdateCoreAsync(connection, command, token), cancellationToken);

    private Task<IReadOnlyList<T>> ListAsync<T>(string select, string? afterId, int limit,
        Func<SqliteDataReader, T> read, CancellationToken cancellationToken) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<T>>(async (connection, token) =>
        {
            var values = new List<T>(); await using var query = connection.CreateCommand();
            query.CommandText = $"{select} WHERE ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            AddNullable(query, "$after", afterId); Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(read(reader));
            return values;
        }, cancellationToken);

    private Task<T?> GetAsync<T>(string select, string id, Func<SqliteDataReader, T> read,
        CancellationToken cancellationToken) where T : class =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand(); query.CommandText = $"{select} WHERE id=$id;";
            Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? read(reader) : null;
        }, cancellationToken);

    private static async Task<ComponentCatalogRecord> UpdateCoreAsync(
        SqliteConnection connection, ComponentCatalogUpdateCommand command, CancellationToken token)
    {
        if (command.State is not null && command.State is not ("enabled" or "disabled" or "error"))
            throw new ToolCatalogValidationException("Component state is invalid.");
        if (command.Resource != "mcp-servers" && command.Endpoint is not null)
            throw new ToolCatalogValidationException("Only MCP servers expose an endpoint.");
        if (command.Endpoint is not null && (string.IsNullOrWhiteSpace(command.Endpoint) || command.Endpoint.Length > 2048))
            throw new ToolCatalogValidationException("MCP endpoint must contain between 1 and 2048 characters.");
        if (command.State is null && command.Endpoint is null)
            throw new ToolCatalogValidationException("At least one mutable field is required.");

        var table = command.Resource switch
        {
            "skills" => "skills",
            "tools" => "tools",
            "plugins" => "plugins",
            "mcp-servers" => "mcp_servers",
            _ => throw new ToolCatalogValidationException("Component resource is invalid."),
        };
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        string previousState;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = tx; current.CommandText = $"SELECT state FROM {table} WHERE id=$id;";
            Add(current, "$id", command.Id); previousState = await current.ExecuteScalarAsync(token) as string
                ?? throw new ToolCatalogNotFoundException(command.Resource);
        }
        var nextState = command.State ?? previousState;
        await using (var mutation = connection.CreateCommand())
        {
            mutation.Transaction = tx;
            mutation.CommandText = table == "mcp_servers"
                ? "UPDATE mcp_servers SET state=$state,endpoint=COALESCE($endpoint,endpoint) WHERE id=$id;"
                : $"UPDATE {table} SET state=$state WHERE id=$id;";
            Add(mutation, "$state", nextState); Add(mutation, "$id", command.Id);
            if (table == "mcp_servers") AddNullable(mutation, "$endpoint", command.Endpoint?.Trim());
            await mutation.ExecuteNonQueryAsync(token);
        }
        if (command.Resource == "tools" && previousState != nextState)
        {
            var toolPayload = JsonSerializer.Serialize(new { toolId = command.Id, from = previousState, to = nextState }, JsonOptions);
            await AppendOutboxAsync(connection, tx, command.TenantId, "tool.statusChanged", toolPayload, command.OccurredAt, token);
        }
        var auditId = UlidValue.New(command.OccurredAt).ToString();
        var detail = $"{command.Resource}/{command.Id} updated: state {previousState}→{nextState}.";
        var auditPayload = JsonSerializer.Serialize(new
        {
            auditEvent = new { id = auditId, actorKind = "user", actorId = command.ActorProfileId, action = "component.updated", targetType = command.Resource, targetId = command.Id, detail, occurredAt = command.OccurredAt },
        }, JsonOptions);
        await AppendLedgerAsync(connection, tx, command.TenantId, "component.updated", auditPayload, command.OccurredAt, token);
        await AppendOutboxAsync(connection, tx, command.TenantId, "audit.eventAppended", auditPayload, command.OccurredAt, token);
        await tx.CommitAsync(token);
        return command.Resource switch
        {
            "skills" => (ComponentCatalogRecord)(await ReadOneAsync(connection, SkillSelect, command.Id, ReadSkill, token))!,
            "tools" => (await ReadOneAsync(connection, ToolSelect, command.Id, ReadTool, token))!,
            "plugins" => (await ReadOneAsync(connection, PluginSelect, command.Id, ReadPlugin, token))!,
            _ => (await ReadOneAsync(connection, McpSelect, command.Id, ReadMcp, token))!,
        };
    }

    private static async Task<T?> ReadOneAsync<T>(SqliteConnection connection, string select, string id,
        Func<SqliteDataReader, T> read, CancellationToken token) where T : class
    {
        await using var query = connection.CreateCommand(); query.CommandText = $"{select} WHERE id=$id;";
        Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? read(reader) : null;
    }

    private static async Task AppendLedgerAsync(SqliteConnection connection, SqliteTransaction tx,
        string tenant, string type, string payload, DateTimeOffset at, CancellationToken token)
    {
        long sequence; string previous;
        await using (var tail = connection.CreateCommand())
        {
            tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            Add(tail, "$tenant", tenant); await using var reader = await tail.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); }
            else { sequence = 1; previous = AuditLedgerHash.Genesis; }
        }
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
        await using var insert = connection.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);";
        Add(insert, "$id", UlidValue.New(at).ToString()); Add(insert, "$tenant", tenant); Add(insert, "$sequence", sequence);
        Add(insert, "$previous", previous); Add(insert, "$hash", hash); Add(insert, "$type", type);
        Add(insert, "$payload", payload); Add(insert, "$at", Store(at)); await insert.ExecuteNonQueryAsync(token);
    }

    private static async Task AppendOutboxAsync(SqliteConnection connection, SqliteTransaction tx,
        string tenant, string type, string payload, DateTimeOffset at, CancellationToken token)
    {
        await using var insert = connection.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,$type,$payload,$at);";
        Add(insert, "$id", UlidValue.New(at).ToString()); Add(insert, "$tenant", tenant); Add(insert, "$type", type);
        Add(insert, "$payload", payload); Add(insert, "$at", Store(at)); await insert.ExecuteNonQueryAsync(token);
    }

    private static SkillCatalogRecord ReadSkill(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    private static ToolCatalogRecord ReadTool(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    private static PluginCatalogRecord ReadPlugin(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions) ?? []);
    private static McpServerCatalogRecord ReadMcp(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5));

    private const string SkillSelect = "SELECT id,skill_key,name,description,version,state FROM skills";
    private const string ToolSelect = "SELECT id,tool_key,name,description,kind,state FROM tools";
    private const string PluginSelect = "SELECT p.id,p.plugin_key,p.name,p.version,p.description,p.state,COALESCE((SELECT json_group_array(tool_id) FROM plugin_tools pt WHERE pt.plugin_id=p.id),'[]') FROM plugins p";
    private const string McpSelect = "SELECT id,name,transport,endpoint,state,tool_count FROM mcp_servers";
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
