using System.Globalization;
using Harness.Persistence.Abstractions.Delivery;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite do registro APPEND-ONLY das marcações da daily (DEL-03). Só há INSERT e SELECT:
/// nunca update nem delete, então cada marcação preserva o registro anterior intacto.
/// </summary>
public sealed class SqliteDeliveryDailyStore(SqliteWriteDispatcher dispatcher) : IDeliveryDailyStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<DeliveryDailyCaptureRecord> AppendAsync(
        DeliveryDailyCaptureAppendCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var insert = c.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO delivery_daily_captures
                    (id,tenant_id,project_id,kind,note,captured_by,created_at)
                VALUES ($id,$tenant,$project,$kind,$note,$by,$at);
                """;
            Add(insert, "$id", command.Id);
            Add(insert, "$tenant", command.TenantId);
            Add(insert, "$project", command.ProjectId);
            Add(insert, "$kind", command.Kind);
            Add(insert, "$note", command.Note);
            Add(insert, "$by", command.CapturedBy);
            Add(insert, "$at", Store(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(t);
            return new DeliveryDailyCaptureRecord(
                command.TenantId, command.Id, command.ProjectId, command.Kind, command.Note,
                command.CapturedBy, command.OccurredAt);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DeliveryDailyCaptureRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = Select +
                "WHERE tenant_id=$tenant AND project_id=$project ORDER BY created_at DESC, id DESC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$project", projectId);
            Add(q, "$limit", Math.Clamp(limit, 1, 1000));
            var results = new List<DeliveryDailyCaptureRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                results.Add(Read(r));
            }

            return (IReadOnlyList<DeliveryDailyCaptureRecord>)results;
        }, cancellationToken);

    private const string Select =
        "SELECT tenant_id,id,project_id,kind,note,captured_by,created_at FROM delivery_daily_captures ";

    private static DeliveryDailyCaptureRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), Parse(r.GetString(6)));

    private static void Validate(DeliveryDailyCaptureAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Note);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CapturedBy);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
