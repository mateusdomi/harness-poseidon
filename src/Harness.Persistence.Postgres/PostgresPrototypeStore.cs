using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresPrototypeStore(NpgsqlDataSource dataSource) : IPrototypeStore
{
    private const string PrototypeSelect =
        "SELECT id,project_id,name,description,state,url,thumbnail_url,source_document_id,created_at,updated_at FROM harness.prototypes";

    private const string ReferenceSelect =
        "SELECT id,project_id,prototype_id,title,image_url,source,tags_json::text,created_at FROM harness.visual_references";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Sources = ["upload", "url", "generated"];
    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        ["draft"] = ["generating", "ready", "archived"],
        ["generating"] = ["draft", "ready", "archived"],
        ["ready"] = ["published", "archived"],
        ["published"] = ["archived"],
        ["archived"] = [],
    };

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<IReadOnlyList<PrototypeRecord>> ListPrototypesAsync(
        string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, projectId, afterId, limit, PrototypeSelect, ReadPrototype, cancellationToken);

    public Task<PrototypeRecord?> GetPrototypeAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetAsync(tenantId, id, PrototypeSelect, ReadPrototype, cancellationToken);

    public Task<IReadOnlyList<VisualReferenceRecord>> ListReferencesAsync(
        string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, projectId, afterId, limit, ReferenceSelect, ReadReference, cancellationToken);

    public Task<VisualReferenceRecord?> GetReferenceAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetAsync(tenantId, id, ReferenceSelect, ReadReference, cancellationToken);

    public Task<PrototypeRecord> CreatePrototypeAsync(
        PrototypeCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreatePrototypeCoreAsync(command, cancellationToken);
    }

    public Task<VisualReferenceRecord> CreateReferenceAsync(
        VisualReferenceCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateReferenceCoreAsync(command, cancellationToken);
    }

    public Task<PrototypeRecord> TransitionPrototypeAsync(
        PrototypeTransitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionCoreAsync(command, cancellationToken);
    }

    public Task DeletePrototypeAsync(
        PrototypeDeleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DeleteCoreAsync(command, "prototypes", "prototype.deleted", cancellationToken);
    }

    public Task DeleteReferenceAsync(
        PrototypeDeleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DeleteCoreAsync(command, "visual_references", "visualReference.deleted", cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string tenant,
        string? project,
        string? after,
        int limit,
        string select,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        await using var query = _dataSource.CreateCommand(
            $"{select} WHERE tenant_id=$1 AND deleted_at IS NULL AND ($2::text IS NULL OR project_id=$2) AND ($3::text IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(NullableText(project));
        query.Parameters.Add(NullableText(after));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task<T?> GetAsync<T>(
        string tenant,
        string id,
        string select,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var query = _dataSource.CreateCommand(
            $"{select} WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL;");
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : null;
    }

    private async Task<PrototypeRecord> CreatePrototypeCoreAsync(
        PrototypeCreateCommand command,
        CancellationToken cancellationToken)
    {
        ValidateId(command.Id);
        ValidateId(command.ProjectId);
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 200 ||
            command.Description?.Length > 4000 ||
            command.SourceDocumentId is not null && !UlidValue.TryParse(command.SourceDocumentId, out _))
        {
            throw new PrototypeValidationException("Prototype is invalid.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireEnabledProjectAsync(
            connection, transaction, command.TenantId, command.ProjectId, cancellationToken);
        if (command.SourceDocumentId is not null && !await ReferenceExistsAsync(
                connection, transaction, "documents", command.TenantId, command.ProjectId,
                command.SourceDocumentId, cancellationToken))
        {
            throw new PrototypeNotFoundException("source_document");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.prototypes (tenant_id,id,project_id,name,description,state,source_document_id,created_at,updated_at) VALUES ($1,$2,$3,$4,$5,'draft',$6,$7,$7);",
            cancellationToken,
            Text(command.TenantId),
            Text(command.Id),
            Text(command.ProjectId),
            Text(command.Name.Trim()),
            Text(command.Description?.Trim() ?? ""),
            NullableText(command.SourceDocumentId),
            Timestamp(command.OccurredAt));
        var value = new PrototypeRecord(
            command.Id, command.ProjectId, command.Name.Trim(), command.Description?.Trim() ?? "",
            "draft", null, null, command.SourceDocumentId, command.OccurredAt, command.OccurredAt);
        await AppendAsync(
            connection, transaction, command.TenantId, "prototype.created",
            JsonSerializer.Serialize(new { projectId = command.ProjectId, prototype = value }, JsonOptions),
            command.OccurredAt, outbox: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    private async Task<VisualReferenceRecord> CreateReferenceCoreAsync(
        VisualReferenceCreateCommand command,
        CancellationToken cancellationToken)
    {
        ValidateId(command.Id);
        ValidateId(command.ProjectId);
        if (command.PrototypeId is not null)
        {
            ValidateId(command.PrototypeId);
        }

        if (string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 200 ||
            string.IsNullOrWhiteSpace(command.ImageUrl) || command.ImageUrl.Length > 2048 ||
            !Sources.Contains(command.Source) || command.Tags is { Count: > 50 } ||
            command.Tags?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100) == true)
        {
            throw new PrototypeValidationException("Visual reference is invalid.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireEnabledProjectAsync(
            connection, transaction, command.TenantId, command.ProjectId, cancellationToken);
        if (command.PrototypeId is not null && !await ReferenceExistsAsync(
                connection, transaction, "prototypes", command.TenantId, command.ProjectId,
                command.PrototypeId, cancellationToken))
        {
            throw new PrototypeNotFoundException("prototype");
        }

        var tags = (command.Tags ?? []).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.visual_references (tenant_id,id,project_id,prototype_id,title,image_url,source,tags_json,created_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9);",
            cancellationToken,
            Text(command.TenantId),
            Text(command.Id),
            Text(command.ProjectId),
            NullableText(command.PrototypeId),
            Text(command.Title.Trim()),
            Text(command.ImageUrl.Trim()),
            Text(command.Source),
            Json(JsonSerializer.Serialize(tags, JsonOptions)),
            Timestamp(command.OccurredAt));
        var value = new VisualReferenceRecord(
            command.Id, command.ProjectId, command.PrototypeId, command.Title.Trim(),
            command.ImageUrl.Trim(), command.Source, tags, command.OccurredAt);
        await AppendAsync(
            connection, transaction, command.TenantId, "visualReference.created",
            JsonSerializer.Serialize(new { projectId = command.ProjectId, visualReference = value }, JsonOptions),
            command.OccurredAt, outbox: false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    private async Task<PrototypeRecord> TransitionCoreAsync(
        PrototypeTransitionCommand command,
        CancellationToken cancellationToken)
    {
        ValidateId(command.Id);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadPrototypeAsync(
                connection, transaction, command.TenantId, command.Id, cancellationToken)
            ?? throw new PrototypeNotFoundException("prototype");
        if (!Transitions.TryGetValue(current.State, out var allowed) ||
            !allowed.Contains(command.State, StringComparer.Ordinal))
        {
            throw new PrototypeValidationException("Prototype transition is invalid.");
        }

        var url = command.Url ?? current.Url;
        var thumb = command.ThumbnailUrl ?? current.ThumbnailUrl;
        if (url?.Length > 2048 || thumb?.Length > 2048 ||
            command.State == "published" && string.IsNullOrWhiteSpace(url))
        {
            throw new PrototypeValidationException("Published prototype requires a URL.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.prototypes SET state=$1,url=$2,thumbnail_url=$3,updated_at=$4 WHERE tenant_id=$5 AND id=$6;",
            cancellationToken,
            Text(command.State),
            NullableText(url),
            NullableText(thumb),
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.Id));
        var value = current with
        {
            State = command.State,
            Url = url,
            ThumbnailUrl = thumb,
            UpdatedAt = command.OccurredAt,
        };
        await AppendAsync(
            connection, transaction, command.TenantId, "prototype.stateChanged",
            JsonSerializer.Serialize(
                new { projectId = current.ProjectId, prototypeId = current.Id, from = current.State, to = command.State },
                JsonOptions),
            command.OccurredAt, outbox: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    private async Task DeleteCoreAsync(
        PrototypeDeleteCommand command,
        string table,
        string eventType,
        CancellationToken cancellationToken)
    {
        ValidateId(command.Id);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string project;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                $"SELECT project_id FROM harness.{table} WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL FOR UPDATE;";
            read.Parameters.Add(Text(command.TenantId));
            read.Parameters.Add(Text(command.Id));
            project = (await read.ExecuteScalarAsync(cancellationToken) as string)?.TrimEnd()
                ?? throw new PrototypeNotFoundException(table == "prototypes" ? "prototype" : "visual_reference");
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"UPDATE harness.{table} SET deleted_at=$1{(table == "prototypes" ? ",state='archived',updated_at=$1" : "")} WHERE tenant_id=$2 AND id=$3;",
            cancellationToken,
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.Id));
        await AppendAsync(
            connection, transaction, command.TenantId, eventType,
            JsonSerializer.Serialize(new { projectId = project, id = command.Id }, JsonOptions),
            command.OccurredAt, outbox: false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RequireEnabledProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string project,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT prototyping_mode FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL;";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(project));
        var mode = await query.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new PrototypeNotFoundException("project");
        if (mode == "notApplicable")
        {
            throw new PrototypeValidationException("Prototyping is waived for this project.");
        }
    }

    private static async Task<bool> ReferenceExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string tenant,
        string project,
        string id,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"SELECT EXISTS(SELECT 1 FROM harness.{table} WHERE tenant_id=$1 AND project_id=$2 AND id=$3{(table == "prototypes" ? " AND deleted_at IS NULL" : "")});";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(project));
        query.Parameters.Add(Text(id));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return reference state."));
    }

    private static async Task<PrototypeRecord?> ReadPrototypeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string id,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{PrototypeSelect} WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL FOR UPDATE;";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPrototype(reader) : null;
    }

    private static async Task AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenant,
        string type,
        string payload,
        DateTimeOffset at,
        bool outbox,
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
        if (outbox)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
                cancellationToken,
                Text(UlidValue.New(at).ToString()),
                Text(tenant),
                Text(type),
                Json(payload),
                Timestamp(at));
        }
    }

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

    private static PrototypeRecord ReadPrototype(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7).TrimEnd(),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetFieldValue<DateTimeOffset>(9));

    private static VisualReferenceRecord ReadReference(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.IsDBNull(2) ? null : reader.GetString(2).TrimEnd(),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions) ?? [],
        reader.GetFieldValue<DateTimeOffset>(7));

    private static void ValidateId(string id)
    {
        if (!UlidValue.TryParse(id, out _))
        {
            throw new PrototypeValidationException("ID must be a ULID.");
        }
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
