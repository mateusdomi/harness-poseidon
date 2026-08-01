using System.Data;
using System.Globalization;
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

    public async Task<DocumentCatalogPageRecord> PageDocumentsAsync(
        string tenantId, DocumentCatalogPageQuery query,
        CancellationToken cancellationToken = default)
    {
        const string filters =
            "d.tenant_id=$1 " +
            "AND ($2 IS NULL OR d.project_id=$2) " +
            "AND ($3 IS NULL OR d.title ILIKE $3 ESCAPE '\\' OR d.id ILIKE $3 ESCAPE '\\') " +
            "AND ($4 IS NULL OR d.kind=$4) " +
            "AND ($5 IS NULL OR d.state=$5) " +
            "AND ($6 IS NULL OR d.phase_name=$6) " +
            "AND (NOT $7 OR d.phase_name IS NULL) " +
            "AND ($8 IS NULL OR d.inconsistent=$8) " +
            "AND ($9 IS NULL OR EXISTS (SELECT 1 FROM harness.document_classifications dc " +
            "WHERE dc.tenant_id=d.tenant_id AND dc.document_id=d.id AND dc.label=$9))";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"SELECT COUNT(*) FROM harness.documents d WHERE {filters};";
        AddDocumentPageParameters(count, tenantId, query);
        var total = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var ids = new List<string>();
        await using (var pageCommand = connection.CreateCommand())
        {
            pageCommand.Transaction = transaction;
            pageCommand.CommandText = $"SELECT d.id FROM harness.documents d WHERE {filters} " +
                "ORDER BY d.updated_at DESC,d.id DESC LIMIT $10 OFFSET $11;";
            AddDocumentPageParameters(pageCommand, tenantId, query);
            pageCommand.Parameters.Add(Integer(query.Limit));
            pageCommand.Parameters.Add(Integer(query.Offset));
            await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0).TrimEnd());
        }

        var rows = new List<DocumentCatalogRecord>();
        foreach (var id in ids)
            rows.Add((await ReadDocumentAsync(connection, tenantId, id, cancellationToken))!);
        await transaction.CommitAsync(cancellationToken);
        return new DocumentCatalogPageRecord(rows, total);
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

    public async Task<DocumentVersionCatalogPageRecord> PageVersionsAsync(
        string tenantId, string? documentId, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var count = connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM harness.document_versions v WHERE v.tenant_id=$1 " +
            "AND ($2 IS NULL OR v.document_id=$2);";
        count.Parameters.Add(Text(tenantId)); count.Parameters.Add(NullableText(documentId));
        var total = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var rows = new List<DocumentVersionCatalogRecord>();
        await using var page = connection.CreateCommand(); page.Transaction = transaction;
        page.CommandText = $"{VersionSelect} WHERE v.tenant_id=$1 " +
            "AND ($2 IS NULL OR v.document_id=$2) " +
            "ORDER BY v.created_at DESC,v.id DESC LIMIT $3 OFFSET $4;";
        page.Parameters.Add(Text(tenantId)); page.Parameters.Add(NullableText(documentId));
        page.Parameters.Add(Integer(limit)); page.Parameters.Add(Integer(offset));
        await using var reader = await page.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadVersion(reader));
        await reader.DisposeAsync(); await transaction.CommitAsync(cancellationToken);
        return new DocumentVersionCatalogPageRecord(rows, total);
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
        string tenantId, string? projectId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<ApprovalCatalogRecord>();
        await using var query = _dataSource.CreateCommand(
            "SELECT * FROM (" + ApprovalSelect + ") a " +
            "WHERE tenant_id=$1 AND ($2 IS NULL OR project_id=$2) " +
            "AND ($3 IS NULL OR task_id=$3) " +
            "AND ($4 IS NULL OR id>$4) ORDER BY id LIMIT $5;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(projectId));
        query.Parameters.Add(NullableText(taskId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadApproval(reader));
        }

        return rows;
    }

    public async Task<ApprovalCatalogPageRecord> PageApprovalsAsync(
        string tenantId, ApprovalCatalogPageQuery query,
        CancellationToken cancellationToken = default)
    {
        const string filters =
            "tenant_id=$1 AND ($2 IS NULL OR project_id=$2) " +
            "AND ($3 IS NULL OR task_id=$3) " +
            "AND ($4 IS NULL OR state=$4) AND ($5 IS NULL OR priority=$5) " +
            "AND ($6='all' OR ($6='overdue' AND due_at IS NOT NULL AND due_at<$7) " +
            "OR ($6='week' AND due_at IS NOT NULL AND due_at<=$8) " +
            "OR ($6='none' AND due_at IS NULL))";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var count = connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM (" + ApprovalSelect + $") a WHERE {filters};";
        AddApprovalPageParameters(count, tenantId, query);
        var total = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var rows = new List<ApprovalCatalogRecord>();
        await using var page = connection.CreateCommand(); page.Transaction = transaction;
        page.CommandText = "SELECT * FROM (" + ApprovalSelect + $") a WHERE {filters} " +
            "ORDER BY CASE WHEN due_at IS NULL THEN 1 ELSE 0 END,due_at," +
            "CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END," +
            "requested_at,id LIMIT $9 OFFSET $10;";
        AddApprovalPageParameters(page, tenantId, query);
        page.Parameters.Add(Integer(query.Limit)); page.Parameters.Add(Integer(query.Offset));
        await using var reader = await page.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadApproval(reader));
        await reader.DisposeAsync(); await transaction.CommitAsync(cancellationToken);
        return new ApprovalCatalogPageRecord(rows, total);
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
                "created_at,updated_at,version,template_code FROM harness.documents WHERE tenant_id=$1 AND id=$2;";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(documentId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            header = await reader.ReadAsync(cancellationToken)
                ? new(reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetBoolean(7), reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9), reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11).TrimEnd())
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
            header.CreatedAt, header.UpdatedAt, header.Version, header.TemplateCode);
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

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

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

    private static NpgsqlParameter NullableBoolean(bool? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Boolean,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };

    private static void AddDocumentPageParameters(
        NpgsqlCommand command, string tenantId, DocumentCatalogPageQuery query)
    {
        command.Parameters.Add(Text(tenantId)); command.Parameters.Add(NullableText(query.ProjectId));
        command.Parameters.Add(NullableText(query.SearchPattern)); command.Parameters.Add(NullableText(query.Kind));
        command.Parameters.Add(NullableText(query.State)); command.Parameters.Add(NullableText(query.PhaseName));
        command.Parameters.Add(Boolean(query.OrphanOnly)); command.Parameters.Add(NullableBoolean(query.Inconsistent));
        command.Parameters.Add(NullableText(query.Classification));
    }
    private static void AddApprovalPageParameters(
        NpgsqlCommand command, string tenantId, ApprovalCatalogPageQuery query)
    {
        command.Parameters.Add(Text(tenantId)); command.Parameters.Add(NullableText(query.ProjectId));
        command.Parameters.Add(NullableText(query.TaskId));
        command.Parameters.Add(NullableText(query.State)); command.Parameters.Add(NullableText(query.Priority));
        command.Parameters.Add(Text(query.Due)); command.Parameters.Add(Timestamp(query.Now));
        command.Parameters.Add(Timestamp(query.Now.AddDays(7)));
    }

    private sealed record DocumentHeader(
        string Id, string ProjectId, string Title, string Kind, string State, int CurrentVersion,
        string? PhaseName, bool Inconsistent, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        long Version, string? TemplateCode);
}
