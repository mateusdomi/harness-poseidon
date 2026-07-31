using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed class PostgresAuditEventStore(NpgsqlDataSource dataSource) : IAuditEventStore
{
    private static readonly JsonSerializerOptions AppendJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<AuditEventRecord>> ListAsync(
        AuditEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = (await ReadRowsAsync(query.TenantId, cancellationToken))
            .Select(Map)
            .Where(value => Matches(value, query));
        if (query.AfterId is not null)
        {
            values = values.Where(value => string.CompareOrdinal(value.Id, query.AfterId) > 0);
        }

        return values.OrderBy(value => value.Id, StringComparer.Ordinal).Take(query.Limit).ToArray();
    }

    public async Task<AuditEventRecord?> GetAsync(
        string tenantId,
        string id,
        CancellationToken cancellationToken = default) =>
        (await ReadRowsAsync(tenantId, cancellationToken))
            .Select(Map)
            .FirstOrDefault(value => value.Id == id);

    public async Task<AuditIntegrityRecord> VerifyIntegrityAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadRowsAsync(tenantId, cancellationToken, orderBySequence: true);
        var previous = AuditLedgerHash.Genesis;
        long expected = 1;
        foreach (var row in rows)
        {
            string computed;
            try
            {
                computed = AuditLedgerHash.Compute(
                    previous,
                    tenantId,
                    row.Sequence,
                    row.EventType,
                    row.PayloadJson,
                    row.OccurredAt);
            }
            catch (JsonException)
            {
                return Broken(rows, row.Sequence);
            }

            if (row.Sequence != expected ||
                !string.Equals(row.PreviousHash, previous, StringComparison.Ordinal) ||
                !string.Equals(row.EventHash, computed, StringComparison.Ordinal))
            {
                return Broken(rows, row.Sequence);
            }

            previous = row.EventHash;
            expected++;
        }

        return new AuditIntegrityRecord(
            true,
            rows.Count,
            rows.Count == 0 ? 0 : rows[^1].Sequence,
            previous,
            null);
    }

    public Task<AuditEventRecord> AppendAsync(
        AuditEventAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ActorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetType);
        return AppendCoreAsync(command, cancellationToken);
    }

    private async Task<AuditEventRecord> AppendCoreAsync(
        AuditEventAppendCommand command,
        CancellationToken cancellationToken)
    {
        const string eventType = "audit.eventAppended";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{command.TenantId}"));
        var eventId = UlidValue.New(command.OccurredAt).ToString();
        var payload = JsonSerializer.Serialize(
            new AppendedAuditEnvelope(new AppendedAuditEvent(
                eventId, command.ActorKind, command.ActorId, command.Action,
                command.TargetType, command.TargetId, command.Detail, command.OccurredAt)),
            AppendJsonOptions);
        // Fase 0A3 (BR-014): o ledger é encadeado por hash e replicado para backup — um segredo
        // gravado aqui é irreversível. Sanitiza ANTES do hash (o que se verifica é o que se
        // persiste) e RECUSA a escrita se algo reconhecível sobreviver.
        payload = PersistenceSanitizer.SanitizeCriticalJson(payload, "audit_ledger");
        var (sequence, previous) = await ReadLedgerTailAsync(
            connection,
            transaction,
            command.TenantId,
            cancellationToken);
        var hash = AuditLedgerHash.Compute(
            previous,
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
            Text(eventId),
            Text(command.TenantId),
            Bigint(sequence),
            Text(previous),
            Text(hash),
            Text(eventType),
            Json(payload),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
            cancellationToken,
            Text(UlidValue.New(command.OccurredAt).ToString()),
            Text(command.TenantId),
            Text(eventType),
            Json(payload),
            Timestamp(command.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return new AuditEventRecord(
            eventId, command.ActorKind, command.ActorId, command.Action,
            command.TargetType, command.TargetId, command.Detail, command.OccurredAt);
    }

    private static AuditIntegrityRecord Broken(IReadOnlyList<LedgerRow> rows, long failedSequence) =>
        new(
            false,
            rows.Count,
            rows.Count == 0 ? 0 : rows[^1].Sequence,
            rows.Count == 0 ? AuditLedgerHash.Genesis : rows[^1].EventHash,
            failedSequence);

    public async Task<IReadOnlyList<AuditChainRowRecord>> ListChainAsync(
        string tenantId, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var rows = new List<AuditChainRowRecord>();
        await using var query = _dataSource.CreateCommand(
            "SELECT sequence, event_type, payload_json::text, previous_hash, event_hash, occurred_at " +
            "FROM harness.audit_ledger WHERE tenant_id = $1 AND sequence > $2 ORDER BY sequence LIMIT $3;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterSequence });
        query.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditChainRowRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).TrimEnd(),
                reader.GetString(4).TrimEnd(),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<LedgerRow>> ReadRowsAsync(
        string tenantId,
        CancellationToken cancellationToken,
        bool orderBySequence = false)
    {
        var rows = new List<LedgerRow>();
        await using var query = _dataSource.CreateCommand(
            "SELECT id, sequence, previous_hash, event_hash, event_type, payload_json::text, occurred_at " +
            $"FROM harness.audit_ledger WHERE tenant_id = $1 ORDER BY {(orderBySequence ? "sequence" : "id")};");
        query.Parameters.Add(Text(tenantId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LedgerRow(
                reader.GetString(0).TrimEnd(),
                reader.GetInt64(1),
                reader.GetString(2).TrimEnd(),
                reader.GetString(3).TrimEnd(),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
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

    private static AuditEventRecord Map(LedgerRow row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("auditEvent", out var audit) &&
                audit.ValueKind == JsonValueKind.Object)
            {
                var id = CanonicalId(ReadString(audit, "id")) ?? row.Id;
                var actorKind = ReadActorKind(audit, "actorKind") ?? "system";
                var actorId = CanonicalId(ReadString(audit, "actorId"));
                var action = ReadString(audit, "action") ?? row.EventType;
                var targetType = ReadString(audit, "targetType") ?? "system";
                var targetId = CanonicalId(ReadString(audit, "targetId"));
                var detail = ReadString(audit, "detail");
                var occurredAt = ReadDate(audit, "occurredAt") ?? row.OccurredAt;
                return new AuditEventRecord(
                    id, actorKind, actorId, action, targetType, targetId, detail, occurredAt);
            }

            var fallbackActor = ReadActor(root);
            var target = ReadTarget(root, row.EventType);
            return new AuditEventRecord(
                row.Id, fallbackActor.Kind, fallbackActor.Id, row.EventType,
                target.Type, target.Id, null, row.OccurredAt);
        }
        catch (JsonException)
        {
            return new AuditEventRecord(
                row.Id, "system", null, row.EventType, "system", null, null, row.OccurredAt);
        }
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
        foreach (var name in new[]
        {
            "actorId", "changedById", "userId", "reviewerId", "resolvedByProfileId", "acceptedByProfileId",
        })
        {
            if (CanonicalId(ReadString(root, name)) is { } id)
            {
                return (kind, id);
            }
        }

        return (kind, null);
    }

    private static (string Type, string? Id) ReadTarget(JsonElement root, string eventType)
    {
        (string Key, string Type)[] candidates =
        [
            ("taskId", "task"), ("attemptId", "attempt"), ("documentId", "document"),
            ("approvalId", "approval"), ("workflowId", "workflow"), ("runId", "workflow-run"),
            ("conversationId", "conversation"), ("executionId", "execution"), ("agentId", "agent"),
            ("toolId", "tool"), ("budgetId", "budget"), ("accountId", "account"),
            ("providerId", "provider"), ("projectId", "project"),
        ];
        foreach (var candidate in candidates)
        {
            if (CanonicalId(ReadString(root, candidate.Key)) is { } id)
            {
                return (candidate.Type, id);
            }
        }

        foreach (var candidate in new[]
        {
            "notification", "task", "attempt", "document", "approval", "project", "message", "conversation",
        })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(candidate, out var nested) &&
                nested.ValueKind == JsonValueKind.Object &&
                CanonicalId(ReadString(nested, "id")) is { } id)
            {
                return (candidate, id);
            }
        }

        var prefix = eventType.Split('.', 2)[0];
        return (string.IsNullOrWhiteSpace(prefix) ? "system" : prefix, null);
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement value, string name) =>
        DateTimeOffset.TryParse(
            ReadString(value, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private static string? ReadActorKind(JsonElement value, string name)
    {
        var kind = ReadString(value, name);
        return kind is "user" or "chief" or "agent" or "system" ? kind : null;
    }

    private static string? CanonicalId(string? value) =>
        UlidValue.TryParse(value, out var id) ? id.ToString() : null;

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

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };

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

    private sealed record LedgerRow(
        string Id,
        long Sequence,
        string PreviousHash,
        string EventHash,
        string EventType,
        string PayloadJson,
        DateTimeOffset OccurredAt);
}
