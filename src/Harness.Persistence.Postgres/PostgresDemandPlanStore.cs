using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL do artefato de PLANO por demanda (PLAT-01). Geração idempotente por
/// (tenant, demand) via <c>ON CONFLICT DO NOTHING</c>; materialização idempotente via UPDATE
/// condicional ao status 'proposed'.
/// </summary>
public sealed class PostgresDemandPlanStore(NpgsqlDataSource dataSource) : IDemandPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<DemandPlanSaveResult> SaveProposedAsync(
        DemandPlanSaveCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO harness.demand_plans
                (id,tenant_id,project_id,demand_id,feature_id,status,cards_json,correlation_id,created_at,materialized_at)
            VALUES ($1,$2,$3,$4,$5,'proposed',$6,$7,$8,NULL)
            ON CONFLICT (tenant_id,demand_id) DO NOTHING;
            """;
        insert.Parameters.Add(Text(command.Id));
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Text(command.DemandId));
        insert.Parameters.Add(Text(command.FeatureId));
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(command.Cards, JsonOptions)));
        insert.Parameters.Add(Text(command.CorrelationId));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        var created = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        var record = await ReadAsync(connection, tx, command.TenantId, "demand_id", command.DemandId, cancellationToken)
            ?? throw new InvalidOperationException("The persisted demand plan could not be read back.");
        await tx.CommitAsync(cancellationToken);
        return new DemandPlanSaveResult(record, created);
    }

    public async Task<DemandPlanRecord?> GetByDemandAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, "demand_id", demandId, cancellationToken);
    }

    public async Task<DemandPlanRecord?> GetAsync(
        string tenantId, string planId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, "id", planId, cancellationToken);
    }

    public async Task<bool> TryMarkMaterializedAsync(
        string tenantId, string planId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            "UPDATE harness.demand_plans SET status='materialized',materialized_at=$1 " +
            "WHERE tenant_id=$2 AND id=$3 AND status='proposed';";
        q.Parameters.Add(Timestamp(occurredAt));
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(planId));
        return await q.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<DemandPlanRecord?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string tenantId, string column,
        string value, CancellationToken cancellationToken)
    {
        await using var q = connection.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT tenant_id,id,project_id,demand_id,feature_id,status,cards_json,correlation_id,created_at,materialized_at " +
            $"FROM harness.demand_plans WHERE tenant_id=$1 AND {column}=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(value));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? Read(r) : null;
    }

    private static DemandPlanRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
        r.GetString(3).TrimEnd(), r.GetString(4), r.GetString(5),
        JsonSerializer.Deserialize<IReadOnlyList<DemandPlanCard>>(r.GetString(6), JsonOptions) ?? [],
        r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));

    private static void Validate(DemandPlanSaveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DemandId);
        ArgumentNullException.ThrowIfNull(command.Cards);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
