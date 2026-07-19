using System.Globalization;
using Harness.Persistence.Abstractions.Prototyping;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteVisualReferenceAssetStore(SqliteWriteDispatcher dispatcher)
    : IVisualReferenceAssetStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<VisualReferenceAssetRecord> CreateAsync(
        VisualReferenceAssetCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO visual_reference_assets
                    (tenant_id,id,reference_id,file_name,content_type,size_bytes,sha256,
                     storage_path,created_at)
                VALUES ($tenant,$id,$reference,$fileName,$contentType,$size,$sha256,
                        $storagePath,$at);
                """;
            insert.Parameters.AddWithValue("$tenant", command.TenantId);
            insert.Parameters.AddWithValue("$id", command.Id);
            insert.Parameters.AddWithValue("$reference", command.ReferenceId);
            insert.Parameters.AddWithValue("$fileName", command.FileName);
            insert.Parameters.AddWithValue("$contentType", command.ContentType);
            insert.Parameters.AddWithValue("$size", command.SizeBytes);
            insert.Parameters.AddWithValue("$sha256", command.Sha256);
            insert.Parameters.AddWithValue("$storagePath", command.StoragePath);
            insert.Parameters.AddWithValue(
                "$at",
                command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(token);
            return new VisualReferenceAssetRecord(
                command.TenantId,
                command.Id,
                command.ReferenceId,
                command.FileName,
                command.ContentType,
                command.SizeBytes,
                command.Sha256,
                command.StoragePath,
                command.OccurredAt);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<VisualReferenceAssetRecord>> ListAsync(
        string tenantId,
        string referenceId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<VisualReferenceAssetRecord>>(
            async (connection, token) =>
            {
                var values = new List<VisualReferenceAssetRecord>();
                await using var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT tenant_id,id,reference_id,file_name,content_type,size_bytes,sha256," +
                    "storage_path,created_at FROM visual_reference_assets " +
                    "WHERE tenant_id=$tenant AND reference_id=$reference ORDER BY id;";
                query.Parameters.AddWithValue("$tenant", tenantId);
                query.Parameters.AddWithValue("$reference", referenceId);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    values.Add(new VisualReferenceAssetRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt64(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        DateTimeOffset.Parse(
                            reader.GetString(8),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind)));
                }

                return values;
            },
            cancellationToken);
}
