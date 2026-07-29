using System.Globalization;
using Harness.Persistence.Abstractions.Conversations;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>Última conversa aberta por perfil e projeto, em SQLite.</summary>
public sealed class SqliteProfileActiveConversationStore(SqliteWriteDispatcher dispatcher)
    : IProfileActiveConversationStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Select =
        "SELECT tenant_id,profile_id,project_id,conversation_id,updated_at " +
        "FROM profile_active_conversations";

    public Task<ProfileActiveConversationRecord> RememberAsync(
        ProfileActiveConversationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var upsert = connection.CreateCommand();
                // Substitui em vez de acumular: duas conversas ativas no mesmo par deixariam a
                // restauração ambígua, e ambiguidade aqui reabre o bug que isto conserta.
                upsert.CommandText =
                    "INSERT INTO profile_active_conversations " +
                    "(tenant_id,profile_id,project_id,conversation_id,updated_at) VALUES " +
                    "($tenant,$profile,$project,$conversation,$at) " +
                    "ON CONFLICT(tenant_id,profile_id,project_id) DO UPDATE SET " +
                    "conversation_id=excluded.conversation_id,updated_at=excluded.updated_at;";
                Add(upsert, "$tenant", record.TenantId);
                Add(upsert, "$profile", record.ProfileId);
                Add(upsert, "$project", record.ProjectId);
                Add(upsert, "$conversation", record.ConversationId);
                Add(upsert, "$at", Store(record.UpdatedAt));
                await upsert.ExecuteNonQueryAsync(token);
                return record;
            },
            cancellationToken);
    }

    public Task<ProfileActiveConversationRecord?> RecallAsync(
        string tenantId, string profileId, string projectId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND profile_id=$profile AND project_id=$project;";
                Add(query, "$tenant", tenantId);
                Add(query, "$profile", profileId);
                Add(query, "$project", projectId);
                await using var reader = await query.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token)
                    ? new ProfileActiveConversationRecord(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3),
                        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture))
                    : null;
            },
            cancellationToken);

    public Task ForgetAsync(
        string tenantId, string profileId, string projectId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var delete = connection.CreateCommand();
                delete.CommandText =
                    "DELETE FROM profile_active_conversations " +
                    "WHERE tenant_id=$tenant AND profile_id=$profile AND project_id=$project;";
                Add(delete, "$tenant", tenantId);
                Add(delete, "$profile", profileId);
                Add(delete, "$project", projectId);
                return await delete.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
