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

    public Task<ChiefTurnLease?> AcquireNextAsync(
        string ownerId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText =
                """
                SELECT m.tenant_id,m.id
                FROM chief_turn_mailbox m
                JOIN chief_states s ON s.tenant_id=m.tenant_id AND s.project_id=m.project_id
                WHERE m.state='pending'
                   OR (m.state='processing' AND s.lease_expires_at<=$now)
                ORDER BY m.created_at,m.id LIMIT 1;
                """;
            Add(query, "$now", Store(now));
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            var tenant = reader.GetString(0); var turn = reader.GetString(1);
            await reader.DisposeAsync();
            return await AcquireCoreAsync(
                connection,
                new ChiefTurnAcquireCommand(tenant, turn, ownerId, now, leaseDuration),
                token);
        }, cancellationToken);

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

    public Task<ChiefTurnBlockRecord> BlockAsync(
        ChiefTurnBlockCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => BlockCoreAsync(connection, command, token), cancellationToken);

    private static async Task<ChiefTurnBlockRecord> BlockCoreAsync(
        SqliteConnection connection, ChiefTurnBlockCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        // Idempotência por mensagem humana: o retry do mesmo envio devolve o bloqueio já
        // registrado, sem inserir mensagem, bloqueio ou evento de novo.
        var existing = await ReadChiefTurnBlockAsync(
            connection, tx, command.TenantId, command.UserMessage.Id, token);
        if (existing is not null)
        {
            await tx.CommitAsync(token);
            return existing;
        }

        var conversation = await ReadConversationAsync(
            connection, tx, command.TenantId, command.ConversationId, token);
        if (conversation is null || conversation.State != "active" ||
            conversation.ProjectId != command.ProjectId)
        {
            throw new ChiefTurnConflictException("The target conversation is not active for this project.");
        }

        var blockers = JsonSerializer.Serialize(command.Blockers, JsonOptions);
        var nextActions = JsonSerializer.Serialize(command.NextActions, JsonOptions);
        await InsertMessageAsync(connection, tx, command.UserMessage, token);
        await UpdateLastMessageAsync(
            connection, tx, command.TenantId, command.ConversationId, command.UserMessage.CreatedAt, token);
        await ExecuteChiefAsync(connection, tx,
            "INSERT INTO inbox_messages (tenant_id,idempotency_key,message_hash,response_json,processed_at) VALUES ($tenant,$key,$hash,$response,$at);",
            token, ("$tenant", command.TenantId), ("$key", command.IdempotencyKey),
            ("$hash", DurableCommandHash.Compute(command)),
            ("$response", JsonSerializer.Serialize(new { turnId = command.TurnId, state = "blocked" }, JsonOptions)),
            ("$at", Store(command.OccurredAt)));
        await ExecuteChiefAsync(connection, tx,
            "INSERT INTO chief_turn_blocks (id,tenant_id,project_id,conversation_id,user_message_id,readiness_state,blockers_json,next_actions_json,correlation_id,created_at) " +
            "VALUES ($id,$tenant,$project,$conversation,$message,$readiness,$blockers,$nextActions,$correlation,$at);",
            token, ("$id", command.TurnId), ("$tenant", command.TenantId), ("$project", command.ProjectId),
            ("$conversation", command.ConversationId), ("$message", command.UserMessage.Id),
            ("$readiness", command.ReadinessState), ("$blockers", blockers), ("$nextActions", nextActions),
            ("$correlation", command.CorrelationId), ("$at", Store(command.OccurredAt)));
        var messagePayload = MessagePayload(command.UserMessage);
        await AppendAuditAsync(
            connection, tx, command.TenantId, "message.appended", messagePayload, command.OccurredAt, token);
        await AppendOutboxAsync(
            connection, tx, command.TenantId, "message.appended", messagePayload,
            command.OccurredAt, command.OccurredAt, token);
        var blockedPayload = JsonSerializer.Serialize(new
        {
            turnId = command.TurnId,
            conversationId = command.ConversationId,
            projectId = command.ProjectId,
            readinessState = command.ReadinessState,
            correlationId = command.CorrelationId,
            blockers = command.Blockers,
            nextActions = command.NextActions,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, tx, command.TenantId, "execution.blocked", blockedPayload, command.OccurredAt, token);
        await AppendOutboxAsync(
            connection, tx, command.TenantId, "execution.blocked", blockedPayload,
            command.OccurredAt, command.OccurredAt, token);
        await tx.CommitAsync(token);
        return new ChiefTurnBlockRecord(
            command.TenantId, command.ProjectId, command.ConversationId, command.TurnId,
            command.UserMessage.Id, command.ReadinessState, command.CorrelationId,
            command.Blockers, command.NextActions, command.OccurredAt);
    }

    // Payload sanitizado do ciclo de vida: identificadores e seleção, nunca conteúdo de
    // prompt, resposta do modelo, credencial ou referência de segredo.
    private static string TurnLifecyclePayload(ChiefTurnEnqueueCommand command, object? extra) =>
        JsonSerializer.Serialize(new
        {
            turnId = command.TurnId,
            conversationId = command.ConversationId,
            projectId = command.ProjectId,
            modelId = command.Selection?.ModelId,
            effort = command.Selection?.Effort,
            extra,
        }, JsonOptions);

    private static async Task AppendTurnLifecycleAsync(
        SqliteConnection connection, SqliteTransaction tx, string tenantId, string eventType,
        string payload, DateTimeOffset occurredAt, DateTimeOffset sequencedAt, CancellationToken token)
    {
        await AppendAuditAsync(connection, tx, tenantId, eventType, payload, occurredAt, token);
        await AppendOutboxAsync(connection, tx, tenantId, eventType, payload, sequencedAt, occurredAt, token);
    }

    private static async Task<ChiefTurnBlockRecord?> ReadChiefTurnBlockAsync(
        SqliteConnection connection, SqliteTransaction? tx, string tenantId, string userMessageId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText =
            "SELECT id,project_id,conversation_id,user_message_id,readiness_state,blockers_json,next_actions_json,correlation_id,created_at " +
            "FROM chief_turn_blocks WHERE tenant_id=$tenant AND user_message_id=$message;";
        Add(query, "$tenant", tenantId);
        Add(query, "$message", userMessageId);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new ChiefTurnBlockRecord(
            tenantId, reader.GetString(1), reader.GetString(2), reader.GetString(0),
            reader.GetString(3), reader.GetString(4), reader.GetString(7),
            JsonSerializer.Deserialize<ChiefTurnBlockerRecord[]>(reader.GetString(5), JsonOptions) ?? [],
            JsonSerializer.Deserialize<ChiefTurnNextActionRecord[]>(reader.GetString(6), JsonOptions) ?? [],
            Parse(reader.GetString(8)));
    }

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
            "INSERT INTO chief_turn_mailbox (id,tenant_id,project_id,conversation_id,user_message_id,state,created_at,account_id,model_id,model_name,effort,provider_effort_value,fallback_model_ids_json,selection_source,selection_reason,estimated_cost_usd,quota_remaining_usd) " +
            "VALUES ($id,$tenant,$project,$conversation,$message,'pending',$at,$account,$model,$modelName,$effort,$providerEffort,$fallbacks,$source,$reason,$cost,$quota);",
            token, ("$id", command.TurnId), ("$tenant", command.TenantId), ("$project", command.ProjectId),
            ("$conversation", command.ConversationId), ("$message", command.UserMessage.Id), ("$at", Store(command.OccurredAt)),
            ("$account", command.Selection?.AccountId ?? (object)DBNull.Value),
            ("$model", command.Selection?.ModelId ?? (object)DBNull.Value),
            ("$modelName", command.Selection?.ModelName ?? (object)DBNull.Value),
            ("$effort", command.Selection?.Effort ?? (object)DBNull.Value),
            ("$providerEffort", command.Selection?.ProviderEffortValue ?? (object)DBNull.Value),
            ("$fallbacks", JsonSerializer.Serialize(command.Selection?.FallbackModelIds ?? [], JsonOptions)),
            ("$source", command.Selection?.Source ?? (object)DBNull.Value),
            ("$reason", command.Selection?.Reason ?? (object)DBNull.Value),
            ("$cost", command.Selection?.EstimatedCostUsd ?? (object)DBNull.Value),
            ("$quota", command.Selection?.QuotaRemainingUsd ?? (object)DBNull.Value));
        var payload = MessagePayload(command.UserMessage);
        await AppendAuditAsync(connection, tx, command.TenantId, "message.appended", payload, command.OccurredAt, token);
        await AppendOutboxAsync(connection, tx, command.TenantId, "message.appended", payload, command.OccurredAt, command.OccurredAt, token);
        // C3/ADR-019: o ciclo de vida do turno é observável e distingue transporte de execução.
        // `message.received` e `turn.registered` são acknowledgements; `execution.enqueued`
        // marca a entrada na fila real. Nenhum deles é resposta do modelo.
        await AppendTurnLifecycleAsync(
            connection, tx, command.TenantId, "message.received",
            TurnLifecyclePayload(command, new { messageId = command.UserMessage.Id }),
            command.OccurredAt, command.OccurredAt.AddTicks(1), token);
        await AppendTurnLifecycleAsync(
            connection, tx, command.TenantId, "turn.registered",
            TurnLifecyclePayload(command, null),
            command.OccurredAt, command.OccurredAt.AddTicks(2), token);
        await AppendTurnLifecycleAsync(
            connection, tx, command.TenantId, "execution.enqueued",
            TurnLifecyclePayload(command, null),
            command.OccurredAt, command.OccurredAt.AddTicks(3), token);
        await AppendTurnLifecycleAsync(
            connection, tx, command.TenantId, "chief.turnStateChanged",
            JsonSerializer.Serialize(new
            {
                turnId = command.TurnId,
                conversationId = command.ConversationId,
                projectId = command.ProjectId,
                state = "pending",
            }, JsonOptions),
            command.OccurredAt, command.OccurredAt.AddTicks(4), token);
        if (command.Selection is not null)
        {
            var selectionPayload = JsonSerializer.Serialize(new
            {
                auditEvent = new
                {
                    id = Harness.SharedKernel.Identifiers.UlidValue.New(command.OccurredAt).ToString(),
                    actorKind = "user",
                    actorId = command.UserMessage.AuthorProfileId,
                    action = "chief.invocationRouted",
                    targetType = "models",
                    targetId = command.Selection.ModelId,
                    detail = command.Selection.Reason,
                    occurredAt = command.OccurredAt,
                },
                turnId = command.TurnId,
                command.Selection.AccountId,
                command.Selection.ModelId,
                command.Selection.Effort,
                command.Selection.ProviderEffortValue,
                command.Selection.EstimatedCostUsd,
                command.Selection.QuotaRemainingUsd,
            }, JsonOptions);
            await AppendAuditAsync(connection, tx, command.TenantId, "chief.invocationRouted",
                selectionPayload, command.OccurredAt, token);
            await AppendOutboxAsync(connection, tx, command.TenantId, "audit.eventAppended",
                selectionPayload, command.OccurredAt.AddTicks(1), command.OccurredAt, token);
        }
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
        var userMessage = await ReadMessageAsync(connection, tx, command.TenantId, turn.UserMessageId, token)
            ?? throw new ChiefTurnConflictException("The Chief turn user message does not exist.");
        // C3: a aquisição do lease é o momento em que o turno passa ao provider real.
        var invokedPayload = JsonSerializer.Serialize(new
        {
            turnId = turn.TurnId,
            conversationId = turn.ConversationId,
            projectId = turn.ProjectId,
            accountId = turn.Selection?.AccountId,
            modelId = turn.Selection?.ModelId,
            modelName = turn.Selection?.ModelName,
            effort = turn.Selection?.Effort,
            providerEffortValue = turn.Selection?.ProviderEffortValue,
            attempt = turn.AttemptCount + 1,
        }, JsonOptions);
        await AppendTurnLifecycleAsync(
            connection, tx, command.TenantId, "provider.invoked", invokedPayload,
            command.Now, command.Now, token);
        await AppendTurnLifecycleAsync(
            connection, tx, command.TenantId, "chief.turnStateChanged",
            JsonSerializer.Serialize(new
            {
                turnId = turn.TurnId,
                conversationId = turn.ConversationId,
                projectId = turn.ProjectId,
                state = "processing",
            }, JsonOptions),
            command.Now, command.Now.AddTicks(1), token);
        await tx.CommitAsync(token);
        return new ChiefTurnLease(turn with { State = "processing", AttemptCount = turn.AttemptCount + 1 }, command.OwnerId, fencing, leaseExpires, agent, userMessage.Content, session);
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
        await MaterializeChiefDemandsAsync(connection, tx, command, token);
        await tx.CommitAsync(token);
    }

    private static async Task MaterializeChiefDemandsAsync(
        SqliteConnection connection, SqliteTransaction tx, ChiefTurnCompleteCommand command,
        CancellationToken token)
    {
        if (command.Demands is not { Count: > 0 })
        {
            return;
        }

        var tenant = command.Lease.Turn.TenantId;
        var project = command.Lease.Turn.ProjectId;
        await using var authorQuery = connection.CreateCommand();
        authorQuery.Transaction = tx;
        authorQuery.CommandText =
            "SELECT author_profile_id FROM conversation_messages WHERE tenant_id=$tenant AND id=$message;";
        Add(authorQuery, "$tenant", tenant);
        Add(authorQuery, "$message", command.Lease.Turn.UserMessageId);
        var author = await authorQuery.ExecuteScalarAsync(token) as string
            ?? throw new ChiefTurnConflictException(
                "The chief turn user message has no author profile for demand materialization.");
        var index = 0;
        foreach (var raw in command.Demands)
        {
            var seed = raw.Normalize();
            var occurredAt = command.OccurredAt.AddMilliseconds(index * 2);
            await ExecuteChiefAsync(connection, tx,
                """
                INSERT INTO solicitations
                    (id,tenant_id,project_id,user_id,content,created_at,kind,title,state,supersedes_id,is_internal)
                VALUES ($id,$tenant,$project,$author,$body,$at,'request',$title,'open',NULL,1);
                """,
                token, ("$id", seed.BackingSolicitationId), ("$tenant", tenant),
                ("$project", project), ("$author", author), ("$body", seed.Description),
                ("$at", Store(occurredAt)), ("$title", seed.Title));
            await ExecuteChiefAsync(connection, tx,
                """
                INSERT INTO demands
                    (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,created_at,
                     description,state,priority,source_solicitation_id,is_internal)
                VALUES ($id,$tenant,$project,$backing,$title,$criteria,$at,$description,'open',
                        $priority,NULL,0);
                """,
                token, ("$id", seed.DemandId), ("$tenant", tenant), ("$project", project),
                ("$backing", seed.BackingSolicitationId), ("$title", seed.Title),
                ("$criteria", JsonSerializer.Serialize(seed.AcceptanceCriteria, JsonOptions)),
                ("$at", Store(occurredAt)), ("$description", seed.Description),
                ("$priority", seed.RiskTier));
            var payload = JsonSerializer.Serialize(new ChiefDemandCreatedPayload(
                project,
                new ChiefDemandPayload(
                    seed.DemandId, project, null, seed.Title, seed.Description, "open",
                    seed.RiskTier, occurredAt)), JsonOptions);
            await AppendAuditAsync(connection, tx, tenant, "demand.created", payload, occurredAt, token);
            await AppendOutboxAsync(
                connection, tx, tenant, "demand.created", payload,
                command.OccurredAt.AddTicks(command.Chunks.Count + 5 + index), occurredAt, token);
            index++;
        }
    }

    private sealed record ChiefDemandCreatedPayload(string ProjectId, ChiefDemandPayload Demand);

    private sealed record ChiefDemandPayload(
        string Id, string ProjectId, string? SolicitationId, string Title, string Description,
        string State, string Priority, DateTimeOffset CreatedAt);

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
        // C3: falha também é transição observável do ciclo do turno.
        await AppendTurnLifecycleAsync(
            connection, tx, command.Lease.Turn.TenantId, "chief.turnStateChanged",
            JsonSerializer.Serialize(new
            {
                turnId = command.Lease.Turn.TurnId,
                conversationId = command.Lease.Turn.ConversationId,
                projectId = command.Lease.Turn.ProjectId,
                state = next,
                errorCode = command.ErrorCode,
            }, JsonOptions),
            command.OccurredAt, command.OccurredAt, token);
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
        // C3: a resposta do MODELO é um evento distinto do acknowledgement de transporte.
        var responded = JsonSerializer.Serialize(new
        {
            turnId = command.Lease.Turn.TurnId,
            conversationId = command.Lease.Turn.ConversationId,
            projectId = command.Lease.Turn.ProjectId,
            messageId = command.ChiefMessage.Id,
            modelId = command.Lease.Turn.Selection?.ModelId,
            modelName = command.Lease.Turn.Selection?.ModelName,
        }, JsonOptions);
        await AppendTurnLifecycleAsync(
            connection, tx, tenant, "model.responded", responded, command.OccurredAt,
            command.OccurredAt.AddTicks(command.Chunks.Count + 3), token);
        await AppendTurnLifecycleAsync(
            connection, tx, tenant, "chief.turnStateChanged",
            JsonSerializer.Serialize(new
            {
                turnId = command.Lease.Turn.TurnId,
                conversationId = command.Lease.Turn.ConversationId,
                projectId = command.Lease.Turn.ProjectId,
                state = "completed",
            }, JsonOptions),
            command.OccurredAt, command.OccurredAt.AddTicks(command.Chunks.Count + 4), token);
    }

    private static async Task<ChiefTurnRecord?> ReadChiefTurnAsync(SqliteConnection connection, SqliteTransaction? tx, string tenant, string turn, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT tenant_id,project_id,conversation_id,id,user_message_id,state,attempt_count,session_id,response_message_id,last_error_code,created_at,completed_at,account_id,model_id,model_name,effort,provider_effort_value,fallback_model_ids_json,selection_source,selection_reason,estimated_cost_usd,quota_remaining_usd FROM chief_turn_mailbox WHERE tenant_id=$tenant AND id=$turn;";
        Add(query, "$tenant", tenant); Add(query, "$turn", turn); await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var selection = reader.IsDBNull(12) ? null : new ChiefInvocationSelection(
            reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
            reader.GetString(16), JsonSerializer.Deserialize<string[]>(reader.GetString(17), JsonOptions) ?? [],
            reader.GetString(18), reader.GetString(19), reader.IsDBNull(20) ? null : reader.GetDecimal(20),
            reader.IsDBNull(21) ? null : reader.GetDecimal(21));
        return new ChiefTurnRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : Parse(reader.GetString(11)), selection);
    }

    private static async Task ExecuteChiefAsync(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken token, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var value in values) Add(command, value.Name, value.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
