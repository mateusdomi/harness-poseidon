using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDocumentStore(NpgsqlDataSource dataSource) : IDocumentStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<DocumentCreateReceipt> CreateAsync(
        DocumentCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentCreateValidator.Validate(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public async Task<DocumentStoreSnapshot?> ReadAsync(
        string tenantId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(documentId, nameof(documentId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        DocumentHeader? header;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                """
                SELECT tenant_id,project_id,id,title,kind,state,current_version,phase_name,
                       inconsistent,version,created_at,updated_at
                FROM harness.documents
                WHERE tenant_id=$1 AND id=$2;
                """;
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(documentId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            header = await reader.ReadAsync(cancellationToken)
                ? new DocumentHeader(
                    Trim(reader.GetString(0)),
                    Trim(reader.GetString(1)),
                    Trim(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetBoolean(8),
                    reader.GetInt64(9),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.GetFieldValue<DateTimeOffset>(11))
                : null;
        }

        if (header is null)
        {
            return null;
        }

        var classifications = await ReadClassificationsAsync(
            connection,
            documentId,
            cancellationToken);
        var versions = await ReadVersionsAsync(connection, documentId, cancellationToken);
        var approvals = await ReadApprovalsAsync(connection, documentId, cancellationToken);
        var transitions = await ReadTransitionsAsync(connection, documentId, cancellationToken);
        return new DocumentStoreSnapshot(
            header.TenantId,
            header.ProjectId,
            header.DocumentId,
            header.Title,
            header.Kind,
            header.State,
            header.CurrentVersion,
            header.PhaseName,
            header.Inconsistent,
            header.Version,
            header.CreatedAt,
            header.UpdatedAt,
            classifications,
            versions,
            approvals,
            transitions);
    }

    private async Task<DocumentCreateReceipt> CreateCoreAsync(
        DocumentCreateCommand value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"document:{value.TenantId}:{value.IdempotencyKey}"));
        var commandHash = DocumentCreateHash.Compute(value);
        var replay = await ReadInboxAsync(
            connection,
            transaction,
            value.TenantId,
            value.IdempotencyKey,
            commandHash,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.documents
                (id,tenant_id,project_id,title,kind,state,current_version,phase_name,
                 inconsistent,version,created_at,updated_at)
            VALUES ($1,$2,$3,$4,$5,'in_elaboration',1,$6,false,1,$7,$7);
            """,
            cancellationToken,
            Text(value.DocumentId),
            Text(value.TenantId),
            Text(value.ProjectId),
            Text(value.Title),
            Text(value.Kind),
            NullableText(value.PhaseName),
            Timestamp(value.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.document_versions
                (id,tenant_id,project_id,document_id,version,catalog_path,content_hash,
                 supersedes_id,author_kind,author_id,created_at)
            VALUES ($1,$2,$3,$4,1,$5,$6,NULL,$7,$8,$9);
            """,
            cancellationToken,
            Text(value.DocumentVersionId),
            Text(value.TenantId),
            Text(value.ProjectId),
            Text(value.DocumentId),
            Text(value.CatalogPath),
            Text(value.ContentHash),
            Text(value.AuthorKind),
            NullableText(value.AuthorId),
            Timestamp(value.OccurredAt));
        for (var index = 0; index < value.Classifications.Count; index++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.document_classifications
                    (tenant_id,project_id,document_id,label,ordinal)
                VALUES ($1,$2,$3,$4,$5);
                """,
                cancellationToken,
                Text(value.TenantId),
                Text(value.ProjectId),
                Text(value.DocumentId),
                Text(value.Classifications[index]),
                Integer(index + 1));
        }

        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"audit-ledger:{value.TenantId}"));
        var payload = JsonSerializer.Serialize(new
        {
            documentId = value.DocumentId,
            projectId = value.ProjectId,
            state = "in_elaboration",
            currentVersion = 1,
            phaseName = value.PhaseName,
            inconsistent = false,
        });
        var (sequence, previousHash) = await ReadLedgerTailAsync(
            connection,
            transaction,
            value.TenantId,
            cancellationToken);
        const string eventType = "document.stateChanged";
        var ledgerHash = AuditLedgerHash.Compute(
            previousHash,
            value.TenantId,
            sequence,
            eventType,
            payload,
            value.OccurredAt);
        var outboxId = UlidValue.New(value.OccurredAt).ToString();
        var receipt = new DocumentCreateReceipt(
            value.DocumentId,
            value.DocumentVersionId,
            1,
            sequence,
            ledgerHash,
            outboxId,
            Replay: false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """,
            cancellationToken,
            Text(UlidValue.New(value.OccurredAt).ToString()),
            Text(value.TenantId),
            Bigint(sequence),
            Text(previousHash),
            Text(ledgerHash),
            Text(eventType),
            Json(payload),
            Timestamp(value.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.outbox_messages
                (id,tenant_id,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(outboxId),
            Text(value.TenantId),
            Text(eventType),
            Json(payload),
            Timestamp(value.OccurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(value.TenantId),
            Text(value.IdempotencyKey),
            Text(commandHash),
            Json(JsonSerializer.Serialize(receipt)),
            Timestamp(value.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<IReadOnlyList<string>> ReadClassificationsAsync(
        NpgsqlConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT label FROM harness.document_classifications WHERE document_id=$1 ORDER BY ordinal;";
        query.Parameters.Add(Text(documentId));
        var result = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentVersionStoreSnapshot>> ReadVersionsAsync(
        NpgsqlConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,version,catalog_path,content_hash,supersedes_id,author_kind,author_id,created_at
            FROM harness.document_versions WHERE document_id=$1 ORDER BY version;
            """;
        query.Parameters.Add(Text(documentId));
        var result = new List<DocumentVersionStoreSnapshot>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentVersionStoreSnapshot(
                Trim(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetString(2),
                Trim(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Trim(reader.GetString(4)),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : Trim(reader.GetString(6)),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentApprovalStoreSnapshot>> ReadApprovalsAsync(
        NpgsqlConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,document_version_id,title,description,priority,due_at,state,
                   requested_by_agent_id,requested_at,resolved_by_profile_id,resolved_at,
                   resolution_note,version
            FROM harness.document_approval_requests
            WHERE document_id=$1 ORDER BY requested_at,id;
            """;
        query.Parameters.Add(Text(documentId));
        var result = new List<DocumentApprovalStoreSnapshot>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentApprovalStoreSnapshot(
                Trim(reader.GetString(0)),
                Trim(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6),
                Trim(reader.GetString(7)),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : Trim(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt64(12)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentTransitionStoreSnapshot>> ReadTransitionsAsync(
        NpgsqlConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,document_version,from_state,to_state,note,actor_kind,actor_id,occurred_at
            FROM harness.document_state_transitions
            WHERE document_id=$1 ORDER BY occurred_at,id;
            """;
        query.Parameters.Add(Text(documentId));
        var result = new List<DocumentTransitionStoreSnapshot>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentTransitionStoreSnapshot(
                Trim(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : Trim(reader.GetString(6)),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return result;
    }

    private static async Task<DocumentCreateReceipt?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash,response_json::text FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(Trim(reader.GetString(0)), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different document command.");
        }

        return JsonSerializer.Deserialize<DocumentCreateReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted document receipt is invalid.");
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1;";
        query.Parameters.Add(Text(tenantId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, Trim(reader.GetString(1)))
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

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        TypedValue = value,
        NpgsqlDbType = NpgsqlDbType.Jsonb,
    };

    private static string Trim(string value) => value.TrimEnd();

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private sealed record DocumentHeader(
        string TenantId,
        string ProjectId,
        string DocumentId,
        string Title,
        string Kind,
        string State,
        int CurrentVersion,
        string? PhaseName,
        bool Inconsistent,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
