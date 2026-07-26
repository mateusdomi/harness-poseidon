using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteAuditEventStore(SqliteWriteDispatcher dispatcher) : IAuditEventStore
{
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<AuditEventRecord>> ListAsync(AuditEventQuery query, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<AuditEventRecord>>(async (connection, token) =>
        {
            var values = (await ReadRowsAsync(connection, query.TenantId, token)).Select(Map).Where(value => Matches(value, query));
            if (query.AfterId is not null) values = values.Where(value => string.CompareOrdinal(value.Id, query.AfterId) > 0);
            return values.OrderBy(value => value.Id, StringComparer.Ordinal).Take(query.Limit).ToArray();
        }, cancellationToken);

    public Task<AuditEventRecord?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
            (await ReadRowsAsync(connection, tenantId, token)).Select(Map).FirstOrDefault(value => value.Id == id), cancellationToken);

    public Task<AuditIntegrityRecord> VerifyIntegrityAsync(string tenantId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => VerifyCoreAsync(connection, tenantId, token), cancellationToken);

    public Task<AuditEventRecord> AppendAsync(AuditEventAppendCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ActorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetType);
        return _dispatcher.ExecuteAsync((connection, token) => AppendCoreAsync(connection, command, token), cancellationToken);
    }

    private static async Task<AuditEventRecord> AppendCoreAsync(SqliteConnection connection, AuditEventAppendCommand command, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var eventId = UlidValue.New(command.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(
            new AppendedAuditEnvelope(new AppendedAuditEvent(
                eventId, command.ActorKind, command.ActorId, command.Action,
                command.TargetType, command.TargetId, command.Detail, command.OccurredAt)),
            AppendJsonOptions);
        await using (var tail = connection.CreateCommand())
        {
            tail.Transaction = transaction;
            tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            tail.Parameters.AddWithValue("$tenant", command.TenantId);
            await using var reader = await tail.ExecuteReaderAsync(token);
            var exists = await reader.ReadAsync(token);
            var sequence = exists ? reader.GetInt64(0) + 1 : 1;
            var previous = exists ? reader.GetString(1) : AuditLedgerHash.Genesis;
            await reader.DisposeAsync();
            var hash = AuditLedgerHash.Compute(previous, command.TenantId, sequence, "audit.eventAppended", payload, command.OccurredAt);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) " +
                "VALUES ($id,$tenant,$sequence,$previous,$hash,'audit.eventAppended',$payload,$at);";
            insert.Parameters.AddWithValue("$id", eventId);
            insert.Parameters.AddWithValue("$tenant", command.TenantId);
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$previous", previous);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$at", command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(token);
        }

        await using (var outbox = connection.CreateCommand())
        {
            outbox.Transaction = transaction;
            outbox.CommandText =
                "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) " +
                "VALUES ($id,$tenant,'audit.eventAppended',$payload,$at);";
            outbox.Parameters.AddWithValue("$id", UlidValue.New(command.OccurredAt).ToString());
            outbox.Parameters.AddWithValue("$tenant", command.TenantId);
            outbox.Parameters.AddWithValue("$payload", payload);
            outbox.Parameters.AddWithValue("$at", command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await outbox.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        return new AuditEventRecord(
            eventId, command.ActorKind, command.ActorId, command.Action,
            command.TargetType, command.TargetId, command.Detail, command.OccurredAt);
    }

    private static readonly JsonSerializerOptions AppendJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record AppendedAuditEnvelope(AppendedAuditEvent AuditEvent);

    private sealed record AppendedAuditEvent(
        string Id,
        string ActorKind,
        string? ActorId,
        string Action,
        string TargetType,
        string? TargetId,
        string? Detail,
        DateTimeOffset OccurredAt);

    private static async Task<AuditIntegrityRecord> VerifyCoreAsync(SqliteConnection connection, string tenantId, CancellationToken token)
    {
        var rows = await ReadRowsAsync(connection, tenantId, token, orderBySequence: true); var previous = AuditLedgerHash.Genesis; long expected = 1;
        foreach (var row in rows)
        {
            string computed;
            try { computed = AuditLedgerHash.Compute(previous, tenantId, row.Sequence, row.EventType, row.PayloadJson, row.OccurredAt); }
            catch (JsonException) { return new(false, rows.Count, rows.Count == 0 ? 0 : rows[^1].Sequence, rows.Count == 0 ? AuditLedgerHash.Genesis : rows[^1].EventHash, row.Sequence); }
            if (row.Sequence != expected || !string.Equals(row.PreviousHash, previous, StringComparison.Ordinal) || !string.Equals(row.EventHash, computed, StringComparison.Ordinal))
                return new(false, rows.Count, rows.Count == 0 ? 0 : rows[^1].Sequence, rows.Count == 0 ? AuditLedgerHash.Genesis : rows[^1].EventHash, row.Sequence);
            previous = row.EventHash; expected++;
        }
        return new(true, rows.Count, rows.Count == 0 ? 0 : rows[^1].Sequence, previous, null);
    }

    public Task<IReadOnlyList<AuditChainRowRecord>> ListChainAsync(
        string tenantId, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return _dispatcher.ExecuteAsync<IReadOnlyList<AuditChainRowRecord>>(async (connection, token) =>
        {
            var rows = new List<AuditChainRowRecord>(); await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT sequence,event_type,payload_json,previous_hash,event_hash,occurred_at " +
                "FROM audit_ledger WHERE tenant_id=$tenant AND sequence>$after ORDER BY sequence LIMIT $limit;";
            query.Parameters.AddWithValue("$tenant", tenantId);
            query.Parameters.AddWithValue("$after", afterSequence);
            query.Parameters.AddWithValue("$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add(new(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), Parse(reader.GetString(5))));
            }

            return rows;
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<LedgerRow>> ReadRowsAsync(SqliteConnection connection, string tenantId, CancellationToken token, bool orderBySequence = false)
    {
        var rows = new List<LedgerRow>(); await using var query = connection.CreateCommand();
        query.CommandText = $"SELECT id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at FROM audit_ledger WHERE tenant_id=$tenant ORDER BY {(orderBySequence ? "sequence" : "id")};";
        query.Parameters.AddWithValue("$tenant", tenantId); await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), Parse(reader.GetString(6))));
        return rows;
    }

    private static AuditEventRecord Map(LedgerRow row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson); var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("auditEvent", out var audit) && audit.ValueKind == JsonValueKind.Object)
            {
                var id = CanonicalId(ReadString(audit, "id")) ?? row.Id;
                var actorKind = ReadActorKind(audit, "actorKind") ?? "system"; var actorId = CanonicalId(ReadString(audit, "actorId"));
                var action = ReadString(audit, "action") ?? row.EventType; var targetType = ReadString(audit, "targetType") ?? "system"; var targetId = CanonicalId(ReadString(audit, "targetId"));
                var detail = ReadString(audit, "detail"); var occurredAt = ReadDate(audit, "occurredAt") ?? row.OccurredAt;
                return new(id, actorKind, actorId, action, targetType, targetId, detail, occurredAt);
            }
            var fallbackActor = ReadActor(root); var target = ReadTarget(root, row.EventType);
            return new(row.Id, fallbackActor.Kind, fallbackActor.Id, row.EventType, target.Type, target.Id, null, row.OccurredAt);
        }
        catch (JsonException) { return new(row.Id, "system", null, row.EventType, "system", null, null, row.OccurredAt); }
    }

    private static bool Matches(AuditEventRecord value, AuditEventQuery query) =>
        (query.ActorKind is null || value.ActorKind == query.ActorKind) &&
        (query.ActorId is null || value.ActorId == query.ActorId) &&
        (query.Action is null || value.Action == query.Action) &&
        (query.TargetType is null || value.TargetType == query.TargetType) &&
        (query.TargetId is null || value.TargetId == query.TargetId) &&
        (query.From is null || value.OccurredAt >= query.From) &&
        (query.To is null || value.OccurredAt <= query.To);

    private static (string Kind, string? Id) ReadActor(JsonElement root)
    {
        var kind = ReadActorKind(root, "actorKind") ?? ReadActorKind(root, "changedByKind") ?? "system";
        foreach (var name in new[] { "actorId", "changedById", "userId", "reviewerId", "resolvedByProfileId", "acceptedByProfileId" })
            if (CanonicalId(ReadString(root, name)) is { } id) return (kind, id);
        return (kind, null);
    }

    private static (string Type, string? Id) ReadTarget(JsonElement root, string eventType)
    {
        (string Key, string Type)[] candidates = [("taskId", "task"), ("attemptId", "attempt"), ("documentId", "document"), ("approvalId", "approval"), ("workflowId", "workflow"), ("runId", "workflow-run"), ("conversationId", "conversation"), ("executionId", "execution"), ("agentId", "agent"), ("toolId", "tool"), ("budgetId", "budget"), ("accountId", "account"), ("providerId", "provider"), ("projectId", "project")];
        foreach (var candidate in candidates) if (CanonicalId(ReadString(root, candidate.Key)) is { } id) return (candidate.Type, id);
        foreach (var candidate in new[] { "notification", "task", "attempt", "document", "approval", "project", "message", "conversation" })
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(candidate, out var nested) && nested.ValueKind == JsonValueKind.Object && CanonicalId(ReadString(nested, "id")) is { } id) return (candidate, id);
        var prefix = eventType.Split('.', 2)[0]; return (string.IsNullOrWhiteSpace(prefix) ? "system" : prefix, null);
    }

    private static string? ReadString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static DateTimeOffset? ReadDate(JsonElement value, string name) => DateTimeOffset.TryParse(ReadString(value, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    private static string? ReadActorKind(JsonElement value, string name) { var kind = ReadString(value, name); return kind is "user" or "chief" or "agent" or "system" ? kind : null; }
    private static string? CanonicalId(string? value) => UlidValue.TryParse(value, out var id) ? id.ToString() : null;
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private sealed record LedgerRow(string Id, long Sequence, string PreviousHash, string EventHash, string EventType, string PayloadJson, DateTimeOffset OccurredAt);
}
