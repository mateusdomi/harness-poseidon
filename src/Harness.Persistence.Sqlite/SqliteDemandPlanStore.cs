using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite do artefato de PLANO por demanda (PLAT-01). Geração idempotente por
/// (tenant, demand) via <c>INSERT OR IGNORE</c>; materialização idempotente via UPDATE condicional
/// ao status 'proposed'.
/// </summary>
public sealed class SqliteDemandPlanStore(SqliteWriteDispatcher dispatcher) : IDemandPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<DemandPlanSaveResult> SaveProposedAsync(
        DemandPlanSaveCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync((c, t) => SaveProposedCoreAsync(c, command, t), cancellationToken);
    }

    public Task<DemandPlanRecord?> GetByDemandAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => ReadAsync(c, tenantId, "demand_id", demandId, t), cancellationToken);

    public Task<DemandPlanRecord?> GetAsync(
        string tenantId, string planId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => ReadAsync(c, tenantId, "id", planId, t), cancellationToken);

    public Task<bool> TryMarkMaterializedAsync(
        string tenantId, string planId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "UPDATE demand_plans SET status='materialized',materialized_at=$at " +
                "WHERE tenant_id=$tenant AND id=$id AND status='proposed';";
            Add(q, "$at", Store(occurredAt));
            Add(q, "$tenant", tenantId);
            Add(q, "$id", planId);
            return await q.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);

    private static async Task<DemandPlanSaveResult> SaveProposedCoreAsync(
        SqliteConnection c, DemandPlanSaveCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using var insert = c.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO demand_plans
                (id,tenant_id,project_id,demand_id,feature_id,status,cards_json,correlation_id,created_at,materialized_at)
            VALUES ($id,$tenant,$project,$demand,$feature,'proposed',$cards,$correlation,$at,NULL);
            """;
        Add(insert, "$id", command.Id);
        Add(insert, "$tenant", command.TenantId);
        Add(insert, "$project", command.ProjectId);
        Add(insert, "$demand", command.DemandId);
        Add(insert, "$feature", command.FeatureId);
        Add(insert, "$cards", JsonSerializer.Serialize(command.Cards, JsonOptions));
        Add(insert, "$correlation", command.CorrelationId);
        Add(insert, "$at", Store(command.OccurredAt));
        var created = await insert.ExecuteNonQueryAsync(token) == 1;
        var record = await ReadInTransactionAsync(c, tx, command.TenantId, "demand_id", command.DemandId, token)
            ?? throw new InvalidOperationException("The persisted demand plan could not be read back.");
        await tx.CommitAsync(token);
        return new DemandPlanSaveResult(record, created);
    }

    private static async Task<DemandPlanRecord?> ReadAsync(
        SqliteConnection c, string tenantId, string column, string value, CancellationToken token) =>
        await ReadInTransactionAsync(c, null, tenantId, column, value, token);

    private static async Task<DemandPlanRecord?> ReadInTransactionAsync(
        SqliteConnection c, SqliteTransaction? tx, string tenantId, string column, string value,
        CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT tenant_id,id,project_id,demand_id,feature_id,status,cards_json,correlation_id,created_at,materialized_at " +
            $"FROM demand_plans WHERE tenant_id=$tenant AND {column}=$value;";
        Add(q, "$tenant", tenantId);
        Add(q, "$value", value);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? Read(r) : null;
    }

    private static DemandPlanRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5),
        JsonSerializer.Deserialize<IReadOnlyList<DemandPlanCard>>(r.GetString(6), JsonOptions) ?? [],
        r.GetString(7), Parse(r.GetString(8)), r.IsDBNull(9) ? null : Parse(r.GetString(9)));

    private static void Validate(DemandPlanSaveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DemandId);
        ArgumentNullException.ThrowIfNull(command.Cards);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
