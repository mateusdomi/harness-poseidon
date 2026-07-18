using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.DurableExecution;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteConversationStore
{
    public Task<ChiefTurnRecord> EnqueueAsync(ChiefTurnEnqueueCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => EnqueueCoreAsync(connection, command, token), cancellationToken);

    public Task<ChiefTurnLease> AcquireAsync(ChiefTurnAcquireCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => AcquireCoreAsync(connection, command, token), cancellationToken);

    public Task CompleteAsync(ChiefTurnCompleteCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
        {
            await CompleteCoreAsync(connection, command, token);
            return null;
        }, cancellationToken);

    public Task FailAsync(ChiefTurnFailCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
        {
            await FailCoreAsync(connection, command, token);
            return null;
        }, cancellationToken);

    public Task<ChiefTurnRecord?> GetAsync(string tenantId, string turnId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ReadChiefTurnAsync(connection, null, tenantId, turnId, token), cancellationToken);

    private static async Task<ChiefTurnRecord> EnqueueCoreAsync(
        SqliteConnection connection, ChiefTurnEnqueueCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var existing = await ReadChiefTurnAsync(connection, tx, command.TenantId, command.TurnId, token);
        if (existing is not null)
        {
            await tx.CommitAsync(token);
            return existing;
        }

        var conversation = await ReadConversationAsync(connection, tx, command.TenantId, command.ConversationId, token);
        if (conversation is null || conversation.State != "active" || conversation.ProjectId != command.ProjectId)
            throw new ChiefTurnConflictException("The target conversation is not active for this project.");

        var requestHash = DurableCommandHash.Compute(command);
        await InsertMessageAsync(connection, tx, command.UserMessage, token);
        await UpdateLastMessageAsync(connection, tx, command.TenantId, command.ConversationId, command.UserMessage.CreatedAt, token);
        await ExecuteChiefAsync(connection, tx,
            "INSERT INTO inbox_messages (tenant_id,idempotency_key,message_hash,response_json,processed_at) VALUES ($tenant,$key,$hash,$response,$at);",
            token, ("$tenant", command.TenantId), ("$key", command.IdempotencyKey), ("$hash", requestHash),
            ("$response", JsonSerializer.Serialize(new { turnId = command.TurnId }, JsonOptions)), ("$at", Store(command.OccurredAt)));
        await ExecuteChiefAsync(connection, tx,
            "INSERT INTO chief_states (tenant_id,project_id,chief_agent_id,state,updated_at) VALUES ($tenant,$project,$agent,'idle',$at) " +
            "ON CONFLICT(tenant_id,project_id) DO NOTHING;",
            token, ("$tenant", command.TenantId), ("$project", command.ProjectId),
            ("$agent", command.ChiefAgentId), ("$at", Store(command.OccurredAt)));
        await ExecuteChiefAsync(connection, tx,
            "INSERT INTO chief_turn_mailbox (id,tenant_id,project_id,conversation_id,user_message_id,state,created_at) " +
            "VALUES ($id,$tenant,$project,$conversation,$message,'pending',$at);",
            token, ("$id", command.TurnId), ("$tenant", command.TenantId), ("$project", command.ProjectId),
            ("$conversation", command.ConversationId), ("$message", command.UserMessage.Id), ("$at", Store(command.OccurredAt)));
        var payload = MessagePayload(command.UserMessage);
        await AppendAuditAsync(connection, tx, command.TenantId, "message.appended", payload, command.OccurredAt, token);
        await AppendOutboxAsync(connection, tx, command.TenantId, "message.appended", payload, command.OccurredAt, command.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadChiefTurnAsync(connection, null, command.TenantId, command.TurnId, token))!;
    }

    private static async Task<ChiefTurnLease> AcquireCoreAsync(
        SqliteConnection connection, ChiefTurnAcquireCommand command, CancellationToken token)
    {
        if (command.LeaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(command));
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var turn = await ReadChiefTurnAsync(connection, tx, command.TenantId, command.TurnId, token)
            ?? throw new ChiefTurnConflictException("The Chief turn does not exist.");
        if (turn.State is "completed" or "failed") throw new ChiefTurnConflictException("The Chief turn is terminal.");
        await using var stateQuery = connection.CreateCommand();
        stateQuery.Transaction = tx;
        stateQuery.CommandText = "SELECT chief_agent_id,lease_owner_id,lease_fencing_token,lease_expires_at,session_id FROM chief_states WHERE tenant_id=$tenant AND project_id=$project;";
        Add(stateQuery, "$tenant", command.TenantId); Add(stateQuery, "$project", turn.ProjectId);
        await using var reader = await stateQuery.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new ChiefTurnConflictException("Chief state does not exist.");
        var agent = reader.GetString(0); var owner = reader.IsDBNull(1) ? null : reader.GetString(1);
        var fencing = reader.GetInt64(2); DateTimeOffset? expires = reader.IsDBNull(3) ? null : Parse(reader.GetString(3));
        var session = reader.IsDBNull(4) ? null : reader.GetString(4);
        await reader.DisposeAsync();
        if (owner is not null && expires > command.Now && owner != command.OwnerId)
            throw new ChiefTurnConflictException("Another Chief turn owns the project lease.");
        fencing++;
        var leaseExpires = command.Now.Add(command.LeaseDuration);
        await ExecuteChiefAsync(connection, tx,
            "UPDATE chief_states SET state='working',lease_owner_id=$owner,lease_fencing_token=$fencing,lease_expires_at=$expires,version=version+1,updated_at=$now WHERE tenant_id=$tenant AND project_id=$project; " +
            "UPDATE chief_turn_mailbox SET state='processing',attempt_count=attempt_count+1,active_fencing_token=$fencing,started_at=$now,last_error_code=NULL WHERE tenant_id=$tenant AND id=$turn; " +
            "UPDATE agents SET state='working',last_heartbeat_at=$now WHERE tenant_id=$tenant AND id=$agent;",
            token, ("$owner", command.OwnerId), ("$fencing", fencing), ("$expires", Store(leaseExpires)),
            ("$now", Store(command.Now)), ("$tenant", command.TenantId), ("$project", turn.ProjectId),
            ("$turn", turn.TurnId), ("$agent", agent));
        await tx.CommitAsync(token);
        return new ChiefTurnLease(turn with { State = "processing", AttemptCount = turn.AttemptCount + 1 }, command.OwnerId, fencing, leaseExpires, agent, session);
    }

    private static async Task CompleteCoreAsync(
        SqliteConnection connection, ChiefTurnCompleteCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await EnsureLeaseAsync(connection, tx, command.Lease, command.OccurredAt, token);
        await InsertMessageAsync(connection, tx, command.ChiefMessage, token);
        await ExecuteChiefAsync(connection, tx,
            "INSERT INTO chat_turns (id,tenant_id,project_id,conversation_id,user_message_id,response_message_id,state,finish_reason,created_at,completed_at) " +
            "VALUES ($id,$tenant,$project,$conversation,$user,$response,'completed','stop',$created,$completed);",
            token, ("$id", command.Lease.Turn.TurnId), ("$tenant", command.Lease.Turn.TenantId),
            ("$project", command.Lease.Turn.ProjectId), ("$conversation", command.Lease.Turn.ConversationId),
            ("$user", command.Lease.Turn.UserMessageId), ("$response", command.ChiefMessage.Id),
            ("$created", Store(command.Lease.Turn.CreatedAt)), ("$completed", Store(command.OccurredAt)));
        await UpdateLastMessageAsync(connection, tx, command.Lease.Turn.TenantId, command.Lease.Turn.ConversationId, command.ChiefMessage.CreatedAt, token);
        await ExecuteChiefAsync(connection, tx,
            "UPDATE chief_turn_mailbox SET state='completed',active_fencing_token=NULL,session_id=$session,response_message_id=$response,completed_at=$at WHERE tenant_id=$tenant AND id=$turn; " +
            "UPDATE chief_states SET state='idle',lease_owner_id=NULL,lease_expires_at=NULL,session_id=$session,last_digest_json=$digest,version=version+1,updated_at=$at WHERE tenant_id=$tenant AND project_id=$project AND lease_fencing_token=$fencing; " +
            "UPDATE agents SET state='idle',tasks_completed=tasks_completed+1,last_heartbeat_at=$at WHERE tenant_id=$tenant AND id=$agent;",
            token, ("$session", command.SessionId), ("$response", command.ChiefMessage.Id), ("$at", Store(command.OccurredAt)),
            ("$tenant", command.Lease.Turn.TenantId), ("$turn", command.Lease.Turn.TurnId),
            ("$digest", command.StatusDigestJson), ("$project", command.Lease.Turn.ProjectId),
            ("$fencing", command.Lease.FencingToken), ("$agent", command.Lease.ChiefAgentId));
        await QueueChiefCompletionEventsAsync(connection, tx, command, token);
        await tx.CommitAsync(token);
    }

    private static async Task FailCoreAsync(SqliteConnection connection, ChiefTurnFailCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await EnsureLeaseAsync(connection, tx, command.Lease, command.OccurredAt, token);
        var next = command.Retryable && command.Lease.Turn.AttemptCount < 3 ? "pending" : "failed";
        await ExecuteChiefAsync(connection, tx,
            "UPDATE chief_turn_mailbox SET state=$state,active_fencing_token=NULL,last_error_code=$error,completed_at=CASE WHEN $state='failed' THEN $at ELSE NULL END WHERE tenant_id=$tenant AND id=$turn; " +
            "UPDATE chief_states SET state=CASE WHEN $state='failed' THEN 'error' ELSE 'idle' END,lease_owner_id=NULL,lease_expires_at=NULL,version=version+1,updated_at=$at WHERE tenant_id=$tenant AND project_id=$project AND lease_fencing_token=$fencing; " +
            "UPDATE agents SET state=CASE WHEN $state='failed' THEN 'error' ELSE 'idle' END,last_heartbeat_at=$at WHERE tenant_id=$tenant AND id=$agent;",
            token, ("$state", next), ("$error", command.ErrorCode), ("$at", Store(command.OccurredAt)),
            ("$tenant", command.Lease.Turn.TenantId), ("$turn", command.Lease.Turn.TurnId),
            ("$project", command.Lease.Turn.ProjectId), ("$fencing", command.Lease.FencingToken),
            ("$agent", command.Lease.ChiefAgentId));
        await tx.CommitAsync(token);
    }

    private static async Task EnsureLeaseAsync(SqliteConnection connection, SqliteTransaction tx, ChiefTurnLease lease, DateTimeOffset now, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM chief_states s JOIN chief_turn_mailbox m ON m.tenant_id=s.tenant_id AND m.project_id=s.project_id WHERE s.tenant_id=$tenant AND s.project_id=$project AND s.lease_owner_id=$owner AND s.lease_fencing_token=$fencing AND s.lease_expires_at>$now AND m.id=$turn AND m.state='processing' AND m.active_fencing_token=$fencing);";
        Add(query, "$tenant", lease.Turn.TenantId); Add(query, "$project", lease.Turn.ProjectId);
        Add(query, "$owner", lease.OwnerId); Add(query, "$fencing", lease.FencingToken);
        Add(query, "$now", Store(now)); Add(query, "$turn", lease.Turn.TurnId);
        if (Convert.ToInt32(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new ChiefTurnConflictException("Chief turn lease is stale or expired.");
    }

    private static async Task QueueChiefCompletionEventsAsync(SqliteConnection connection, SqliteTransaction tx, ChiefTurnCompleteCommand command, CancellationToken token)
    {
        var tenant = command.Lease.Turn.TenantId; var conversation = command.Lease.Turn.ConversationId;
        var started = JsonSerializer.Serialize(new { conversationId = conversation, turnId = command.Lease.Turn.TurnId, agentId = command.Lease.ChiefAgentId }, JsonOptions);
        await AppendOutboxAsync(connection, tx, tenant, "chat.turnStarted", started, command.OccurredAt, command.OccurredAt, token);
        for (var index = 0; index < command.Chunks.Count; index++)
        {
            var payload = JsonSerializer.Serialize(new { conversationId = conversation, turnId = command.Lease.Turn.TurnId, index, text = command.Chunks[index] }, JsonOptions);
            await AppendOutboxAsync(connection, tx, tenant, "chat.turnChunk", payload, command.OccurredAt.AddTicks(index + 1), command.OccurredAt, token);
        }
        var messagePayload = MessagePayload(command.ChiefMessage);
        await AppendAuditAsync(connection, tx, tenant, "message.appended", messagePayload, command.ChiefMessage.CreatedAt, token);
        await AppendOutboxAsync(connection, tx, tenant, "message.appended", messagePayload, command.OccurredAt.AddTicks(command.Chunks.Count + 1), command.OccurredAt, token);
        var completed = JsonSerializer.Serialize(new { conversationId = conversation, turnId = command.Lease.Turn.TurnId, messageId = command.ChiefMessage.Id, finishReason = "stop" }, JsonOptions);
        await AppendAuditAsync(connection, tx, tenant, "chat.turnCompleted", completed, command.OccurredAt, token);
        await AppendOutboxAsync(connection, tx, tenant, "chat.turnCompleted", completed, command.OccurredAt.AddTicks(command.Chunks.Count + 2), command.OccurredAt, token);
    }

    private static async Task<ChiefTurnRecord?> ReadChiefTurnAsync(SqliteConnection connection, SqliteTransaction? tx, string tenant, string turn, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT tenant_id,project_id,conversation_id,id,user_message_id,state,attempt_count,session_id,response_message_id,last_error_code,created_at,completed_at FROM chief_turn_mailbox WHERE tenant_id=$tenant AND id=$turn;";
        Add(query, "$tenant", tenant); Add(query, "$turn", turn); await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new ChiefTurnRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : Parse(reader.GetString(11))) : null;
    }

    private static async Task ExecuteChiefAsync(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken token, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var value in values) Add(command, value.Name, value.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
