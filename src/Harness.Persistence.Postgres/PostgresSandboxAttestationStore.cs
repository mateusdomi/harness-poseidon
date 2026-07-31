using System.Text.Json;
using Harness.Persistence.Abstractions.Execution;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL da attestation de sandbox e do aceite de modo inseguro (Fase 0B1).
/// Paridade semântica exata com o SQLite.
/// </summary>
public sealed class PostgresSandboxAttestationStore(NpgsqlDataSource dataSource)
    : ISandboxAttestationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<SandboxAttestationRecord> SaveAsync(
        SandboxAttestationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        // A PRIMEIRA emissão é a que vale: uma segunda não pode "melhorar" a avaliação de uma
        // execução em curso — seria a porta para atestar depois o que não foi contido antes.
        insert.CommandText =
            """
            INSERT INTO harness.sandbox_attestations
                (tenant_id,attempt_id,project_id,provider,provider_version,sandbox_identity,
                 mounts_json,network_policy,root_filesystem_read_only,worktree_isolated,
                 egress_restricted,resource_limits_applied,verified,verification_detail,
                 configuration_hash,issued_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            ON CONFLICT (tenant_id,attempt_id) DO NOTHING;
            """;
        insert.Parameters.Add(Text(record.TenantId));
        insert.Parameters.Add(Text(record.AttemptId));
        insert.Parameters.Add(Text(record.ProjectId));
        insert.Parameters.Add(Text(record.Provider));
        insert.Parameters.Add(Text(record.ProviderVersion));
        insert.Parameters.Add(Text(record.SandboxIdentity));
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(record.Mounts, JsonOptions)));
        insert.Parameters.Add(Text(record.NetworkPolicy));
        insert.Parameters.Add(Boolean(record.RootFilesystemReadOnly));
        insert.Parameters.Add(Boolean(record.WorktreeIsolated));
        insert.Parameters.Add(Boolean(record.EgressRestricted));
        insert.Parameters.Add(Boolean(record.ResourceLimitsApplied));
        insert.Parameters.Add(Boolean(record.Verified));
        insert.Parameters.Add(Text(record.VerificationDetail));
        insert.Parameters.Add(Text(record.ConfigurationHash));
        insert.Parameters.Add(Timestamp(record.IssuedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        var stored = await ReadAsync(
                connection, tx, record.TenantId, record.AttemptId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The persisted sandbox attestation could not be read back.");
        await tx.CommitAsync(cancellationToken);
        return stored;
    }

    public async Task<SandboxAttestationRecord?> GetAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, attemptId, cancellationToken);
    }

    public async Task<UnsafeExecutionAcceptanceRecord?> GetUnsafeAcceptanceAsync(
        string tenantId, string projectId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        // Vencido ou revogado conta como AUSENTE: um aceite de ontem não autoriza hoje.
        q.CommandText =
            "SELECT tenant_id,project_id,accepted_by_profile_id,reason,accepted_at,expires_at,revoked_at " +
            "FROM harness.unsafe_execution_acceptances " +
            "WHERE tenant_id=$1 AND project_id=$2 AND revoked_at IS NULL AND expires_at>$3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        q.Parameters.Add(Timestamp(now));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new UnsafeExecutionAcceptanceRecord(
                r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
                r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
                r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6))
            : null;
    }

    public async Task<UnsafeExecutionAcceptanceRecord> AcceptUnsafeAsync(
        UnsafeExecutionAcceptanceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            """
            INSERT INTO harness.unsafe_execution_acceptances
                (tenant_id,project_id,accepted_by_profile_id,reason,accepted_at,expires_at,revoked_at)
            VALUES ($1,$2,$3,$4,$5,$6,NULL)
            ON CONFLICT (tenant_id,project_id) DO UPDATE SET
                accepted_by_profile_id=excluded.accepted_by_profile_id,
                reason=excluded.reason,
                accepted_at=excluded.accepted_at,
                expires_at=excluded.expires_at,
                revoked_at=NULL;
            """;
        q.Parameters.Add(Text(record.TenantId));
        q.Parameters.Add(Text(record.ProjectId));
        q.Parameters.Add(Text(record.AcceptedByProfileId));
        q.Parameters.Add(Text(record.Reason));
        q.Parameters.Add(Timestamp(record.AcceptedAt));
        q.Parameters.Add(Timestamp(record.ExpiresAt));
        await q.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<bool> RevokeUnsafeAsync(
        string tenantId, string projectId, DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            "UPDATE harness.unsafe_execution_acceptances SET revoked_at=$1 " +
            "WHERE tenant_id=$2 AND project_id=$3 AND revoked_at IS NULL;";
        q.Parameters.Add(Timestamp(revokedAt));
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        return await q.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<SandboxAttestationRecord?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string tenantId, string attemptId,
        CancellationToken cancellationToken)
    {
        await using var q = connection.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT tenant_id,attempt_id,project_id,provider,provider_version,sandbox_identity," +
            "mounts_json::text,network_policy,root_filesystem_read_only,worktree_isolated," +
            "egress_restricted,resource_limits_applied,verified,verification_detail," +
            "configuration_hash,issued_at FROM harness.sandbox_attestations " +
            "WHERE tenant_id=$1 AND attempt_id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(attemptId));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new SandboxAttestationRecord(
                r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
                r.GetString(3), r.GetString(4), r.GetString(5),
                JsonSerializer.Deserialize<string[]>(r.GetString(6), JsonOptions) ?? [],
                r.GetString(7), r.GetBoolean(8), r.GetBoolean(9), r.GetBoolean(10),
                r.GetBoolean(11), r.GetBoolean(12), r.GetString(13), r.GetString(14),
                r.GetFieldValue<DateTimeOffset>(15))
            : null;
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
