using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteDocumentStore(SqliteWriteDispatcher dispatcher) : IDocumentStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<DocumentCreateReceipt> CreateAsync(
        DocumentCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        DocumentCreateValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DocumentStoreSnapshot?> ReadAsync(
        string tenantId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(documentId, nameof(documentId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadCoreAsync(connection, tenantId, documentId, token),
            cancellationToken);
    }

    private static async Task<DocumentCreateReceipt> CreateCoreAsync(
        SqliteConnection connection,
        DocumentCreateCommand value,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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

        var occurredAt = Store(value.OccurredAt);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO documents
                    (id,tenant_id,project_id,title,kind,state,current_version,phase_name,
                     inconsistent,version,created_at,updated_at)
                VALUES ($documentId,$tenantId,$projectId,$title,$kind,'in_elaboration',1,
                        $phaseName,0,1,$occurredAt,$occurredAt);

                INSERT INTO document_versions
                    (id,tenant_id,project_id,document_id,version,catalog_path,content_hash,
                     supersedes_id,author_kind,author_id,created_at)
                VALUES ($documentVersionId,$tenantId,$projectId,$documentId,1,$catalogPath,
                        $contentHash,NULL,$authorKind,$authorId,$occurredAt);
                """;
            Add(insert, "$documentId", value.DocumentId);
            Add(insert, "$tenantId", value.TenantId);
            Add(insert, "$projectId", value.ProjectId);
            Add(insert, "$title", value.Title);
            Add(insert, "$kind", value.Kind);
            AddNullable(insert, "$phaseName", value.PhaseName);
            Add(insert, "$occurredAt", occurredAt);
            Add(insert, "$documentVersionId", value.DocumentVersionId);
            Add(insert, "$catalogPath", value.CatalogPath);
            Add(insert, "$contentHash", value.ContentHash);
            Add(insert, "$authorKind", value.AuthorKind);
            AddNullable(insert, "$authorId", value.AuthorId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < value.Classifications.Count; index++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO document_classifications
                    (tenant_id,project_id,document_id,label,ordinal)
                VALUES ($tenantId,$projectId,$documentId,$label,$ordinal);
                """,
                cancellationToken,
                ("$tenantId", value.TenantId),
                ("$projectId", value.ProjectId),
                ("$documentId", value.DocumentId),
                ("$label", value.Classifications[index]),
                ("$ordinal", index + 1));
        }

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
            INSERT INTO audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($ledgerId,$tenantId,$sequence,$previousHash,$ledgerHash,
                    $eventType,$payload,$occurredAt);
            INSERT INTO outbox_messages
                (id,tenant_id,event_type,payload_json,occurred_at)
            VALUES ($outboxId,$tenantId,$eventType,$payload,$occurredAt);
            INSERT INTO inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($tenantId,$key,$commandHash,$response,$occurredAt);
            """,
            cancellationToken,
            ("$ledgerId", UlidValue.New(value.OccurredAt).ToString()),
            ("$tenantId", value.TenantId),
            ("$sequence", sequence),
            ("$previousHash", previousHash),
            ("$ledgerHash", ledgerHash),
            ("$eventType", eventType),
            ("$payload", payload),
            ("$occurredAt", occurredAt),
            ("$outboxId", outboxId),
            ("$key", value.IdempotencyKey),
            ("$commandHash", commandHash),
            ("$response", JsonSerializer.Serialize(receipt)));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<DocumentStoreSnapshot?> ReadCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string documentId,
        CancellationToken cancellationToken)
    {
        DocumentHeader? header;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                """
                SELECT tenant_id,project_id,id,title,kind,state,current_version,phase_name,
                       inconsistent,version,created_at,updated_at
                FROM documents
                WHERE tenant_id=$tenantId AND id=$documentId;
                """;
            Add(query, "$tenantId", tenantId);
            Add(query, "$documentId", documentId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            header = await reader.ReadAsync(cancellationToken)
                ? new DocumentHeader(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetBoolean(8),
                    reader.GetInt64(9),
                    Parse(reader.GetString(10)),
                    Parse(reader.GetString(11)))
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

    private static async Task<IReadOnlyList<string>> ReadClassificationsAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT label FROM document_classifications WHERE document_id=$documentId ORDER BY ordinal;";
        Add(query, "$documentId", documentId);
        var result = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentVersionStoreSnapshot>> ReadVersionsAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,version,catalog_path,content_hash,supersedes_id,author_kind,author_id,created_at
            FROM document_versions WHERE document_id=$documentId ORDER BY version;
            """;
        Add(query, "$documentId", documentId);
        var result = new List<DocumentVersionStoreSnapshot>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentVersionStoreSnapshot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Parse(reader.GetString(7))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentApprovalStoreSnapshot>> ReadApprovalsAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,document_version_id,title,description,priority,due_at,state,
                   requested_by_agent_id,requested_at,resolved_by_profile_id,resolved_at,
                   resolution_note,version
            FROM document_approval_requests
            WHERE document_id=$documentId ORDER BY requested_at,id;
            """;
        Add(query, "$documentId", documentId);
        var result = new List<DocumentApprovalStoreSnapshot>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentApprovalStoreSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt64(12)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentTransitionStoreSnapshot>> ReadTransitionsAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT id,document_version,from_state,to_state,note,actor_kind,actor_id,occurred_at
            FROM document_state_transitions
            WHERE document_id=$documentId ORDER BY occurred_at,id;
            """;
        Add(query, "$documentId", documentId);
        var result = new List<DocumentTransitionStoreSnapshot>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentTransitionStoreSnapshot(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Parse(reader.GetString(7))));
        }

        return result;
    }

    private static async Task<DocumentCreateReceipt?> ReadInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash,response_json FROM inbox_messages WHERE tenant_id=$tenantId AND idempotency_key=$key;";
        Add(query, "$tenantId", tenantId);
        Add(query, "$key", key);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different document command.");
        }

        return JsonSerializer.Deserialize<DocumentCreateReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted document receipt is invalid.");
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenantId ORDER BY sequence DESC LIMIT 1;";
        Add(query, "$tenantId", tenantId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1))
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
