using System.Globalization;
using Harness.Persistence.Abstractions.Documents;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Registro das exportações de documentos em SQLite. Ver <see cref="IDocumentExportStore"/>.
/// Só insere e lê: o histórico do que saiu do produto não se reescreve.
/// </summary>
public sealed class SqliteDocumentExportStore(SqliteWriteDispatcher dispatcher)
    : IDocumentExportStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Select =
        "SELECT export_id,project_id,document_count,requested_by_profile_id,manifest_json,created_at " +
        "FROM document_exports";

    public Task RecordAsync(
        DocumentExportRecordCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync<int>(
            async (connection, token) =>
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO document_exports " +
                    "(tenant_id,export_id,project_id,document_count,requested_by_profile_id," +
                    "manifest_json,created_at) VALUES " +
                    "($tenant,$id,$project,$count,$profile,$manifest,$at);";
                Add(insert, "$tenant", command.TenantId);
                Add(insert, "$id", command.ExportId);
                Add(insert, "$project", command.ProjectId);
                Add(insert, "$count", command.DocumentCount);
                Add(insert, "$profile", command.RequestedByProfileId);
                Add(insert, "$manifest", command.ManifestJson);
                Add(insert, "$at", Store(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<DocumentExportRecord>> ListAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<DocumentExportRecord>>(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND project_id=$project " +
                    "ORDER BY created_at DESC, export_id DESC LIMIT $limit;";
                Add(query, "$tenant", tenantId);
                Add(query, "$project", projectId);
                Add(query, "$limit", Math.Clamp(limit, 1, 200));
                var items = new List<DocumentExportRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(new DocumentExportRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
                }
                return items;
            },
            cancellationToken);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
