using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Notifications;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteNotificationStore(SqliteWriteDispatcher dispatcher) : INotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Severities = ["info", "warning", "error", "critical"];
    private static readonly HashSet<string> Categories = ["system", "task", "approval", "quota", "license", "chat", "workflow"];
    private static readonly HashSet<string> Themes = ["dark", "light", "system"];
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<NotificationRecord>> ListAsync(string tenantId, string profileId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<NotificationRecord>>(async (connection, token) =>
        {
            var values = new List<NotificationRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText = $"{NotificationSelect} WHERE tenant_id=$tenant AND profile_id=$profile AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(query, "$tenant", tenantId); Add(query, "$profile", profileId); AddNullable(query, "$after", afterId); Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadNotification(reader));
            return values;
        }, cancellationToken);

    public Task<NotificationRecord?> GetAsync(string tenantId, string profileId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ReadNotificationAsync(connection, null, tenantId, profileId, id, token), cancellationToken);

    public Task<NotificationRecord> CreateAsync(NotificationCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => CreateCoreAsync(connection, command, token), cancellationToken);

    public Task<int> SetStatusAsync(NotificationStatusCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => SetStatusCoreAsync(connection, command, token), cancellationToken);

    public Task<IReadOnlyList<SettingsRecord>> ListSettingsAsync(string tenantId, string profileId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<SettingsRecord>>(async (connection, token) =>
        {
            var values = new List<SettingsRecord>(); await using var query = connection.CreateCommand();
            query.CommandText = $"{SettingsSelect} WHERE tenant_id=$tenant AND profile_id=$profile ORDER BY id;";
            Add(query, "$tenant", tenantId); Add(query, "$profile", profileId); await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadSettings(reader)); return values;
        }, cancellationToken);

    public Task<SettingsRecord?> GetSettingsAsync(string tenantId, string profileId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand(); query.CommandText = $"{SettingsSelect} WHERE tenant_id=$tenant AND profile_id=$profile AND id=$id;";
            Add(query, "$tenant", tenantId); Add(query, "$profile", profileId); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadSettings(reader) : null;
        }, cancellationToken);

    public Task<SettingsRecord> UpdateSettingsAsync(SettingsUpdateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => UpdateSettingsCoreAsync(connection, command, token), cancellationToken);

    private static async Task<NotificationRecord> CreateCoreAsync(SqliteConnection connection, NotificationCreateCommand command, CancellationToken token)
    {
        ValidateNotification(command); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        if (!await ProfileExistsAsync(connection, transaction, command.TenantId, command.ProfileId, token)) throw new NotificationNotFoundException("profile");
        NotificationRecord result;
        if (command.GroupKey is not null && await ReadGroupedAsync(connection, transaction, command, token) is { } grouped)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE notifications SET severity=$severity,category=$category,title=$title,body=$body,link=$link,dedupe_count=dedupe_count+1,created_at=$at WHERE tenant_id=$tenant AND id=$id;", token,
                ("$severity", command.Severity), ("$category", command.Category), ("$title", command.Title.Trim()), ("$body", command.Body.Trim()),
                ("$link", command.Link ?? (object)DBNull.Value), ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", grouped.Id));
            result = grouped with { Severity = command.Severity, Category = command.Category, Title = command.Title.Trim(), Body = command.Body.Trim(), Link = command.Link, DedupeCount = grouped.DedupeCount + 1, CreatedAt = command.OccurredAt };
        }
        else
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO notifications(tenant_id,id,profile_id,severity,category,title,body,group_key,dedupe_count,status,link,created_at) VALUES($tenant,$id,$profile,$severity,$category,$title,$body,$group,1,'unread',$link,$at);", token,
                ("$tenant", command.TenantId), ("$id", command.Id), ("$profile", command.ProfileId), ("$severity", command.Severity), ("$category", command.Category),
                ("$title", command.Title.Trim()), ("$body", command.Body.Trim()), ("$group", command.GroupKey ?? (object)DBNull.Value), ("$link", command.Link ?? (object)DBNull.Value), ("$at", Store(command.OccurredAt)));
            result = new(command.Id, command.ProfileId, command.Severity, command.Category, command.Title.Trim(), command.Body.Trim(), command.GroupKey, 1, "unread", command.Link, command.OccurredAt, null);
        }
        var payload = JsonSerializer.Serialize(new { notification = result }, JsonOptions);
        await AppendOutboxAsync(connection, transaction, command.TenantId, "notification.created", payload, command.OccurredAt, token);
        await AppendAuditAsync(connection, transaction, command.TenantId, command.ProfileId, "notification.created", "notification", result.Id, "Notification created or coalesced.", command.OccurredAt, token);
        await transaction.CommitAsync(token); return result;
    }

    private static async Task<int> SetStatusCoreAsync(SqliteConnection connection, NotificationStatusCommand command, CancellationToken token)
    {
        if (command.Status is not ("read" or "muted") || command.Ids.Count is < 1 or > 200 || command.Ids.Any(id => !UlidValue.TryParse(id, out _)))
            throw new NotificationValidationException("Notification status command is invalid.");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token); var changed = 0;
        foreach (var id in command.Ids.Distinct(StringComparer.Ordinal))
        {
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE notifications SET status=$status,read_at=CASE WHEN $status='read' THEN $at ELSE read_at END WHERE tenant_id=$tenant AND profile_id=$profile AND id=$id AND status<>$status;";
            Add(update, "$status", command.Status); Add(update, "$at", Store(command.OccurredAt)); Add(update, "$tenant", command.TenantId); Add(update, "$profile", command.ProfileId); Add(update, "$id", id);
            changed += await update.ExecuteNonQueryAsync(token);
        }
        if (changed > 0)
        {
            await AppendAuditAsync(connection, transaction, command.TenantId, command.ProfileId, $"notification.{command.Status}", "notifications", null, $"{changed} notification(s) changed to {command.Status}.", command.OccurredAt, token);
        }
        await transaction.CommitAsync(token); return changed;
    }

    private static async Task<SettingsRecord> UpdateSettingsCoreAsync(SqliteConnection connection, SettingsUpdateCommand command, CancellationToken token)
    {
        using var document = JsonDocument.Parse(command.PatchJson); if (document.RootElement.ValueKind != JsonValueKind.Object) throw new NotificationValidationException("Settings patch must be an object.");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var current = await ReadSettingsByIdAsync(connection, transaction, command.TenantId, command.ProfileId, command.Id, token) ?? throw new NotificationNotFoundException("settings");
        var root = document.RootElement; var theme = ReadString(root, "theme", current.Theme); if (!Themes.Contains(theme)) throw new NotificationValidationException("theme is invalid.");
        var language = ReadString(root, "language", current.Language); if (string.IsNullOrWhiteSpace(language) || language.Length > 35) throw new NotificationValidationException("language is invalid.");
        var enabled = ReadBool(root, "notificationsEnabled", current.NotificationsEnabled);
        var muted = ReadCategories(root, current.MutedCategories); var working = ReadNullableString(root, "workingDirectory", current.WorkingDirectory);
        var unsafeAt = ReadNullableDate(root, "unsafeModeAcceptedAt", current.UnsafeModeAcceptedAt);
        await using var update = connection.CreateCommand(); update.Transaction = transaction; update.CommandText = "UPDATE profile_settings SET theme=$theme,language=$language,notifications_enabled=$enabled,muted_categories_json=$muted,working_directory=$working,unsafe_mode_accepted_at=$unsafe,updated_at=$at WHERE tenant_id=$tenant AND profile_id=$profile AND id=$id;";
        Add(update, "$theme", theme); Add(update, "$language", language.Trim()); Add(update, "$enabled", enabled ? 1 : 0); Add(update, "$muted", JsonSerializer.Serialize(muted, JsonOptions)); AddNullable(update, "$working", working); AddNullable(update, "$unsafe", unsafeAt is null ? null : Store(unsafeAt.Value)); Add(update, "$at", Store(command.OccurredAt)); Add(update, "$tenant", command.TenantId); Add(update, "$profile", command.ProfileId); Add(update, "$id", command.Id);
        if (await update.ExecuteNonQueryAsync(token) != 1) throw new NotificationNotFoundException("settings");
        await AppendAuditAsync(connection, transaction, command.TenantId, command.ProfileId, "settings.updated", "settings", command.Id, "Profile settings updated.", command.OccurredAt, token);
        await transaction.CommitAsync(token);
        return current with { Theme = theme, Language = language.Trim(), NotificationsEnabled = enabled, MutedCategories = muted, WorkingDirectory = working, UnsafeModeAcceptedAt = unsafeAt, UpdatedAt = command.OccurredAt };
    }

    private static void ValidateNotification(NotificationCreateCommand command)
    {
        if (!UlidValue.TryParse(command.Id, out _) || !UlidValue.TryParse(command.ProfileId, out _) || !Severities.Contains(command.Severity) || !Categories.Contains(command.Category) || string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 200 || string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > 4000 || command.GroupKey?.Length > 200 || command.Link?.Length > 2000)
            throw new NotificationValidationException("Notification is invalid.");
    }

    private static async Task<bool> ProfileExistsAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string profile, CancellationToken token)
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT EXISTS(SELECT 1 FROM local_users WHERE tenant_id=$tenant AND id=$profile);"; Add(q, "$tenant", tenant); Add(q, "$profile", profile); return Convert.ToInt32(await q.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1; }
    private static async Task<NotificationRecord?> ReadGroupedAsync(SqliteConnection c, SqliteTransaction tx, NotificationCreateCommand command, CancellationToken token)
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"{NotificationSelect} WHERE tenant_id=$tenant AND profile_id=$profile AND group_key=$group AND status='unread';"; Add(q, "$tenant", command.TenantId); Add(q, "$profile", command.ProfileId); Add(q, "$group", command.GroupKey!); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? ReadNotification(r) : null; }
    private static async Task<NotificationRecord?> ReadNotificationAsync(SqliteConnection c, SqliteTransaction? tx, string tenant, string profile, string id, CancellationToken token)
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"{NotificationSelect} WHERE tenant_id=$tenant AND profile_id=$profile AND id=$id;"; Add(q, "$tenant", tenant); Add(q, "$profile", profile); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? ReadNotification(r) : null; }
    private static async Task<SettingsRecord?> ReadSettingsByIdAsync(SqliteConnection c, SqliteTransaction? transaction, string tenant, string profile, string id, CancellationToken token)
    { await using var q = c.CreateCommand(); q.Transaction = transaction; q.CommandText = $"{SettingsSelect} WHERE tenant_id=$tenant AND profile_id=$profile AND id=$id;"; Add(q, "$tenant", tenant); Add(q, "$profile", profile); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? ReadSettings(r) : null; }
    private static NotificationRecord ReadNotification(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), Parse(r.GetString(10)), r.IsDBNull(11) ? null : Parse(r.GetString(11)));
    private static SettingsRecord ReadSettings(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, JsonSerializer.Deserialize<string[]>(r.GetString(5), JsonOptions) ?? [], r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : Parse(r.GetString(7)), Parse(r.GetString(8)));
    private static string ReadString(JsonElement root, string name, string current) { if (!root.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null) return current; if (node.ValueKind != JsonValueKind.String) throw new NotificationValidationException($"{name} is invalid."); return node.GetString() ?? string.Empty; }
    private static bool ReadBool(JsonElement root, string name, bool current) { if (!root.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null) return current; if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new NotificationValidationException($"{name} is invalid."); return node.GetBoolean(); }
    private static string? ReadNullableString(JsonElement root, string name, string? current) { if (!root.TryGetProperty(name, out var node)) return current; if (node.ValueKind == JsonValueKind.Null) return null; if (node.ValueKind != JsonValueKind.String || node.GetString()!.Length > 2000) throw new NotificationValidationException($"{name} is invalid."); return node.GetString(); }
    private static DateTimeOffset? ReadNullableDate(JsonElement root, string name, DateTimeOffset? current) { if (!root.TryGetProperty(name, out var node)) return current; if (node.ValueKind == JsonValueKind.Null) return null; if (node.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)) throw new NotificationValidationException($"{name} is invalid."); return value; }
    private static IReadOnlyList<string> ReadCategories(JsonElement root, IReadOnlyList<string> current) { if (!root.TryGetProperty("mutedCategories", out var node) || node.ValueKind == JsonValueKind.Null) return current; if (node.ValueKind != JsonValueKind.Array) throw new NotificationValidationException("mutedCategories is invalid."); var values = new List<string>(); foreach (var item in node.EnumerateArray()) { if (item.ValueKind != JsonValueKind.String || !Categories.Contains(item.GetString()!)) throw new NotificationValidationException("mutedCategories is invalid."); if (!values.Contains(item.GetString()!, StringComparer.Ordinal)) values.Add(item.GetString()!); } return values; }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string, object)[] values) { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var (name, value) in values) Add(q, name, value); await q.ExecuteNonQueryAsync(token); }
    private static async Task AppendAuditAsync(SqliteConnection connection, SqliteTransaction transaction, string tenant, string actorId, string action, string targetType, string? targetId, string detail, DateTimeOffset at, CancellationToken token)
    {
        var auditId = UlidValue.New(at).ToString(); var payload = JsonSerializer.Serialize(new { auditEvent = new { id = auditId, actorKind = "user", actorId, action, targetType, targetId, detail, occurredAt = at } }, JsonOptions);
        long sequence; string previousHash; await using (var query = connection.CreateCommand()) { query.Transaction = transaction; query.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(query, "$tenant", tenant); await using var reader = await query.ExecuteReaderAsync(token); if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previousHash = reader.GetString(1); } else { sequence = 1; previousHash = AuditLedgerHash.Genesis; } }
        var hash = AuditLedgerHash.Compute(previousHash, tenant, sequence, action, payload, at);
        await ExecuteAsync(connection, transaction, "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);", token, ("$id", auditId), ("$tenant", tenant), ("$sequence", sequence), ("$previous", previousHash), ("$hash", hash), ("$type", action), ("$payload", payload), ("$at", Store(at)));
        await AppendOutboxAsync(connection, transaction, tenant, "audit.eventAppended", payload, at, token);
    }
    private static Task AppendOutboxAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx, "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($id,$tenant,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$type", type), ("$payload", payload), ("$at", Store(at)));
    private const string NotificationSelect = "SELECT id,profile_id,severity,category,title,body,group_key,dedupe_count,status,link,created_at,read_at FROM notifications";
    private const string SettingsSelect = "SELECT id,profile_id,theme,language,notifications_enabled,muted_categories_json,working_directory,unsafe_mode_accepted_at,updated_at FROM profile_settings";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
