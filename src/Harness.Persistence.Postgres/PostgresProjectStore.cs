using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresProjectStore(NpgsqlDataSource dataSource) : IProjectStore
{
    private const string SelectSql =
        """
        SELECT tenant_id,id,organization_id,name,project_key,description,state,criticality,
               repository_url,repository_provider,default_branch,technologies_json::text,logo_url,
               primary_color,secondary_color,typography,member_profile_ids_json::text,config_version,
               chief_agent_id,operation_mode,created_at,COALESCE(last_activity_at,created_at),version,
               prototyping_mode,prototyping_waiver_reason,prototyping_waiver_granted_at,target_deadline
        FROM harness.projects
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<ProjectRecord?> GetAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, tenantId, projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListAsync(
        string tenantId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<ProjectRecord>();
        await using var command = _dataSource.CreateCommand(
            $"{SelectSql} WHERE tenant_id=$1 AND deleted_at IS NULL AND ($2::text IS NULL OR id>$2) ORDER BY id LIMIT $3;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(Read(reader));
        }

        return values;
    }

    public Task<ProjectMutationResult> CreateAsync(
        ProjectCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public Task<ProjectMutationResult> UpdateAsync(
        ProjectUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateCoreAsync(command, cancellationToken);
    }

    public async Task<ProjectMutationResult> DeleteAsync(
        string tenantId,
        string projectId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var delete = connection.CreateCommand();
        delete.CommandText =
            """
            UPDATE harness.projects SET deleted_at=$1,version=version+1
            WHERE tenant_id=$2 AND id=$3 AND deleted_at IS NULL AND version=$4;
            """;
        delete.Parameters.Add(Timestamp(occurredAt));
        delete.Parameters.Add(Text(tenantId));
        delete.Parameters.Add(Text(projectId));
        delete.Parameters.Add(Bigint(expectedVersion));
        if (await delete.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return new ProjectMutationResult(ProjectMutationStatus.Applied);
        }

        return new ProjectMutationResult(
            await ReadAsync(connection, tenantId, projectId, cancellationToken) is null
                ? ProjectMutationStatus.NotFound
                : ProjectMutationStatus.VersionConflict);
    }

    private async Task<ProjectMutationResult> CreateCoreAsync(
        ProjectCreateCommand command,
        CancellationToken cancellationToken)
    {
        var project = command.Project;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText =
                "SELECT EXISTS(SELECT 1 FROM harness.organizations WHERE tenant_id=$1 AND id=$2);";
            exists.Parameters.Add(Text(command.TenantId));
            exists.Parameters.Add(Text(project.OrganizationId));
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not return organization state.")))
            {
                await transaction.CommitAsync(cancellationToken);
                return new ProjectMutationResult(ProjectMutationStatus.OrganizationNotFound);
            }
        }

        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO harness.projects
                        (id,tenant_id,organization_id,name,project_key,description,state,criticality,
                         repository_url,repository_provider,default_branch,technologies_json,logo_url,
                         primary_color,secondary_color,typography,member_profile_ids_json,config_version,
                         chief_agent_id,operation_mode,prototyping_mode,prototyping_waiver_reason,
                         prototyping_waiver_granted_at,target_deadline,version,created_at,last_activity_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,
                            $21,$22,$23,$24,1,$25,$25);
                    """;
                Bind(insert, command.TenantId, project);
                insert.Parameters.Add(Timestamp(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var chief = connection.CreateCommand())
            {
                chief.Transaction = transaction;
                chief.CommandText =
                    """
                    INSERT INTO harness.agents
                        (id,tenant_id,definition_id,project_id,name,state,lease_fencing_token,
                         lease_expires_at,last_heartbeat_at,created_at)
                    VALUES ($1,$2,'01ARZ3NDEKTSV4RRFFQ69G5FAV',$3,$4,'idle',1,$5,$6,$6);
                    """;
                chief.Parameters.Add(Text(project.ChiefAgentId));
                chief.Parameters.Add(Text(command.TenantId));
                chief.Parameters.Add(Text(project.Id));
                chief.Parameters.Add(Text($"Chief — {project.Key}"));
                chief.Parameters.Add(Timestamp(command.OccurredAt.AddMinutes(1)));
                chief.Parameters.Add(Timestamp(command.OccurredAt));
                await chief.ExecuteNonQueryAsync(cancellationToken);
            }

            var payload = JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                organizationId = project.OrganizationId,
                name = project.Name,
                key = project.Key,
            });
            await ExecuteAsync(
                connection,
                transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
                cancellationToken,
                Text($"audit-ledger:{command.TenantId}"));
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection,
                transaction,
                command.TenantId,
                cancellationToken);
            const string eventType = "project.created";
            var eventHash = AuditLedgerHash.Compute(
                previousHash,
                command.TenantId,
                sequence,
                eventType,
                payload,
                command.OccurredAt);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.audit_ledger
                    (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Bigint(sequence),
                Text(previousHash),
                Text(eventHash),
                Text(eventType),
                Json(payload),
                Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt.AddTicks(1)).ToString()),
                Text(command.TenantId),
                Text(eventType),
                Json(payload),
                Timestamp(command.OccurredAt));
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProjectMutationResult(ProjectMutationStatus.AlreadyExists);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ProjectMutationResult(
            ProjectMutationStatus.Applied,
            await ReadAsync(connection, command.TenantId, project.Id, cancellationToken));
    }

    private async Task<ProjectMutationResult> UpdateCoreAsync(
        ProjectUpdateCommand command,
        CancellationToken cancellationToken)
    {
        var project = command.Project;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE harness.projects
                SET name=$4,description=$6,state=$7,criticality=$8,repository_url=$9,
                    repository_provider=$10,default_branch=$11,technologies_json=$12,logo_url=$13,
                    primary_color=$14,secondary_color=$15,typography=$16,member_profile_ids_json=$17,
                    config_version=$18,prototyping_mode=$21,prototyping_waiver_reason=$22,
                    prototyping_waiver_granted_at=$23,target_deadline=$24,last_activity_at=$25,version=version+1
                WHERE tenant_id=$2 AND id=$1 AND deleted_at IS NULL AND version=$26;
                """;
            Bind(update, project.TenantId, project);
            update.Parameters.Add(Timestamp(project.LastActivityAt));
            update.Parameters.Add(Bigint(command.ExpectedVersion));
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return new ProjectMutationResult(
                    ProjectMutationStatus.Applied,
                    await ReadAsync(connection, project.TenantId, project.Id, cancellationToken));
            }
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            return new ProjectMutationResult(ProjectMutationStatus.AlreadyExists);
        }

        return new ProjectMutationResult(
            await ReadAsync(connection, project.TenantId, project.Id, cancellationToken) is null
                ? ProjectMutationStatus.NotFound
                : ProjectMutationStatus.VersionConflict);
    }

    private static async Task<ProjectRecord?> ReadAsync(
        NpgsqlConnection connection,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectSql} WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL;";
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(projectId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static ProjectRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            Deserialize(reader.GetString(11)),
            new ProjectBrandRecord(
                Nullable(reader, 12),
                Nullable(reader, 13),
                Nullable(reader, 14),
                Nullable(reader, 15)),
            Deserialize(reader.GetString(16)),
            reader.GetInt64(17),
            reader.GetString(18).TrimEnd(),
            reader.GetString(19),
            reader.GetFieldValue<DateTimeOffset>(20),
            reader.GetFieldValue<DateTimeOffset>(21),
            reader.GetInt64(22))
        {
            Prototyping = new(
                reader.GetString(23),
                reader.IsDBNull(24)
                    ? null
                    : new(reader.GetString(24), reader.GetFieldValue<DateTimeOffset>(25))),
            TargetDeadline = reader.IsDBNull(26) ? null : reader.GetFieldValue<DateTimeOffset>(26),
        };

    private static void Bind(NpgsqlCommand command, string tenantId, ProjectRecord project)
    {
        command.Parameters.Add(Text(project.Id));
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(project.OrganizationId));
        command.Parameters.Add(Text(project.Name));
        command.Parameters.Add(Text(project.Key));
        command.Parameters.Add(Text(project.Description));
        command.Parameters.Add(Text(project.State));
        command.Parameters.Add(Text(project.Criticality));
        command.Parameters.Add(NullableText(project.RepositoryUrl));
        command.Parameters.Add(Text(project.RepositoryProvider));
        command.Parameters.Add(Text(project.DefaultBranch));
        command.Parameters.Add(Json(JsonSerializer.Serialize(project.Technologies, JsonOptions)));
        command.Parameters.Add(NullableText(project.Brand.LogoUrl));
        command.Parameters.Add(NullableText(project.Brand.PrimaryColor));
        command.Parameters.Add(NullableText(project.Brand.SecondaryColor));
        command.Parameters.Add(NullableText(project.Brand.Typography));
        command.Parameters.Add(Json(JsonSerializer.Serialize(project.MemberProfileIds, JsonOptions)));
        command.Parameters.Add(Bigint(project.ConfigVersion));
        command.Parameters.Add(Text(project.ChiefAgentId));
        command.Parameters.Add(Text(project.OperationMode));
        command.Parameters.Add(Text(project.Prototyping.Mode));
        command.Parameters.Add(NullableText(project.Prototyping.Waiver?.Reason));
        command.Parameters.Add(NullableTimestamp(project.Prototyping.Waiver?.GrantedAt));
        command.Parameters.Add(NullableTimestamp(project.TargetDeadline));
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
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

    private static string[] Deserialize(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("Persisted project JSON cannot be null.");

    private static string? Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool IsConstraintViolation(PostgresException exception) =>
        exception.SqlState.StartsWith("23", StringComparison.Ordinal);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.TimestampTz,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
