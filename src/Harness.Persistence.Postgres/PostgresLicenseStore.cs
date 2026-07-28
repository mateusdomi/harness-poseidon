using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Licensing;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresLicenseStore(NpgsqlDataSource dataSource) : ILicenseStore
{
    private const string LicenseSelect =
        "SELECT tenant_id,id,state,plan,device_id,device_name,expires_at,grace_period_ends_at,offline_mode,last_validated_at FROM harness.licenses";

    private const string EntitlementSelect =
        "SELECT tenant_id,id,license_id,key,description,included,numeric_limit FROM harness.entitlements";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly (string Key, string Description, int? Limit)[] ProEntitlements =
    [
        ("projects.max", "Projetos ativos simultâneos", 10),
        ("agents.concurrent", "Agentes concorrentes por projeto", 8),
        ("offline-mode", "Modo offline com fila local", null),
        ("sso.oidc", "Login corporativo via OIDC", null),
        ("audit.retention", "Retenção do log de auditoria (dias)", 90),
        ("presentation.technical", "Detalhes técnicos autorizados", null),
    ];

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<LicenseRecord> GetOrCreateAsync(
        LicenseBootstrapCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return GetOrCreateCoreAsync(command, cancellationToken);
    }

    public async Task<LicenseRecord?> GetAsync(
        string tenantId, string licenseId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand($"{LicenseSelect} WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(licenseId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLicense(reader) : null;
    }

    public Task<LicenseRecord> ActivateAsync(
        LicenseActivationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ActivateCoreAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<EntitlementRecord>> ListEntitlementsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        var values = new List<EntitlementRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{EntitlementSelect} WHERE tenant_id=$1 AND ($2::text IS NULL OR id>$2) ORDER BY id LIMIT $3;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadEntitlement(reader));
        }

        return values;
    }

    public async Task<EntitlementRecord?> GetEntitlementAsync(
        string tenantId, string entitlementId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand($"{EntitlementSelect} WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(entitlementId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntitlement(reader) : null;
    }

    private async Task<LicenseRecord> GetOrCreateCoreAsync(
        LicenseBootstrapCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO harness.licenses
                    (id,tenant_id,state,plan,device_id,device_name,offline_mode,created_at,updated_at)
                VALUES ($1,$2,'unlicensed','Pro',$3,$4,false,$5,$5)
                ON CONFLICT (tenant_id) DO NOTHING;
                """;
            insert.Parameters.Add(Text(command.LicenseId));
            insert.Parameters.Add(Text(command.TenantId));
            insert.Parameters.Add(Text(command.DeviceId));
            insert.Parameters.Add(Text(command.DeviceName));
            insert.Parameters.Add(Timestamp(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ReadByTenantAsync(connection, command.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("License bootstrap did not create a record.");
    }

    private async Task<LicenseRecord> ActivateCoreAsync(
        LicenseActivationCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadByTenantAsync(connection, command.TenantId, cancellationToken, transaction)
            ?? throw new LicenseNotFoundException();
        var expires = current.ExpiresAt is null || current.ExpiresAt < command.OccurredAt
            ? command.OccurredAt.AddYears(1)
            : current.ExpiresAt.Value;
        var grace = expires.AddDays(14);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Key)));
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE harness.licenses SET state='active',plan='Pro',expires_at=$1,
                    grace_period_ends_at=$2,offline_mode=false,last_validated_at=$3,
                    activation_key_hash=$4,updated_at=$3
                WHERE tenant_id=$5;
                """;
            update.Parameters.Add(Timestamp(expires));
            update.Parameters.Add(Timestamp(grace));
            update.Parameters.Add(Timestamp(command.OccurredAt));
            update.Parameters.Add(Text(hash));
            update.Parameters.Add(Text(command.TenantId));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < ProEntitlements.Length; index++)
        {
            var definition = ProEntitlements[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO harness.entitlements
                    (id,tenant_id,license_id,key,description,included,numeric_limit)
                VALUES ($1,$2,$3,$4,$5,$6,$7)
                ON CONFLICT (tenant_id,key) DO UPDATE SET
                    license_id=excluded.license_id,description=excluded.description,
                    included=excluded.included,numeric_limit=excluded.numeric_limit;
                """;
            insert.Parameters.Add(Text(UlidValue.New(command.OccurredAt.AddTicks(index + 1)).ToString()));
            insert.Parameters.Add(Text(command.TenantId));
            insert.Parameters.Add(Text(current.Id));
            insert.Parameters.Add(Text(definition.Key));
            insert.Parameters.Add(Text(definition.Description));
            insert.Parameters.Add(Boolean(definition.Key != "sso.oidc"));
            insert.Parameters.Add(NullableInteger(definition.Limit));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var masked = $"{command.Key[..4]}-****-****-****";
        var auditId = UlidValue.New(command.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(
            new
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
            },
            JsonOptions);
        await AppendLedgerAsync(
            connection, transaction, command.TenantId, "license.activated", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "audit.eventAppended", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LicenseRecord(
            current.TenantId, current.Id, "active", "Pro", current.DeviceId, current.DeviceName,
            expires, grace, false, command.OccurredAt);
    }

    private static async Task<LicenseRecord?> ReadByTenantAsync(
        NpgsqlConnection connection,
        string tenant,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{LicenseSelect} WHERE tenant_id=$1{(transaction is null ? "" : " FOR UPDATE")};";
        query.Parameters.Add(Text(tenant));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLicense(reader) : null;
    }

    private static LicenseRecord ReadLicense(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetBoolean(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));

    private static EntitlementRecord ReadEntitlement(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetBoolean(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6));

    private static async Task AppendLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string type,
        string payload,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
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
            Json(payload),
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

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

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
