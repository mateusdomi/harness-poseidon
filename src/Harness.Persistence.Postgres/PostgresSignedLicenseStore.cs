using System.Text.Json;
using Harness.Persistence.Abstractions.Licensing;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresSignedLicenseStore(NpgsqlDataSource dataSource) : ISignedLicenseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<SignedLicenseRecord> SaveAsync(
        SignedLicenseSaveCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SaveCoreAsync(command, cancellationToken);
    }

    public async Task<SignedLicenseRecord?> GetCurrentAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            "SELECT tenant_id,license_id,licensee,device_fingerprint,issued_at,expires_at," +
            "grace_days,entitlements_json::text,document_json::text,signature,activated_at " +
            "FROM harness.signed_licenses WHERE tenant_id=$1 " +
            "ORDER BY activated_at DESC, license_id DESC LIMIT 1;");
        query.Parameters.Add(Text(tenantId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SignedLicenseRecord(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetInt32(6),
            JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? [],
            reader.GetString(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10));
    }

    public async Task<int> AddRevocationsAsync(
        string tenantId,
        IReadOnlyList<string> licenseIds,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseIds);
        var added = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var licenseId in licenseIds)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO harness.license_revocations (tenant_id,license_id,revoked_at) " +
                "VALUES ($1,$2,$3) ON CONFLICT (tenant_id,license_id) DO NOTHING;";
            insert.Parameters.Add(Text(tenantId));
            insert.Parameters.Add(Text(licenseId));
            insert.Parameters.Add(Timestamp(revokedAt));
            added += await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return added;
    }

    public async Task<bool> IsRevokedAsync(
        string tenantId,
        string licenseId,
        CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM harness.license_revocations " +
            "WHERE tenant_id=$1 AND license_id=$2);");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(licenseId));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return revocation state."));
    }

    private async Task<SignedLicenseRecord> SaveCoreAsync(
        SignedLicenseSaveCommand command,
        CancellationToken cancellationToken)
    {
        await using var insert = _dataSource.CreateCommand(
            """
            INSERT INTO harness.signed_licenses
                (tenant_id,license_id,licensee,device_fingerprint,issued_at,expires_at,
                 grace_days,entitlements_json,document_json,signature,activated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            ON CONFLICT (tenant_id, license_id) DO UPDATE SET
                activated_at=excluded.activated_at,
                document_json=excluded.document_json,
                signature=excluded.signature;
            """);
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.LicenseId));
        insert.Parameters.Add(Text(command.Licensee));
        insert.Parameters.Add(NullableText(command.DeviceFingerprint));
        insert.Parameters.Add(Timestamp(command.IssuedAt));
        insert.Parameters.Add(Timestamp(command.ExpiresAt));
        insert.Parameters.Add(Integer(command.GraceDays));
        insert.Parameters.Add(Jsonb(JsonSerializer.Serialize(command.Entitlements, JsonOptions)));
        insert.Parameters.Add(Json(command.DocumentJson));
        insert.Parameters.Add(Text(command.Signature));
        insert.Parameters.Add(Timestamp(command.ActivatedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new SignedLicenseRecord(
            command.TenantId,
            command.LicenseId,
            command.Licensee,
            command.DeviceFingerprint,
            command.IssuedAt,
            command.ExpiresAt,
            command.GraceDays,
            command.Entitlements,
            command.DocumentJson,
            command.Signature,
            command.ActivatedAt);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Jsonb(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Json,
        TypedValue = value,
    };
}
