using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkChainStore(
    SqliteWriteDispatcher dispatcher,
    int maximumReviewCycles = 3) : IWorkChainStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly int _maximumReviewCycles = maximumReviewCycles > 0
        ? maximumReviewCycles
        : throw new ArgumentOutOfRangeException(
            nameof(maximumReviewCycles),
            "Maximum review cycles must be positive.");

    public Task<WorkChainCreateReceipt> CreateAsync(
        WorkChainCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainCreateValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<WorkChainSnapshot?> ReadAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(tenantId, nameof(tenantId));
        ValidateId(solicitationId, nameof(solicitationId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadCoreAsync(connection, tenantId, solicitationId, token),
            cancellationToken);
    }

    private static async Task<WorkChainCreateReceipt> CreateCoreAsync(
        SqliteConnection connection,
        WorkChainCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var commandHash = WorkChainCreateHash.Compute(command);
        var replay = await ReadInboxAsync(connection, transaction, command, commandHash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replay = true };
        }

        var occurredAt = ToStorage(command.OccurredAt);
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText =
                """
                INSERT INTO solicitations
                    (id, tenant_id, project_id, user_id, content, created_at, kind, title, state, is_internal)
                VALUES ($solicitationId, $tenantId, $projectId, $userId, $solicitationContent,
                        $occurredAt, 'request', $demandTitle, 'open', 0);
                INSERT INTO demands
                    (id, tenant_id, project_id, solicitation_id, title, acceptance_criteria_json,
                     created_at, description, state, priority, source_solicitation_id, is_internal)
                VALUES
                    ($demandId, $tenantId, $projectId, $solicitationId, $demandTitle,
                     $acceptanceCriteriaJson, $occurredAt, $solicitationContent, 'open',
                     $riskTier, $solicitationId, 0);
                INSERT INTO work_tasks
                    (id, tenant_id, project_id, demand_id, title, risk_tier, weight,
                     state, version, created_at, updated_at, source_demand_id, board_state, priority)
                VALUES
                    ($taskId, $tenantId, $projectId, $demandId, $taskTitle, $riskTier,
                     $weight, 'draft', 1, $occurredAt, $occurredAt, $demandId, 'backlog', $riskTier);
                INSERT INTO instruction_versions
                    (id, tenant_id, project_id, task_id, version, content, content_hash, created_at,
                     author_kind, author_id)
                VALUES
                    ($instructionId, $tenantId, $projectId, $taskId, 1, $instructionContent,
                     $instructionHash, $occurredAt, 'chief', NULL);
                """;
            AddStateParameters(state, command, occurredAt);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new
        {
            projectId = command.ProjectId,
            task = new
            {
                id = command.TaskId,
                projectId = command.ProjectId,
                demandId = command.DemandId,
                title = command.TaskTitle,
                state = "backlog",
                priority = command.RiskTier,
                assigneeAgentId = (string?)null,
                blockedReason = (string?)null,
                instructionVersion = 1,
                progress = new { executed = 0, validated = 0, approved = 0 },
                createdAt = command.OccurredAt,
                updatedAt = command.OccurredAt,
                dueAt = (DateTimeOffset?)null,
            },
        });
        var (ledgerSequence, previousHash) = await ReadLedgerTailAsync(
            connection,
            transaction,
            command.TenantId,
            cancellationToken);
        const string eventType = "task.created";
        var ledgerHash = AuditLedgerHash.Compute(
            previousHash,
            command.TenantId,
            ledgerSequence,
            eventType,
            payload,
            command.OccurredAt);
        var ledgerId = UlidValue.New(command.OccurredAt).ToString();
        var outboxId = UlidValue.New(command.OccurredAt).ToString();
        var receipt = new WorkChainCreateReceipt(
            command.SolicitationId,
            command.DemandId,
            command.TaskId,
            command.InstructionVersionId,
            ledgerSequence,
            ledgerHash,
            outboxId,
            Replay: false);
        await using (var messaging = connection.CreateCommand())
        {
            messaging.Transaction = transaction;
            messaging.CommandText =
                """
                INSERT INTO audit_ledger
                    (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
                VALUES
                    ($ledgerId, $tenantId, $ledgerSequence, $previousHash, $ledgerHash,
                     $eventType, $payloadJson, $occurredAt);
                INSERT INTO outbox_messages
                    (id, tenant_id, event_type, payload_json, occurred_at)
                VALUES ($outboxId, $tenantId, $eventType, $payloadJson, $occurredAt);
                INSERT INTO inbox_messages
                    (tenant_id, idempotency_key, message_hash, response_json, processed_at)
                VALUES
                    ($tenantId, $idempotencyKey, $messageHash, $responseJson, $occurredAt);
                """;
            Add(messaging, "$ledgerId", ledgerId);
            Add(messaging, "$tenantId", command.TenantId);
            Add(messaging, "$ledgerSequence", ledgerSequence);
            Add(messaging, "$previousHash", previousHash);
            Add(messaging, "$ledgerHash", ledgerHash);
            Add(messaging, "$eventType", eventType);
            Add(messaging, "$payloadJson", payload);
            Add(messaging, "$occurredAt", occurredAt);
            Add(messaging, "$outboxId", outboxId);
            Add(messaging, "$idempotencyKey", command.IdempotencyKey);
            Add(messaging, "$messageHash", commandHash);
            Add(messaging, "$responseJson", JsonSerializer.Serialize(receipt));
            await messaging.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<WorkChainCreateReceipt?> ReadInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkChainCreateCommand command,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var inbox = connection.CreateCommand();
        inbox.Transaction = transaction;
        inbox.CommandText =
            "SELECT message_hash, response_json FROM inbox_messages WHERE tenant_id = $tenantId AND idempotency_key = $key;";
        Add(inbox, "$tenantId", command.TenantId);
        Add(inbox, "$key", command.IdempotencyKey);
        await using var reader = await inbox.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), commandHash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different work-chain command.");
        }

        return JsonSerializer.Deserialize<WorkChainCreateReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted work-chain receipt is invalid.");
    }

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            "SELECT sequence, event_hash FROM audit_ledger WHERE tenant_id = $tenantId ORDER BY sequence DESC LIMIT 1;";
        Add(tail, "$tenantId", tenantId);
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1))
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task<WorkChainSnapshot?> ReadCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT s.tenant_id, s.project_id, s.id, s.content, d.id, t.id, t.state,
                   t.version, t.risk_tier, t.weight, i.id, i.version, i.content_hash,
                   (SELECT COUNT(*) FROM work_attempts a WHERE a.task_id = t.id),
                   (SELECT COUNT(*) FROM work_evidence e JOIN work_attempts a ON a.id = e.attempt_id
                    WHERE a.task_id = t.id),
                   (SELECT COUNT(*) FROM work_reviews r JOIN work_attempts a ON a.id = r.attempt_id
                    WHERE a.task_id = t.id)
            FROM solicitations s
            JOIN demands d ON d.solicitation_id = s.id
            JOIN work_tasks t ON t.demand_id = d.id
            JOIN instruction_versions i ON i.task_id = t.id
            WHERE s.tenant_id = $tenantId AND s.id = $solicitationId
            ORDER BY i.version DESC LIMIT 1;
            """;
        Add(query, "$tenantId", tenantId);
        Add(query, "$solicitationId", solicitationId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkChainSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
            reader.GetString(8), reader.GetDecimal(9), reader.GetString(10), reader.GetInt32(11),
            reader.GetString(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15));
    }

    private static void AddStateParameters(
        SqliteCommand command,
        WorkChainCreateCommand value,
        string occurredAt)
    {
        Add(command, "$tenantId", value.TenantId);
        Add(command, "$projectId", value.ProjectId);
        Add(command, "$userId", value.UserId);
        Add(command, "$solicitationId", value.SolicitationId);
        Add(command, "$solicitationContent", value.SolicitationContent);
        Add(command, "$demandId", value.DemandId);
        Add(command, "$demandTitle", value.DemandTitle);
        Add(command, "$acceptanceCriteriaJson", value.AcceptanceCriteriaJson);
        Add(command, "$taskId", value.TaskId);
        Add(command, "$taskTitle", value.TaskTitle);
        Add(command, "$riskTier", value.RiskTier);
        Add(command, "$weight", value.Weight);
        Add(command, "$instructionId", value.InstructionVersionId);
        Add(command, "$instructionContent", value.InstructionContent);
        Add(command, "$instructionHash", value.InstructionContentHash);
        Add(command, "$occurredAt", occurredAt);
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private static string ToStorage(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
