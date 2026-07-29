using System.Text.Json;
using Harness.Persistence.Abstractions.Coordination;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>Guardas de laço em PostgreSQL. Paridade estrita com o SQLite.</summary>
public sealed class PostgresChiefLoopGuardStore(NpgsqlDataSource dataSource)
    : IChiefLoopGuardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string SelectEdge =
        "SELECT tenant_id,id,project_id,cause_key,effect_key,relation,occurred_at " +
        "FROM harness.chief_causal_edges";

    private const string SelectInterruption =
        "SELECT tenant_id,id,project_id,demand_id,demand_plan_id,reason_code,detail," +
        "cycle_path_json::text,occurred_at FROM harness.chief_loop_interruptions";

    public async Task<ChiefSelfTriggeredTurnRecord> RecordSelfTriggeredTurnAsync(
        ChiefSelfTriggeredTurnRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        // Idempotente por id: reprocessar a mesma decisão não infla o contador do teto.
        insert.CommandText = """
            INSERT INTO harness.chief_self_triggered_turns
            (tenant_id,id,project_id,demand_id,demand_plan_id,chief_turn_id,cause_key,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT (tenant_id,id) DO NOTHING;
            """;
        insert.Parameters.Add(Text(record.TenantId));
        insert.Parameters.Add(Text(record.Id));
        insert.Parameters.Add(Text(record.ProjectId));
        insert.Parameters.Add(Text(record.DemandId));
        insert.Parameters.Add(Nullable(record.DemandPlanId));
        insert.Parameters.Add(Nullable(record.ChiefTurnId));
        insert.Parameters.Add(Nullable(record.CauseKey));
        insert.Parameters.Add(Timestamp(record.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<int> CountSelfTriggeredTurnsAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT COUNT(*) FROM harness.chief_self_triggered_turns " +
            "WHERE tenant_id=$1 AND demand_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(demandId));
        return (int)(long)(await query.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<DateTimeOffset>> ListSelfTriggeredTurnsSinceAsync(
        string tenantId, string demandId, DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT occurred_at FROM harness.chief_self_triggered_turns " +
            "WHERE tenant_id=$1 AND demand_id=$2 AND occurred_at>$3 ORDER BY occurred_at;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(demandId));
        query.Parameters.Add(Timestamp(since));
        var rows = new List<DateTimeOffset>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetFieldValue<DateTimeOffset>(0));
        }

        return rows;
    }

    public async Task AddCausalEdgeAsync(
        ChiefCausalEdgeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        // A mesma causa gerando o mesmo efeito pela mesma relação é UM fato.
        insert.CommandText = """
            INSERT INTO harness.chief_causal_edges
            (tenant_id,id,project_id,cause_key,effect_key,relation,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT (tenant_id,cause_key,effect_key,relation) DO NOTHING;
            """;
        insert.Parameters.Add(Text(record.TenantId));
        insert.Parameters.Add(Text(record.Id));
        insert.Parameters.Add(Text(record.ProjectId));
        insert.Parameters.Add(Text(record.CauseKey));
        insert.Parameters.Add(Text(record.EffectKey));
        insert.Parameters.Add(Text(record.Relation));
        insert.Parameters.Add(Timestamp(record.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChiefCausalEdgeRecord>> ListCausalEdgesAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{SelectEdge} WHERE tenant_id=$1 AND project_id=$2 ORDER BY occurred_at,id;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        var rows = new List<ChiefCausalEdgeRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ChiefCausalEdgeRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    public async Task RecordInterruptionAsync(
        ChiefLoopInterruptionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO harness.chief_loop_interruptions
            (tenant_id,id,project_id,demand_id,demand_plan_id,reason_code,detail,cycle_path_json,
             occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            ON CONFLICT (tenant_id,id) DO NOTHING;
            """;
        insert.Parameters.Add(Text(record.TenantId));
        insert.Parameters.Add(Text(record.Id));
        insert.Parameters.Add(Text(record.ProjectId));
        insert.Parameters.Add(Text(record.DemandId));
        insert.Parameters.Add(Nullable(record.DemandPlanId));
        insert.Parameters.Add(Text(record.ReasonCode));
        insert.Parameters.Add(Nullable(record.Detail));
        insert.Parameters.Add(Json(JsonSerializer.Serialize(record.CyclePath ?? [], JsonOptions)));
        insert.Parameters.Add(Timestamp(record.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChiefLoopInterruptionRecord>> ListInterruptionsAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{SelectInterruption} WHERE tenant_id=$1 AND demand_id=$2 ORDER BY occurred_at,id;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(demandId));
        var rows = new List<ChiefLoopInterruptionRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ChiefLoopInterruptionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? [],
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return rows;
    }

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value };

    private static NpgsqlParameter Json(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };
}
