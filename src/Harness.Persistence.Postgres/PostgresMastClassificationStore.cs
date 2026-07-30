using Harness.Persistence.Abstractions.Coordination;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Classificação MAST em PostgreSQL (migration 0105). Paridade estrita com o SQLite: UPSERT pela
/// chave da tentativa, porque reclassificar SUBSTITUI — acumular faria a distribuição inventar a
/// concentração que ela existe para revelar.
/// </summary>
public sealed class PostgresMastClassificationStore(NpgsqlDataSource dataSource)
    : IMastClassificationStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string Select =
        "SELECT tenant_id,attempt_id,project_id,task_id,failure_mode_code,category," +
        "classified_by,evidence,occurred_at FROM harness.mast_attempt_classifications";

    public async Task ClassifyAsync(
        MastAttemptClassificationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO harness.mast_attempt_classifications" +
            "(tenant_id,attempt_id,project_id,task_id,failure_mode_code,category," +
            "classified_by,evidence,occurred_at) " +
            "VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) " +
            "ON CONFLICT (tenant_id,attempt_id) DO UPDATE SET " +
            "failure_mode_code=EXCLUDED.failure_mode_code,category=EXCLUDED.category," +
            "classified_by=EXCLUDED.classified_by,evidence=EXCLUDED.evidence," +
            "occurred_at=EXCLUDED.occurred_at;";
        command.Parameters.Add(Text(record.TenantId));
        command.Parameters.Add(Text(record.AttemptId));
        command.Parameters.Add(Text(record.ProjectId));
        command.Parameters.Add(Text(record.TaskId));
        command.Parameters.Add(Text(record.FailureModeCode));
        command.Parameters.Add(Text(record.Category));
        command.Parameters.Add(Text(record.ClassifiedBy));
        command.Parameters.Add(Nullable(record.Evidence));
        command.Parameters.Add(Timestamp(record.OccurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<MastAttemptClassificationRecord>> ListByProjectAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        ListAsync("project_id", tenantId, projectId, cancellationToken);

    public Task<IReadOnlyList<MastAttemptClassificationRecord>> ListByTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default) =>
        ListAsync("task_id", tenantId, taskId, cancellationToken);

    private async Task<IReadOnlyList<MastAttemptClassificationRecord>> ListAsync(
        string column, string tenantId, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND {column}=$2 ORDER BY occurred_at;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(value));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var items = new List<MastAttemptClassificationRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Map(reader));
        }

        return items;
    }

    private static MastAttemptClassificationRecord Map(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8));

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value };
}
