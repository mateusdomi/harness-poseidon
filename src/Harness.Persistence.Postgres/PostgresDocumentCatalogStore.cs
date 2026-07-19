using Harness.Persistence.Abstractions.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDocumentCatalogStore(NpgsqlDataSource dataSource) : IDocumentCatalogStore
{
    private const string VersionSelect =
        "SELECT v.id,v.document_id,v.version,v.catalog_path,v.content_hash,v.author_kind," +
        "v.author_id,v.created_at FROM harness.document_versions v";
    private const string ApprovalSelect =
        "SELECT tenant_id,id,project_id,NULL AS gate_id,NULL AS task_id,document_id,title," +
        "description,priority,due_at,state,requested_by_agent_id,requested_at," +
        "resolved_by_profile_id,resolved_at,resolution_note,version FROM harness.document_approval_requests " +
        "UNION ALL SELECT tenant_id,id,project_id,gate_id,task_id,NULL AS document_id,title," +
        "description,priority,due_at,state,requested_by_agent_id,requested_at," +
        "resolved_by_profile_id,resolved_at,resolution_note,version FROM harness.general_approval_requests";

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<DocumentCatalogRecord>> ListDocumentsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var ids = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT id FROM harness.documents WHERE tenant_id=$1 " +
                "AND ($2 IS NULL OR project_id=$2) AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(NullableText(projectId));
            query.Parameters.Add(NullableText(afterId));
            query.Parameters.Add(Integer(limit));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0).TrimEnd());
            }
        }

        var rows = new List<DocumentCatalogRecord>();
        foreach (var id in ids)
        {
            rows.Add((await ReadDocumentAsync(connection, tenantId, id, cancellationToken))!);
        }

        return rows;
    }

    public async Task<DocumentCatalogRecord?> GetDocumentAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadDocumentAsync(connection, tenantId, documentId, cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? documentId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<DocumentVersionCatalogRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{VersionSelect} WHERE v.tenant_id=$1 AND ($2 IS NULL OR v.document_id=$2) " +
            "AND ($3 IS NULL OR v.id>$3) ORDER BY v.id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(documentId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadVersion(reader));
        }

        return rows;
    }

    public async Task<DocumentVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            $"{VersionSelect} WHERE v.tenant_id=$1 AND v.id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(versionId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    public async Task<IReadOnlyList<ApprovalCatalogRecord>> ListApprovalsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<ApprovalCatalogRecord>();
        await using var query = _dataSource.CreateCommand(
            "SELECT * FROM (" + ApprovalSelect + ") a " +
            "WHERE tenant_id=$1 AND ($2 IS NULL OR project_id=$2) " +
            "AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(projectId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadApproval(reader));
        }

        return rows;
    }

    public async Task<ApprovalCatalogRecord?> GetApprovalAsync(
        string tenantId, string approvalId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            "SELECT * FROM (" + ApprovalSelect + ") a WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(approvalId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadApproval(reader) : null;
    }

    private static async Task<DocumentCatalogRecord?> ReadDocumentAsync(
        NpgsqlConnection connection, string tenantId, string documentId,
        CancellationToken cancellationToken)
    {
        DocumentHeader? header;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT id,project_id,title,kind,state,current_version,phase_name,inconsistent," +
                "created_at,updated_at,version FROM harness.documents WHERE tenant_id=$1 AND id=$2;";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(documentId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            header = await reader.ReadAsync(cancellationToken)
                ? new(reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetBoolean(7), reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9), reader.GetInt64(10))
                : null;
        }

        if (header is null)
        {
            return null;
        }

        var classifications = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT label FROM harness.document_classifications WHERE document_id=$1 ORDER BY ordinal;";
            query.Parameters.Add(Text(documentId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                classifications.Add(reader.GetString(0));
            }
        }

        return new(header.Id, header.ProjectId, header.Title, header.Kind, header.State,
            header.CurrentVersion, classifications, header.PhaseName, header.Inconsistent,
            header.CreatedAt, header.UpdatedAt, header.Version);
    }

    private static DocumentVersionCatalogRecord ReadVersion(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), reader.GetInt32(2),
        reader.GetString(3), reader.GetString(4).TrimEnd(), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6).TrimEnd(),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static ApprovalCatalogRecord ReadApproval(NpgsqlDataReader reader) => new(
        reader.GetString(1).TrimEnd(), reader.GetString(2).TrimEnd(),
        reader.IsDBNull(3) ? null : reader.GetString(3).TrimEnd(),
        reader.IsDBNull(4) ? null : reader.GetString(4).TrimEnd(),
        reader.IsDBNull(5) ? null : reader.GetString(5).TrimEnd(),
        reader.GetString(6), reader.GetString(7), reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetString(10), reader.GetString(11).TrimEnd(),
        reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetString(13).TrimEnd(),
        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetInt64(16));

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

    private sealed record DocumentHeader(
        string Id, string ProjectId, string Title, string Kind, string State, int CurrentVersion,
        string? PhaseName, bool Inconsistent, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        long Version);
}
