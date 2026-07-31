using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed class PostgresRunTargetStore(NpgsqlDataSource dataSource) : IRunTargetStore
{
    private const string SelectPublic =
        "SELECT id,project_id,name,kind,url,port,state,user_facing,detected_at,last_check_at FROM harness.run_targets";

    private const string SelectLaunch =
        "SELECT id,project_id,name,kind,url,port,state,user_facing,detected_at,last_check_at,working_directory,executable,arguments_json::text,environment_json::text FROM harness.run_targets";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> States = ["running", "stopped", "unknown"];

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<IReadOnlyList<RunTargetRecord>> SynchronizeAsync(
        RunTargetSynchronizationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SynchronizeCoreAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<RunTargetRecord>> ListAsync(
        string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ListCoreAsync(connection, tenantId, projectId, afterId, limit, cancellationToken);
    }

    public async Task<RunTargetRecord?> GetAsync(
        string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand($"{SelectPublic} WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublic(reader) : null;
    }

    public async Task<RunTargetLaunchRecord?> GetLaunchAsync(
        string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand($"{SelectLaunch} WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var target = ReadPublic(reader);
        return new RunTargetLaunchRecord(
            target,
            reader.GetString(10),
            reader.GetString(11),
            JsonSerializer.Deserialize<string[]>(reader.GetString(12), JsonOptions) ?? [],
            JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(13), JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public Task<RunTargetRecord> SetStateAsync(
        RunTargetStateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetStateCoreAsync(command, cancellationToken);
    }

    public async Task AppendLogAsync(
        RunTargetLogCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await AppendLogCoreAsync(connection, null, command, cancellationToken);
    }

    public Task<int> CleanupAsync(
        RunTargetCleanupCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CleanupCoreAsync(command, cancellationToken);
    }

    public async Task MarkCheckedAsync(
        string tenantId, string id, DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
    {
        await using var update = _dataSource.CreateCommand(
            "UPDATE harness.run_targets SET last_check_at=$1 WHERE tenant_id=$2 AND id=$3;");
        update.Parameters.Add(Timestamp(checkedAt));
        update.Parameters.Add(Text(tenantId));
        update.Parameters.Add(Text(id));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<RunTargetRecord>> SynchronizeCoreAsync(
        RunTargetSynchronizationCommand command,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(command.ProjectId, out _) || command.Definitions.Count > 100)
        {
            throw new RunTargetValidationException("Run target synchronization is invalid.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProjectExistsAsync(connection, transaction, command.TenantId, command.ProjectId, cancellationToken))
        {
            throw new RunTargetNotFoundException("project");
        }

        var offset = 0;
        foreach (var definition in command.Definitions)
        {
            ValidateDefinition(definition);
            var id = UlidValue.New(command.OccurredAt.AddTicks(offset++)).ToString();
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.run_targets
                    (tenant_id,id,project_id,fingerprint,name,kind,url,port,state,working_directory,
                     executable,arguments_json,environment_json,detected_at,last_check_at,user_facing)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'stopped',$9,$10,$11,$12,$13,$13,$14)
                ON CONFLICT (tenant_id,project_id,fingerprint) DO UPDATE SET
                    name=excluded.name,kind=excluded.kind,
                    working_directory=excluded.working_directory,executable=excluded.executable,
                    last_check_at=excluded.last_check_at,user_facing=excluded.user_facing;
                """,
                cancellationToken,
                Text(command.TenantId),
                Text(id),
                Text(command.ProjectId),
                Text(definition.Fingerprint),
                Text(definition.Name),
                Text(definition.Kind),
                NullableText(definition.Url),
                NullableInteger(definition.Port),
                Text(definition.WorkingDirectory),
                Text(definition.Executable),
                Json(JsonSerializer.Serialize(definition.Arguments, JsonOptions)),
                Json(JsonSerializer.Serialize(definition.Environment, JsonOptions)),
                Timestamp(command.OccurredAt),
                Boolean(definition.UserFacing));
        }

        await transaction.CommitAsync(cancellationToken);
        return await ListCoreAsync(connection, command.TenantId, command.ProjectId, null, 200, cancellationToken);
    }

    private static async Task<IReadOnlyList<RunTargetRecord>> ListCoreAsync(
        NpgsqlConnection connection,
        string tenant,
        string? project,
        string? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var values = new List<RunTargetRecord>();
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{SelectPublic} WHERE tenant_id=$1 AND ($2::text IS NULL OR project_id=$2) AND ($3::text IS NULL OR id>$3) ORDER BY id LIMIT $4;";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(NullableText(project));
        query.Parameters.Add(NullableText(after));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadPublic(reader));
        }

        return values;
    }

    private async Task<RunTargetRecord> SetStateCoreAsync(
        RunTargetStateCommand command,
        CancellationToken cancellationToken)
    {
        if (!States.Contains(command.State) || string.IsNullOrWhiteSpace(command.LogLine) ||
            command.LogLine.Length > 4_000)
        {
            throw new RunTargetValidationException("Run target state is invalid.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadInTransactionAsync(
                connection, transaction, command.TenantId, command.Id, cancellationToken)
            ?? throw new RunTargetNotFoundException("run_target");
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.run_targets SET state=$1,last_check_at=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text(command.State),
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.Id));
        var result = current with { State = command.State, LastCheckAt = command.OccurredAt };
        var auditId = UlidValue.New(command.OccurredAt).ToString();
        var auditPayload = JsonSerializer.Serialize(
            new
            {
                projectId = current.ProjectId,
                auditEvent = new
                {
                    id = auditId,
                    actorKind = "user",
                    actorId = command.ActorProfileId,
                    action = "runTarget.stateChanged",
                    targetType = "run-target",
                    targetId = command.Id,
                    detail = $"State changed from {current.State} to {command.State}.",
                    occurredAt = command.OccurredAt,
                },
            },
            JsonOptions);
        await AppendLedgerAsync(
            connection, transaction, command.TenantId, "runTarget.stateChanged", auditPayload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "audit.eventAppended", auditPayload,
            command.OccurredAt, cancellationToken);
        await AppendLogCoreAsync(
            connection, transaction,
            new RunTargetLogCommand(command.TenantId, current.ProjectId, command.LogLine.Trim(), command.OccurredAt),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task AppendLogCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RunTargetLogCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Line) || command.Line.Length > 4_000)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new { projectId = command.ProjectId, runId = (string?)null, attemptId = (string?)null, line = command.Line },
            JsonOptions);
        if (transaction is null)
        {
            await using var local = await connection.BeginTransactionAsync(cancellationToken);
            await AppendOutboxAsync(
                connection, local, command.TenantId, "run.logAppended", payload,
                command.OccurredAt, cancellationToken);
            await local.CommitAsync(cancellationToken);
        }
        else
        {
            await AppendOutboxAsync(
                connection, transaction, command.TenantId, "run.logAppended", payload,
                command.OccurredAt, cancellationToken);
        }
    }

    private async Task<int> CleanupCoreAsync(
        RunTargetCleanupCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            "UPDATE harness.run_targets SET state='stopped',last_check_at=$1 WHERE tenant_id=$2 AND project_id=$3 AND state<>'stopped';";
        update.Parameters.Add(Timestamp(command.OccurredAt));
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.ProjectId));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        var auditId = UlidValue.New(command.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(
            new
            {
                projectId = command.ProjectId,
                auditEvent = new
                {
                    id = auditId,
                    actorKind = "user",
                    actorId = command.ActorProfileId,
                    action = "run.environmentCleaned",
                    targetType = "project",
                    targetId = command.ProjectId,
                    detail = $"Stopped {changed} managed run target(s).",
                    occurredAt = command.OccurredAt,
                },
            },
            JsonOptions);
        await AppendLedgerAsync(
            connection, transaction, command.TenantId, "run.environmentCleaned", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "audit.eventAppended", payload,
            command.OccurredAt, cancellationToken);
        await AppendLogCoreAsync(
            connection, transaction,
            new RunTargetLogCommand(
                command.TenantId, command.ProjectId,
                $"Cleanup completed: {changed} managed service(s) stopped.", command.OccurredAt),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task<RunTargetRecord?> ReadInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string id,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{SelectPublic} WHERE tenant_id=$1 AND id=$2 FOR UPDATE;";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublic(reader) : null;
    }

    private static async Task<bool> ProjectExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string project,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL);";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(project));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return project state."));
    }

    private static void ValidateDefinition(RunTargetDefinition value)
    {
        if (value.Fingerprint.Length != 64 || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 200 ||
            value.Kind is not ("http" or "tcp" or "process") || value.Port is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(value.WorkingDirectory) || string.IsNullOrWhiteSpace(value.Executable) ||
            value.Arguments.Count > 100 || value.Environment.Count > 100)
        {
            throw new RunTargetValidationException("Detected run target is invalid.");
        }
    }

    private static RunTargetRecord ReadPublic(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetBoolean(7));

    private static async Task AppendLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string type,
        string payload,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenant}"));
        var (sequence, previous) = await ReadLedgerTailAsync(connection, transaction, tenant, cancellationToken);
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(UlidValue.New(at).ToString()),
            Text(tenant),
            Bigint(sequence),
            Text(previous),
            Text(hash),
            Text(type),
            Json(payload),
            Timestamp(at));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string type,
        string payload,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
            cancellationToken,
            Text(UlidValue.New(at).ToString()),
            Text(tenant),
            Text(type),
            Json(PersistenceSanitizer.SanitizeJson(payload)),
            Timestamp(at));

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenant));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter NullableInteger(int? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Integer,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
