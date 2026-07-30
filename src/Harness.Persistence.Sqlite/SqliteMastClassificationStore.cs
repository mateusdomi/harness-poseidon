using System.Globalization;
using Harness.Persistence.Abstractions.Coordination;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Classificação MAST em SQLite (migration 0105). A gravação é um UPSERT pela chave da tentativa:
/// reclassificar SUBSTITUI. Acumular contaria a mesma falha várias vezes e a distribuição — que
/// existe justamente para revelar concentração — passaria a inventá-la.
/// </summary>
public sealed class SqliteMastClassificationStore(SqliteWriteDispatcher dispatcher)
    : IMastClassificationStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Select =
        "SELECT tenant_id,attempt_id,project_id,task_id,failure_mode_code,category," +
        "classified_by,evidence,occurred_at FROM mast_attempt_classifications";

    public Task ClassifyAsync(
        MastAttemptClassificationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO mast_attempt_classifications" +
                    "(tenant_id,attempt_id,project_id,task_id,failure_mode_code,category," +
                    "classified_by,evidence,occurred_at) " +
                    "VALUES($tenant,$attempt,$project,$task,$mode,$category,$by,$evidence,$at) " +
                    "ON CONFLICT(tenant_id,attempt_id) DO UPDATE SET " +
                    "failure_mode_code=excluded.failure_mode_code,category=excluded.category," +
                    "classified_by=excluded.classified_by,evidence=excluded.evidence," +
                    "occurred_at=excluded.occurred_at;";
                Add(command, "$tenant", record.TenantId);
                Add(command, "$attempt", record.AttemptId);
                Add(command, "$project", record.ProjectId);
                Add(command, "$task", record.TaskId);
                Add(command, "$mode", record.FailureModeCode);
                Add(command, "$category", record.Category);
                Add(command, "$by", record.ClassifiedBy);
                AddNullable(command, "$evidence", record.Evidence);
                Add(command, "$at", Store(record.OccurredAt));
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<MastAttemptClassificationRecord>> ListByProjectAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        ListAsync("project_id", tenantId, projectId, cancellationToken);

    public Task<IReadOnlyList<MastAttemptClassificationRecord>> ListByTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default) =>
        ListAsync("task_id", tenantId, taskId, cancellationToken);

    private Task<IReadOnlyList<MastAttemptClassificationRecord>> ListAsync(
        string column, string tenantId, string value, CancellationToken cancellationToken) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND {column}=$value ORDER BY occurred_at;";
                Add(query, "$tenant", tenantId);
                Add(query, "$value", value);
                await using var reader = await query.ExecuteReaderAsync(token);
                var items = new List<MastAttemptClassificationRecord>();
                while (await reader.ReadAsync(token))
                {
                    items.Add(Map(reader));
                }

                return (IReadOnlyList<MastAttemptClassificationRecord>)items;
            },
            cancellationToken);

    private static MastAttemptClassificationRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture));

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
