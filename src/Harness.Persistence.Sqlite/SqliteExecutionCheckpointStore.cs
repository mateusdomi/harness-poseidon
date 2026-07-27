using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.DurableExecution;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>Checkpoint de execução em SQLite. Ver <see cref="IExecutionCheckpointStore"/>.</summary>
public sealed class SqliteExecutionCheckpointStore(SqliteWriteDispatcher dispatcher)
    : IExecutionCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Select =
        "SELECT tenant_id,checkpoint_id,project_id,task_id,execution_id,source_attempt_id," +
        "source_account_alias,source_role,origin,branch_name,source_commit,repository_root," +
        "scope_claims_json,changed_files_json,progress_note,pending_json,evidence_json," +
        "origin_fencing_token,consumed_by_attempt_id,consumed_at,created_at FROM execution_checkpoints";

    public Task<ExecutionCheckpointRecord> SaveAsync(
        ExecutionCheckpointSaveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
                // O checkpoint anterior ainda disponível é encerrado pelo novo: só um vale por card.
                await using (var supersede = connection.CreateCommand())
                {
                    supersede.Transaction = tx;
                    supersede.CommandText =
                        "UPDATE execution_checkpoints SET consumed_by_attempt_id=$superseded," +
                        "consumed_at=$at WHERE tenant_id=$tenant AND task_id=$task " +
                        "AND consumed_by_attempt_id IS NULL;";
                    Add(supersede, "$superseded", $"superseded:{command.CheckpointId}");
                    Add(supersede, "$at", Store(command.OccurredAt));
                    Add(supersede, "$tenant", command.TenantId);
                    Add(supersede, "$task", command.TaskId);
                    await supersede.ExecuteNonQueryAsync(token);
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText =
                        "INSERT INTO execution_checkpoints " +
                        "(tenant_id,checkpoint_id,project_id,task_id,execution_id,source_attempt_id," +
                        "source_account_alias,source_role,origin,branch_name,source_commit," +
                        "repository_root,scope_claims_json,changed_files_json,progress_note," +
                        "pending_json,evidence_json,origin_fencing_token,created_at) VALUES " +
                        "($tenant,$id,$project,$task,$execution,$attempt,$alias,$role,$origin," +
                        "$branch,$commit,$repository,$claims,$files,$note,$pending,$evidence,$fencing,$at);";
                    Add(insert, "$tenant", command.TenantId);
                    Add(insert, "$id", command.CheckpointId);
                    Add(insert, "$project", command.ProjectId);
                    Add(insert, "$task", command.TaskId);
                    Add(insert, "$execution", command.ExecutionId);
                    Add(insert, "$attempt", command.SourceAttemptId);
                    Add(insert, "$alias", command.SourceAccountAlias);
                    Add(insert, "$role", command.SourceRole);
                    Add(insert, "$origin", command.Origin);
                    Add(insert, "$branch", command.BranchName);
                    AddNullable(insert, "$commit", command.SourceCommit);
                    Add(insert, "$repository", command.RepositoryRoot);
                    Add(insert, "$claims", JsonSerializer.Serialize(command.ScopeClaims ?? [], JsonOptions));
                    Add(insert, "$files", JsonSerializer.Serialize(command.ChangedFiles ?? [], JsonOptions));
                    AddNullable(insert, "$note", command.ProgressNote);
                    Add(insert, "$pending", JsonSerializer.Serialize(command.Pending ?? [], JsonOptions));
                    Add(insert, "$evidence", JsonSerializer.Serialize(command.Evidence ?? [], JsonOptions));
                    Add(insert, "$fencing", command.OriginFencingToken);
                    Add(insert, "$at", Store(command.OccurredAt));
                    await insert.ExecuteNonQueryAsync(token);
                }

                await tx.CommitAsync(token);
                return (await ReadAsync(connection, command.TenantId, command.CheckpointId, token))!;
            },
            cancellationToken);
    }

    public Task<ExecutionCheckpointRecord?> GetAvailableAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND task_id=$task " +
                    "AND consumed_by_attempt_id IS NULL ORDER BY created_at DESC LIMIT 1;";
                Add(query, "$tenant", tenantId);
                Add(query, "$task", taskId);
                await using var reader = await query.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? Map(reader) : null;
            },
            cancellationToken);

    public Task<bool> ConsumeAsync(
        string tenantId, string checkpointId, string attemptId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var update = connection.CreateCommand();
                // Idempotente por tentativa: repetir o consumo não reabre nem duplica.
                update.CommandText =
                    "UPDATE execution_checkpoints SET consumed_by_attempt_id=$attempt,consumed_at=$at " +
                    "WHERE tenant_id=$tenant AND checkpoint_id=$id " +
                    "AND (consumed_by_attempt_id IS NULL OR consumed_by_attempt_id=$attempt);";
                Add(update, "$attempt", attemptId);
                Add(update, "$at", Store(occurredAt));
                Add(update, "$tenant", tenantId);
                Add(update, "$id", checkpointId);
                return await update.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);

    public Task<IReadOnlyList<ExecutionCheckpointRecord>> ListForTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND task_id=$task ORDER BY created_at;";
                Add(query, "$tenant", tenantId);
                Add(query, "$task", taskId);
                var rows = new List<ExecutionCheckpointRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(Map(reader));
                }

                return (IReadOnlyList<ExecutionCheckpointRecord>)rows;
            },
            cancellationToken);

    private static async Task<ExecutionCheckpointRecord?> ReadAsync(
        SqliteConnection connection, string tenantId, string checkpointId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$tenant AND checkpoint_id=$id;";
        Add(query, "$tenant", tenantId);
        Add(query, "$id", checkpointId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static ExecutionCheckpointRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
        JsonSerializer.Deserialize<string[]>(reader.GetString(12), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(13), JsonOptions) ?? [],
        reader.IsDBNull(14) ? null : reader.GetString(14),
        JsonSerializer.Deserialize<string[]>(reader.GetString(15), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(16), JsonOptions) ?? [],
        reader.GetInt64(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture));

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
