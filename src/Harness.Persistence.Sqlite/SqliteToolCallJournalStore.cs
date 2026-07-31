using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Tools;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Diário durável de chamadas de ferramenta (Fase 0B2). Guarda TODA decisão — inclusive as
/// negativas, que são as que explicam um incidente — e serve de fonte da idempotência.
/// </summary>
public sealed class SqliteToolCallJournalStore(SqliteWriteDispatcher dispatcher) : IToolCallJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ToolCallJournalEntry?> FindAllowedAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            // Só uma chamada PERMITIDA é replayable: repetir uma negativa precisa ser avaliada de
            // novo, porque a política pode ter mudado — e negar de novo é barato.
            q.CommandText =
                "SELECT tenant_id,idempotency_key,project_id,card_id,attempt_id,agent_id,profile," +
                "tool_id,fencing_token,paths_json,network_enabled,mutating,allowed,code,detail," +
                "output,output_truncated,exit_code,occurred_at FROM tool_call_journal " +
                "WHERE tenant_id=$tenant AND idempotency_key=$key AND allowed=1;";
            Add(q, "$tenant", tenantId);
            Add(q, "$key", idempotencyKey);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t)
                ? new ToolCallJournalEntry(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8),
                    JsonSerializer.Deserialize<string[]>(r.GetString(9), JsonOptions) ?? [],
                    r.GetInt32(10) == 1, r.GetInt32(11) == 1, r.GetInt32(12) == 1, r.GetString(13),
                    r.GetString(14), r.GetString(15), r.GetInt32(16) == 1, r.GetInt32(17),
                    DateTimeOffset.Parse(r.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                : null;
        }, cancellationToken);

    public Task RecordAsync(
        ToolCallJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _dispatcher.ExecuteAsync<object?>(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                """
                INSERT INTO tool_call_journal
                    (tenant_id,idempotency_key,project_id,card_id,attempt_id,agent_id,profile,
                     tool_id,fencing_token,paths_json,network_enabled,mutating,allowed,code,detail,
                     output,output_truncated,exit_code,occurred_at)
                VALUES ($tenant,$key,$project,$card,$attempt,$agent,$profile,$tool,$fencing,$paths,
                        $network,$mutating,$allowed,$code,$detail,$output,$truncated,$exit,$at)
                ON CONFLICT(tenant_id,idempotency_key) DO NOTHING;
                """;
            Add(q, "$tenant", entry.TenantId);
            Add(q, "$key", entry.IdempotencyKey);
            Add(q, "$project", entry.ProjectId);
            Add(q, "$card", entry.CardId);
            Add(q, "$attempt", entry.AttemptId);
            Add(q, "$agent", entry.AgentId);
            Add(q, "$profile", entry.Profile);
            Add(q, "$tool", entry.ToolId);
            Add(q, "$fencing", entry.FencingToken);
            Add(q, "$paths", JsonSerializer.Serialize(entry.Paths, JsonOptions));
            Add(q, "$network", entry.NetworkEnabled ? 1 : 0);
            Add(q, "$mutating", entry.Mutating ? 1 : 0);
            Add(q, "$allowed", entry.Allowed ? 1 : 0);
            Add(q, "$code", entry.Code);
            Add(q, "$detail", entry.Detail);
            Add(q, "$output", entry.Output);
            Add(q, "$truncated", entry.OutputTruncated ? 1 : 0);
            Add(q, "$exit", entry.ExitCode);
            Add(q, "$at", entry.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await q.ExecuteNonQueryAsync(t);
            return null;
        }, cancellationToken);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
