using Harness.Persistence.Abstractions.Coordination;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Circuito por card em PostgreSQL. Paridade estrita com o SQLite, inclusive na transação única
/// da transição: ler e gravar em passos separados deixaria duas rodadas do mesmo card contando a
/// mesma falha duas vezes.
/// </summary>
public sealed class PostgresCardCircuitBreakerStore(NpgsqlDataSource dataSource)
    : ICardCircuitBreakerStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string Select =
        "SELECT tenant_id,task_id,project_id,state,consecutive_failures,last_failure_reason_code," +
        "last_failure_at,opened_at,replanned_at,replan_note,updated_at " +
        "FROM harness.card_circuit_breakers";

    public async Task<CardCircuitRecord?> GetAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, taskId, cancellationToken);
    }

    public async Task<CardCircuitRecord> RecordFailureAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        int consecutiveFailureThreshold,
        string? reasonCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailureThreshold);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadAsync(connection, tx, tenantId, taskId, cancellationToken);

        // Circuito já aberto: a falha atualiza o motivo e não conta de novo.
        if (current is { IsOpen: true })
        {
            await using (var touch = connection.CreateCommand())
            {
                touch.Transaction = tx;
                touch.CommandText = """
                    UPDATE harness.card_circuit_breakers
                    SET last_failure_reason_code=$1,last_failure_at=$2,updated_at=$2
                    WHERE tenant_id=$3 AND task_id=$4;
                    """;
                touch.Parameters.Add(Nullable(reasonCode ?? current.LastFailureReasonCode));
                touch.Parameters.Add(Timestamp(occurredAt));
                touch.Parameters.Add(Text(tenantId));
                touch.Parameters.Add(Text(taskId));
                await touch.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return (await ReadAsync(connection, null, tenantId, taskId, cancellationToken))!;
        }

        var failures = (current?.ConsecutiveFailures ?? 0) + 1;
        var opens = failures >= consecutiveFailureThreshold;
        await UpsertAsync(
            connection, tx, tenantId, projectId, taskId,
            opens ? CardCircuitRecord.OpenState : CardCircuitRecord.ClosedState,
            failures, reasonCode, occurredAt, opens ? occurredAt : null,
            current?.ReplannedAt, current?.ReplanNote, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (await ReadAsync(connection, null, tenantId, taskId, cancellationToken))!;
    }

    public async Task<CardCircuitRecord> RecordSuccessAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadAsync(connection, tx, tenantId, taskId, cancellationToken);
        await UpsertAsync(
            connection, tx, tenantId, projectId, taskId,
            CardCircuitRecord.ClosedState, 0, null, occurredAt, null,
            current?.ReplannedAt, current?.ReplanNote, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (await ReadAsync(connection, null, tenantId, taskId, cancellationToken))!;
    }

    public async Task<CardCircuitRecord> ReplanAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        string? replanNote = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertAsync(
            connection, tx, tenantId, projectId, taskId,
            CardCircuitRecord.ClosedState, 0, null, null, null,
            occurredAt, replanNote, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (await ReadAsync(connection, null, tenantId, taskId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<CardCircuitRecord>> ListOpenAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND project_id=$2 AND state='open' " +
            "ORDER BY opened_at,task_id;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        var rows = new List<CardCircuitRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static async Task UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string tenantId,
        string projectId,
        string taskId,
        string state,
        int consecutiveFailures,
        string? reasonCode,
        DateTimeOffset? lastFailureAt,
        DateTimeOffset? openedAt,
        DateTimeOffset? replannedAt,
        string? replanNote,
        CancellationToken cancellationToken)
    {
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = """
            INSERT INTO harness.card_circuit_breakers
            (tenant_id,task_id,project_id,state,consecutive_failures,last_failure_reason_code,
             last_failure_at,opened_at,replanned_at,replan_note,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            ON CONFLICT (tenant_id,task_id) DO UPDATE SET
                state=excluded.state,
                consecutive_failures=excluded.consecutive_failures,
                last_failure_reason_code=excluded.last_failure_reason_code,
                last_failure_at=excluded.last_failure_at,
                opened_at=excluded.opened_at,
                replanned_at=excluded.replanned_at,
                replan_note=excluded.replan_note,
                updated_at=excluded.updated_at;
            """;
        upsert.Parameters.Add(Text(tenantId));
        upsert.Parameters.Add(Text(taskId));
        upsert.Parameters.Add(Text(projectId));
        upsert.Parameters.Add(Text(state));
        upsert.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = consecutiveFailures
        });
        upsert.Parameters.Add(Nullable(reasonCode));
        upsert.Parameters.Add(NullableTimestamp(lastFailureAt));
        upsert.Parameters.Add(NullableTimestamp(openedAt));
        upsert.Parameters.Add(NullableTimestamp(replannedAt));
        upsert.Parameters.Add(Nullable(replanNote));
        upsert.Parameters.Add(Timestamp(
            lastFailureAt ?? openedAt ?? replannedAt ?? DateTimeOffset.UtcNow));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CardCircuitRecord?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        string tenantId,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND task_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(taskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static CardCircuitRecord Map(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetFieldValue<DateTimeOffset>(10));

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value };

    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) =>
        new()
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = value is { } instant ? instant : DBNull.Value
        };
}
