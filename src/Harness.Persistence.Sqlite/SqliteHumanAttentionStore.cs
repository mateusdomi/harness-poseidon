using System.Globalization;
using Harness.Persistence.Abstractions.Attention;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteHumanAttentionStore(SqliteWriteDispatcher dispatcher) : IHumanAttentionStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Columns =
        "tenant_id,id,project_id,conversation_id,card_id,graph_node_id,question,reason," +
        "severity,blocking_scope,status,source_agent,correlation_id,reminder_count," +
        "channel_status,answer,created_at,acknowledged_at,answered_at,next_action_at";

    public Task<HumanAttentionRecord> CreateAsync(
        HumanAttentionCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO human_attention_requests
                        (tenant_id,id,project_id,conversation_id,card_id,graph_node_id,question,
                         reason,classification,severity,blocking_scope,status,source_agent,
                         correlation_id,reminder_count,channel_status,created_at,next_action_at)
                    VALUES ($tenant,$id,$project,$conversation,$card,$graphNode,$question,$reason,
                            'ask',$severity,$scope,'open',$source,$correlation,0,'none',$at,$at)
                    ON CONFLICT(tenant_id, correlation_id) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$tenant", command.TenantId);
                insert.Parameters.AddWithValue("$id", command.Id);
                insert.Parameters.AddWithValue("$project", command.ProjectId);
                insert.Parameters.AddWithValue("$conversation", (object?)command.ConversationId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$card", (object?)command.CardId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$graphNode", (object?)command.GraphNodeId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$question", command.Question);
                insert.Parameters.AddWithValue("$reason", command.Reason);
                insert.Parameters.AddWithValue("$severity", command.Severity);
                insert.Parameters.AddWithValue("$scope", command.BlockingScope);
                insert.Parameters.AddWithValue("$source", command.SourceAgent);
                insert.Parameters.AddWithValue("$correlation", command.CorrelationId);
                insert.Parameters.AddWithValue("$at", Store(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
            }

            var existing = await ReadByCorrelationAsync(
                connection, command.TenantId, command.CorrelationId, token);
            return existing ?? throw new InvalidOperationException(
                "O pedido de atenção não pôde ser lido após a criação.");
        }, cancellationToken);
    }

    public Task<IReadOnlyList<HumanAttentionRecord>> ListOpenAsync(
        string tenantId, string? projectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return _dispatcher.ExecuteAsync<IReadOnlyList<HumanAttentionRecord>>(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"SELECT {Columns} FROM human_attention_requests " +
                    "WHERE tenant_id=$tenant AND status IN ('open','notified','acknowledged') " +
                    (projectId is null ? string.Empty : "AND project_id=$project ") +
                    "ORDER BY created_at;";
                query.Parameters.AddWithValue("$tenant", tenantId);
                if (projectId is not null)
                {
                    query.Parameters.AddWithValue("$project", projectId);
                }

                var records = new List<HumanAttentionRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    records.Add(Read(reader));
                }

                return records;
            }, cancellationToken);
    }

    public Task<HumanAttentionRecord?> GetByCorrelationAsync(
        string tenantId, string correlationId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadByCorrelationAsync(connection, tenantId, correlationId, token),
            cancellationToken);

    public Task RecordEscalationAsync(
        string tenantId, string id, string status, int reminderCount, DateTimeOffset? nextActionAt,
        string channelStatus, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE human_attention_requests
                SET status=$status, reminder_count=$reminders, next_action_at=$next,
                    channel_status=$channel
                WHERE tenant_id=$tenant AND id=$id
                  AND status IN ('open','notified','acknowledged');
                """;
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$reminders", reminderCount);
            update.Parameters.AddWithValue(
                "$next", nextActionAt is { } next ? Store(next) : DBNull.Value);
            update.Parameters.AddWithValue("$channel", channelStatus);
            await update.ExecuteNonQueryAsync(token);
            return 0;
        }, cancellationToken);

    public Task AcknowledgeAsync(
        string tenantId, string id, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE human_attention_requests
                SET status='acknowledged', acknowledged_at=$at
                WHERE tenant_id=$tenant AND id=$id AND status IN ('open','notified');
                """;
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$at", Store(occurredAt));
            await update.ExecuteNonQueryAsync(token);
            return 0;
        }, cancellationToken);

    public Task AnswerAsync(
        string tenantId, string id, string answer, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE human_attention_requests
                SET status='answered', answer=$answer, answered_at=$at, next_action_at=NULL
                WHERE tenant_id=$tenant AND id=$id
                  AND status IN ('open','notified','acknowledged');
                """;
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$answer", answer);
            update.Parameters.AddWithValue("$at", Store(occurredAt));
            await update.ExecuteNonQueryAsync(token);
            return 0;
        }, cancellationToken);

    public Task SupersedeAsync(
        string tenantId, string id, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE human_attention_requests
                SET status='superseded', next_action_at=NULL
                WHERE tenant_id=$tenant AND id=$id
                  AND status IN ('open','notified','acknowledged');
                """;
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$at", Store(occurredAt));
            await update.ExecuteNonQueryAsync(token);
            return 0;
        }, cancellationToken);

    private static async Task<HumanAttentionRecord?> ReadByCorrelationAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string tenantId,
        string correlationId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"SELECT {Columns} FROM human_attention_requests " +
            "WHERE tenant_id=$tenant AND correlation_id=$correlation;";
        query.Parameters.AddWithValue("$tenant", tenantId);
        query.Parameters.AddWithValue("$correlation", correlationId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    private static HumanAttentionRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13),
        reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        Parse(reader.GetString(16)),
        reader.IsDBNull(17) ? null : Parse(reader.GetString(17)),
        reader.IsDBNull(18) ? null : Parse(reader.GetString(18)),
        reader.IsDBNull(19) ? null : Parse(reader.GetString(19)));

    private static string Store(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
