using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Licensing;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteSignedLicenseStore(SqliteWriteDispatcher dispatcher) : ISignedLicenseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<SignedLicenseRecord> SaveAsync(
        SignedLicenseSaveCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO signed_licenses
                    (tenant_id,license_id,licensee,device_fingerprint,issued_at,expires_at,
                     grace_days,entitlements_json,document_json,signature,activated_at)
                VALUES ($tenant,$license,$licensee,$device,$issued,$expires,$grace,$entitlements,
                        $document,$signature,$activated)
                ON CONFLICT (tenant_id, license_id) DO UPDATE SET
                    activated_at=excluded.activated_at,
                    document_json=excluded.document_json,
                    signature=excluded.signature;
                """;
            insert.Parameters.AddWithValue("$tenant", command.TenantId);
            insert.Parameters.AddWithValue("$license", command.LicenseId);
            insert.Parameters.AddWithValue("$licensee", command.Licensee);
            insert.Parameters.AddWithValue(
                "$device", command.DeviceFingerprint ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$issued", Store(command.IssuedAt));
            insert.Parameters.AddWithValue("$expires", Store(command.ExpiresAt));
            insert.Parameters.AddWithValue("$grace", command.GraceDays);
            insert.Parameters.AddWithValue(
                "$entitlements", JsonSerializer.Serialize(command.Entitlements, JsonOptions));
            insert.Parameters.AddWithValue("$document", command.DocumentJson);
            insert.Parameters.AddWithValue("$signature", command.Signature);
            insert.Parameters.AddWithValue("$activated", Store(command.ActivatedAt));
            await insert.ExecuteNonQueryAsync(token);
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
        }, cancellationToken);
    }

    public Task<SignedLicenseRecord?> GetCurrentAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT tenant_id,license_id,licensee,device_fingerprint,issued_at,expires_at," +
                "grace_days,entitlements_json,document_json,signature,activated_at " +
                "FROM signed_licenses WHERE tenant_id=$tenant " +
                "ORDER BY activated_at DESC, license_id DESC LIMIT 1;";
            query.Parameters.AddWithValue("$tenant", tenantId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            return new SignedLicenseRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Load(reader.GetString(4)),
                Load(reader.GetString(5)),
                reader.GetInt32(6),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? [],
                reader.GetString(8),
                reader.GetString(9),
                Load(reader.GetString(10)));
        }, cancellationToken);

    public Task<int> AddRevocationsAsync(
        string tenantId,
        IReadOnlyList<string> licenseIds,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseIds);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var added = 0;
            foreach (var licenseId in licenseIds)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT OR IGNORE INTO license_revocations (tenant_id,license_id,revoked_at) " +
                    "VALUES ($tenant,$license,$at);";
                insert.Parameters.AddWithValue("$tenant", tenantId);
                insert.Parameters.AddWithValue("$license", licenseId);
                insert.Parameters.AddWithValue("$at", Store(revokedAt));
                added += await insert.ExecuteNonQueryAsync(token);
            }

            return added;
        }, cancellationToken);
    }

    public Task<bool> IsRevokedAsync(
        string tenantId,
        string licenseId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT EXISTS(SELECT 1 FROM license_revocations " +
                "WHERE tenant_id=$tenant AND license_id=$license);";
            query.Parameters.AddWithValue("$tenant", tenantId);
            query.Parameters.AddWithValue("$license", licenseId);
            return Convert.ToInt32(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
        }, cancellationToken);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Load(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
