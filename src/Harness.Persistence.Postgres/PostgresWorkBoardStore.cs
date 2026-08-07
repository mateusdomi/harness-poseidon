using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkBoardStore(NpgsqlDataSource dataSource) : IWorkBoardStore
{
    private const string SolicitationSelect =
        "SELECT tenant_id,id,project_id,user_id,kind,title,content,state,supersedes_id,created_at,is_internal FROM harness.solicitations";
    private const string DemandSelect =
        "SELECT tenant_id,id,project_id,source_solicitation_id,title,description,state,priority,created_at,is_internal,phase_name FROM harness.demands";
    private const string TaskSelect =
        """
        SELECT t.tenant_id,t.id,t.project_id,t.source_demand_id,t.title,t.board_state,t.priority,
               t.assignee_agent_id,t.blocked_reason,
               (SELECT MAX(i.version) FROM harness.instruction_versions i WHERE i.task_id=t.id),
               t.created_at,t.updated_at,t.due_at,t.archived_at,t.version,
               d.solicitation_id,t.demand_id,t.state,t.phase_name,t.card_type,t.risk_tier
        FROM harness.work_tasks t JOIN harness.demands d ON d.id=t.demand_id
        """;
    private const string InstructionSelect =
        "SELECT i.tenant_id,i.id,i.task_id,i.version,i.content,i.author_kind,i.author_id,i.created_at FROM harness.instruction_versions i";
    private const string AttemptSelect =
        """
        SELECT a.tenant_id,a.id,a.task_id,a.attempt_number,a.state,a.producer_agent_id,a.started_at,
               a.completed_at,a.duration_ms,a.cost_usd,a.tokens_input,a.tokens_output,
               COALESCE((SELECT jsonb_agg(e.reference ORDER BY e.ordinal)
                         FROM harness.work_evidence e WHERE e.attempt_id=a.id),'[]'::jsonb)::text,
               a.summary,a.failure_reason,a.operational_state FROM harness.work_attempts a
        """;
    private const string AttemptEventSelect =
        "SELECT tenant_id,id,attempt_id,kind,content,occurred_at,severity FROM harness.attempt_events";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<BoardSolicitationRecord?> GetSolicitationAsync(
        string tenantId, string solicitationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSolicitationAsync(
            connection, null, tenantId, solicitationId, includeInternal: false, cancellationToken);
    }

    public async Task<IReadOnlyList<BoardSolicitationRecord>> ListSolicitationsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<BoardSolicitationRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{SolicitationSelect} WHERE tenant_id=$1 AND is_internal=false " +
            "AND ($2 IS NULL OR project_id=$2) AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(projectId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadSolicitation(reader));
        }

        return values;
    }

    public Task<BoardSolicitationRecord> CreateSolicitationAsync(
        BoardSolicitationCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateSolicitationCoreAsync(command, cancellationToken);
    }

    public async Task<BoardDemandRecord?> GetDemandAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadDemandAsync(
            connection, null, tenantId, demandId, includeInternal: false, cancellationToken);
    }

    public async Task<IReadOnlyList<BoardDemandRecord>> ListDemandsAsync(
        string tenantId, string? projectId, string? solicitationId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<BoardDemandRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{DemandSelect} WHERE tenant_id=$1 AND is_internal=false " +
            "AND ($2 IS NULL OR project_id=$2) AND ($3 IS NULL OR source_solicitation_id=$3) " +
            "AND ($4 IS NULL OR id>$4) ORDER BY id LIMIT $5;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(projectId));
        query.Parameters.Add(NullableText(solicitationId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadDemand(reader));
        }

        return values;
    }

    public Task<BoardDemandRecord> CreateDemandAsync(
        BoardDemandCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateDemandCoreAsync(command, cancellationToken);
    }

    public async Task<BoardTaskRecord?> GetTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadTaskAsync(connection, null, tenantId, taskId, cancellationToken);
    }

    public async Task<IReadOnlyList<BoardTaskRecord>> ListTasksAsync(
        string tenantId, string? projectId, string? demandId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<BoardTaskRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{TaskSelect} WHERE t.tenant_id=$1 AND ($2 IS NULL OR t.project_id=$2) " +
            "AND ($3 IS NULL OR t.source_demand_id=$3) AND ($4 IS NULL OR t.id>$4) " +
            "ORDER BY t.id LIMIT $5;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(projectId));
        query.Parameters.Add(NullableText(demandId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadTask(reader));
        }

        return values;
    }

    public async Task<BoardTaskPageRecord> PageTasksAsync(
        string tenantId, BoardTaskPageQuery query,
        CancellationToken cancellationToken = default)
    {
        const string filters =
            "t.tenant_id=$1 " +
            "AND ($2 IS NULL OR t.project_id=$2) " +
            "AND ($3 IS NULL OR t.source_demand_id=$3) " +
            "AND ($4 IS NULL OR t.title ILIKE $4 ESCAPE '\\' OR t.id ILIKE $4 ESCAPE '\\') " +
            "AND ($5 IS NULL OR t.board_state=$5) " +
            "AND ($6 IS NULL OR t.priority=$6) " +
            "AND ($7 IS NULL OR t.assignee_agent_id=$7) " +
            "AND ($8 IS NULL OR t.phase_name=$8) " +
            "AND ($9='all' OR ($9='active' AND t.archived_at IS NULL) " +
            "OR ($9='archived' AND t.archived_at IS NOT NULL)) " +
            "AND ($10 IS NULL OR t.updated_at >= $10)";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"SELECT COUNT(*) FROM harness.work_tasks t WHERE {filters};";
        AddTaskPageParameters(count, tenantId, query);
        var total = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var values = new List<BoardTaskRecord>();
        await using var pageCommand = connection.CreateCommand();
        pageCommand.Transaction = transaction;
        pageCommand.CommandText = $"{TaskSelect} WHERE {filters} " +
            "ORDER BY t.updated_at DESC,t.id DESC LIMIT $11 OFFSET $12;";
        AddTaskPageParameters(pageCommand, tenantId, query);
        pageCommand.Parameters.Add(Integer(query.Limit));
        pageCommand.Parameters.Add(Integer(query.Offset));
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(ReadTask(reader));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return new BoardTaskPageRecord(values, total);
    }

    public async Task<BoardTaskCreateResult> CreateTaskAsync(
        BoardTaskCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var outcome = await CreateTaskCoreAsync(command, null, null, cancellationToken);
        return new BoardTaskCreateResult(outcome.Task, outcome.Instruction!);
    }

    public async Task<BoardPlanCardResult> CreatePlanCardAsync(
        BoardPlanCardCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PlanSliceKey);
        var outcome = await CreateTaskCoreAsync(
            command.Task, command.PlanId, command.PlanSliceKey, cancellationToken);
        return new BoardPlanCardResult(outcome.Task, outcome.Created);
    }

    public async Task<IReadOnlyList<BoardPlanCardRecord>> ListPlanCardsAsync(
        string tenantId, string planId, CancellationToken cancellationToken = default)
    {
        var values = new List<BoardPlanCardRecord>();
        await using var query = _dataSource.CreateCommand(
            "SELECT plan_slice_key,id,title,board_state,state,created_at FROM harness.work_tasks " +
            "WHERE tenant_id=$1 AND plan_id=$2 AND plan_slice_key IS NOT NULL " +
            "ORDER BY plan_slice_key;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(planId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new BoardPlanCardRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return values;
    }

    public async Task<int> AdoptPlanCardsAsync(
        BoardPlanCardAdoptCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var adopted = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var slice in command.Slices)
        {
            // Um card por fatia, escolhido de forma determinística (menor id) e só quando o slot
            // ainda está livre: a adoção jamais pode violar a unicidade nem escolher ao acaso
            // entre duplicatas legadas.
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText =
                """
                UPDATE harness.work_tasks SET plan_id=$1,plan_slice_key=$2
                WHERE id = (
                    SELECT id FROM harness.work_tasks
                    WHERE tenant_id=$3 AND demand_id=$4 AND title=$5 AND plan_id IS NULL
                    ORDER BY id LIMIT 1)
                  AND NOT EXISTS (
                    SELECT 1 FROM harness.work_tasks
                    WHERE tenant_id=$3 AND plan_id=$1 AND plan_slice_key=$2);
                """;
            query.Parameters.Add(Text(command.PlanId));
            query.Parameters.Add(Text(slice.SliceKey));
            query.Parameters.Add(Text(command.TenantId));
            query.Parameters.Add(Text(command.DemandId));
            query.Parameters.Add(Text(slice.Title));
            adopted += await query.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return adopted;
    }

    public async Task<BoardInstructionRecord?> GetInstructionAsync(
        string tenantId, string instructionId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            $"{InstructionSelect} WHERE i.tenant_id=$1 AND i.id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(instructionId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInstruction(reader) : null;
    }

    public async Task<IReadOnlyList<BoardInstructionRecord>> ListInstructionsAsync(
        string tenantId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<BoardInstructionRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{InstructionSelect} WHERE i.tenant_id=$1 AND ($2 IS NULL OR i.task_id=$2) " +
            "AND ($3 IS NULL OR i.id>$3) ORDER BY i.id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(taskId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadInstruction(reader));
        }

        return values;
    }

    public async Task<BoardAttemptRecord?> GetAttemptAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            $"{AttemptSelect} WHERE a.tenant_id=$1 AND a.id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(attemptId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
    }

    public async Task<IReadOnlyList<BoardAttemptRecord>> ListAttemptsAsync(
        string tenantId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        // Paridade com o SQLite: sem cursor, o limite corta as tentativas mais ANTIGAS. Um card
        // com mais tentativas que o limite escondia justamente a que estava em execução, e quem
        // lê por aqui — colheita, reconciliação, circuito do card e replanejamento — passava a
        // decidir sobre um passado congelado. Com cursor, a ordem crescente permanece.
        var descending = afterId is null;
        var values = new List<BoardAttemptRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{AttemptSelect} WHERE a.tenant_id=$1 AND ($2 IS NULL OR a.task_id=$2) " +
            "AND ($3 IS NULL OR a.id>$3) " +
            (descending ? "ORDER BY a.id DESC LIMIT $4;" : "ORDER BY a.id LIMIT $4;"));
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(taskId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadAttempt(reader));
        }

        if (descending)
        {
            values.Reverse();
        }

        return values;
    }

    public async Task<IReadOnlyList<FeatureAttemptRow>> ListFeatureAttemptRowsAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        var values = new List<FeatureAttemptRow>();
        await using var query = _dataSource.CreateCommand(
            """
            SELECT a.task_id,t.title,a.attempt_number,a.state,a.operational_state,a.cost_usd,
                   a.tokens_input,a.tokens_output,a.duration_ms,a.failure_reason,i.content_hash,
                   a.id,a.producer_agent_id
            FROM harness.work_attempts a
            JOIN harness.work_tasks t ON t.tenant_id=a.tenant_id AND t.id=a.task_id
            JOIN harness.instruction_versions i
                ON i.tenant_id=a.tenant_id AND i.id=a.instruction_version_id
            WHERE a.tenant_id=$1 AND a.project_id=$2
            ORDER BY a.task_id,a.attempt_number;
            """);
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new FeatureAttemptRow(
                reader.GetString(0).TrimEnd(), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetDecimal(5), reader.GetInt64(6),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11).TrimEnd(), reader.GetString(12).TrimEnd()));
        }

        return values;
    }

    public async Task<BoardAttemptEventRecord?> GetAttemptEventAsync(
        string tenantId, string eventId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            $"{AttemptEventSelect} WHERE tenant_id=$1 AND id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(eventId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttemptEvent(reader) : null;
    }

    public async Task<IReadOnlyList<BoardAttemptEventRecord>> ListAttemptEventsAsync(
        string tenantId, string? attemptId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        var values = new List<BoardAttemptEventRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{AttemptEventSelect} WHERE tenant_id=$1 AND ($2 IS NULL OR attempt_id=$2) " +
            "AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(attemptId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadAttemptEvent(reader));
        }

        return values;
    }

    private async Task<BoardSolicitationRecord> CreateSolicitationCoreAsync(
        BoardSolicitationCreateCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProjectExistsAsync(
                connection, transaction, command.TenantId, command.ProjectId, cancellationToken))
        {
            throw new WorkBoardReferenceNotFoundException("project");
        }

        if (command.SupersedesId is not null && await ReadSolicitationAsync(
                connection, transaction, command.TenantId, command.SupersedesId,
                includeInternal: false, cancellationToken) is null)
        {
            throw new WorkBoardReferenceNotFoundException("supersedes");
        }

        await InsertSolicitationAsync(
            connection, transaction, command.TenantId, command.Id, command.ProjectId,
            command.AuthorProfileId, command.Kind, command.Title, command.Body,
            command.SupersedesId, internalRow: false, command.OccurredAt, cancellationToken);
        var record = new BoardSolicitationRecord(
            command.TenantId, command.Id, command.ProjectId, command.AuthorProfileId, command.Kind,
            command.Title, command.Body, "open", command.SupersedesId, command.OccurredAt, false);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = command.ProjectId,
            solicitationId = command.Id,
            state = "open",
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "solicitation.created", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    private async Task<BoardDemandRecord> CreateDemandCoreAsync(
        BoardDemandCreateCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProjectExistsAsync(
                connection, transaction, command.TenantId, command.ProjectId, cancellationToken))
        {
            throw new WorkBoardReferenceNotFoundException("project");
        }

        var backing = command.SolicitationId ?? command.BackingSolicitationId;
        if (command.SolicitationId is null)
        {
            await InsertSolicitationAsync(
                connection, transaction, command.TenantId, backing, command.ProjectId,
                command.AuthorProfileId, "request", command.Title, command.Description,
                supersedes: null, internalRow: true, command.OccurredAt, cancellationToken);
        }
        else if (await ReadSolicitationAsync(
                connection, transaction, command.TenantId, backing, includeInternal: false,
                cancellationToken) is null)
        {
            throw new WorkBoardReferenceNotFoundException("solicitation");
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.demands
                (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,created_at,
                 description,state,priority,source_solicitation_id,is_internal,phase_name)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'open',$9,$10,false,$11);
            """,
            cancellationToken,
            Text(command.Id), Text(command.TenantId), Text(command.ProjectId), Text(backing),
            Text(command.Title),
            Json(JsonSerializer.Serialize(new[] { command.Description }, JsonOptions)),
            Timestamp(command.OccurredAt), Text(command.Description), Text(command.Priority),
            NullableText(command.SolicitationId), NullableText(command.PhaseName));
        var record = new BoardDemandRecord(
            command.TenantId, command.Id, command.ProjectId, command.SolicitationId, command.Title,
            command.Description, "open", command.Priority, command.OccurredAt, false,
            command.PhaseName);
        var payload = DemandPayload(record);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "demand.created", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "demand.created", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    /// <summary>Card já existente da fatia (não criado) ou o card recém-criado com sua instrução.</summary>
    private sealed record TaskCreateOutcome(
        BoardTaskRecord Task, BoardInstructionRecord? Instruction, bool Created);

    private async Task<TaskCreateOutcome> CreateTaskCoreAsync(
        BoardTaskCreateCommand command, string? planId, string? sliceKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (planId is not null && sliceKey is not null)
        {
            // Fase 0A1: a fatia do plano é a identidade do card. Se ela já foi materializada, o
            // card existente é a resposta — retry, evento duplicado e consumidor concorrente
            // convergem para o MESMO card, e não para um board com trabalho repetido.
            var existing = await ReadPlanCardAsync(
                connection, transaction, command.TenantId, planId, sliceKey, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new TaskCreateOutcome(existing, null, Created: false);
            }
        }

        if (!await ProjectExistsAsync(
                connection, transaction, command.TenantId, command.ProjectId, cancellationToken))
        {
            throw new WorkBoardReferenceNotFoundException("project");
        }

        var backingDemand = command.DemandId ?? command.BackingDemandId;
        var phaseName = command.PhaseName;
        string backingSolicitation;
        if (command.DemandId is null)
        {
            backingSolicitation = command.BackingSolicitationId;
            await InsertSolicitationAsync(
                connection, transaction, command.TenantId, backingSolicitation, command.ProjectId,
                command.AuthorProfileId, "request", command.Title, command.InstructionBody,
                supersedes: null, internalRow: true, command.OccurredAt, cancellationToken);
            await InsertInternalDemandAsync(
                connection, transaction, command, backingSolicitation, cancellationToken);
        }
        else
        {
            var demand = await ReadDemandAsync(
                    connection, transaction, command.TenantId, backingDemand,
                    includeInternal: false, cancellationToken)
                ?? throw new WorkBoardReferenceNotFoundException("demand");
            if (demand.ProjectId != command.ProjectId)
            {
                throw new WorkBoardReferenceNotFoundException("demand");
            }

            phaseName ??= demand.PhaseName;

            backingSolicitation = await ReadBackingSolicitationIdAsync(
                connection, transaction, backingDemand, cancellationToken);
        }

        var cardType = string.IsNullOrWhiteSpace(command.CardType) ? "agent_task" : command.CardType;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.InstructionBody)));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.work_tasks
                (id,tenant_id,project_id,demand_id,title,risk_tier,weight,state,version,created_at,
                 updated_at,source_demand_id,board_state,priority,assignee_agent_id,due_at,phase_name,card_type,
                 plan_id,plan_slice_key)
            VALUES ($1,$2,$3,$4,$5,$6,1,'ready',1,$7,$7,$8,'backlog',$6,$9,$10,$11,$12,$13,$14);
            """,
            cancellationToken,
            Text(command.Id), Text(command.TenantId), Text(command.ProjectId), Text(backingDemand),
            Text(command.Title), Text(command.Priority), Timestamp(command.OccurredAt),
            NullableText(command.DemandId), NullableText(command.AssigneeAgentId),
            NullableTimestamp(command.DueAt), NullableText(phaseName), Text(cardType),
            NullableText(planId), NullableText(sliceKey));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.instruction_versions
                (id,tenant_id,project_id,task_id,version,content,content_hash,created_at,
                 author_kind,author_id)
            VALUES ($1,$2,$3,$4,1,$5,$6,$7,'chief',NULL);
            """,
            cancellationToken,
            Text(command.InstructionId), Text(command.TenantId), Text(command.ProjectId),
            Text(command.Id), Text(command.InstructionBody), Text(hash),
            Timestamp(command.OccurredAt));
        var task = new BoardTaskRecord(
            command.TenantId, command.Id, command.ProjectId, command.DemandId, command.Title,
            "backlog", command.Priority, command.AssigneeAgentId, null, 1,
            new BoardProgressRecord(0, 0, 0), command.OccurredAt, command.OccurredAt,
            command.DueAt, null, 1, "ready", backingSolicitation, backingDemand, phaseName, cardType,
            command.Priority);
        var instruction = new BoardInstructionRecord(
            command.TenantId, command.InstructionId, command.Id, 1, command.InstructionBody,
            "chief", null, command.OccurredAt);
        var payload = TaskPayload(task);
        await AppendAuditAsync(
            connection, transaction, command.TenantId, "task.created", payload,
            command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "task.created", payload,
            command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskCreateOutcome(task, instruction, Created: true);
    }

    private static async Task<BoardTaskRecord?> ReadPlanCardAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, string planId,
        string sliceKey, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{TaskSelect} WHERE t.tenant_id=$1 AND t.plan_id=$2 AND t.plan_slice_key=$3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(planId));
        query.Parameters.Add(Text(sliceKey));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTask(reader) : null;
    }

    private static async Task InsertSolicitationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant, string id,
        string project, string author, string kind, string title, string body, string? supersedes,
        bool internalRow, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.solicitations
                (id,tenant_id,project_id,user_id,content,created_at,kind,title,state,supersedes_id,is_internal)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'open',$9,$10);
            """,
            cancellationToken,
            Text(id), Text(tenant), Text(project), Text(author), Text(body), Timestamp(at),
            Text(kind), Text(title), NullableText(supersedes), Boolean(internalRow));
    }

    private static async Task InsertInternalDemandAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, BoardTaskCreateCommand command,
        string solicitationId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.demands
                (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,created_at,
                 description,state,priority,source_solicitation_id,is_internal,phase_name)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'open',$9,NULL,true,$10);
            """,
            cancellationToken,
            Text(command.BackingDemandId), Text(command.TenantId), Text(command.ProjectId),
            Text(solicitationId), Text(command.Title),
            Json(JsonSerializer.Serialize(new[] { command.InstructionBody }, JsonOptions)),
            Timestamp(command.OccurredAt), Text(command.InstructionBody), Text(command.Priority),
            NullableText(command.PhaseName));
    }

    private static async Task<string> ReadBackingSolicitationIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string demandId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT solicitation_id FROM harness.demands WHERE id=$1;";
        query.Parameters.Add(Text(demandId));
        var stored = await query.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new WorkBoardReferenceNotFoundException("demand");
        return stored.TrimEnd();
    }

    private static async Task<bool> ProjectExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant, string project,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL);";
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(project));
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return an existence flag."));
    }

    private static async Task<BoardSolicitationRecord?> ReadSolicitationAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenant, string id,
        bool includeInternal, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{SolicitationSelect} WHERE tenant_id=$1 AND id=$2 AND ($3 OR is_internal=false)" +
            (transaction is null ? ";" : " FOR UPDATE;");
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        query.Parameters.Add(Boolean(includeInternal));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSolicitation(reader) : null;
    }

    private static async Task<BoardDemandRecord?> ReadDemandAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenant, string id,
        bool includeInternal, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{DemandSelect} WHERE tenant_id=$1 AND id=$2 AND ($3 OR is_internal=false)" +
            (transaction is null ? ";" : " FOR UPDATE;");
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        query.Parameters.Add(Boolean(includeInternal));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDemand(reader) : null;
    }

    private static async Task<BoardTaskRecord?> ReadTaskAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenant, string id,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{TaskSelect} WHERE t.tenant_id=$1 AND t.id=$2" +
            (transaction is null ? ";" : " FOR UPDATE OF t;");
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTask(reader) : null;
    }

    private static BoardSolicitationRecord ReadSolicitation(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(), reader.GetString(3).TrimEnd(), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8).TrimEnd(),
        reader.GetFieldValue<DateTimeOffset>(9), reader.GetBoolean(10));

    private static BoardDemandRecord ReadDemand(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(),
        reader.IsDBNull(3) ? null : reader.GetString(3).TrimEnd(), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8), reader.GetBoolean(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static BoardTaskRecord ReadTask(NpgsqlDataReader reader)
    {
        var internalState = reader.GetString(17);
        var progress = internalState switch
        {
            "awaiting_review" => new BoardProgressRecord(100, 0, 0),
            "completed" => new BoardProgressRecord(100, 100, 100),
            _ => new BoardProgressRecord(0, 0, 0),
        };
        return new BoardTaskRecord(
            reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.IsDBNull(3) ? null : reader.GetString(3).TrimEnd(), reader.GetString(4),
            reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), progress,
            reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetInt64(14), internalState, reader.GetString(15).TrimEnd(),
            reader.GetString(16).TrimEnd(),
            reader.IsDBNull(18) ? null : reader.GetString(18), reader.GetString(19).TrimEnd(),
            reader.GetString(20).TrimEnd());
    }

    private static BoardInstructionRecord ReadInstruction(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(), reader.GetInt32(3), reader.GetString(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static BoardAttemptRecord ReadAttempt(NpgsqlDataReader reader)
    {
        var started = reader.GetFieldValue<DateTimeOffset>(6);
        DateTimeOffset? finished = reader.IsDBNull(7)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(7);
        var duration = reader.IsDBNull(8)
            ? finished is null ? null : (long?)(finished.Value - started).TotalMilliseconds
            : reader.GetInt64(8);
        var state = reader.GetString(15);
        return new BoardAttemptRecord(
            reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(), reader.GetInt32(3), state, reader.GetString(5),
            started, finished, duration, reader.GetDecimal(9), reader.GetInt64(10),
            reader.GetInt64(11),
            JsonSerializer.Deserialize<string[]>(reader.GetString(12), JsonOptions) ?? [],
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private static BoardAttemptEventRecord ReadAttemptEvent(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(), reader.GetString(3), reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6));

    private static string DemandPayload(BoardDemandRecord demand) => JsonSerializer.Serialize(new
    {
        projectId = demand.ProjectId,
        demand = new
        {
            demand.Id,
            demand.ProjectId,
            demand.SolicitationId,
            demand.Title,
            demand.Description,
            demand.State,
            demand.Priority,
            demand.CreatedAt,
            demand.PhaseName,
        },
    }, JsonOptions);

    private static string TaskPayload(BoardTaskRecord task) => JsonSerializer.Serialize(new
    {
        projectId = task.ProjectId,
        task = new
        {
            task.Id,
            task.ProjectId,
            task.DemandId,
            task.Title,
            task.State,
            task.Priority,
            task.AssigneeAgentId,
            task.BlockedReason,
            task.InstructionVersion,
            progress = new { task.Progress.Executed, task.Progress.Validated, task.Progress.Approved },
            task.CreatedAt,
            task.UpdatedAt,
            task.DueAt,
            task.ArchivedAt,
            task.PhaseName,
        },
    }, JsonOptions);

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant, string type,
        string payload, DateTimeOffset at, CancellationToken cancellationToken)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenant}"));
        var (sequence, previous) = await ReadLedgerTailAsync(
            connection, transaction, tenant, cancellationToken);
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """,
            cancellationToken,
            Text(UlidValue.New(at).ToString()), Text(tenant), Bigint(sequence), Text(previous),
            Text(hash), Text(type), Json(payload), Timestamp(at));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant, string type,
        string payload, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection, transaction,
            "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($1,$2,$3,$4,$5);",
            cancellationToken,
            Text(UlidValue.New(at).ToString()), Text(tenant), Text(type), Json(PersistenceSanitizer.SanitizeJson(payload)),
            Timestamp(at));

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;";
        tail.Parameters.Add(Text(tenant));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
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

    private static void AddTaskPageParameters(
        NpgsqlCommand command, string tenantId, BoardTaskPageQuery query)
    {
        command.Parameters.Add(Text(tenantId)); command.Parameters.Add(NullableText(query.ProjectId));
        command.Parameters.Add(NullableText(query.DemandId)); command.Parameters.Add(NullableText(query.Search));
        command.Parameters.Add(NullableText(query.State)); command.Parameters.Add(NullableText(query.Priority));
        command.Parameters.Add(NullableText(query.AssigneeAgentId)); command.Parameters.Add(NullableText(query.PhaseName));
        command.Parameters.Add(Text(query.Archive));
        command.Parameters.Add(NullableTimestamp(query.UpdatedSince));
    }
}
