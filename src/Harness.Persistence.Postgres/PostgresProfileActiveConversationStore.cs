using Harness.Persistence.Abstractions.Conversations;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>Última conversa aberta por perfil e projeto, em PostgreSQL. Paridade com o SQLite.</summary>
public sealed class PostgresProfileActiveConversationStore(NpgsqlDataSource dataSource)
    : IProfileActiveConversationStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string Select =
        "SELECT tenant_id,profile_id,project_id,conversation_id,updated_at " +
        "FROM harness.profile_active_conversations";

    public async Task<ProfileActiveConversationRecord> RememberAsync(
        ProfileActiveConversationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO harness.profile_active_conversations
            (tenant_id,profile_id,project_id,conversation_id,updated_at)
            VALUES ($1,$2,$3,$4,$5)
            ON CONFLICT (tenant_id,profile_id,project_id) DO UPDATE SET
                conversation_id=excluded.conversation_id,
                updated_at=excluded.updated_at;
            """;
        upsert.Parameters.Add(Text(record.TenantId));
        upsert.Parameters.Add(Text(record.ProfileId));
        upsert.Parameters.Add(Text(record.ProjectId));
        upsert.Parameters.Add(Text(record.ConversationId));
        upsert.Parameters.Add(Timestamp(record.UpdatedAt));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<ProfileActiveConversationRecord?> RecallAsync(
        string tenantId, string profileId, string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND profile_id=$2 AND project_id=$3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(profileId));
        query.Parameters.Add(Text(projectId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ProfileActiveConversationRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    public async Task ForgetAsync(
        string tenantId, string profileId, string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var delete = connection.CreateCommand();
        delete.CommandText =
            "DELETE FROM harness.profile_active_conversations " +
            "WHERE tenant_id=$1 AND profile_id=$2 AND project_id=$3;";
        delete.Parameters.Add(Text(tenantId));
        delete.Parameters.Add(Text(profileId));
        delete.Parameters.Add(Text(projectId));
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value };
}
