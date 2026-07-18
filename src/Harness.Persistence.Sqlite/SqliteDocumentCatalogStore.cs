using System.Globalization;
using Harness.Persistence.Abstractions.Documents;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteDocumentCatalogStore(SqliteWriteDispatcher dispatcher) : IDocumentCatalogStore
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
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<ApprovalCatalogRecord>>(async (connection, token) =>
        {
            var rows = new List<ApprovalCatalogRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText = ApprovalSelect +
                " WHERE tenant_id=$tenant AND ($project IS NULL OR project_id=$project) " +
                "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(query, "$tenant", tenantId); AddNullable(query, "$project", projectId);
            AddNullable(query, "$after", afterId); Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(ReadApproval(reader));
            return rows;
        }, cancellationToken);

    public Task<ApprovalCatalogRecord?> GetApprovalAsync(
        string tenantId, string approvalId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<ApprovalCatalogRecord?>(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText = ApprovalSelect + " WHERE tenant_id=$tenant AND id=$id;";
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
                "created_at,updated_at,version FROM documents WHERE tenant_id=$tenant AND id=$id;";
            Add(query, "$tenant", tenantId);
            Add(query, "$id", documentId);
            await using var reader = await query.ExecuteReaderAsync(token);
            header = await reader.ReadAsync(token)
                ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7),
                    Parse(reader.GetString(8)), Parse(reader.GetString(9)), reader.GetInt64(10))
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
            header.CreatedAt, header.UpdatedAt, header.Version);
    }

    private static DocumentVersionCatalogRecord ReadVersion(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        Parse(reader.GetString(7)));

    private const string VersionSelect =
        "SELECT v.id,v.document_id,v.version,v.catalog_path,v.content_hash,v.author_kind," +
        "v.author_id,v.created_at FROM document_versions v";
    private const string ApprovalSelect =
        "SELECT id,project_id,document_id,title,description,priority,due_at,state," +
        "requested_by_agent_id,requested_at,resolved_by_profile_id,resolved_at,resolution_note,version " +
        "FROM document_approval_requests";

    private static ApprovalCatalogRecord ReadApproval(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), null, null, reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : Parse(reader.GetString(6)), reader.GetString(7),
        reader.GetString(8), Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13));

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private sealed record DocumentHeader(
        string Id, string ProjectId, string Title, string Kind, string State, int CurrentVersion,
        string? PhaseName, bool Inconsistent, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        long Version);
}
