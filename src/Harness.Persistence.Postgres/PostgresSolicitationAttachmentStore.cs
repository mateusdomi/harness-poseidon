using Harness.Persistence.Abstractions.Coordination;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresSolicitationAttachmentStore(NpgsqlDataSource dataSource)
    : ISolicitationAttachmentStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<SolicitationAttachmentRecord> CreateAsync(
        SolicitationAttachmentCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<SolicitationAttachmentRecord>> ListAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default)
    {
        var values = new List<SolicitationAttachmentRecord>();
        await using var query = _dataSource.CreateCommand(
            "SELECT tenant_id,id,solicitation_id,file_name,content_type,size_bytes,sha256," +
            "state,storage_path,created_at FROM harness.solicitation_attachments " +
            "WHERE tenant_id=$1 AND solicitation_id=$2 ORDER BY id;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(solicitationId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new SolicitationAttachmentRecord(
                reader.GetString(0).TrimEnd(),
                reader.GetString(1).TrimEnd(),
                reader.GetString(2).TrimEnd(),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6).TrimEnd(),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return values;
    }

    private async Task<SolicitationAttachmentRecord> CreateCoreAsync(
        SolicitationAttachmentCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var insert = _dataSource.CreateCommand(
            """
            INSERT INTO harness.solicitation_attachments
                (tenant_id,id,solicitation_id,file_name,content_type,size_bytes,sha256,
                 state,storage_path,created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,'accepted',$8,$9);
            """);
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.Id));
        insert.Parameters.Add(Text(command.SolicitationId));
        insert.Parameters.Add(Text(command.FileName));
        insert.Parameters.Add(Text(command.ContentType));
        insert.Parameters.Add(Bigint(command.SizeBytes));
        insert.Parameters.Add(Text(command.Sha256));
        insert.Parameters.Add(Text(command.StoragePath));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
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
            command.OccurredAt);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
