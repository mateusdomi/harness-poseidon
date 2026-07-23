using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Delivery;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite do histórico APPEND-ONLY de previsões (DEL-09). Só há INSERT e SELECT: nunca
/// update nem delete, então cada previsão preserva o histórico anterior intacto.
/// </summary>
public sealed class SqliteDeliveryForecastStore(SqliteWriteDispatcher dispatcher) : IDeliveryForecastStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<DeliveryForecastRecord> AppendAsync(
        DeliveryForecastAppendCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var insert = c.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO delivery_forecasts
                    (id,tenant_id,project_id,forecast_date,confidence,confidence_percent,
                     has_sufficient_evidence,basis_json,created_at)
                VALUES ($id,$tenant,$project,$forecast,$confidence,$percent,$sufficient,$basis,$at);
                """;
            Add(insert, "$id", command.Id);
            Add(insert, "$tenant", command.TenantId);
            Add(insert, "$project", command.ProjectId);
            Add(insert, "$forecast", command.ForecastDate.HasValue ? Store(command.ForecastDate.Value) : null);
            Add(insert, "$confidence", command.Confidence);
            Add(insert, "$percent", command.ConfidencePercent);
            Add(insert, "$sufficient", command.HasSufficientEvidence ? 1 : 0);
            Add(insert, "$basis", JsonSerializer.Serialize(command.Basis, JsonOptions));
            Add(insert, "$at", Store(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(t);
            return new DeliveryForecastRecord(
                command.TenantId, command.Id, command.ProjectId, command.ForecastDate,
                command.Confidence, command.ConfidencePercent, command.HasSufficientEvidence,
                command.Basis, command.OccurredAt);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DeliveryForecastRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = Select +
                "WHERE tenant_id=$tenant AND project_id=$project ORDER BY created_at DESC, id DESC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$project", projectId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<DeliveryForecastRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                results.Add(Read(r));
            }

            return (IReadOnlyList<DeliveryForecastRecord>)results;
        }, cancellationToken);

    public Task<DeliveryForecastRecord?> GetLatestAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = Select +
                "WHERE tenant_id=$tenant AND project_id=$project ORDER BY created_at DESC, id DESC LIMIT 1;";
            Add(q, "$tenant", tenantId);
            Add(q, "$project", projectId);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? Read(r) : null;
        }, cancellationToken);

    private const string Select =
        "SELECT tenant_id,id,project_id,forecast_date,confidence,confidence_percent," +
        "has_sufficient_evidence,basis_json,created_at FROM delivery_forecasts ";

    private static DeliveryForecastRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : Parse(r.GetString(3)),
        r.GetString(4), r.GetInt32(5), r.GetInt32(6) == 1,
        JsonSerializer.Deserialize<IReadOnlyList<DeliveryForecastBasisEntry>>(r.GetString(7), JsonOptions) ?? [],
        Parse(r.GetString(8)));

    private static void Validate(DeliveryForecastAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Confidence);
        ArgumentNullException.ThrowIfNull(command.Basis);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
