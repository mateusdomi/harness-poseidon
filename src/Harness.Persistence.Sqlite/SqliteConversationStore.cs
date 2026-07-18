using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteConversationStore(SqliteWriteDispatcher dispatcher) : IConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadConversationAsync(
                connection, null, tenantId, conversationId, token),
            cancellationToken);

    public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        string? projectId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<ConversationRecord>>(async (connection, token) =>
        {
            var values = new List<ConversationRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText =
                $"{ConversationSelect} WHERE tenant_id=$tenant AND deleted_at IS NULL " +
                "AND ($project IS NULL OR project_id=$project) " +
                "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(query, "$tenant", tenantId);
            AddNullable(query, "$project", projectId);
            AddNullable(query, "$after", afterId);
            Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                values.Add(ReadConversation(reader));
            }

            return values;
        }, cancellationToken);

    public Task<ConversationMutationResult> CreateConversationAsync(
        ConversationCreateCommand command,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => CreateConversationCoreAsync(connection, command, token),
            cancellationToken);

    public Task<ConversationMutationResult> DeleteConversationAsync(
        string tenantId,
        string conversationId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => DeleteConversationCoreAsync(
                connection, tenantId, conversationId, expectedVersion, occurredAt, token),
            cancellationToken);

    public Task<MessageRecord?> GetMessageAsync(
        string tenantId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadMessageAsync(connection, null, tenantId, messageId, token),
            cancellationToken);

    public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        string tenantId,
        string? conversationId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<MessageRecord>>(async (connection, token) =>
        {
            var values = new List<MessageRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText =
                $"{MessageSelect} WHERE tenant_id=$tenant " +
                "AND ($conversation IS NULL OR conversation_id=$conversation) " +
                "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(query, "$tenant", tenantId);
            AddNullable(query, "$conversation", conversationId);
            AddNullable(query, "$after", afterId);
            Add(query, "$limit", limit);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                values.Add(ReadMessage(reader));
            }

            return values;
        }, cancellationToken);

    public Task<MessageMutationResult> CreateMessageAsync(
        MessageCreateCommand command,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => CreateMessageCoreAsync(connection, command, token),
            cancellationToken);

    public Task<ChatTurnMutationResult> StartTurnAsync(
        ChatTurnCommand command,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => StartTurnCoreAsync(connection, command, token),
            cancellationToken);

    private static async Task<ConversationMutationResult> CreateConversationCoreAsync(
        SqliteConnection connection,
        ConversationCreateCommand command,
        CancellationToken cancellationToken)
    {
        var value = command.Conversation;
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProjectExistsAsync(
                connection, transaction, value.TenantId, value.ProjectId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.ProjectNotFound);
        }

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO conversations
                    (id,tenant_id,project_id,title,state,created_by_profile_id,created_at,
                     last_message_at,version)
                VALUES ($id,$tenant,$project,$title,$state,$profile,$created,NULL,$version);
                """;
            Add(insert, "$id", value.Id);
            Add(insert, "$tenant", value.TenantId);
            Add(insert, "$project", value.ProjectId);
            Add(insert, "$title", value.Title);
            Add(insert, "$state", value.State);
            Add(insert, "$profile", value.CreatedByProfileId);
            Add(insert, "$created", Store(value.CreatedAt));
            Add(insert, "$version", value.Version);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            var payload = JsonSerializer.Serialize(
                new { conversationId = value.Id, projectId = value.ProjectId, title = value.Title },
                JsonOptions);
            await AppendAuditAsync(
                connection, transaction, value.TenantId, "conversation.created", payload,
                command.OccurredAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.Applied, value);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.AlreadyExists);
        }
    }

    private static async Task<ConversationMutationResult> DeleteConversationCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string conversationId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE conversations
            SET state='archived',deleted_at=$at,version=version+1
            WHERE tenant_id=$tenant AND id=$id AND state='active'
              AND deleted_at IS NULL AND version=$version;
            """;
        Add(update, "$at", Store(occurredAt));
        Add(update, "$tenant", tenantId);
        Add(update, "$id", conversationId);
        Add(update, "$version", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            var payload = JsonSerializer.Serialize(new { conversationId, state = "archived" }, JsonOptions);
            await AppendAuditAsync(
                connection, transaction, tenantId, "conversation.archived", payload,
                occurredAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.Applied);
        }

        var current = await ReadConversationAsync(
            connection, transaction, tenantId, conversationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConversationMutationResult(
            current is null
                ? ConversationMutationStatus.NotFound
                : current.State != "active"
                    ? ConversationMutationStatus.Inactive
                    : ConversationMutationStatus.VersionConflict);
    }

    private static async Task<MessageMutationResult> CreateMessageCoreAsync(
        SqliteConnection connection,
        MessageCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var conversation = await ReadConversationAsync(
            connection, transaction, command.TenantId, command.Message.ConversationId,
            cancellationToken);
        if (conversation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MessageMutationResult(MessageMutationStatus.ConversationNotFound);
        }

        if (conversation.State != "active")
        {
            await transaction.CommitAsync(cancellationToken);
            return new MessageMutationResult(MessageMutationStatus.ConversationInactive);
        }

        try
        {
            await InsertMessageAsync(connection, transaction, command.Message, cancellationToken);
            await UpdateLastMessageAsync(
                connection, transaction, command.TenantId, command.Message.ConversationId,
                command.Message.CreatedAt, cancellationToken);
            var payload = MessagePayload(command.Message);
            await AppendAuditAsync(
                connection, transaction, command.TenantId, "message.appended", payload,
                command.OccurredAt, cancellationToken);
            await AppendOutboxAsync(
                connection, transaction, command.TenantId, "message.appended", payload,
                command.OccurredAt, command.OccurredAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MessageMutationResult(MessageMutationStatus.Applied, command.Message);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageMutationResult(MessageMutationStatus.AlreadyExists);
        }
    }

    private static async Task<ChatTurnMutationResult> StartTurnCoreAsync(
        SqliteConnection connection,
        ChatTurnCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var conversation = await ReadConversationAsync(
            connection, transaction, command.TenantId, command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ChatTurnMutationResult(
                MessageMutationStatus.ConversationNotFound, command.TurnId, command.ConversationId);
        }

        if (conversation.State != "active")
        {
            await transaction.CommitAsync(cancellationToken);
            return new ChatTurnMutationResult(
                MessageMutationStatus.ConversationInactive, command.TurnId, command.ConversationId);
        }

        try
        {
            await InsertMessageAsync(connection, transaction, command.UserMessage, cancellationToken);
            await InsertMessageAsync(connection, transaction, command.ChiefMessage, cancellationToken);
            await using (var turn = connection.CreateCommand())
            {
                turn.Transaction = transaction;
                turn.CommandText =
                    """
                    INSERT INTO chat_turns
                        (id,tenant_id,project_id,conversation_id,user_message_id,response_message_id,
                         state,finish_reason,created_at,completed_at)
                    VALUES ($id,$tenant,$project,$conversation,$user,$response,
                            'completed','stop',$created,$completed);
                    """;
                Add(turn, "$id", command.TurnId);
                Add(turn, "$tenant", command.TenantId);
                Add(turn, "$project", command.UserMessage.ProjectId);
                Add(turn, "$conversation", command.ConversationId);
                Add(turn, "$user", command.UserMessage.Id);
                Add(turn, "$response", command.ChiefMessage.Id);
                Add(turn, "$created", Store(command.OccurredAt));
                Add(turn, "$completed", Store(command.ChiefMessage.CreatedAt.AddMilliseconds(1)));
                await turn.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpdateLastMessageAsync(
                connection, transaction, command.TenantId, command.ConversationId,
                command.ChiefMessage.CreatedAt, cancellationToken);

            var completedPayload = JsonSerializer.Serialize(
                new
                {
                    conversationId = command.ConversationId,
                    turnId = command.TurnId,
                    messageId = command.ChiefMessage.Id,
                    finishReason = "stop",
                },
                JsonOptions);
            await AppendAuditAsync(
                connection, transaction, command.TenantId, "message.appended",
                MessagePayload(command.UserMessage), command.UserMessage.CreatedAt,
                cancellationToken);
            await AppendAuditAsync(
                connection, transaction, command.TenantId, "message.appended",
                MessagePayload(command.ChiefMessage), command.ChiefMessage.CreatedAt,
                cancellationToken);
            await AppendAuditAsync(
                connection, transaction, command.TenantId, "chat.turnCompleted",
                completedPayload, command.ChiefMessage.CreatedAt.AddMilliseconds(1),
                cancellationToken);

            var eventIndex = 0;
            await QueueTurnEventAsync(
                "message.appended", MessagePayload(command.UserMessage), eventIndex++);
            await QueueTurnEventAsync(
                "chat.turnStarted",
                JsonSerializer.Serialize(
                    new
                    {
                        conversationId = command.ConversationId,
                        turnId = command.TurnId,
                        agentId = command.ChiefMessage.AuthorAgentId,
                    },
                    JsonOptions),
                eventIndex++);
            for (var index = 0; index < command.Chunks.Count; index++)
            {
                await QueueTurnEventAsync(
                    "chat.turnChunk",
                    JsonSerializer.Serialize(
                        new
                        {
                            conversationId = command.ConversationId,
                            turnId = command.TurnId,
                            index,
                            text = command.Chunks[index],
                        },
                        JsonOptions),
                    eventIndex++);
            }

            await QueueTurnEventAsync(
                "message.appended", MessagePayload(command.ChiefMessage), eventIndex++);
            await QueueTurnEventAsync("chat.turnCompleted", completedPayload, eventIndex);
            await transaction.CommitAsync(cancellationToken);
            return new ChatTurnMutationResult(
                MessageMutationStatus.Applied, command.TurnId, command.ConversationId);

            Task QueueTurnEventAsync(string eventType, string payload, int index)
            {
                var occurredAt = command.OccurredAt.AddMilliseconds(index);
                return AppendOutboxAsync(
                    connection, transaction, command.TenantId, eventType, payload,
                    occurredAt, command.OccurredAt, cancellationToken);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ChatTurnMutationResult(
                MessageMutationStatus.AlreadyExists, command.TurnId, command.ConversationId);
        }
    }

    private static async Task InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageRecord message,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO conversation_messages
                (id,tenant_id,project_id,conversation_id,author_role,author_profile_id,
                 author_agent_id,content,token_count,created_at)
            VALUES ($id,$tenant,$project,$conversation,$role,$profile,$agent,$content,$tokens,$created);
            """;
        Add(insert, "$id", message.Id);
        Add(insert, "$tenant", message.TenantId);
        Add(insert, "$project", message.ProjectId);
        Add(insert, "$conversation", message.ConversationId);
        Add(insert, "$role", message.AuthorRole);
        AddNullable(insert, "$profile", message.AuthorProfileId);
        AddNullable(insert, "$agent", message.AuthorAgentId);
        Add(insert, "$content", message.Content);
        AddNullable(insert, "$tokens", message.TokenCount);
        Add(insert, "$created", Store(message.CreatedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateLastMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string conversationId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE conversations
            SET last_message_at=$at,version=version+1
            WHERE tenant_id=$tenant AND id=$conversation AND state='active' AND deleted_at IS NULL;
            """;
        Add(update, "$at", Store(at));
        Add(update, "$tenant", tenantId);
        Add(update, "$conversation", conversationId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Active conversation disappeared during message append.");
        }
    }

    private static async Task AppendAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
        Add(tail, "$tenant", tenantId);
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        var hasTail = await reader.ReadAsync(cancellationToken);
        var sequence = hasTail ? reader.GetInt64(0) + 1 : 1;
        var previousHash = hasTail ? reader.GetString(1) : AuditLedgerHash.Genesis;
        await reader.DisposeAsync();
        var hash = AuditLedgerHash.Compute(
            previousHash, tenantId, sequence, eventType, payload, occurredAt);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);
            """;
        Add(insert, "$id", UlidValue.New(occurredAt).ToString());
        Add(insert, "$tenant", tenantId);
        Add(insert, "$sequence", sequence);
        Add(insert, "$previous", previousHash);
        Add(insert, "$hash", hash);
        Add(insert, "$type", eventType);
        Add(insert, "$payload", payload);
        Add(insert, "$at", Store(occurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO outbox_messages
                (id,tenant_id,event_type,payload_json,occurred_at,available_at)
            VALUES ($id,$tenant,$type,$payload,$occurred,$available);
            """;
        Add(insert, "$id", UlidValue.New(occurredAt).ToString());
        Add(insert, "$tenant", tenantId);
        Add(insert, "$type", eventType);
        Add(insert, "$payload", payload);
        Add(insert, "$occurred", Store(occurredAt));
        Add(insert, "$available", Store(availableAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ProjectExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL);";
        Add(query, "$tenant", tenantId);
        Add(query, "$project", projectId);
        return Convert.ToInt64(
            await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<ConversationRecord?> ReadConversationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{ConversationSelect} WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL;";
        Add(query, "$tenant", tenantId);
        Add(query, "$id", conversationId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    private static ConversationRecord ReadConversation(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
            reader.GetInt64(8));

    private static async Task<MessageRecord?> ReadMessageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{MessageSelect} WHERE tenant_id=$tenant AND id=$id;";
        Add(query, "$tenant", tenantId);
        Add(query, "$id", messageId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMessage(reader) : null;
    }

    private static MessageRecord ReadMessage(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Parse(reader.GetString(9)));

    private static string MessagePayload(MessageRecord message) =>
        JsonSerializer.Serialize(
            new
            {
                message = new
                {
                    message.Id,
                    message.ConversationId,
                    message.AuthorRole,
                    message.AuthorProfileId,
                    message.AuthorAgentId,
                    message.Content,
                    message.TokenCount,
                    message.CreatedAt,
                },
            },
            JsonOptions);

    private const string ConversationSelect =
        "SELECT tenant_id,id,project_id,title,state,created_by_profile_id,created_at,last_message_at,version FROM conversations";
    private const string MessageSelect =
        "SELECT tenant_id,project_id,id,conversation_id,author_role,author_profile_id,author_agent_id,content,token_count,created_at FROM conversation_messages";

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
