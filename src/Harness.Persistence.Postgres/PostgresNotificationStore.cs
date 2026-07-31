using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Notifications;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed class PostgresNotificationStore(NpgsqlDataSource dataSource) : INotificationStore
{
    private const string NotificationSelect =
        "SELECT id,profile_id,severity,category,title,body,group_key,dedupe_count,status,link,created_at,read_at FROM harness.notifications";

    private const string SettingsSelect =
        "SELECT id,profile_id,theme,language,notifications_enabled,muted_categories_json::text,working_directory,unsafe_mode_accepted_at,updated_at FROM harness.profile_settings";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Severities = ["info", "warning", "error", "critical"];
    private static readonly HashSet<string> Categories = ["system", "task", "approval", "quota", "license", "chat", "workflow"];
    private static readonly HashSet<string> Themes = ["dark", "light", "system"];

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<NotificationRecord>> ListAsync(
        string tenantId,
        string profileId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<NotificationRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{NotificationSelect} WHERE tenant_id=$1 AND profile_id=$2 AND ($3::text IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(profileId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadNotification(reader));
        }

        return values;
    }

    public async Task<NotificationRecord?> GetAsync(
        string tenantId,
        string profileId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadNotificationAsync(connection, null, tenantId, profileId, id, cancellationToken);
    }

    public Task<NotificationRecord> CreateAsync(
        NotificationCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public Task<int> SetStatusAsync(
        NotificationStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetStatusCoreAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<SettingsRecord>> ListSettingsAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var values = new List<SettingsRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{SettingsSelect} WHERE tenant_id=$1 AND profile_id=$2 ORDER BY id;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(profileId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSettings(reader));
        }

        return values;
    }

    public async Task<SettingsRecord?> GetSettingsAsync(
        string tenantId,
        string profileId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            $"{SettingsSelect} WHERE tenant_id=$1 AND profile_id=$2 AND id=$3;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(profileId));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettings(reader) : null;
    }

    public Task<SettingsRecord> UpdateSettingsAsync(
        SettingsUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateSettingsCoreAsync(command, cancellationToken);
    }

    private async Task<NotificationRecord> CreateCoreAsync(
        NotificationCreateCommand command,
        CancellationToken cancellationToken)
    {
        ValidateNotification(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"notifications:{command.TenantId}:{command.ProfileId}"));
        if (!await ProfileExistsAsync(connection, transaction, command.TenantId, command.ProfileId, cancellationToken))
        {
            throw new NotificationNotFoundException("profile");
        }

        NotificationRecord result;
        if (command.GroupKey is not null &&
            await ReadGroupedAsync(connection, transaction, command, cancellationToken) is { } grouped)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE harness.notifications SET severity=$1,category=$2,title=$3,body=$4,link=$5,dedupe_count=dedupe_count+1,created_at=$6 WHERE tenant_id=$7 AND id=$8;",
                cancellationToken,
                Text(command.Severity),
                Text(command.Category),
                Text(command.Title.Trim()),
                Text(command.Body.Trim()),
                NullableText(command.Link),
                Timestamp(command.OccurredAt),
                Text(command.TenantId),
                Text(grouped.Id));
            result = grouped with
            {
                Severity = command.Severity,
                Category = command.Category,
                Title = command.Title.Trim(),
                Body = command.Body.Trim(),
                Link = command.Link,
                DedupeCount = grouped.DedupeCount + 1,
                CreatedAt = command.OccurredAt,
            };
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO harness.notifications (tenant_id,id,profile_id,severity,category,title,body,group_key,dedupe_count,status,link,created_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,1,'unread',$9,$10);",
                cancellationToken,
                Text(command.TenantId),
                Text(command.Id),
                Text(command.ProfileId),
                Text(command.Severity),
                Text(command.Category),
                Text(command.Title.Trim()),
                Text(command.Body.Trim()),
                NullableText(command.GroupKey),
                NullableText(command.Link),
                Timestamp(command.OccurredAt));
            result = new NotificationRecord(
                command.Id, command.ProfileId, command.Severity, command.Category, command.Title.Trim(),
                command.Body.Trim(), command.GroupKey, 1, "unread", command.Link, command.OccurredAt, null);
        }

        var payload = JsonSerializer.Serialize(new { notification = result }, JsonOptions);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "notification.created", payload,
            command.OccurredAt, cancellationToken);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, command.ProfileId, "notification.created",
            "notification", result.Id, "Notification created or coalesced.", command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<int> SetStatusCoreAsync(
        NotificationStatusCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Status is not ("read" or "muted") || command.Ids.Count is < 1 or > 200 ||
            command.Ids.Any(id => !UlidValue.TryParse(id, out _)))
        {
            throw new NotificationValidationException("Notification status command is invalid.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var changed = 0;
        foreach (var id in command.Ids.Distinct(StringComparer.Ordinal))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE harness.notifications SET status=$1,read_at=CASE WHEN $1='read' THEN $2 ELSE read_at END WHERE tenant_id=$3 AND profile_id=$4 AND id=$5 AND status<>$1;";
            update.Parameters.Add(Text(command.Status));
            update.Parameters.Add(Timestamp(command.OccurredAt));
            update.Parameters.Add(Text(command.TenantId));
            update.Parameters.Add(Text(command.ProfileId));
            update.Parameters.Add(Text(id));
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (changed > 0)
        {
            await AppendAuditAsync(
                connection, transaction, command.TenantId, command.ProfileId,
                $"notification.{command.Status}", "notifications", null,
                $"{changed} notification(s) changed to {command.Status}.", command.OccurredAt, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private async Task<SettingsRecord> UpdateSettingsCoreAsync(
        SettingsUpdateCommand command,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(command.PatchJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new NotificationValidationException("Settings patch must be an object.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadSettingsByIdAsync(
                connection, transaction, command.TenantId, command.ProfileId, command.Id, cancellationToken)
            ?? throw new NotificationNotFoundException("settings");
        var root = document.RootElement;
        var theme = ReadString(root, "theme", current.Theme);
        if (!Themes.Contains(theme))
        {
            throw new NotificationValidationException("theme is invalid.");
        }

        var language = ReadString(root, "language", current.Language);
        if (string.IsNullOrWhiteSpace(language) || language.Length > 35)
        {
            throw new NotificationValidationException("language is invalid.");
        }

        var enabled = ReadBool(root, "notificationsEnabled", current.NotificationsEnabled);
        var muted = ReadCategories(root, current.MutedCategories);
        var working = ReadNullableString(root, "workingDirectory", current.WorkingDirectory);
        var unsafeAt = ReadNullableDate(root, "unsafeModeAcceptedAt", current.UnsafeModeAcceptedAt);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            "UPDATE harness.profile_settings SET theme=$1,language=$2,notifications_enabled=$3,muted_categories_json=$4,working_directory=$5,unsafe_mode_accepted_at=$6,updated_at=$7 WHERE tenant_id=$8 AND profile_id=$9 AND id=$10;";
        update.Parameters.Add(Text(theme));
        update.Parameters.Add(Text(language.Trim()));
        update.Parameters.Add(Boolean(enabled));
        update.Parameters.Add(Json(JsonSerializer.Serialize(muted, JsonOptions)));
        update.Parameters.Add(NullableText(working));
        update.Parameters.Add(NullableTimestamp(unsafeAt));
        update.Parameters.Add(Timestamp(command.OccurredAt));
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.ProfileId));
        update.Parameters.Add(Text(command.Id));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new NotificationNotFoundException("settings");
        }

        await AppendAuditAsync(
            connection, transaction, command.TenantId, command.ProfileId, "settings.updated",
            "settings", command.Id, "Profile settings updated.", command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            Theme = theme,
            Language = language.Trim(),
            NotificationsEnabled = enabled,
            MutedCategories = muted,
            WorkingDirectory = working,
            UnsafeModeAcceptedAt = unsafeAt,
            UpdatedAt = command.OccurredAt,
        };
    }

    private static void ValidateNotification(NotificationCreateCommand command)
    {
        if (!UlidValue.TryParse(command.Id, out _) || !UlidValue.TryParse(command.ProfileId, out _) ||
            !Severities.Contains(command.Severity) || !Categories.Contains(command.Category) ||
            string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 200 ||
            string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > 4000 ||
            command.GroupKey?.Length > 200 || command.Link?.Length > 2000)
        {
            throw new NotificationValidationException("Notification is invalid.");
        }
    }

    private static async Task<bool> ProfileExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string profile,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.local_users WHERE tenant_id=$1 AND id=$2);";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(profile));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return profile state."));
    }

    private static async Task<NotificationRecord?> ReadGroupedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NotificationCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{NotificationSelect} WHERE tenant_id=$1 AND profile_id=$2 AND group_key=$3 AND status='unread';";
        query.Parameters.Add(Text(command.TenantId));
        query.Parameters.Add(Text(command.ProfileId));
        query.Parameters.Add(Text(command.GroupKey!));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotification(reader) : null;
    }

    private static async Task<NotificationRecord?> ReadNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenant,
        string profile,
        string id,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{NotificationSelect} WHERE tenant_id=$1 AND profile_id=$2 AND id=$3;";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(profile));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotification(reader) : null;
    }

    private static async Task<SettingsRecord?> ReadSettingsByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenant,
        string profile,
        string id,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{SettingsSelect} WHERE tenant_id=$1 AND profile_id=$2 AND id=$3{(transaction is null ? "" : " FOR UPDATE")};";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(profile));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettings(reader) : null;
    }

    private static NotificationRecord ReadNotification(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11));

    private static SettingsRecord ReadSettings(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        JsonSerializer.Deserialize<string[]>(reader.GetString(5), JsonOptions) ?? [],
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8));

    private static string ReadString(JsonElement root, string name, string current)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            throw new NotificationValidationException($"{name} is invalid.");
        }

        return node.GetString() ?? string.Empty;
    }

    private static bool ReadBool(JsonElement root, string name, bool current)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new NotificationValidationException($"{name} is invalid.");
        }

        return node.GetBoolean();
    }

    private static string? ReadNullableString(JsonElement root, string name, string? current)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return current;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String || node.GetString()!.Length > 2000)
        {
            throw new NotificationValidationException($"{name} is invalid.");
        }

        return node.GetString();
    }

    private static DateTimeOffset? ReadNullableDate(JsonElement root, string name, DateTimeOffset? current)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return current;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            throw new NotificationValidationException($"{name} is invalid.");
        }

        return value;
    }

    private static IReadOnlyList<string> ReadCategories(JsonElement root, IReadOnlyList<string> current)
    {
        if (!root.TryGetProperty("mutedCategories", out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            throw new NotificationValidationException("mutedCategories is invalid.");
        }

        var values = new List<string>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Categories.Contains(item.GetString()!))
            {
                throw new NotificationValidationException("mutedCategories is invalid.");
            }

            if (!values.Contains(item.GetString()!, StringComparer.Ordinal))
            {
                values.Add(item.GetString()!);
            }
        }

        return values;
    }

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string actorId,
        string action,
        string targetType,
        string? targetId,
        string detail,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var auditId = UlidValue.New(at).ToString();
        var payload = JsonSerializer.Serialize(
            new
            {
                auditEvent = new
                {
                    id = auditId,
                    actorKind = "user",
                    actorId,
                    action,
                    targetType,
                    targetId,
                    detail,
                    occurredAt = at,
                },
            },
            JsonOptions);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenant}"));
        var (sequence, previousHash) = await ReadLedgerTailAsync(connection, transaction, tenant, cancellationToken);
        var hash = AuditLedgerHash.Compute(previousHash, tenant, sequence, action, payload, at);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(auditId),
            Text(tenant),
            Bigint(sequence),
            Text(previousHash),
            Text(hash),
            Text(action),
            Json(payload),
            Timestamp(at));
        await AppendOutboxAsync(connection, transaction, tenant, "audit.eventAppended", payload, at, cancellationToken);
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

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

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
