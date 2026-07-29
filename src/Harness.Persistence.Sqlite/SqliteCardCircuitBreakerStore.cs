using System.Globalization;
using Harness.Persistence.Abstractions.Coordination;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Circuito por card em SQLite. A transição inteira acontece em UMA transação: ler, decidir e
/// gravar em passos separados deixaria duas rodadas concorrentes do mesmo card contando a mesma
/// falha duas vezes, ou pior, uma sobrescrevendo a abertura da outra.
/// </summary>
public sealed class SqliteCardCircuitBreakerStore(SqliteWriteDispatcher dispatcher)
    : ICardCircuitBreakerStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Select =
        "SELECT tenant_id,task_id,project_id,state,consecutive_failures,last_failure_reason_code," +
        "last_failure_at,opened_at,replanned_at,replan_note,updated_at FROM card_circuit_breakers";

    public Task<CardCircuitRecord?> GetAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText = $"{Select} WHERE tenant_id=$tenant AND task_id=$task;";
                Add(query, "$tenant", tenantId);
                Add(query, "$task", taskId);
                await using var reader = await query.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? Map(reader) : null;
            },
            cancellationToken);

    public Task<CardCircuitRecord> RecordFailureAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        int consecutiveFailureThreshold,
        string? reasonCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailureThreshold);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
                var current = await ReadAsync(connection, tx, tenantId, taskId, token);

                // Circuito já aberto: a falha não conta de novo, só atualiza o motivo. Contar aqui
                // inflaria a reincidência de um card que sequer deveria ter sido despachado.
                if (current is { IsOpen: true })
                {
                    await using (var touch = connection.CreateCommand())
                    {
                        touch.Transaction = tx;
                        touch.CommandText =
                            "UPDATE card_circuit_breakers SET last_failure_reason_code=$reason," +
                            "last_failure_at=$at,updated_at=$at " +
                            "WHERE tenant_id=$tenant AND task_id=$task;";
                        AddNullable(touch, "$reason", reasonCode ?? current.LastFailureReasonCode);
                        Add(touch, "$at", Store(occurredAt));
                        Add(touch, "$tenant", tenantId);
                        Add(touch, "$task", taskId);
                        await touch.ExecuteNonQueryAsync(token);
                    }

                    await tx.CommitAsync(token);
                    return (await ReadAsync(connection, null, tenantId, taskId, token))!;
                }

                var failures = (current?.ConsecutiveFailures ?? 0) + 1;
                var opens = failures >= consecutiveFailureThreshold;
                await UpsertAsync(
                    connection,
                    tx,
                    tenantId,
                    projectId,
                    taskId,
                    opens ? CardCircuitRecord.OpenState : CardCircuitRecord.ClosedState,
                    failures,
                    reasonCode,
                    occurredAt,
                    opens ? occurredAt : null,
                    replannedAt: current?.ReplannedAt,
                    replanNote: current?.ReplanNote,
                    token);
                await tx.CommitAsync(token);
                return (await ReadAsync(connection, null, tenantId, taskId, token))!;
            },
            cancellationToken);
    }

    public Task<CardCircuitRecord> RecordSuccessAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
                var current = await ReadAsync(connection, tx, tenantId, taskId, token);
                await UpsertAsync(
                    connection, tx, tenantId, projectId, taskId,
                    CardCircuitRecord.ClosedState, 0, null, occurredAt, null,
                    current?.ReplannedAt, current?.ReplanNote, token);
                await tx.CommitAsync(token);
                return (await ReadAsync(connection, null, tenantId, taskId, token))!;
            },
            cancellationToken);

    public Task<CardCircuitRecord> ReplanAsync(
        string tenantId,
        string projectId,
        string taskId,
        DateTimeOffset occurredAt,
        string? replanNote = null,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
                await UpsertAsync(
                    connection, tx, tenantId, projectId, taskId,
                    CardCircuitRecord.ClosedState, 0, null, null, null,
                    occurredAt, replanNote, token);
                await tx.CommitAsync(token);
                return (await ReadAsync(connection, null, tenantId, taskId, token))!;
            },
            cancellationToken);

    public Task<IReadOnlyList<CardCircuitRecord>> ListOpenAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND project_id=$project AND state='open' " +
                    "ORDER BY opened_at,task_id;";
                Add(query, "$tenant", tenantId);
                Add(query, "$project", projectId);
                var rows = new List<CardCircuitRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(Map(reader));
                }

                return (IReadOnlyList<CardCircuitRecord>)rows;
            },
            cancellationToken);

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
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
        CancellationToken token)
    {
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText =
            "INSERT INTO card_circuit_breakers " +
            "(tenant_id,task_id,project_id,state,consecutive_failures,last_failure_reason_code," +
            "last_failure_at,opened_at,replanned_at,replan_note,updated_at) VALUES " +
            "($tenant,$task,$project,$state,$failures,$reason,$failureAt,$openedAt,$replannedAt," +
            "$replanNote,$updatedAt) " +
            "ON CONFLICT(tenant_id,task_id) DO UPDATE SET " +
            "state=excluded.state,consecutive_failures=excluded.consecutive_failures," +
            "last_failure_reason_code=excluded.last_failure_reason_code," +
            "last_failure_at=excluded.last_failure_at,opened_at=excluded.opened_at," +
            "replanned_at=excluded.replanned_at,replan_note=excluded.replan_note," +
            "updated_at=excluded.updated_at;";
        Add(upsert, "$tenant", tenantId);
        Add(upsert, "$task", taskId);
        Add(upsert, "$project", projectId);
        Add(upsert, "$state", state);
        Add(upsert, "$failures", consecutiveFailures);
        AddNullable(upsert, "$reason", reasonCode);
        AddNullable(upsert, "$failureAt", lastFailureAt is { } failure ? Store(failure) : null);
        AddNullable(upsert, "$openedAt", openedAt is { } opened ? Store(opened) : null);
        AddNullable(upsert, "$replannedAt", replannedAt is { } replanned ? Store(replanned) : null);
        AddNullable(upsert, "$replanNote", replanNote);
        Add(upsert, "$updatedAt", Store(
            lastFailureAt ?? openedAt ?? replannedAt ?? DateTimeOffset.UtcNow));
        await upsert.ExecuteNonQueryAsync(token);
    }

    private static async Task<CardCircuitRecord?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string tenantId,
        string taskId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText = $"{Select} WHERE tenant_id=$tenant AND task_id=$task;";
        Add(query, "$tenant", tenantId);
        Add(query, "$task", taskId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static CardCircuitRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        ReadTimestamp(reader, 6),
        ReadTimestamp(reader, 7),
        ReadTimestamp(reader, 8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
