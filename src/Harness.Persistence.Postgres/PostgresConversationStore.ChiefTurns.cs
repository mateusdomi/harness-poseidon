using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.DurableExecution;
using Npgsql;
using NpgsqlTypes;

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

    public Task CompleteAsync(
        ChiefTurnCompleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CompleteCoreAsync(command, cancellationToken);
    }

    public Task FailAsync(
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
                command.OccurredAt.AddTicks(command.Chunks.Count + 3 + index), occurredAt,
                cancellationToken);
            index++;
        }
    }

    private sealed record ChiefDemandCreatedPayload(string ProjectId, ChiefDemandPayload Demand);

    private sealed record ChiefDemandPayload(
        string Id, string ProjectId, string? SolicitationId, string Title, string Description,
        string State, string Priority, DateTimeOffset CreatedAt);

    private async Task FailCoreAsync(
        ChiefTurnFailCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, command.Lease, command.OccurredAt, cancellationToken);
        var next = command.Retryable && command.Lease.Turn.AttemptCount < 3 ? "pending" : "failed";
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
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.agents SET state=$1,last_heartbeat_at=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text(failed ? "error" : "idle"),
            Timestamp(command.OccurredAt),
            Text(command.Lease.Turn.TenantId),
            Text(command.Lease.ChiefAgentId));
        await transaction.CommitAsync(cancellationToken);
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
