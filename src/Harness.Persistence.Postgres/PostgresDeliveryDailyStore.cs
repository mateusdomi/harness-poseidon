using Harness.Persistence.Abstractions.Delivery;
using Npgsql;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL do registro APPEND-ONLY das marcações da daily (DEL-03). Só INSERT e SELECT:
/// cada marcação é uma linha nova; o registro anterior nunca é sobrescrito nem apagado.
/// </summary>
public sealed class PostgresDeliveryDailyStore(NpgsqlDataSource dataSource) : IDeliveryDailyStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<DeliveryDailyCaptureRecord> AppendAsync(
        DeliveryDailyCaptureAppendCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO harness.delivery_daily_captures
                (id,tenant_id,project_id,kind,note,captured_by,created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7);
            """;
        insert.Parameters.Add(Text(command.Id));
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Text(command.Kind));
        insert.Parameters.Add(Text(command.Note));
        insert.Parameters.Add(Text(command.CapturedBy));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new DeliveryDailyCaptureRecord(
            command.TenantId, command.Id, command.ProjectId, command.Kind, command.Note,
            command.CapturedBy, command.OccurredAt);
    }

    public async Task<IReadOnlyList<DeliveryDailyCaptureRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText = Select +
            "WHERE tenant_id=$1 AND project_id=$2 ORDER BY created_at DESC, id DESC LIMIT $3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        q.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Clamp(limit, 1, 1000) });
        var results = new List<DeliveryDailyCaptureRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            results.Add(Read(r));
        }

        return results;
    }

    private const string Select =
        "SELECT tenant_id,id,project_id,kind,note,captured_by,created_at FROM harness.delivery_daily_captures ";

    private static DeliveryDailyCaptureRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
        r.GetString(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));

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

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
