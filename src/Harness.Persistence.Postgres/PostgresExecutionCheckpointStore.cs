using System.Text.Json;
using Harness.Persistence.Abstractions.DurableExecution;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>Checkpoint de execução em PostgreSQL. Paridade estrita com o SQLite.</summary>
public sealed class PostgresExecutionCheckpointStore(NpgsqlDataSource dataSource)
    : IExecutionCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string Select =
        "SELECT tenant_id,checkpoint_id,project_id,task_id,execution_id,source_attempt_id," +
        "source_account_alias,source_role,origin,branch_name,source_commit,repository_root," +
        "scope_claims_json::text,changed_files_json::text,progress_note,pending_json::text," +
        "evidence_json::text,origin_fencing_token,consumed_by_attempt_id,consumed_at,created_at " +
        "FROM harness.execution_checkpoints";

    public async Task<ExecutionCheckpointRecord> SaveAsync(
        ExecutionCheckpointSaveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var supersede = connection.CreateCommand())
        {
            supersede.Transaction = tx;
            supersede.CommandText = """
                UPDATE harness.execution_checkpoints
                SET consumed_by_attempt_id=$1,consumed_at=$2
                WHERE tenant_id=$3 AND task_id=$4 AND consumed_by_attempt_id IS NULL;
                """;
            supersede.Parameters.Add(Text($"superseded:{command.CheckpointId}"));
            supersede.Parameters.Add(Timestamp(command.OccurredAt));
            supersede.Parameters.Add(Text(command.TenantId));
            supersede.Parameters.Add(Text(command.TaskId));
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO harness.execution_checkpoints
                (tenant_id,checkpoint_id,project_id,task_id,execution_id,source_attempt_id,
                 source_account_alias,source_role,origin,branch_name,source_commit,repository_root,
                 scope_claims_json,changed_files_json,progress_note,pending_json,evidence_json,
                 origin_fencing_token,created_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13::jsonb,$14::jsonb,$15,
                        $16::jsonb,$17::jsonb,$18,$19);
                """;
            insert.Parameters.Add(Text(command.TenantId));
            insert.Parameters.Add(Text(command.CheckpointId));
            insert.Parameters.Add(Text(command.ProjectId));
            insert.Parameters.Add(Text(command.TaskId));
            insert.Parameters.Add(Text(command.ExecutionId));
            insert.Parameters.Add(Text(command.SourceAttemptId));
            insert.Parameters.Add(Text(command.SourceAccountAlias));
            insert.Parameters.Add(Text(command.SourceRole));
            insert.Parameters.Add(Text(command.Origin));
            insert.Parameters.Add(Text(command.BranchName));
            insert.Parameters.Add(Nullable(command.SourceCommit));
            insert.Parameters.Add(Text(command.RepositoryRoot));
            insert.Parameters.Add(Text(JsonSerializer.Serialize(command.ScopeClaims ?? [], JsonOptions)));
            insert.Parameters.Add(Text(JsonSerializer.Serialize(command.ChangedFiles ?? [], JsonOptions)));
            insert.Parameters.Add(Nullable(command.ProgressNote));
            insert.Parameters.Add(Text(JsonSerializer.Serialize(command.Pending ?? [], JsonOptions)));
            insert.Parameters.Add(Text(JsonSerializer.Serialize(command.Evidence ?? [], JsonOptions)));
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = command.OriginFencingToken });
            insert.Parameters.Add(Timestamp(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return (await ReadAsync(connection, command.TenantId, command.CheckpointId, cancellationToken))!;
    }

    public async Task<ExecutionCheckpointRecord?> GetAvailableAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND task_id=$2 AND consumed_by_attempt_id IS NULL " +
            "ORDER BY created_at DESC LIMIT 1;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(taskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<bool> ConsumeAsync(
        string tenantId, string checkpointId, string attemptId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE harness.execution_checkpoints SET consumed_by_attempt_id=$1,consumed_at=$2
            WHERE tenant_id=$3 AND checkpoint_id=$4
              AND (consumed_by_attempt_id IS NULL OR consumed_by_attempt_id=$1);
            """;
        update.Parameters.Add(Text(attemptId));
        update.Parameters.Add(Timestamp(occurredAt));
        update.Parameters.Add(Text(tenantId));
        update.Parameters.Add(Text(checkpointId));
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ExecutionCheckpointRecord>> ListForTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND task_id=$2 ORDER BY created_at;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(taskId));
        var rows = new List<ExecutionCheckpointRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static async Task<ExecutionCheckpointRecord?> ReadAsync(
        NpgsqlConnection connection, string tenantId, string checkpointId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND checkpoint_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(checkpointId));
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static ExecutionCheckpointRecord Map(NpgsqlDataReader reader) => new(
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
        reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
        reader.GetFieldValue<DateTimeOffset>(20));

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value };
}
