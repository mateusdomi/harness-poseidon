using Harness.Persistence.Abstractions.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Registro das exportações de documentos em PostgreSQL. Paridade estrita com o
/// SQLite. Ver <see cref="IDocumentExportStore"/>.
/// </summary>
public sealed class PostgresDocumentExportStore(NpgsqlDataSource dataSource)
    : IDocumentExportStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string Select =
        "SELECT export_id,project_id,document_count,requested_by_profile_id," +
        "manifest_json::text,created_at FROM harness.document_exports";

    public async Task RecordAsync(
        DocumentExportRecordCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO harness.document_exports
            (tenant_id,export_id,project_id,document_count,requested_by_profile_id,
             manifest_json,created_at)
            VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7);
            """;
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ExportId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Integer(command.DocumentCount));
        insert.Parameters.Add(Text(command.RequestedByProfileId));
        insert.Parameters.Add(Text(command.ManifestJson));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentExportRecord>> ListAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND project_id=$2 " +
            "ORDER BY created_at DESC, export_id DESC LIMIT $3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        query.Parameters.Add(Integer(Math.Clamp(limit, 1, 200)));
        var items = new List<DocumentExportRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DocumentExportRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return items;
    }

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Integer(int value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = value };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value };
}
