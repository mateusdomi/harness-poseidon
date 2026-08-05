using System.Globalization;
using Harness.Persistence.Abstractions.Coordination;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteSolicitationAttachmentStore(SqliteWriteDispatcher dispatcher)
    : ISolicitationAttachmentStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<SolicitationAttachmentRecord> CreateAsync(
        SolicitationAttachmentCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO solicitation_attachments
                    (tenant_id,id,solicitation_id,file_name,content_type,size_bytes,sha256,
                     state,storage_path,created_at,role)
                VALUES ($tenant,$id,$solicitation,$fileName,$contentType,$size,$sha256,
                        'accepted',$storagePath,$at,$role);
                """;
            insert.Parameters.AddWithValue("$tenant", command.TenantId);
            insert.Parameters.AddWithValue("$id", command.Id);
            insert.Parameters.AddWithValue("$solicitation", command.SolicitationId);
            insert.Parameters.AddWithValue("$fileName", command.FileName);
            insert.Parameters.AddWithValue("$contentType", command.ContentType);
            insert.Parameters.AddWithValue("$size", command.SizeBytes);
            insert.Parameters.AddWithValue("$sha256", command.Sha256);
            insert.Parameters.AddWithValue("$storagePath", command.StoragePath);
            insert.Parameters.AddWithValue("$role", command.Role);
            insert.Parameters.AddWithValue(
                "$at",
                command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(token);
            return new SolicitationAttachmentRecord(
                command.TenantId,
                command.Id,
                command.SolicitationId,
                command.FileName,
                command.ContentType,
                command.SizeBytes,
                command.Sha256,
                "accepted",
                command.StoragePath,
                command.OccurredAt,
                command.Role);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SolicitationAttachmentRecord>> ListAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<SolicitationAttachmentRecord>>(
            async (connection, token) =>
            {
                var values = new List<SolicitationAttachmentRecord>();
                await using var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT tenant_id,id,solicitation_id,file_name,content_type,size_bytes,sha256," +
                    "state,storage_path,created_at,role FROM solicitation_attachments " +
                    "WHERE tenant_id=$tenant AND solicitation_id=$solicitation ORDER BY id;";
                query.Parameters.AddWithValue("$tenant", tenantId);
                query.Parameters.AddWithValue("$solicitation", solicitationId);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    values.Add(new SolicitationAttachmentRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt64(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        DateTimeOffset.Parse(
                            reader.GetString(9),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                        reader.GetString(10)));
                }

                return values;
            },
            cancellationToken);
}
