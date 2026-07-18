using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Licensing;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteLicenseStore(SqliteWriteDispatcher dispatcher) : ILicenseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly (string Key, string Description, int? Limit)[] ProEntitlements =
    [
        ("projects.max", "Projetos ativos simultâneos", 10),
        ("agents.concurrent", "Agentes concorrentes por projeto", 8),
        ("offline-mode", "Modo offline com fila local", null),
        ("sso.oidc", "Login corporativo via OIDC", null),
        ("audit.retention", "Retenção do log de auditoria (dias)", 90),
    ];
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<LicenseRecord> GetOrCreateAsync(
        LicenseBootstrapCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => GetOrCreateCoreAsync(connection, command, token), cancellationToken);

    public Task<LicenseRecord?> GetAsync(
        string tenantId, string licenseId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ReadAsync(connection, tenantId, licenseId, token), cancellationToken);

    public Task<LicenseRecord> ActivateAsync(
        LicenseActivationCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ActivateCoreAsync(connection, command, token), cancellationToken);

    public Task<IReadOnlyList<EntitlementRecord>> ListEntitlementsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<EntitlementRecord>>(async (connection, token) =>
        {
            var values = new List<EntitlementRecord>(); await using var query = connection.CreateCommand();
            query.CommandText = $"{EntitlementSelect} WHERE tenant_id=$tenant AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(query, "$tenant", tenantId); AddNullable(query, "$after", afterId); Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadEntitlement(reader));
            return values;
        }, cancellationToken);

    public Task<EntitlementRecord?> GetEntitlementAsync(
        string tenantId, string entitlementId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand(); query.CommandText = $"{EntitlementSelect} WHERE tenant_id=$tenant AND id=$id;";
            Add(query, "$tenant", tenantId); Add(query, "$id", entitlementId);
            await using var reader = await query.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadEntitlement(reader) : null;
        }, cancellationToken);

    private static async Task<LicenseRecord> GetOrCreateCoreAsync(
        SqliteConnection connection, LicenseBootstrapCommand command, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction; insert.CommandText =
                """
                INSERT INTO licenses
                    (id,tenant_id,state,plan,device_id,device_name,offline_mode,created_at,updated_at)
                VALUES ($id,$tenant,'unlicensed','Pro',$deviceId,$deviceName,0,$at,$at)
                ON CONFLICT(tenant_id) DO NOTHING;
                """;
            Add(insert, "$id", command.LicenseId); Add(insert, "$tenant", command.TenantId);
            Add(insert, "$deviceId", command.DeviceId); Add(insert, "$deviceName", command.DeviceName);
            Add(insert, "$at", Store(command.OccurredAt)); await insert.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return await ReadByTenantAsync(connection, command.TenantId, token)
            ?? throw new InvalidOperationException("License bootstrap did not create a record.");
    }

    private static async Task<LicenseRecord> ActivateCoreAsync(
        SqliteConnection connection, LicenseActivationCommand command, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var current = await ReadByTenantAsync(connection, command.TenantId, token, transaction)
            ?? throw new LicenseNotFoundException();
        var expires = current.ExpiresAt is null || current.ExpiresAt < command.OccurredAt
            ? command.OccurredAt.AddYears(1)
            : current.ExpiresAt.Value;
        var grace = expires.AddDays(14);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Key)));
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction; update.CommandText =
                """
                UPDATE licenses SET state='active',plan='Pro',expires_at=$expires,
                    grace_period_ends_at=$grace,offline_mode=0,last_validated_at=$at,
                    activation_key_hash=$hash,updated_at=$at
                WHERE tenant_id=$tenant;
                """;
            Add(update, "$expires", Store(expires)); Add(update, "$grace", Store(grace));
            Add(update, "$at", Store(command.OccurredAt)); Add(update, "$hash", hash);
            Add(update, "$tenant", command.TenantId); await update.ExecuteNonQueryAsync(token);
        }
        for (var index = 0; index < ProEntitlements.Length; index++)
        {
            var definition = ProEntitlements[index]; await using var insert = connection.CreateCommand();
            insert.Transaction = transaction; insert.CommandText =
                """
                INSERT INTO entitlements
                    (id,tenant_id,license_id,key,description,included,numeric_limit)
                VALUES ($id,$tenant,$license,$key,$description,$included,$limit)
                ON CONFLICT(tenant_id,key) DO UPDATE SET
                    license_id=excluded.license_id,description=excluded.description,
                    included=excluded.included,numeric_limit=excluded.numeric_limit;
                """;
            Add(insert, "$id", UlidValue.New(command.OccurredAt.AddTicks(index + 1)).ToString());
            Add(insert, "$tenant", command.TenantId); Add(insert, "$license", current.Id);
            Add(insert, "$key", definition.Key); Add(insert, "$description", definition.Description);
            Add(insert, "$included", definition.Key == "sso.oidc" ? 0 : 1);
            AddNullable(insert, "$limit", definition.Limit); await insert.ExecuteNonQueryAsync(token);
        }
        var masked = $"{command.Key[..4]}-****-****-****";
        var auditId = UlidValue.New(command.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            auditEvent = new
            {
                id = auditId,
                actorKind = "user",
                actorId = command.ProfileId,
                action = "license.activated",
                targetType = "license",
                targetId = current.Id,
                detail = $"License Pro activated on this device with key {masked}.",
                occurredAt = command.OccurredAt,
            },
        }, JsonOptions);
        await AppendLedgerAsync(connection, transaction, command.TenantId, "license.activated", payload, command.OccurredAt, token);
        await AppendOutboxAsync(connection, transaction, command.TenantId, "audit.eventAppended", payload, command.OccurredAt, token);
        await transaction.CommitAsync(token);
        return new(current.TenantId, current.Id, "active", "Pro", current.DeviceId, current.DeviceName,
            expires, grace, false, command.OccurredAt);
    }

    private static async Task<LicenseRecord?> ReadAsync(SqliteConnection connection, string tenant, string id, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.CommandText = $"{LicenseSelect} WHERE tenant_id=$tenant AND id=$id;";
        Add(query, "$tenant", tenant); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadLicense(reader) : null;
    }

    private static async Task<LicenseRecord?> ReadByTenantAsync(
        SqliteConnection connection, string tenant, CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = $"{LicenseSelect} WHERE tenant_id=$tenant;"; Add(query, "$tenant", tenant);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadLicense(reader) : null;
    }

    private static LicenseRecord ReadLicense(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), NullableDate(reader, 6), NullableDate(reader, 7),
        reader.GetInt32(8) == 1, NullableDate(reader, 9));
    private static EntitlementRecord ReadEntitlement(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetInt32(5) == 1, reader.IsDBNull(6) ? null : reader.GetInt32(6));

    private static async Task AppendLedgerAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token)
    {
        long sequence; string previous; await using (var query = c.CreateCommand())
        {
            query.Transaction = tx; query.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            Add(query, "$tenant", tenant); await using var reader = await query.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); }
            else { sequence = 1; previous = AuditLedgerHash.Genesis; }
        }
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
        await ExecuteAsync(c, tx, "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);", token,
            ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$sequence", sequence), ("$previous", previous), ("$hash", hash), ("$type", type), ("$payload", payload), ("$at", Store(at)));
    }
    private static Task AppendOutboxAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token) =>
        ExecuteAsync(c, tx, "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($id,$tenant,$type,$payload,$at);", token,
            ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$type", type), ("$payload", payload), ("$at", Store(at)));
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string Name, object Value)[] values)
    { await using var query = c.CreateCommand(); query.Transaction = tx; query.CommandText = sql; foreach (var value in values) Add(query, value.Name, value.Value); await query.ExecuteNonQueryAsync(token); }
    private const string LicenseSelect = "SELECT tenant_id,id,state,plan,device_id,device_name,expires_at,grace_period_ends_at,offline_mode,last_validated_at FROM licenses";
    private const string EntitlementSelect = "SELECT tenant_id,id,license_id,key,description,included,numeric_limit FROM entitlements";
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
