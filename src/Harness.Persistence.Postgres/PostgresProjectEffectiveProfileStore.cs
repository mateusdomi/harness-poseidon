using Harness.Persistence.Abstractions.Product;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>Paridade do histórico append-only do perfil efetivo no modo servidor.</summary>
public sealed class PostgresProjectEffectiveProfileStore(NpgsqlDataSource dataSource)
    : IProjectEffectiveProfileStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<ProjectEffectiveProfileSaveResult> SaveAsync(
        ProjectEffectiveProfileSaveCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProfileJson);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadCurrentAsync(
            connection, transaction, command.TenantId, command.ProjectId, cancellationToken);

        if (current is not null &&
            string.Equals(current.Fingerprint, command.Fingerprint, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ProjectEffectiveProfileSaveResult(current, false);
        }

        if (current is not null)
        {
            await using var supersede = connection.CreateCommand();
            supersede.Transaction = (NpgsqlTransaction)transaction;
            supersede.CommandText =
                "UPDATE harness.project_effective_profiles SET status='superseded' " +
                "WHERE tenant_id=$1 AND project_id=$2 AND version=$3;";
            supersede.Parameters.Add(Text(command.TenantId));
            supersede.Parameters.Add(Text(command.ProjectId));
            supersede.Parameters.Add(Integer(current.Version));
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        var version = (current?.Version ?? 0) + 1;
        await using var insert = connection.CreateCommand();
        insert.Transaction = (NpgsqlTransaction)transaction;
        insert.CommandText =
            "INSERT INTO harness.project_effective_profiles " +
            "(tenant_id,project_id,version,fingerprint,baseline_version,modality,profile_json," +
            "resolved_at,resolved_by,status) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,'active');";
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Integer(version));
        insert.Parameters.Add(Text(command.Fingerprint));
        insert.Parameters.Add(Text(command.BaselineVersion));
        insert.Parameters.Add(Text(command.Modality));
        insert.Parameters.Add(Json(command.ProfileJson));
        insert.Parameters.Add(Timestamp(command.ResolvedAt));
        insert.Parameters.Add(Text(command.ResolvedBy));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ProjectEffectiveProfileSaveResult(
            new ProjectEffectiveProfileRecord(
                command.TenantId, command.ProjectId, version, command.Fingerprint,
                command.BaselineVersion, command.Modality, command.ProfileJson,
                command.ResolvedAt, command.ResolvedBy, "active"),
            true);
    }

    public async Task<ProjectEffectiveProfileRecord?> GetCurrentAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadCurrentAsync(connection, null, tenantId, projectId, cancellationToken);
    }

    public async Task<ProjectEffectiveProfileRecord?> GetVersionAsync(
        string tenantId, string projectId, int version, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND project_id=$2 AND version=$3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        query.Parameters.Add(Integer(version));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ProjectEffectiveProfileRecord>> ListAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND project_id=$2 ORDER BY version DESC;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        var rows = new List<ProjectEffectiveProfileRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static async Task<ProjectEffectiveProfileRecord?> ReadCurrentAsync(
        NpgsqlConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = (NpgsqlTransaction?)transaction;
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND project_id=$2 ORDER BY version DESC LIMIT 1;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private const string Select =
        "SELECT tenant_id,project_id,version,fingerprint,baseline_version,modality," +
        "profile_json::text,resolved_at,resolved_by,status FROM harness.project_effective_profiles";

    private static ProjectEffectiveProfileRecord Map(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetFieldValue<string>(6),
        reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9));

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };
    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
}
