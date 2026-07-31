using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL do compromisso de materialização (Fase 0A1). Paridade semântica exata
/// com o SQLite: toda transição é condicional ao estado atual e ao par (dono, tentativa).
/// </summary>
public sealed class PostgresPlanMaterializationStore(NpgsqlDataSource dataSource)
    : IPlanMaterializationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<PlanMaterializationRecord?> GetAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, demandId, cancellationToken);
    }

    public async Task<PlanMaterializationRecord> RequestAsync(
        PlanMaterializationRequestCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DemandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentNullException.ThrowIfNull(command.Request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO harness.demand_materializations
                (tenant_id,demand_id,project_id,turn_id,plan_id,status,attempt_count,
                 expected_cards,materialized_cards,request_json,owner_id,last_error,
                 requested_at,updated_at,completed_at)
            VALUES ($1,$2,$3,$4,NULL,'pending',0,NULL,NULL,$5,NULL,NULL,$6,$6,NULL)
            ON CONFLICT (tenant_id,demand_id) DO NOTHING;
            """;
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.DemandId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(NullableText(command.TurnId));
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(command.Request, JsonOptions)));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        var record = await ReadAsync(
                connection, tx, command.TenantId, command.DemandId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The persisted plan materialization could not be read back.");
        await tx.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<PlanMaterializationRecord?> TryBeginAsync(
        PlanMaterializationBeginCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OwnerId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText =
            """
            UPDATE harness.demand_materializations
            SET status='processing',owner_id=$1,attempt_count=attempt_count+1,updated_at=$2
            WHERE tenant_id=$3 AND demand_id=$4
              AND (status IN ('pending','failed')
                   OR (status='processing' AND updated_at<=$5)
                   OR (status='completed' AND $6));
            """;
        update.Parameters.Add(Text(command.OwnerId));
        update.Parameters.Add(Timestamp(command.Now));
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.DemandId));
        update.Parameters.Add(Timestamp(command.Now - command.StaleAfter));
        update.Parameters.Add(Boolean(command.AllowCompleted));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        var record = await ReadAsync(
            connection, tx, command.TenantId, command.DemandId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<bool> TryCompleteAsync(
        PlanMaterializationCompleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            """
            UPDATE harness.demand_materializations
            SET status='completed',plan_id=$1,expected_cards=$2,materialized_cards=$3,
                last_error=NULL,updated_at=$4,completed_at=$4
            WHERE tenant_id=$5 AND demand_id=$6 AND status='processing'
              AND owner_id=$7 AND attempt_count=$8;
            """;
        q.Parameters.Add(Text(command.PlanId));
        q.Parameters.Add(Integer(command.ExpectedCards));
        q.Parameters.Add(Integer(command.MaterializedCards));
        q.Parameters.Add(Timestamp(command.OccurredAt));
        q.Parameters.Add(Text(command.TenantId));
        q.Parameters.Add(Text(command.DemandId));
        q.Parameters.Add(Text(command.OwnerId));
        q.Parameters.Add(Integer(command.AttemptCount));
        return await q.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryFailAsync(
        PlanMaterializationFailCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ErrorCode);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            """
            UPDATE harness.demand_materializations
            SET status='failed',last_error=$1,updated_at=$2
            WHERE tenant_id=$3 AND demand_id=$4 AND status='processing'
              AND owner_id=$5 AND attempt_count=$6;
            """;
        q.Parameters.Add(Text(command.ErrorCode));
        q.Parameters.Add(Timestamp(command.OccurredAt));
        q.Parameters.Add(Text(command.TenantId));
        q.Parameters.Add(Text(command.DemandId));
        q.Parameters.Add(Text(command.OwnerId));
        q.Parameters.Add(Integer(command.AttemptCount));
        return await q.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PlanMaterializationRecord>> ListForReconciliationAsync(
        PlanMaterializationCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var values = new List<PlanMaterializationRecord>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            $"{Selection} WHERE $1 IS NULL OR tenant_id>$1 OR (tenant_id=$1 AND demand_id>$2) " +
            "ORDER BY tenant_id,demand_id LIMIT $3;";
        q.Parameters.Add(NullableText(after?.TenantId));
        q.Parameters.Add(NullableText(after?.DemandId));
        q.Parameters.Add(Integer(limit));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            values.Add(Read(r));
        }

        return values;
    }

    private const string Selection =
        "SELECT tenant_id,demand_id,project_id,turn_id,plan_id,status,attempt_count,expected_cards," +
        "materialized_cards,request_json,owner_id,last_error,requested_at,updated_at,completed_at " +
        "FROM harness.demand_materializations";

    private static async Task<PlanMaterializationRecord?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string tenantId, string demandId,
        CancellationToken cancellationToken)
    {
        await using var q = connection.CreateCommand();
        q.Transaction = tx;
        q.CommandText = $"{Selection} WHERE tenant_id=$1 AND demand_id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(demandId));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? Read(r) : null;
    }

    private static PlanMaterializationRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
        r.IsDBNull(3) ? null : r.GetString(3).TrimEnd(),
        r.IsDBNull(4) ? null : r.GetString(4).TrimEnd(),
        r.GetString(5), r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8),
        JsonSerializer.Deserialize<PlanMaterializationRequest>(r.GetString(9), JsonOptions)
            ?? new PlanMaterializationRequest([]),
        r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.GetFieldValue<DateTimeOffset>(12), r.GetFieldValue<DateTimeOffset>(13),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14));

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
