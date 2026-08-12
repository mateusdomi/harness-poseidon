using System.Globalization;
using Harness.Persistence.Abstractions.Conversations;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteChannelLinkStore(SqliteWriteDispatcher dispatcher) : IChannelLinkStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ChannelLinkRecord> GetOrCreateAsync(
        ChannelLinkCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            var existing = await ReadByIdentityAsync(
                connection, transaction, command.TenantId, command.Kind, command.ExternalIdentity, token);
            if (existing is not null)
            {
                await transaction.CommitAsync(token);
                return existing;
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO channel_links
                        (tenant_id,id,kind,external_identity,profile_id,project_id,conversation_id,linked_at,display_name)
                    VALUES ($tenant,$id,$kind,$identity,$profile,$project,$conversation,$at,$displayName);
                    """;
                insert.Parameters.AddWithValue("$tenant", command.TenantId);
                insert.Parameters.AddWithValue("$id", command.Id);
                insert.Parameters.AddWithValue("$kind", command.Kind);
                insert.Parameters.AddWithValue("$identity", command.ExternalIdentity);
                insert.Parameters.AddWithValue("$profile", command.ProfileId);
                insert.Parameters.AddWithValue("$project", command.ProjectId);
                insert.Parameters.AddWithValue("$conversation", command.ConversationId);
                insert.Parameters.AddWithValue(
                    "$at", command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$displayName", command.DisplayName ?? (object)DBNull.Value);
                await insert.ExecuteNonQueryAsync(token);
            }

            await transaction.CommitAsync(token);
            return new ChannelLinkRecord(
                command.TenantId,
                command.Id,
                command.Kind,
                command.ExternalIdentity,
                command.ProfileId,
                command.ProjectId,
                command.ConversationId,
                command.OccurredAt,
                DisplayName: command.DisplayName);
        }, cancellationToken);
    }

    public Task<ChannelLinkRecord?> GetAsync(
        string tenantId,
        string linkId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText = $"{Select} WHERE tenant_id=$tenant AND id=$id;";
            query.Parameters.AddWithValue("$tenant", tenantId);
            query.Parameters.AddWithValue("$id", linkId);
            await using var reader = await query.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? Read(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ChannelLinkRecord>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<ChannelLinkRecord>>(async (connection, token) =>
        {
            var values = new List<ChannelLinkRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText = $"{Select} WHERE tenant_id=$tenant ORDER BY id;";
            query.Parameters.AddWithValue("$tenant", tenantId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                values.Add(Read(reader));
            }

            return values;
        }, cancellationToken);

    public Task MarkInboundAsync(
        string tenantId,
        string linkId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE channel_links SET last_inbound_at=$at WHERE tenant_id=$tenant AND id=$id;";
            update.Parameters.AddWithValue(
                "$at", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$id", linkId);
            await update.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);

    public Task UpdateDisplayNameAsync(
        string tenantId,
        string linkId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE channel_links SET display_name=$displayName WHERE tenant_id=$tenant AND id=$id;";
            update.Parameters.AddWithValue("$displayName", displayName);
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$id", linkId);
            await update.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);

    private const string Select =
        "SELECT tenant_id,id,kind,external_identity,profile_id,project_id,conversation_id,linked_at," +
        "last_inbound_at,display_name FROM channel_links";

    private static async Task<ChannelLinkRecord?> ReadByIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string kind,
        string externalIdentity,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{Select} WHERE tenant_id=$tenant AND kind=$kind AND external_identity=$identity;";
        query.Parameters.AddWithValue("$tenant", tenantId);
        query.Parameters.AddWithValue("$kind", kind);
        query.Parameters.AddWithValue("$identity", externalIdentity);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    private static ChannelLinkRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        DateTimeOffset.Parse(
            reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(8)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(9) ? null : reader.GetString(9));
}
