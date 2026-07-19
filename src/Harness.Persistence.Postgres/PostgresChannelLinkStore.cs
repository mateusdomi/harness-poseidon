using Harness.Persistence.Abstractions.Conversations;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresChannelLinkStore(NpgsqlDataSource dataSource) : IChannelLinkStore
{
    private const string Select =
        "SELECT tenant_id,id,kind,external_identity,profile_id,project_id,conversation_id,linked_at " +
        "FROM harness.channel_links";

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<ChannelLinkRecord> GetOrCreateAsync(
        ChannelLinkCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return GetOrCreateCoreAsync(command, cancellationToken);
    }

    public async Task<ChannelLinkRecord?> GetAsync(
        string tenantId,
        string linkId,
        CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand($"{Select} WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(linkId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<ChannelLinkRecord>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var values = new List<ChannelLinkRecord>();
        await using var query = _dataSource.CreateCommand($"{Select} WHERE tenant_id=$1 ORDER BY id;");
        query.Parameters.Add(Text(tenantId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(Read(reader));
        }

        return values;
    }

    private async Task<ChannelLinkRecord> GetOrCreateCoreAsync(
        ChannelLinkCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var mutex = connection.CreateCommand())
        {
            mutex.Transaction = transaction;
            mutex.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));";
            mutex.Parameters.Add(
                Text($"channel-link:{command.TenantId}:{command.Kind}:{command.ExternalIdentity}"));
            await mutex.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = await ReadByIdentityAsync(
            connection, transaction, command.TenantId, command.Kind, command.ExternalIdentity, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO harness.channel_links
                    (tenant_id,id,kind,external_identity,profile_id,project_id,conversation_id,linked_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """;
            insert.Parameters.Add(Text(command.TenantId));
            insert.Parameters.Add(Text(command.Id));
            insert.Parameters.Add(Text(command.Kind));
            insert.Parameters.Add(Text(command.ExternalIdentity));
            insert.Parameters.Add(Text(command.ProfileId));
            insert.Parameters.Add(Text(command.ProjectId));
            insert.Parameters.Add(Text(command.ConversationId));
            insert.Parameters.Add(Timestamp(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChannelLinkRecord(
            command.TenantId,
            command.Id,
            command.Kind,
            command.ExternalIdentity,
            command.ProfileId,
            command.ProjectId,
            command.ConversationId,
            command.OccurredAt);
    }

    private static async Task<ChannelLinkRecord?> ReadByIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string kind,
        string externalIdentity,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND kind=$2 AND external_identity=$3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(kind));
        query.Parameters.Add(Text(externalIdentity));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static ChannelLinkRecord Read(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(),
        reader.GetString(1).TrimEnd(),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4).TrimEnd(),
        reader.GetString(5).TrimEnd(),
        reader.GetString(6).TrimEnd(),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
