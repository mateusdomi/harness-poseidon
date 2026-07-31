using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite do compromisso de materialização (Fase 0A1). Toda transição é condicional ao
/// estado atual e ao par (dono, tentativa): nenhum caminho aqui pode declarar conclusão sem que a
/// linha estivesse de fato em <c>processing</c> sob o mesmo dono.
/// </summary>
public sealed class SqlitePlanMaterializationStore(SqliteWriteDispatcher dispatcher)
    : IPlanMaterializationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<PlanMaterializationRecord?> GetAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => ReadAsync(c, null, tenantId, demandId, t), cancellationToken);

    public Task<PlanMaterializationRecord> RequestAsync(
        PlanMaterializationRequestCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DemandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentNullException.ThrowIfNull(command.Request);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(t);
            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO demand_materializations
                    (tenant_id,demand_id,project_id,turn_id,plan_id,status,attempt_count,
                     expected_cards,materialized_cards,request_json,owner_id,last_error,
                     requested_at,updated_at,completed_at)
                VALUES ($tenant,$demand,$project,$turn,NULL,'pending',0,NULL,NULL,$request,NULL,
                        NULL,$at,$at,NULL);
                """;
            Add(insert, "$tenant", command.TenantId);
            Add(insert, "$demand", command.DemandId);
            Add(insert, "$project", command.ProjectId);
            Add(insert, "$turn", command.TurnId);
            Add(insert, "$request", JsonSerializer.Serialize(command.Request, JsonOptions));
            Add(insert, "$at", Store(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(t);
            var record = await ReadAsync(c, tx, command.TenantId, command.DemandId, t)
                ?? throw new InvalidOperationException(
                    "The persisted plan materialization could not be read back.");
            await tx.CommitAsync(t);
            return record;
        }, cancellationToken);
    }

    public Task<PlanMaterializationRecord?> TryBeginAsync(
        PlanMaterializationBeginCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OwnerId);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(t);
            await using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE demand_materializations
                SET status='processing',owner_id=$owner,attempt_count=attempt_count+1,updated_at=$now
                WHERE tenant_id=$tenant AND demand_id=$demand
                  AND (status IN ('pending','failed')
                       OR (status='processing' AND updated_at<=$stale)
                       OR (status='completed' AND $allowCompleted=1));
                """;
            Add(update, "$owner", command.OwnerId);
            Add(update, "$now", Store(command.Now));
            Add(update, "$tenant", command.TenantId);
            Add(update, "$demand", command.DemandId);
            Add(update, "$stale", Store(command.Now - command.StaleAfter));
            Add(update, "$allowCompleted", command.AllowCompleted ? 1 : 0);
            if (await update.ExecuteNonQueryAsync(t) != 1)
            {
                await tx.CommitAsync(t);
                return null;
            }

            var record = await ReadAsync(c, tx, command.TenantId, command.DemandId, t);
            await tx.CommitAsync(t);
            return record;
        }, cancellationToken);
    }

    public Task<bool> TryCompleteAsync(
        PlanMaterializationCompleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                """
                UPDATE demand_materializations
                SET status='completed',plan_id=$plan,expected_cards=$expected,
                    materialized_cards=$materialized,last_error=NULL,updated_at=$at,completed_at=$at
                WHERE tenant_id=$tenant AND demand_id=$demand AND status='processing'
                  AND owner_id=$owner AND attempt_count=$attempt;
                """;
            Add(q, "$plan", command.PlanId);
            Add(q, "$expected", command.ExpectedCards);
            Add(q, "$materialized", command.MaterializedCards);
            Add(q, "$at", Store(command.OccurredAt));
            Add(q, "$tenant", command.TenantId);
            Add(q, "$demand", command.DemandId);
            Add(q, "$owner", command.OwnerId);
            Add(q, "$attempt", command.AttemptCount);
            return await q.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);
    }

    public Task<bool> TryFailAsync(
        PlanMaterializationFailCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ErrorCode);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                """
                UPDATE demand_materializations
                SET status='failed',last_error=$error,updated_at=$at
                WHERE tenant_id=$tenant AND demand_id=$demand AND status='processing'
                  AND owner_id=$owner AND attempt_count=$attempt;
                """;
            Add(q, "$error", command.ErrorCode);
            Add(q, "$at", Store(command.OccurredAt));
            Add(q, "$tenant", command.TenantId);
            Add(q, "$demand", command.DemandId);
            Add(q, "$owner", command.OwnerId);
            Add(q, "$attempt", command.AttemptCount);
            return await q.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PlanMaterializationRecord>> ListForReconciliationAsync(
        PlanMaterializationCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return _dispatcher.ExecuteAsync<IReadOnlyList<PlanMaterializationRecord>>(async (c, t) =>
        {
            var values = new List<PlanMaterializationRecord>();
            await using var q = c.CreateCommand();
            q.CommandText =
                $"{Selection} WHERE $tenant IS NULL OR tenant_id>$tenant " +
                "OR (tenant_id=$tenant AND demand_id>$demand) " +
                "ORDER BY tenant_id,demand_id LIMIT $limit;";
            Add(q, "$tenant", after?.TenantId);
            Add(q, "$demand", after?.DemandId);
            Add(q, "$limit", limit);
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                values.Add(Read(r));
            }

            return values;
        }, cancellationToken);
    }

    private const string Selection =
        "SELECT tenant_id,demand_id,project_id,turn_id,plan_id,status,attempt_count,expected_cards," +
        "materialized_cards,request_json,owner_id,last_error,requested_at,updated_at,completed_at " +
        "FROM demand_materializations";

    private static async Task<PlanMaterializationRecord?> ReadAsync(
        SqliteConnection c, SqliteTransaction? tx, string tenantId, string demandId,
        CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = $"{Selection} WHERE tenant_id=$tenant AND demand_id=$demand;";
        Add(q, "$tenant", tenantId);
        Add(q, "$demand", demandId);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? Read(r) : null;
    }

    private static PlanMaterializationRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8),
        JsonSerializer.Deserialize<PlanMaterializationRequest>(r.GetString(9), JsonOptions)
            ?? new PlanMaterializationRequest([]),
        r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
        Parse(r.GetString(12)), Parse(r.GetString(13)),
        r.IsDBNull(14) ? null : Parse(r.GetString(14)));

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
