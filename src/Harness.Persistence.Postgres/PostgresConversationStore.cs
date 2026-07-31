using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresConversationStore(NpgsqlDataSource dataSource) : IConversationStore, IChiefTurnStore
{
    private const string ConversationSelect =
        "SELECT tenant_id,id,project_id,title,state,created_by_profile_id,created_at,last_message_at,version FROM harness.conversations";
    private const string MessageSelect =
        "SELECT tenant_id,project_id,id,conversation_id,author_role,author_profile_id,author_agent_id,content,token_count,created_at FROM harness.conversation_messages";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadConversationAsync(connection, null, tenantId, conversationId, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        string? projectId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<ConversationRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{ConversationSelect} WHERE tenant_id=$1 AND deleted_at IS NULL " +
            "AND ($2::text IS NULL OR project_id=$2) " +
            "AND ($3::text IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(projectId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadConversation(reader));
        }

        return values;
    }

    public Task<ConversationMutationResult> CreateConversationAsync(
        ConversationCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateConversationCoreAsync(command, cancellationToken);
    }

    public async Task<ConversationMutationResult> DeleteConversationAsync(
        string tenantId,
        string conversationId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE harness.conversations
            SET state='archived',deleted_at=$1,version=version+1
            WHERE tenant_id=$2 AND id=$3 AND state='active'
              AND deleted_at IS NULL AND version=$4;
            """;
        update.Parameters.Add(Timestamp(occurredAt));
        update.Parameters.Add(Text(tenantId));
        update.Parameters.Add(Text(conversationId));
        update.Parameters.Add(Bigint(expectedVersion));
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

    public async Task<ConversationMutationResult> RenameConversationAsync(
        string tenantId,
        string conversationId,
        long expectedVersion,
        string title,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE harness.conversations
            SET title=$1,version=version+1
            WHERE tenant_id=$2 AND id=$3 AND deleted_at IS NULL AND version=$4;
            """;
        update.Parameters.Add(Text(title));
        update.Parameters.Add(Text(tenantId));
        update.Parameters.Add(Text(conversationId));
        update.Parameters.Add(Bigint(expectedVersion));
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            var renamed = await ReadConversationAsync(
                connection, transaction, tenantId, conversationId, cancellationToken);
            var payload = JsonSerializer.Serialize(new { conversationId, title }, JsonOptions);
            await AppendAuditAsync(
                connection, transaction, tenantId, "conversation.renamed", payload,
                occurredAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.Applied, renamed);
        }

        var current = await ReadConversationAsync(
            connection, transaction, tenantId, conversationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConversationMutationResult(
            current is null
                ? ConversationMutationStatus.NotFound
                : ConversationMutationStatus.VersionConflict);
    }

    public async Task<MessageRecord?> GetMessageAsync(
        string tenantId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadMessageAsync(connection, null, tenantId, messageId, cancellationToken);
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        string tenantId,
        string? conversationId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<MessageRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{MessageSelect} WHERE tenant_id=$1 " +
            "AND ($2::text IS NULL OR conversation_id=$2) " +
            "AND ($3::text IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(conversationId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadMessage(reader));
        }

        return values;
    }

    public async Task<IReadOnlyList<MessageRecord>> ListRecentMessagesAsync(
        string tenantId,
        string conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var values = new List<MessageRecord>();
        // DESC no armazenamento (o índice escolhe as últimas sem varrer a conversa inteira) e
        // inversão antes de devolver, porque quem monta o contexto precisa de ordem cronológica.
        await using var query = _dataSource.CreateCommand(
            $"{MessageSelect} WHERE tenant_id=$1 AND conversation_id=$2 ORDER BY id DESC LIMIT $3;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(conversationId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadMessage(reader));
        }

        values.Reverse();
        return values;
    }

    public async Task<MessageRecord?> GetFirstMessageAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        await using var query = _dataSource.CreateCommand(
            $"{MessageSelect} WHERE tenant_id=$1 AND conversation_id=$2 ORDER BY id LIMIT 1;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(conversationId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMessage(reader) : null;
    }

    public Task<MessageMutationResult> CreateMessageAsync(
        MessageCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateMessageCoreAsync(command, cancellationToken);
    }

    public Task<ChatTurnMutationResult> StartTurnAsync(
        ChatTurnCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return StartTurnCoreAsync(command, cancellationToken);
    }

    private async Task<ConversationMutationResult> CreateConversationCoreAsync(
        ConversationCreateCommand command,
        CancellationToken cancellationToken)
    {
        var value = command.Conversation;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProjectExistsAsync(
                connection, transaction, value.TenantId, value.ProjectId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.ProjectNotFound);
        }

        try
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.conversations
                    (id,tenant_id,project_id,title,state,created_by_profile_id,created_at,
                     last_message_at,version)
                VALUES ($1,$2,$3,$4,$5,$6,$7,NULL,$8);
                """,
                cancellationToken,
                Text(value.Id),
                Text(value.TenantId),
                Text(value.ProjectId),
                Text(value.Title),
                Text(value.State),
                Text(value.CreatedByProfileId),
                Timestamp(value.CreatedAt),
                Bigint(value.Version));

            var payload = JsonSerializer.Serialize(
                new { conversationId = value.Id, projectId = value.ProjectId, title = value.Title },
                JsonOptions);
            await AppendAuditAsync(
                connection, transaction, value.TenantId, "conversation.created", payload,
                command.OccurredAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.Applied, value);
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationMutationResult(ConversationMutationStatus.AlreadyExists);
        }
    }

    private async Task<MessageMutationResult> CreateMessageCoreAsync(
        MessageCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageMutationResult(MessageMutationStatus.AlreadyExists);
        }
    }

    private async Task<ChatTurnMutationResult> StartTurnCoreAsync(
        ChatTurnCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.chat_turns
                    (id,tenant_id,project_id,conversation_id,user_message_id,response_message_id,
                     state,finish_reason,created_at,completed_at)
                VALUES ($1,$2,$3,$4,$5,$6,'completed','stop',$7,$8);
                """,
                cancellationToken,
                Text(command.TurnId),
                Text(command.TenantId),
                Text(command.UserMessage.ProjectId),
                Text(command.ConversationId),
                Text(command.UserMessage.Id),
                Text(command.ChiefMessage.Id),
                Timestamp(command.OccurredAt),
                Timestamp(command.ChiefMessage.CreatedAt.AddMilliseconds(1)));

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
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ChatTurnMutationResult(
                MessageMutationStatus.AlreadyExists, command.TurnId, command.ConversationId);
        }
    }

    private static async Task InsertMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MessageRecord message,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.conversation_messages
                (id,tenant_id,project_id,conversation_id,author_role,author_profile_id,
                 author_agent_id,content,token_count,created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10);
            """,
            cancellationToken,
            Text(message.Id),
            Text(message.TenantId),
            Text(message.ProjectId),
            Text(message.ConversationId),
            Text(message.AuthorRole),
            NullableText(message.AuthorProfileId),
            NullableText(message.AuthorAgentId),
            Text(message.Content),
            NullableInteger(message.TokenCount),
            Timestamp(message.CreatedAt));
    }

    private static async Task UpdateLastMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string conversationId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE harness.conversations
            SET last_message_at=$1,version=version+1
            WHERE tenant_id=$2 AND id=$3 AND state='active' AND deleted_at IS NULL;
            """;
        update.Parameters.Add(Timestamp(at));
        update.Parameters.Add(Text(tenantId));
        update.Parameters.Add(Text(conversationId));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Active conversation disappeared during message append.");
        }
    }

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenantId}"));
        var (sequence, previousHash) = await ReadLedgerTailAsync(
            connection, transaction, tenantId, cancellationToken);
        var hash = AuditLedgerHash.Compute(
            previousHash, tenantId, sequence, eventType, payload, occurredAt);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """,
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Bigint(sequence),
            Text(previousHash),
            Text(hash),
            Text(eventType),
            Json(payload),
            Timestamp(occurredAt));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.outbox_messages
                (id,tenant_id,event_type,payload_json,occurred_at,available_at)
            VALUES ($1,$2,$3,$4,$5,$6);
            """,
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Text(eventType),
            Json(payload),
            Timestamp(occurredAt),
            Timestamp(availableAt));

    private static async Task<bool> ProjectExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL);";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return project state."));
    }

    private static async Task<ConversationRecord?> ReadConversationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{ConversationSelect} WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(conversationId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    private static ConversationRecord ReadConversation(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5).TrimEnd(),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetInt64(8));

    private static async Task<MessageRecord?> ReadMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"{MessageSelect} WHERE tenant_id=$1 AND id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(messageId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMessage(reader) : null;
    }

    private static MessageRecord ReadMessage(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.GetString(3).TrimEnd(),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5).TrimEnd(),
            reader.IsDBNull(6) ? null : reader.GetString(6).TrimEnd(),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.GetFieldValue<DateTimeOffset>(9));

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

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsConstraintViolation(PostgresException exception) =>
        exception.SqlState.StartsWith("23", StringComparison.Ordinal);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter NullableInteger(int? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Integer,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.TimestampTz,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
