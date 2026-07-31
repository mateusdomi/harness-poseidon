using System.Text.Json;
using Harness.Persistence.Abstractions.Tools;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Diário durável de chamadas de ferramenta (Fase 0B2). Paridade semântica com o SQLite: guarda
/// TODA decisão — inclusive as negativas, que explicam um incidente — e é a fonte da idempotência.
/// </summary>
public sealed class PostgresToolCallJournalStore(NpgsqlDataSource dataSource) : IToolCallJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<ToolCallJournalEntry?> FindAllowedAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        // Só uma chamada PERMITIDA é replayable: repetir uma negativa precisa ser avaliada de novo,
        // porque a política pode ter mudado — e negar de novo é barato.
        q.CommandText =
            "SELECT tenant_id,idempotency_key,project_id,card_id,attempt_id,agent_id,profile," +
            "tool_id,fencing_token,paths_json::text,network_enabled,mutating,allowed,code,detail," +
            "output,output_truncated,exit_code,occurred_at FROM harness.tool_call_journal " +
            "WHERE tenant_id=$1 AND idempotency_key=$2 AND allowed=true;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(idempotencyKey));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new ToolCallJournalEntry(
                r.GetString(0).TrimEnd(), r.GetString(1), r.GetString(2).TrimEnd(),
                r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetInt64(8),
                JsonSerializer.Deserialize<string[]>(r.GetString(9), JsonOptions) ?? [],
                r.GetBoolean(10), r.GetBoolean(11), r.GetBoolean(12), r.GetString(13),
                r.GetString(14), r.GetString(15), r.GetBoolean(16), r.GetInt32(17),
                r.GetFieldValue<DateTimeOffset>(18))
            : null;
    }

    public async Task RecordAsync(
        ToolCallJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            """
            INSERT INTO harness.tool_call_journal
                (tenant_id,idempotency_key,project_id,card_id,attempt_id,agent_id,profile,
                 tool_id,fencing_token,paths_json,network_enabled,mutating,allowed,code,detail,
                 output,output_truncated,exit_code,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19)
            ON CONFLICT (tenant_id,idempotency_key) DO NOTHING;
            """;
        q.Parameters.Add(Text(entry.TenantId));
        q.Parameters.Add(Text(entry.IdempotencyKey));
        q.Parameters.Add(Text(entry.ProjectId));
        q.Parameters.Add(Text(entry.CardId));
        q.Parameters.Add(Text(entry.AttemptId));
        q.Parameters.Add(Text(entry.AgentId));
        q.Parameters.Add(Text(entry.Profile));
        q.Parameters.Add(Text(entry.ToolId));
        q.Parameters.Add(Bigint(entry.FencingToken));
        q.Parameters.Add(Jsonb(JsonSerializer.Serialize(entry.Paths, JsonOptions)));
        q.Parameters.Add(Boolean(entry.NetworkEnabled));
        q.Parameters.Add(Boolean(entry.Mutating));
        q.Parameters.Add(Boolean(entry.Allowed));
        q.Parameters.Add(Text(entry.Code));
        q.Parameters.Add(Text(entry.Detail));
        q.Parameters.Add(Text(entry.Output));
        q.Parameters.Add(Boolean(entry.OutputTruncated));
        q.Parameters.Add(Integer(entry.ExitCode));
        q.Parameters.Add(Timestamp(entry.OccurredAt));
        await q.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
