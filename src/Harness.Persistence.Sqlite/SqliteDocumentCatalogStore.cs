using System.Globalization;
using Harness.Persistence.Abstractions.Documents;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDocumentCatalogStore(SqliteWriteDispatcher dispatcher) : IDocumentCatalogStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<DocumentCatalogRecord>> ListDocumentsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<DocumentCatalogRecord>>(async (connection, token) =>
        {
            var ids = new List<string>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText =
                    "SELECT id FROM documents WHERE tenant_id=$tenant " +
                    "AND ($project IS NULL OR project_id=$project) " +
                    "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
                Add(query, "$tenant", tenantId);
                AddNullable(query, "$project", projectId);
                AddNullable(query, "$after", afterId);
                Add(query, "$limit", limit);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
            }

            var rows = new List<DocumentCatalogRecord>();
            foreach (var id in ids)
            {
                rows.Add((await ReadDocumentAsync(connection, tenantId, id, token))!);
            }

            return rows;
        }, cancellationToken);

    public Task<DocumentCatalogPageRecord> PageDocumentsAsync(
        string tenantId, DocumentCatalogPageQuery query,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            const string filters =
                "d.tenant_id=$tenant " +
                "AND ($project IS NULL OR d.project_id=$project) " +
                "AND ($search IS NULL OR lower(d.title) LIKE $search ESCAPE '\\' OR lower(d.id) LIKE $search ESCAPE '\\') " +
                "AND ($kind IS NULL OR d.kind=$kind) " +
                "AND ($state IS NULL OR d.state=$state) " +
                "AND ($phase IS NULL OR d.phase_name=$phase) " +
                "AND ($orphan=0 OR d.phase_name IS NULL) " +
                "AND ($inconsistent IS NULL OR d.inconsistent=$inconsistent) " +
                "AND ($classification IS NULL OR EXISTS (SELECT 1 FROM document_classifications dc " +
                "WHERE dc.tenant_id=d.tenant_id AND dc.document_id=d.id AND dc.label=$classification))";

            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM documents d WHERE {filters};";
            AddDocumentPageParameters(count, tenantId, query);
            var total = Convert.ToInt32(
                await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);

            var ids = new List<string>();
            await using (var page = connection.CreateCommand())
            {
                page.CommandText = $"SELECT d.id FROM documents d WHERE {filters} " +
                    "ORDER BY d.updated_at DESC,d.id DESC LIMIT $limit OFFSET $offset;";
                AddDocumentPageParameters(page, tenantId, query);
                Add(page, "$limit", query.Limit); Add(page, "$offset", query.Offset);
                await using var reader = await page.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
            }

            var rows = new List<DocumentCatalogRecord>();
            foreach (var id in ids)
                rows.Add((await ReadDocumentAsync(connection, tenantId, id, token))!);
            return new DocumentCatalogPageRecord(rows, total);
        }, cancellationToken);

    public Task<DocumentCatalogRecord?> GetDocumentAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadDocumentAsync(connection, tenantId, documentId, token),
            cancellationToken);

    public Task<IReadOnlyList<DocumentVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? documentId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<DocumentVersionCatalogRecord>>(async (connection, token) =>
        {
            var rows = new List<DocumentVersionCatalogRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText = VersionSelect +
                " WHERE v.tenant_id=$tenant AND ($document IS NULL OR v.document_id=$document) " +
                "AND ($after IS NULL OR v.id>$after) ORDER BY v.id LIMIT $limit;";
            Add(query, "$tenant", tenantId);
            AddNullable(query, "$document", documentId);
            AddNullable(query, "$after", afterId);
            Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(ReadVersion(reader));
            return rows;
        }, cancellationToken);

    public Task<DocumentVersionCatalogPageRecord> PageVersionsAsync(
        string tenantId, string? documentId, int offset, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM document_versions v WHERE v.tenant_id=$tenant " +
                "AND ($document IS NULL OR v.document_id=$document);";
            Add(count, "$tenant", tenantId); AddNullable(count, "$document", documentId);
            var total = Convert.ToInt32(
                await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            var rows = new List<DocumentVersionCatalogRecord>();
            await using var page = connection.CreateCommand();
            page.CommandText = VersionSelect +
                " WHERE v.tenant_id=$tenant AND ($document IS NULL OR v.document_id=$document) " +
                "ORDER BY v.created_at DESC,v.id DESC LIMIT $limit OFFSET $offset;";
            Add(page, "$tenant", tenantId); AddNullable(page, "$document", documentId);
            Add(page, "$limit", limit); Add(page, "$offset", offset);
            await using var reader = await page.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(ReadVersion(reader));
            return new DocumentVersionCatalogPageRecord(rows, total);
        }, cancellationToken);

    public Task<DocumentVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<DocumentVersionCatalogRecord?>(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText = VersionSelect + " WHERE v.tenant_id=$tenant AND v.id=$id;";
            Add(query, "$tenant", tenantId);
            Add(query, "$id", versionId);
            await using var reader = await query.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadVersion(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ApprovalCatalogRecord>> ListApprovalsAsync(
        string tenantId, string? projectId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<ApprovalCatalogRecord>>(async (connection, token) =>
        {
            var rows = new List<ApprovalCatalogRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT * FROM (" + ApprovalSelect + ") a " +
                "WHERE tenant_id=$tenant AND ($project IS NULL OR project_id=$project) " +
                "AND ($task IS NULL OR task_id=$task) " +
                "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(query, "$tenant", tenantId); AddNullable(query, "$project", projectId);
            AddNullable(query, "$task", taskId);
            AddNullable(query, "$after", afterId); Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(ReadApproval(reader));
            return rows;
        }, cancellationToken);

    public Task<ApprovalCatalogPageRecord> PageApprovalsAsync(
        string tenantId, ApprovalCatalogPageQuery query,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            const string filters =
                "tenant_id=$tenant AND ($project IS NULL OR project_id=$project) " +
                "AND ($task IS NULL OR task_id=$task) " +
                "AND ($state IS NULL OR state=$state) AND ($priority IS NULL OR priority=$priority) " +
                "AND ($due='all' OR ($due='overdue' AND due_at IS NOT NULL AND due_at<$now) " +
                "OR ($due='week' AND due_at IS NOT NULL AND due_at<=$week) " +
                "OR ($due='none' AND due_at IS NULL))";
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM (" + ApprovalSelect + $") a WHERE {filters};";
            AddApprovalPageParameters(count, tenantId, query);
            var total = Convert.ToInt32(
                await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            var rows = new List<ApprovalCatalogRecord>();
            await using var page = connection.CreateCommand();
            page.CommandText = "SELECT * FROM (" + ApprovalSelect + $") a WHERE {filters} " +
                "ORDER BY CASE WHEN due_at IS NULL THEN 1 ELSE 0 END,due_at," +
                "CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END," +
                "requested_at,id LIMIT $limit OFFSET $offset;";
            AddApprovalPageParameters(page, tenantId, query);
            Add(page, "$limit", query.Limit); Add(page, "$offset", query.Offset);
            await using var reader = await page.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(ReadApproval(reader));
            return new ApprovalCatalogPageRecord(rows, total);
        }, cancellationToken);

    public Task<ApprovalCatalogRecord?> GetApprovalAsync(
        string tenantId, string approvalId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<ApprovalCatalogRecord?>(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT * FROM (" + ApprovalSelect +
                ") a WHERE tenant_id=$tenant AND id=$id;";
            Add(query, "$tenant", tenantId); Add(query, "$id", approvalId);
            await using var reader = await query.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadApproval(reader) : null;
        }, cancellationToken);

    private static async Task<DocumentCatalogRecord?> ReadDocumentAsync(
        SqliteConnection connection, string tenantId, string documentId, CancellationToken token)
    {
        DocumentHeader? header;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT id,project_id,title,kind,state,current_version,phase_name,inconsistent," +
                "created_at,updated_at,version,template_code FROM documents WHERE tenant_id=$tenant AND id=$id;";
            Add(query, "$tenant", tenantId);
            Add(query, "$id", documentId);
            await using var reader = await query.ExecuteReaderAsync(token);
            header = await reader.ReadAsync(token)
                ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7),
                    Parse(reader.GetString(8)), Parse(reader.GetString(9)), reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11))
                : null;
        }

        if (header is null) return null;
        var classifications = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT label FROM document_classifications WHERE document_id=$id ORDER BY ordinal;";
            Add(query, "$id", documentId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) classifications.Add(reader.GetString(0));
        }

        return new(header.Id, header.ProjectId, header.Title, header.Kind, header.State,
            header.CurrentVersion, classifications, header.PhaseName, header.Inconsistent,
            header.CreatedAt, header.UpdatedAt, header.Version, header.TemplateCode);
    }

    private static DocumentVersionCatalogRecord ReadVersion(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        Parse(reader.GetString(7)));

    private const string VersionSelect =
        "SELECT v.id,v.document_id,v.version,v.catalog_path,v.content_hash,v.author_kind," +
        "v.author_id,v.created_at FROM document_versions v";
    private const string ApprovalSelect =
        "SELECT tenant_id,id,project_id,NULL AS gate_id,NULL AS task_id,document_id,title," +
        "description,priority,due_at,state,requested_by_agent_id,requested_at," +
        "resolved_by_profile_id,resolved_at,resolution_note,version FROM document_approval_requests " +
        "UNION ALL SELECT tenant_id,id,project_id,gate_id,task_id,NULL AS document_id,title," +
        "description,priority,due_at,state,requested_by_agent_id,requested_at," +
        "resolved_by_profile_id,resolved_at,resolution_note,version FROM general_approval_requests";

    private static ApprovalCatalogRecord ReadApproval(SqliteDataReader reader) => new(
        reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetString(8),
        reader.IsDBNull(9) ? null : Parse(reader.GetString(9)), reader.GetString(10),
        reader.GetString(11), Parse(reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : Parse(reader.GetString(14)),
        reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetInt64(16));

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void AddDocumentPageParameters(
        SqliteCommand command, string tenantId, DocumentCatalogPageQuery query)
    {
        Add(command, "$tenant", tenantId); AddNullable(command, "$project", query.ProjectId);
        AddNullable(command, "$search", query.SearchPattern); AddNullable(command, "$kind", query.Kind);
        AddNullable(command, "$state", query.State); AddNullable(command, "$phase", query.PhaseName);
        Add(command, "$orphan", query.OrphanOnly); AddNullable(command, "$inconsistent", query.Inconsistent);
        AddNullable(command, "$classification", query.Classification);
    }
    private static void AddApprovalPageParameters(
        SqliteCommand command, string tenantId, ApprovalCatalogPageQuery query)
    {
        Add(command, "$tenant", tenantId); AddNullable(command, "$project", query.ProjectId);
        AddNullable(command, "$task", query.TaskId);
        AddNullable(command, "$state", query.State); AddNullable(command, "$priority", query.Priority);
        Add(command, "$due", query.Due); Add(command, "$now", query.Now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$week", query.Now.AddDays(7).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private sealed record DocumentHeader(
        string Id, string ProjectId, string Title, string Kind, string State, int CurrentVersion,
        string? PhaseName, bool Inconsistent, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        long Version, string? TemplateCode);
}
