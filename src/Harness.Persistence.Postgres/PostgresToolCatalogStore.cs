using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Tools;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed class PostgresToolCatalogStore(NpgsqlDataSource dataSource) : IToolCatalogStore
{
    private const string SkillSelect =
        "SELECT id,skill_key,name,description,version,state FROM harness.skills";

    private const string ToolSelect =
        "SELECT id,tool_key,name,description,kind,state FROM harness.tools";

    private const string PluginSelect =
        "SELECT p.id,p.plugin_key,p.name,p.version,p.description,p.state," +
        "COALESCE((SELECT jsonb_agg(pt.tool_id::text ORDER BY pt.tool_id) " +
        "FROM harness.plugin_tools pt WHERE pt.plugin_id=p.id),'[]'::jsonb)::text " +
        "FROM harness.plugins p";

    private const string McpSelect =
        "SELECT id,name,transport,endpoint,state,tool_count FROM harness.mcp_servers";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<IReadOnlyList<SkillCatalogRecord>> ListSkillsAsync(
        string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(SkillSelect, afterId, limit, ReadSkill, cancellationToken);

    public Task<SkillCatalogRecord?> GetSkillAsync(
        string id, CancellationToken cancellationToken = default) =>
        GetAsync(SkillSelect, id, ReadSkill, cancellationToken);

    public Task<IReadOnlyList<ToolCatalogRecord>> ListToolsAsync(
        string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(ToolSelect, afterId, limit, ReadTool, cancellationToken);

    public Task<ToolCatalogRecord?> GetToolAsync(
        string id, CancellationToken cancellationToken = default) =>
        GetAsync(ToolSelect, id, ReadTool, cancellationToken);

    public Task<IReadOnlyList<PluginCatalogRecord>> ListPluginsAsync(
        string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(PluginSelect, afterId, limit, ReadPlugin, cancellationToken);

    public Task<PluginCatalogRecord?> GetPluginAsync(
        string id, CancellationToken cancellationToken = default) =>
        GetAsync(PluginSelect, id, ReadPlugin, cancellationToken);

    public Task<IReadOnlyList<McpServerCatalogRecord>> ListMcpServersAsync(
        string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(McpSelect, afterId, limit, ReadMcp, cancellationToken);

    public Task<McpServerCatalogRecord?> GetMcpServerAsync(
        string id, CancellationToken cancellationToken = default) =>
        GetAsync(McpSelect, id, ReadMcp, cancellationToken);

    public Task<ComponentCatalogRecord> UpdateAsync(
        ComponentCatalogUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateCoreAsync(command, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string select,
        string? afterId,
        int limit,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        var values = new List<T>();
        await using var command = _dataSource.CreateCommand(
            $"{select} WHERE ($1::text IS NULL OR id>$1) ORDER BY id LIMIT $2;");
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(read(reader));
        }

        return values;
    }

    private async Task<T?> GetAsync<T>(
        string select,
        string id,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var command = _dataSource.CreateCommand($"{select} WHERE id=$1;");
        command.Parameters.Add(Text(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : null;
    }

    private async Task<ComponentCatalogRecord> UpdateCoreAsync(
        ComponentCatalogUpdateCommand command,
        CancellationToken cancellationToken)
    {
        if (command.State is not null && command.State is not ("enabled" or "disabled" or "error"))
        {
            throw new ToolCatalogValidationException("Component state is invalid.");
        }

        if (command.Resource != "mcp-servers" && command.Endpoint is not null)
        {
            throw new ToolCatalogValidationException("Only MCP servers expose an endpoint.");
        }

        if (command.Endpoint is not null &&
            (string.IsNullOrWhiteSpace(command.Endpoint) || command.Endpoint.Length > 2048))
        {
            throw new ToolCatalogValidationException(
                "MCP endpoint must contain between 1 and 2048 characters.");
        }

        if (command.State is null && command.Endpoint is null)
        {
            throw new ToolCatalogValidationException("At least one mutable field is required.");
        }

        var table = command.Resource switch
        {
            "skills" => "skills",
            "tools" => "tools",
            "plugins" => "plugins",
            "mcp-servers" => "mcp_servers",
            _ => throw new ToolCatalogValidationException("Component resource is invalid."),
        };
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
                cancellationToken,
                Text($"audit-ledger:{command.TenantId}"));
            string previousState;
            await using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = $"SELECT state FROM harness.{table} WHERE id=$1;";
                current.Parameters.Add(Text(command.Id));
                previousState = await current.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new ToolCatalogNotFoundException(command.Resource);
            }

            var nextState = command.State ?? previousState;
            if (table == "mcp_servers")
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE harness.mcp_servers SET state=$1,endpoint=COALESCE($2,endpoint) WHERE id=$3;",
                    cancellationToken,
                    Text(nextState),
                    NullableText(command.Endpoint?.Trim()),
                    Text(command.Id));
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"UPDATE harness.{table} SET state=$1 WHERE id=$2;",
                    cancellationToken,
                    Text(nextState),
                    Text(command.Id));
            }

            if (command.Resource == "tools" && previousState != nextState)
            {
                var toolPayload = JsonSerializer.Serialize(
                    new { toolId = command.Id, from = previousState, to = nextState },
                    JsonOptions);
                await AppendOutboxAsync(
                    connection,
                    transaction,
                    command.TenantId,
                    "tool.statusChanged",
                    toolPayload,
                    command.OccurredAt,
                    cancellationToken);
            }

            var auditId = UlidValue.New(command.OccurredAt).ToString();
            var detail = $"{command.Resource}/{command.Id} updated: state {previousState}→{nextState}.";
            var auditPayload = JsonSerializer.Serialize(
                new
                {
                    auditEvent = new
                    {
                        id = auditId,
                        actorKind = "user",
                        actorId = command.ActorProfileId,
                        action = "component.updated",
                        targetType = command.Resource,
                        targetId = command.Id,
                        detail,
                        occurredAt = command.OccurredAt,
                    },
                },
                JsonOptions);
            await AppendLedgerAsync(
                connection,
                transaction,
                command.TenantId,
                "component.updated",
                auditPayload,
                command.OccurredAt,
                cancellationToken);
            await AppendOutboxAsync(
                connection,
                transaction,
                command.TenantId,
                "audit.eventAppended",
                auditPayload,
                command.OccurredAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return command.Resource switch
        {
            "skills" => (ComponentCatalogRecord)(await ReadOneAsync(
                connection, SkillSelect, command.Id, ReadSkill, cancellationToken))!,
            "tools" => (await ReadOneAsync(
                connection, ToolSelect, command.Id, ReadTool, cancellationToken))!,
            "plugins" => (await ReadOneAsync(
                connection, PluginSelect, command.Id, ReadPlugin, cancellationToken))!,
            _ => (await ReadOneAsync(
                connection, McpSelect, command.Id, ReadMcp, cancellationToken))!,
        };
    }

    private static async Task<T?> ReadOneAsync<T>(
        NpgsqlConnection connection,
        string select,
        string id,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{select} WHERE id=$1;";
        command.Parameters.Add(Text(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : null;
    }

    private static async Task AppendLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        var (sequence, previous) = await ReadLedgerTailAsync(
            connection, transaction, tenantId, cancellationToken);
        var hash = AuditLedgerHash.Compute(
            previous, tenantId, sequence, eventType, payload, occurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Bigint(sequence),
            Text(previous),
            Text(hash),
            Text(eventType),
            Json(payload),
            Timestamp(occurredAt));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Text(eventType),
            Json(PersistenceSanitizer.SanitizeJson(payload)),
            Timestamp(occurredAt));

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SkillCatalogRecord ReadSkill(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));

    private static ToolCatalogRecord ReadTool(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));

    private static PluginCatalogRecord ReadPlugin(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions) ?? []);

    private static McpServerCatalogRecord ReadMcp(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5));

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
