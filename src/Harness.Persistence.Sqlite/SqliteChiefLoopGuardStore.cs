using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Coordination;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Estado durável das guardas de laço em SQLite. Ver a migration 0098: contadores em memória
/// zeram no reinício, e o reinício é exatamente quando um laço volta a girar do começo.
/// </summary>
public sealed class SqliteChiefLoopGuardStore(SqliteWriteDispatcher dispatcher)
    : IChiefLoopGuardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string SelectEdge =
        "SELECT tenant_id,id,project_id,cause_key,effect_key,relation,occurred_at " +
        "FROM chief_causal_edges";

    private const string SelectInterruption =
        "SELECT tenant_id,id,project_id,demand_id,demand_plan_id,reason_code,detail," +
        "cycle_path_json,occurred_at FROM chief_loop_interruptions";

    public Task<ChiefSelfTriggeredTurnRecord> RecordSelfTriggeredTurnAsync(
        ChiefSelfTriggeredTurnRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var insert = connection.CreateCommand();
                // Idempotente por id: reprocessar a mesma decisão não infla o contador do teto.
                insert.CommandText =
                    "INSERT OR IGNORE INTO chief_self_triggered_turns " +
                    "(tenant_id,id,project_id,demand_id,demand_plan_id,chief_turn_id,cause_key," +
                    "occurred_at) VALUES " +
                    "($tenant,$id,$project,$demand,$plan,$turn,$cause,$at);";
                Add(insert, "$tenant", record.TenantId);
                Add(insert, "$id", record.Id);
                Add(insert, "$project", record.ProjectId);
                Add(insert, "$demand", record.DemandId);
                AddNullable(insert, "$plan", record.DemandPlanId);
                AddNullable(insert, "$turn", record.ChiefTurnId);
                AddNullable(insert, "$cause", record.CauseKey);
                Add(insert, "$at", Store(record.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
                return record;
            },
            cancellationToken);
    }

    public Task<int> CountSelfTriggeredTurnsAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT COUNT(*) FROM chief_self_triggered_turns " +
                    "WHERE tenant_id=$tenant AND demand_id=$demand;";
                Add(query, "$tenant", tenantId);
                Add(query, "$demand", demandId);
                return Convert.ToInt32(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            },
            cancellationToken);

    public Task<IReadOnlyList<DateTimeOffset>> ListSelfTriggeredTurnsSinceAsync(
        string tenantId, string demandId, DateTimeOffset since,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT occurred_at FROM chief_self_triggered_turns " +
                    "WHERE tenant_id=$tenant AND demand_id=$demand AND occurred_at>$since " +
                    "ORDER BY occurred_at;";
                Add(query, "$tenant", tenantId);
                Add(query, "$demand", demandId);
                Add(query, "$since", Store(since));
                var rows = new List<DateTimeOffset>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
                }

                return (IReadOnlyList<DateTimeOffset>)rows;
            },
            cancellationToken);

    public Task AddCausalEdgeAsync(
        ChiefCausalEdgeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var insert = connection.CreateCommand();
                // A mesma causa gerando o mesmo efeito pela mesma relação é UM fato: duplicar
                // arestas faria o detector enxergar caminhos que não existem.
                insert.CommandText =
                    "INSERT OR IGNORE INTO chief_causal_edges " +
                    "(tenant_id,id,project_id,cause_key,effect_key,relation,occurred_at) VALUES " +
                    "($tenant,$id,$project,$cause,$effect,$relation,$at);";
                Add(insert, "$tenant", record.TenantId);
                Add(insert, "$id", record.Id);
                Add(insert, "$project", record.ProjectId);
                Add(insert, "$cause", record.CauseKey);
                Add(insert, "$effect", record.EffectKey);
                Add(insert, "$relation", record.Relation);
                Add(insert, "$at", Store(record.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<ChiefCausalEdgeRecord>> ListCausalEdgesAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{SelectEdge} WHERE tenant_id=$tenant AND project_id=$project " +
                    "ORDER BY occurred_at,id;";
                Add(query, "$tenant", tenantId);
                Add(query, "$project", projectId);
                var rows = new List<ChiefCausalEdgeRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(new ChiefCausalEdgeRecord(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5),
                        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
                }

                return (IReadOnlyList<ChiefCausalEdgeRecord>)rows;
            },
            cancellationToken);

    public Task RecordInterruptionAsync(
        ChiefLoopInterruptionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT OR IGNORE INTO chief_loop_interruptions " +
                    "(tenant_id,id,project_id,demand_id,demand_plan_id,reason_code,detail," +
                    "cycle_path_json,occurred_at) VALUES " +
                    "($tenant,$id,$project,$demand,$plan,$reason,$detail,$cycle,$at);";
                Add(insert, "$tenant", record.TenantId);
                Add(insert, "$id", record.Id);
                Add(insert, "$project", record.ProjectId);
                Add(insert, "$demand", record.DemandId);
                AddNullable(insert, "$plan", record.DemandPlanId);
                Add(insert, "$reason", record.ReasonCode);
                AddNullable(insert, "$detail", record.Detail);
                Add(insert, "$cycle", JsonSerializer.Serialize(record.CyclePath ?? [], JsonOptions));
                Add(insert, "$at", Store(record.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<ChiefLoopInterruptionRecord>> ListInterruptionsAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{SelectInterruption} WHERE tenant_id=$tenant AND demand_id=$demand " +
                    "ORDER BY occurred_at,id;";
                Add(query, "$tenant", tenantId);
                Add(query, "$demand", demandId);
                var rows = new List<ChiefLoopInterruptionRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(new ChiefLoopInterruptionRecord(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? [],
                        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
                }

                return (IReadOnlyList<ChiefLoopInterruptionRecord>)rows;
            },
            cancellationToken);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
