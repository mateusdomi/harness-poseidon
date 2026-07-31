using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.WorkChain;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresConversationStore
{
    private const string ChiefTurnSelect =
        "SELECT tenant_id,project_id,conversation_id,id,user_message_id,state,attempt_count,session_id,response_message_id,last_error_code,created_at,completed_at,account_id,model_id,model_name,effort,provider_effort_value,fallback_model_ids_json::text,selection_source,selection_reason,estimated_cost_usd,quota_remaining_usd FROM harness.chief_turn_mailbox";

    public Task<ChiefTurnRecord> EnqueueAsync(
        ChiefTurnEnqueueCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return EnqueueCoreAsync(command, cancellationToken);
    }

    public Task<ChiefTurnLease> AcquireAsync(
        ChiefTurnAcquireCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AcquireCoreAsync(command, cancellationToken);
    }

    public Task<ChiefTurnBlockRecord> BlockAsync(
        ChiefTurnBlockCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return BlockCoreAsync(command, cancellationToken);
    }

    public async Task RecordActivityAsync(
        ChiefTurnActivityCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var payload = ChiefTurnStatePayload(
            command.TurnId, command.ConversationId, command.ProjectId, command.State,
            command.LastActivityAt, null, command.AgentName, command.ActivityStartedAt, command.Detail);
        await AppendTurnLifecycleAsync(
            connection, transaction, command.TenantId, "chief.turnStateChanged", payload,
            command.LastActivityAt, command.LastActivityAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Payload canônico do evento chief.turnStateChanged: identificadores, estado
    // granular, heartbeat (lastActivityAt) e metadados opcionais de delegação —
    // nunca conteúdo de prompt, resposta do modelo, credencial ou segredo.
    private static string ChiefTurnStatePayload(
        string turnId, string conversationId, string projectId, string state,
        DateTimeOffset lastActivityAt, string? errorCode,
        string? agentName, DateTimeOffset? activityStartedAt, string? detail) =>
        JsonSerializer.Serialize(new
        {
            turnId,
            conversationId,
            projectId,
            state,
            errorCode,
            lastActivityAt,
            agentName,
            activityStartedAt,
            detail,
        }, JsonOptions);

    private async Task<ChiefTurnBlockRecord> BlockCoreAsync(
        ChiefTurnBlockCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"chief-turn-block:{command.TenantId}:{command.UserMessage.Id}"));
        // Idempotência por mensagem humana: o retry do mesmo envio devolve o bloqueio já
        // registrado, sem inserir mensagem, bloqueio ou evento de novo.
        var existing = await ReadChiefTurnBlockAsync(
            connection, transaction, command.TenantId, command.UserMessage.Id, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var conversation = await ReadConversationAsync(
            connection, transaction, command.TenantId, command.ConversationId, cancellationToken);
        if (conversation is null || conversation.State != "active" ||
            conversation.ProjectId != command.ProjectId)
        {
            throw new ChiefTurnConflictException("The target conversation is not active for this project.");
        }

        var blockers = JsonSerializer.Serialize(command.Blockers, JsonOptions);
        var nextActions = JsonSerializer.Serialize(command.NextActions, JsonOptions);
        await InsertMessageAsync(connection, transaction, command.UserMessage, cancellationToken);
        await UpdateLastMessageAsync(
            connection, transaction, command.TenantId, command.ConversationId,
            command.UserMessage.CreatedAt, cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.IdempotencyKey),
            Text(DurableCommandHash.Compute(command)),
            Json(JsonSerializer.Serialize(
                new { turnId = command.TurnId, state = "blocked" }, JsonOptions)),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.chief_turn_blocks
                (id,tenant_id,project_id,conversation_id,user_message_id,readiness_state,
                 blockers_json,next_actions_json,correlation_id,created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10);
            """,
            cancellationToken,
            Text(command.TurnId),
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.ConversationId),
            Text(command.UserMessage.Id),
            Text(command.ReadinessState),
            Json(blockers),
            Json(nextActions),
            Text(command.CorrelationId),
            Timestamp(command.OccurredAt));
        var messagePayload = MessagePayload(command.UserMessage);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "message.appended", messagePayload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "message.appended", messagePayload,
            command.OccurredAt, command.OccurredAt, cancellationToken);
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
            connection, transaction, command.TenantId, "execution.blocked", blockedPayload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "execution.blocked", blockedPayload,
            command.OccurredAt, command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string eventType, string payload, DateTimeOffset occurredAt, DateTimeOffset sequencedAt,
        CancellationToken cancellationToken)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        await AppendAuditAsync(
            connection, transaction, tenantId, eventType, payload, occurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, tenantId, eventType, payload, sequencedAt, occurredAt,
            cancellationToken);
    }

    private static async Task<ChiefTurnBlockRecord?> ReadChiefTurnBlockAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenantId,
        string userMessageId, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT id,project_id,conversation_id,user_message_id,readiness_state," +
            "blockers_json::text,next_actions_json::text,correlation_id,created_at " +
            "FROM harness.chief_turn_blocks WHERE tenant_id=$1 AND user_message_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(userMessageId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ChiefTurnBlockRecord(
            tenantId, reader.GetString(1), reader.GetString(2), reader.GetString(0),
            reader.GetString(3), reader.GetString(4), reader.GetString(7),
            JsonSerializer.Deserialize<ChiefTurnBlockerRecord[]>(reader.GetString(5), JsonOptions) ?? [],
            JsonSerializer.Deserialize<ChiefTurnNextActionRecord[]>(reader.GetString(6), JsonOptions) ?? [],
            reader.GetFieldValue<DateTimeOffset>(8));
    }

    public async Task<ChiefTurnLease?> AcquireNextAsync(
        string ownerId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string tenantId;
        string turnId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                """
                SELECT m.tenant_id,m.id
                FROM harness.chief_turn_mailbox m
                JOIN harness.chief_states s ON s.tenant_id=m.tenant_id AND s.project_id=m.project_id
                WHERE m.state='pending'
                   OR (m.state='processing' AND s.lease_expires_at<=$1)
                ORDER BY m.created_at,m.id LIMIT 1
                FOR UPDATE OF m SKIP LOCKED;
                """;
            query.Parameters.Add(Timestamp(now));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            tenantId = reader.GetString(0).TrimEnd();
            turnId = reader.GetString(1).TrimEnd();
        }

        var lease = await AcquireInTransactionAsync(
            connection, transaction,
            new ChiefTurnAcquireCommand(tenantId, turnId, ownerId, now, leaseDuration),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    public async Task<ChiefTurnRenewOutcome> TryRenewAsync(
        ChiefTurnRenewCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var expiresAt = command.Now.Add(command.LeaseDuration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        // O critério é o FENCING, não o relógio: se o lease venceu e ninguém o tomou, renovar é o
        // certo — evita que este trabalho vivo seja readquirido e pago duas vezes. Se outro dono já
        // adquiriu, o token mudou e nenhuma linha é afetada.
        update.CommandText =
            """
            UPDATE harness.chief_states
            SET lease_expires_at=$1,version=version+1,updated_at=$2
            WHERE tenant_id=$3 AND project_id=$4 AND lease_owner_id=$5
              AND lease_fencing_token=$6
              AND EXISTS (
                SELECT 1 FROM harness.chief_turn_mailbox
                WHERE tenant_id=$3 AND id=$7 AND state='processing'
                  AND active_fencing_token=$6);
            """;
        update.Parameters.Add(Timestamp(expiresAt));
        update.Parameters.Add(Timestamp(command.Now));
        update.Parameters.Add(Text(command.Lease.Turn.TenantId));
        update.Parameters.Add(Text(command.Lease.Turn.ProjectId));
        update.Parameters.Add(Text(command.Lease.OwnerId));
        update.Parameters.Add(Bigint(command.Lease.FencingToken));
        update.Parameters.Add(Text(command.Lease.Turn.TurnId));
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1
            ? new ChiefTurnRenewOutcome(true, expiresAt)
            : new ChiefTurnRenewOutcome(false, null);
    }

    public Task CompleteAsync(
        ChiefTurnCompleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CompleteCoreAsync(command, cancellationToken);
    }

    public Task<ChiefTurnFailOutcome> FailAsync(
        ChiefTurnFailCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return FailCoreAsync(command, cancellationToken);
    }

    public async Task<ChiefTurnRecord?> GetAsync(
        string tenantId, string turnId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadChiefTurnAsync(
            connection, null, tenantId, turnId, forUpdate: false, cancellationToken);
    }

    private async Task<ChiefTurnRecord> EnqueueCoreAsync(
        ChiefTurnEnqueueCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"chief-turn:{command.TenantId}:{command.TurnId}"));
        var existing = await ReadChiefTurnAsync(
            connection, transaction, command.TenantId, command.TurnId, forUpdate: false,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var conversation = await ReadConversationAsync(
            connection, transaction, command.TenantId, command.ConversationId, cancellationToken);
        if (conversation is null || conversation.State != "active" || conversation.ProjectId != command.ProjectId)
        {
            throw new ChiefTurnConflictException("The target conversation is not active for this project.");
        }

        var requestHash = DurableCommandHash.Compute(command);
        await InsertMessageAsync(connection, transaction, command.UserMessage, cancellationToken);
        await UpdateLastMessageAsync(
            connection, transaction, command.TenantId, command.ConversationId,
            command.UserMessage.CreatedAt, cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.IdempotencyKey),
            Text(requestHash),
            Json(JsonSerializer.Serialize(new { turnId = command.TurnId }, JsonOptions)),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.chief_states
                (tenant_id,project_id,chief_agent_id,state,updated_at)
            VALUES ($1,$2,$3,'idle',$4)
            ON CONFLICT (tenant_id,project_id) DO NOTHING;
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.ChiefAgentId),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.chief_turn_mailbox
                (id,tenant_id,project_id,conversation_id,user_message_id,state,created_at,
                 account_id,model_id,model_name,effort,provider_effort_value,
                 fallback_model_ids_json,selection_source,selection_reason,
                 estimated_cost_usd,quota_remaining_usd)
            VALUES ($1,$2,$3,$4,$5,'pending',$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16);
            """,
            cancellationToken,
            Text(command.TurnId),
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.ConversationId),
            Text(command.UserMessage.Id),
            Timestamp(command.OccurredAt),
            NullableText(command.Selection?.AccountId),
            NullableText(command.Selection?.ModelId),
            NullableText(command.Selection?.ModelName),
            NullableText(command.Selection?.Effort),
            NullableText(command.Selection?.ProviderEffortValue),
            Json(JsonSerializer.Serialize(command.Selection?.FallbackModelIds ?? [], JsonOptions)),
            NullableText(command.Selection?.Source),
            NullableText(command.Selection?.Reason),
            NullableSelectionNumeric(command.Selection?.EstimatedCostUsd),
            NullableSelectionNumeric(command.Selection?.QuotaRemainingUsd));
        var payload = MessagePayload(command.UserMessage);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "message.appended", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "message.appended", payload,
            command.OccurredAt, command.OccurredAt, cancellationToken);
        // C3/ADR-019: ciclo de vida observável, distinguindo transporte de execução real.
        await AppendTurnLifecycleAsync(
            connection, transaction, command.TenantId, "message.received",
            TurnLifecyclePayload(command, new { messageId = command.UserMessage.Id }),
            command.OccurredAt, command.OccurredAt.AddTicks(1), cancellationToken);
        await AppendTurnLifecycleAsync(
            connection, transaction, command.TenantId, "turn.registered",
            TurnLifecyclePayload(command, null),
            command.OccurredAt, command.OccurredAt.AddTicks(2), cancellationToken);
        await AppendTurnLifecycleAsync(
            connection, transaction, command.TenantId, "execution.enqueued",
            TurnLifecyclePayload(command, null),
            command.OccurredAt, command.OccurredAt.AddTicks(3), cancellationToken);
        await AppendTurnLifecycleAsync(
            connection, transaction, command.TenantId, "chief.turnStateChanged",
            ChiefTurnStatePayload(
                command.TurnId, command.ConversationId, command.ProjectId, "pending",
                command.OccurredAt, null, null, null, null),
            command.OccurredAt, command.OccurredAt.AddTicks(4), cancellationToken);
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
            await AppendAuditAsync(connection, transaction, command.TenantId,
                "chief.invocationRouted", selectionPayload, command.OccurredAt, cancellationToken);
            await AppendOutboxAsync(connection, transaction, command.TenantId,
                "audit.eventAppended", selectionPayload, command.OccurredAt.AddTicks(1),
                command.OccurredAt, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ReadChiefTurnAsync(
                connection, null, command.TenantId, command.TurnId, forUpdate: false,
                cancellationToken)
            ?? throw new InvalidOperationException("The enqueued Chief turn disappeared after commit.");
    }

    private async Task<ChiefTurnLease> AcquireCoreAsync(
        ChiefTurnAcquireCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var lease = await AcquireInTransactionAsync(connection, transaction, command, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    private static async Task<ChiefTurnLease> AcquireInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefTurnAcquireCommand command,
        CancellationToken cancellationToken)
    {
        if (command.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var turn = await ReadChiefTurnAsync(
                connection, transaction, command.TenantId, command.TurnId, forUpdate: true,
                cancellationToken)
            ?? throw new ChiefTurnConflictException("The Chief turn does not exist.");
        if (turn.State is "completed" or "failed")
        {
            throw new ChiefTurnConflictException("The Chief turn is terminal.");
        }

        string agent;
        string? owner;
        long fencing;
        DateTimeOffset? expires;
        string? session;
        await using (var stateQuery = connection.CreateCommand())
        {
            stateQuery.Transaction = transaction;
            stateQuery.CommandText =
                "SELECT chief_agent_id,lease_owner_id,lease_fencing_token,lease_expires_at,session_id " +
                "FROM harness.chief_states WHERE tenant_id=$1 AND project_id=$2 FOR UPDATE;";
            stateQuery.Parameters.Add(Text(command.TenantId));
            stateQuery.Parameters.Add(Text(turn.ProjectId));
            await using var reader = await stateQuery.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ChiefTurnConflictException("Chief state does not exist.");
            }

            agent = reader.GetString(0).TrimEnd();
            owner = reader.IsDBNull(1) ? null : reader.GetString(1);
            fencing = reader.GetInt64(2);
            expires = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
            session = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        if (owner is not null && expires > command.Now && owner != command.OwnerId)
        {
            throw new ChiefTurnConflictException("Another Chief turn owns the project lease.");
        }

        fencing++;
        var leaseExpires = command.Now.Add(command.LeaseDuration);
        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE harness.chief_states
            SET state='working',lease_owner_id=$1,lease_fencing_token=$2,lease_expires_at=$3,
                version=version+1,updated_at=$4
            WHERE tenant_id=$5 AND project_id=$6;
            """,
            cancellationToken,
            Text(command.OwnerId),
            Bigint(fencing),
            Timestamp(leaseExpires),
            Timestamp(command.Now),
            Text(command.TenantId),
            Text(turn.ProjectId));
        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE harness.chief_turn_mailbox
            SET state='processing',attempt_count=attempt_count+1,active_fencing_token=$1,
                started_at=$2,last_error_code=NULL
            WHERE tenant_id=$3 AND id=$4;
            """,
            cancellationToken,
            Bigint(fencing),
            Timestamp(command.Now),
            Text(command.TenantId),
            Text(turn.TurnId));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.agents SET state='working',last_heartbeat_at=$1 WHERE tenant_id=$2 AND id=$3;",
            cancellationToken,
            Timestamp(command.Now),
            Text(command.TenantId),
            Text(agent));
        var userMessage = await ReadMessageAsync(
                connection, transaction, command.TenantId, turn.UserMessageId, cancellationToken)
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
            connection, transaction, command.TenantId, "provider.invoked", invokedPayload,
            command.Now, command.Now, cancellationToken);
        await AppendTurnLifecycleAsync(
            connection, transaction, command.TenantId, "chief.turnStateChanged",
            ChiefTurnStatePayload(
                turn.TurnId, turn.ConversationId, turn.ProjectId, "processing",
                command.Now, null, null, null, null),
            command.Now, command.Now.AddTicks(1), cancellationToken);
        return new ChiefTurnLease(
            turn with { State = "processing", AttemptCount = turn.AttemptCount + 1 },
            command.OwnerId, fencing, leaseExpires, agent, userMessage.Content, session);
    }

    private async Task CompleteCoreAsync(
        ChiefTurnCompleteCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, command.Lease, command.OccurredAt, cancellationToken);
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
            Text(command.Lease.Turn.TurnId),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.Turn.ProjectId),
            Text(command.Lease.Turn.ConversationId),
            Text(command.Lease.Turn.UserMessageId),
            Text(command.ChiefMessage.Id),
            Timestamp(command.Lease.Turn.CreatedAt),
            Timestamp(command.OccurredAt));
        await UpdateLastMessageAsync(
            connection, transaction, command.Lease.Turn.TenantId,
            command.Lease.Turn.ConversationId, command.ChiefMessage.CreatedAt, cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE harness.chief_turn_mailbox
            SET state='completed',active_fencing_token=NULL,session_id=$1,
                response_message_id=$2,completed_at=$3
            WHERE tenant_id=$4 AND id=$5;
            """,
            cancellationToken,
            Text(command.SessionId),
            Text(command.ChiefMessage.Id),
            Timestamp(command.OccurredAt),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.Turn.TurnId));
        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE harness.chief_states
            SET state='idle',lease_owner_id=NULL,lease_expires_at=NULL,session_id=$1,
                last_digest_json=$2,version=version+1,updated_at=$3
            WHERE tenant_id=$4 AND project_id=$5 AND lease_fencing_token=$6;
            """,
            cancellationToken,
            Text(command.SessionId),
            Json(command.StatusDigestJson),
            Timestamp(command.OccurredAt),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.Turn.ProjectId),
            Bigint(command.Lease.FencingToken));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.agents SET state='idle',tasks_completed=tasks_completed+1,last_heartbeat_at=$1 WHERE tenant_id=$2 AND id=$3;",
            cancellationToken,
            Timestamp(command.OccurredAt),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.ChiefAgentId));
        await QueueChiefCompletionEventsAsync(connection, transaction, command, cancellationToken);
        await MaterializeChiefDemandsAsync(connection, transaction, command, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MaterializeChiefDemandsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefTurnCompleteCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Demands is not { Count: > 0 })
        {
            return;
        }

        var tenant = command.Lease.Turn.TenantId;
        var project = command.Lease.Turn.ProjectId;
        string? author;
        await using (var authorQuery = connection.CreateCommand())
        {
            authorQuery.Transaction = transaction;
            authorQuery.CommandText =
                "SELECT author_profile_id FROM harness.conversation_messages WHERE tenant_id=$1 AND id=$2;";
            authorQuery.Parameters.Add(Text(tenant));
            authorQuery.Parameters.Add(Text(command.Lease.Turn.UserMessageId));
            author = (await authorQuery.ExecuteScalarAsync(cancellationToken) as string)?.TrimEnd();
        }

        if (author is null)
        {
            throw new ChiefTurnConflictException(
                "The chief turn user message has no author profile for demand materialization.");
        }

        var index = 0;
        foreach (var raw in command.Demands)
        {
            var seed = raw.Normalize();
            var occurredAt = command.OccurredAt.AddMilliseconds(index * 2);
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.solicitations
                    (id,tenant_id,project_id,user_id,content,created_at,kind,title,state,supersedes_id,is_internal)
                VALUES ($1,$2,$3,$4,$5,$6,'request',$7,'open',NULL,true);
                """,
                cancellationToken,
                Text(seed.BackingSolicitationId),
                Text(tenant),
                Text(project),
                Text(author),
                Text(seed.Description),
                Timestamp(occurredAt),
                Text(seed.Title));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.demands
                    (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,created_at,
                     description,state,priority,source_solicitation_id,is_internal)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'open',$9,NULL,false);
                """,
                cancellationToken,
                Text(seed.DemandId),
                Text(tenant),
                Text(project),
                Text(seed.BackingSolicitationId),
                Text(seed.Title),
                Json(JsonSerializer.Serialize(seed.AcceptanceCriteria, JsonOptions)),
                Timestamp(occurredAt),
                Text(seed.Description),
                Text(seed.RiskTier));
            var payload = JsonSerializer.Serialize(new ChiefDemandCreatedPayload(
                project,
                new ChiefDemandPayload(
                    seed.DemandId, project, null, seed.Title, seed.Description, "open",
                    seed.RiskTier, occurredAt)), JsonOptions);
            await AppendAuditAsync(
                connection, transaction, tenant, "demand.created", payload, occurredAt,
                cancellationToken);
            await AppendOutboxAsync(
                connection, transaction, tenant, "demand.created", payload,
                command.OccurredAt.AddTicks(command.Chunks.Count + 5 + index), occurredAt,
                cancellationToken);
            // A sequência do comando fica DEPOIS de todos os `demand.created` do turno: com duas
            // demandas, reaproveitar a faixa colidiria o comando de uma com o evento da outra.
            await CommitPlanMaterializationAsync(
                connection, transaction, command, seed, occurredAt,
                sequencedAt: command.OccurredAt.AddTicks(
                    command.Chunks.Count + 5 + command.Demands!.Count + index),
                cancellationToken);
            index++;
        }
    }

    /// <summary>
    /// Fase 0A1 (BR-004): o COMPROMISSO de materializar o plano nasce aqui, na mesma transação que
    /// conclui o turno e persiste a demanda. Antes, a decomposição acontecia em memória depois do
    /// commit, dentro de um try/catch: uma queda entre as duas coisas deixava a demanda sem plano e
    /// sem cards para sempre, e nada no produto sabia disso.
    ///
    /// São dois registros complementares, e ambos são necessários. O <c>demand_materializations</c>
    /// carrega a INTENÇÃO do turno (critérios, especialidade e superfícies declaradas) e o estado
    /// factual do planejamento — é o que torna a lacuna visível e o que permite ao reconciliador
    /// convergir mesmo se o comando se perder. O evento de outbox é o gatilho que faz o trabalho
    /// acontecer agora, com lease, fencing e retry próprios.
    /// </summary>
    private static async Task CommitPlanMaterializationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefTurnCompleteCommand command,
        ChiefDemandSeed seed,
        DateTimeOffset occurredAt,
        DateTimeOffset sequencedAt,
        CancellationToken cancellationToken)
    {
        var tenant = command.Lease.Turn.TenantId;
        var project = command.Lease.Turn.ProjectId;
        var request = new PlanMaterializationRequest(
            seed.AcceptanceCriteria,
            seed.Specialty,
            seed.Surfaces is null
                ? null
                : new PlanMaterializationSurfaces(
                    seed.Surfaces.Frontend,
                    seed.Surfaces.Backend,
                    seed.Surfaces.ExternalCredential,
                    seed.Surfaces.TechnicalUncertainty,
                    seed.Surfaces.Decision));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.demand_materializations
                (tenant_id,demand_id,project_id,turn_id,plan_id,status,attempt_count,
                 expected_cards,materialized_cards,request_json,owner_id,last_error,
                 requested_at,updated_at,completed_at)
            VALUES ($1,$2,$3,$4,NULL,'pending',0,NULL,NULL,$5,NULL,NULL,$6,$6,NULL);
            """,
            cancellationToken,
            Text(tenant),
            Text(seed.DemandId),
            Text(project),
            Text(command.Lease.Turn.TurnId),
            Json(JsonSerializer.Serialize(request, JsonOptions)),
            Timestamp(occurredAt));
        var commandPayload = JsonSerializer.Serialize(new
        {
            tenantId = tenant,
            projectId = project,
            demandId = seed.DemandId,
            turnId = command.Lease.Turn.TurnId,
        }, JsonOptions);
        await AppendOutboxAsync(
            connection, transaction, tenant, PlanMaterializationRequestedEventType, commandPayload,
            sequencedAt, occurredAt, cancellationToken);
    }

    /// <summary>Comando interno da outbox; não é evento de tempo real e não vai para o navegador.</summary>
    internal const string PlanMaterializationRequestedEventType = "plan.materializationRequested";

    private sealed record ChiefDemandCreatedPayload(string ProjectId, ChiefDemandPayload Demand);

    private sealed record ChiefDemandPayload(
        string Id, string ProjectId, string? SolicitationId, string Title, string Description,
        string State, string Priority, DateTimeOffset CreatedAt);

    /// <summary>Retentativas de um turno do chefe antes de ele ser dado como perdido.</summary>
    private const int MaxChiefTurnAttempts = 3;

    private async Task<ChiefTurnFailOutcome> FailCoreAsync(
        ChiefTurnFailCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, command.Lease, command.OccurredAt, cancellationToken);
        var next = command.Retryable && command.Lease.Turn.AttemptCount < MaxChiefTurnAttempts ? "pending" : "failed";
        var failed = next == "failed";
        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE harness.chief_turn_mailbox
            SET state=$1,active_fencing_token=NULL,last_error_code=$2,completed_at=$3
            WHERE tenant_id=$4 AND id=$5;
            """,
            cancellationToken,
            Text(next),
            Text(command.ErrorCode),
            NullableTimestamp(failed ? command.OccurredAt : (DateTimeOffset?)null),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.Turn.TurnId));
        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE harness.chief_states
            SET state=$1,lease_owner_id=NULL,lease_expires_at=NULL,version=version+1,updated_at=$2
            WHERE tenant_id=$3 AND project_id=$4 AND lease_fencing_token=$5;
            """,
            cancellationToken,
            Text(failed ? "error" : "idle"),
            Timestamp(command.OccurredAt),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.Turn.ProjectId),
            Bigint(command.Lease.FencingToken));
        // O AGENTE volta a `idle` mesmo quando o turno morre (paridade com o SQLite): ele não
        // está quebrado — quem falhou foi o turno, e isso já fica registrado no mailbox, no
        // `chief_states`, no evento de ciclo e na mensagem publicada na conversa. Em `error`, a
        // prontidão bloqueava TODO turno seguinte e o projeto perdia a única voz com o usuário.
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.agents SET state=$1,last_heartbeat_at=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text("idle"),
            Timestamp(command.OccurredAt),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.ChiefAgentId));
        // C3: falha também é transição observável do ciclo do turno.
        await AppendTurnLifecycleAsync(
            connection, transaction, command.Lease.Turn.TenantId, "chief.turnStateChanged",
            ChiefTurnStatePayload(
                command.Lease.Turn.TurnId, command.Lease.Turn.ConversationId,
                command.Lease.Turn.ProjectId, next, command.OccurredAt, command.ErrorCode, null, null, null),
            command.OccurredAt, command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ChiefTurnFailOutcome(failed, command.Lease.Turn.AttemptCount);
    }

    private static async Task EnsureLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefTurnLease lease,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT 1
            FROM harness.chief_states s
            JOIN harness.chief_turn_mailbox m ON m.tenant_id=s.tenant_id AND m.project_id=s.project_id
            WHERE s.tenant_id=$1 AND s.project_id=$2 AND s.lease_owner_id=$3 AND s.lease_fencing_token=$4
              AND s.lease_expires_at>$5 AND m.id=$6 AND m.state='processing' AND m.active_fencing_token=$4
            FOR UPDATE OF s, m;
            """;
        query.Parameters.Add(Text(lease.Turn.TenantId));
        query.Parameters.Add(Text(lease.Turn.ProjectId));
        query.Parameters.Add(Text(lease.OwnerId));
        query.Parameters.Add(Bigint(lease.FencingToken));
        query.Parameters.Add(Timestamp(now));
        query.Parameters.Add(Text(lease.Turn.TurnId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ChiefTurnConflictException("Chief turn lease is stale or expired.");
        }
    }

    private static async Task QueueChiefCompletionEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefTurnCompleteCommand command,
        CancellationToken cancellationToken)
    {
        var tenant = command.Lease.Turn.TenantId;
        var conversation = command.Lease.Turn.ConversationId;
        var started = JsonSerializer.Serialize(
            new
            {
                conversationId = conversation,
                turnId = command.Lease.Turn.TurnId,
                agentId = command.Lease.ChiefAgentId,
            },
            JsonOptions);
        await AppendOutboxAsync(
            connection, transaction, tenant, "chat.turnStarted", started,
            command.OccurredAt, command.OccurredAt, cancellationToken);
        for (var index = 0; index < command.Chunks.Count; index++)
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    conversationId = conversation,
                    turnId = command.Lease.Turn.TurnId,
                    index,
                    text = command.Chunks[index],
                },
                JsonOptions);
            await AppendOutboxAsync(
                connection, transaction, tenant, "chat.turnChunk", payload,
                command.OccurredAt.AddTicks(index + 1), command.OccurredAt, cancellationToken);
        }

        var messagePayload = MessagePayload(command.ChiefMessage);
        await AppendAuditAsync(
            connection, transaction, tenant, "message.appended", messagePayload,
            command.ChiefMessage.CreatedAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, tenant, "message.appended", messagePayload,
            command.OccurredAt.AddTicks(command.Chunks.Count + 1), command.OccurredAt,
            cancellationToken);
        var completed = JsonSerializer.Serialize(
            new
            {
                conversationId = conversation,
                turnId = command.Lease.Turn.TurnId,
                messageId = command.ChiefMessage.Id,
                finishReason = "stop",
            },
            JsonOptions);
        await AppendAuditAsync(
            connection, transaction, tenant, "chat.turnCompleted", completed,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, tenant, "chat.turnCompleted", completed,
            command.OccurredAt.AddTicks(command.Chunks.Count + 2), command.OccurredAt,
            cancellationToken);
        // C3: a resposta do MODELO é evento distinto do acknowledgement de transporte.
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
            connection, transaction, tenant, "model.responded", responded, command.OccurredAt,
            command.OccurredAt.AddTicks(command.Chunks.Count + 3), cancellationToken);
        await AppendTurnLifecycleAsync(
            connection, transaction, tenant, "chief.turnStateChanged",
            ChiefTurnStatePayload(
                command.Lease.Turn.TurnId, command.Lease.Turn.ConversationId,
                command.Lease.Turn.ProjectId, "completed", command.OccurredAt, null, null, null, null),
            command.OccurredAt, command.OccurredAt.AddTicks(command.Chunks.Count + 4),
            cancellationToken);
    }

    private static async Task<ChiefTurnRecord?> ReadChiefTurnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string turnId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{ChiefTurnSelect} WHERE tenant_id=$1 AND id=$2{(forUpdate ? " FOR UPDATE" : string.Empty)};";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(turnId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var selection = reader.IsDBNull(12) ? null : new ChiefInvocationSelection(
            reader.GetString(12).TrimEnd(), reader.GetString(13).TrimEnd(), reader.GetString(14),
            reader.GetString(15), reader.GetString(16),
            JsonSerializer.Deserialize<string[]>(reader.GetString(17), JsonOptions) ?? [],
            reader.GetString(18), reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetDecimal(20),
            reader.IsDBNull(21) ? null : reader.GetDecimal(21));
        return new ChiefTurnRecord(
                reader.GetString(0).TrimEnd(),
                reader.GetString(1).TrimEnd(),
                reader.GetString(2).TrimEnd(),
                reader.GetString(3).TrimEnd(),
                reader.GetString(4).TrimEnd(),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8).TrimEnd(),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                selection);
    }

    private static NpgsqlParameter NullableSelectionNumeric(decimal? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Numeric,
        Value = value is null ? DBNull.Value : value.Value,
    };
}
