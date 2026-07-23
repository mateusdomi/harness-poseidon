using System.Text.Json;
using Harness.Persistence.Abstractions.Delivery;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL do histórico APPEND-ONLY de previsões (DEL-09). Só INSERT e SELECT: cada
/// previsão é uma linha nova; o histórico anterior nunca é sobrescrito nem apagado.
/// </summary>
public sealed class PostgresDeliveryForecastStore(NpgsqlDataSource dataSource) : IDeliveryForecastStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<DeliveryForecastRecord> AppendAsync(
        DeliveryForecastAppendCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO harness.delivery_forecasts
                (id,tenant_id,project_id,forecast_date,confidence,confidence_percent,
                 has_sufficient_evidence,basis_json,created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9);
            """;
        insert.Parameters.Add(Text(command.Id));
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(NullableTimestamp(command.ForecastDate));
        insert.Parameters.Add(Text(command.Confidence));
        insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = command.ConfidencePercent });
        insert.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = command.HasSufficientEvidence });
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(command.Basis, JsonOptions)));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new DeliveryForecastRecord(
            command.TenantId, command.Id, command.ProjectId, command.ForecastDate,
            command.Confidence, command.ConfidencePercent, command.HasSufficientEvidence,
            command.Basis, command.OccurredAt);
    }

    public async Task<IReadOnlyList<DeliveryForecastRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText = Select +
            "WHERE tenant_id=$1 AND project_id=$2 ORDER BY created_at DESC, id DESC LIMIT $3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        q.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Clamp(limit, 1, 500) });
        var results = new List<DeliveryForecastRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            results.Add(Read(r));
        }

        return results;
    }

    public async Task<DeliveryForecastRecord?> GetLatestAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText = Select +
            "WHERE tenant_id=$1 AND project_id=$2 ORDER BY created_at DESC, id DESC LIMIT 1;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? Read(r) : null;
    }

    private const string Select =
        "SELECT tenant_id,id,project_id,forecast_date,confidence,confidence_percent," +
        "has_sufficient_evidence,basis_json,created_at FROM harness.delivery_forecasts ";

    private static DeliveryForecastRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
        r.GetString(4), r.GetInt32(5), r.GetBoolean(6),
        JsonSerializer.Deserialize<IReadOnlyList<DeliveryForecastBasisEntry>>(r.GetString(7), JsonOptions) ?? [],
        r.GetFieldValue<DateTimeOffset>(8));

    private static void Validate(DeliveryForecastAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Confidence);
        ArgumentNullException.ThrowIfNull(command.Basis);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)value ?? DBNull.Value };
}
