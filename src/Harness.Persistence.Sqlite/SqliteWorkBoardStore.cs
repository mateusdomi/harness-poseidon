using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkBoardStore(SqliteWriteDispatcher dispatcher) : IWorkBoardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<BoardSolicitationRecord?> GetSolicitationAsync(
        string tenantId, string solicitationId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (c, t) => ReadSolicitationAsync(c, null, tenantId, solicitationId, false, t),
            cancellationToken);

    public Task<IReadOnlyList<BoardSolicitationRecord>> ListSolicitationsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<BoardSolicitationRecord>>(async (c, t) =>
        {
            var values = new List<BoardSolicitationRecord>();
            await using var q = c.CreateCommand();
            q.CommandText = $"{SolicitationSelect} WHERE tenant_id=$tenant AND is_internal=0 " +
                "AND ($project IS NULL OR project_id=$project) " +
                "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$project", projectId);
            AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) values.Add(ReadSolicitation(r));
            return values;
        }, cancellationToken);

    public Task<BoardSolicitationRecord> CreateSolicitationAsync(
        BoardSolicitationCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (c, t) => CreateSolicitationCoreAsync(c, command, t), cancellationToken);

    public Task<BoardDemandRecord?> GetDemandAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (c, t) => ReadDemandAsync(c, null, tenantId, demandId, false, t), cancellationToken);

    public Task<IReadOnlyList<BoardDemandRecord>> ListDemandsAsync(
        string tenantId, string? projectId, string? solicitationId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<BoardDemandRecord>>(async (c, t) =>
        {
            var values = new List<BoardDemandRecord>();
            await using var q = c.CreateCommand();
            q.CommandText = $"{DemandSelect} WHERE tenant_id=$tenant AND is_internal=0 " +
                "AND ($project IS NULL OR project_id=$project) " +
                "AND ($solicitation IS NULL OR source_solicitation_id=$solicitation) " +
                "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$project", projectId);
            AddNullable(q, "$solicitation", solicitationId); AddNullable(q, "$after", afterId);
            Add(q, "$limit", limit); await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) values.Add(ReadDemand(r));
            return values;
        }, cancellationToken);

    public Task<BoardDemandRecord> CreateDemandAsync(
        BoardDemandCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => CreateDemandCoreAsync(c, command, t), cancellationToken);

    public Task<BoardTaskRecord?> GetTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => ReadTaskAsync(c, null, tenantId, taskId, t), cancellationToken);

    public Task<IReadOnlyList<BoardTaskRecord>> ListTasksAsync(
        string tenantId, string? projectId, string? demandId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<BoardTaskRecord>>(async (c, t) =>
        {
            var values = new List<BoardTaskRecord>(); await using var q = c.CreateCommand();
            q.CommandText = $"{TaskSelect} WHERE t.tenant_id=$tenant " +
                "AND ($project IS NULL OR t.project_id=$project) " +
                "AND ($demand IS NULL OR t.source_demand_id=$demand) " +
                "AND ($after IS NULL OR t.id>$after) ORDER BY t.id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$project", projectId);
            AddNullable(q, "$demand", demandId); AddNullable(q, "$after", afterId);
            Add(q, "$limit", limit); await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) values.Add(ReadTask(r)); return values;
        }, cancellationToken);

    public Task<BoardTaskPageRecord> PageTasksAsync(
        string tenantId, BoardTaskPageQuery query,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            const string filters =
                "t.tenant_id=$tenant " +
                "AND ($project IS NULL OR t.project_id=$project) " +
                "AND ($demand IS NULL OR t.source_demand_id=$demand) " +
                "AND ($search IS NULL OR lower(t.title) LIKE $search ESCAPE '\\' OR lower(t.id) LIKE $search ESCAPE '\\') " +
                "AND ($state IS NULL OR t.board_state=$state) " +
                "AND ($priority IS NULL OR t.priority=$priority) " +
                "AND ($agent IS NULL OR t.assignee_agent_id=$agent) " +
                "AND ($phase IS NULL OR t.phase_name=$phase) " +
                "AND ($archive='all' OR ($archive='active' AND t.archived_at IS NULL) " +
                "OR ($archive='archived' AND t.archived_at IS NOT NULL)) " +
                "AND ($since IS NULL OR t.updated_at >= $since)";

            await using var count = c.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM work_tasks t WHERE {filters};";
            AddTaskPageParameters(count, tenantId, query);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(t), CultureInfo.InvariantCulture);

            var values = new List<BoardTaskRecord>();
            await using var page = c.CreateCommand();
            page.CommandText = $"{TaskSelect} WHERE {filters} " +
                "ORDER BY t.updated_at DESC,t.id DESC LIMIT $limit OFFSET $offset;";
            AddTaskPageParameters(page, tenantId, query);
            Add(page, "$limit", query.Limit); Add(page, "$offset", query.Offset);
            await using var reader = await page.ExecuteReaderAsync(t);
            while (await reader.ReadAsync(t)) values.Add(ReadTask(reader));
            return new BoardTaskPageRecord(values, total);
        }, cancellationToken);

    public Task<BoardTaskCreateResult> CreateTaskAsync(
        BoardTaskCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => CreateTaskCoreAsync(c, command, t), cancellationToken);

    public Task<BoardInstructionRecord?> GetInstructionAsync(
        string tenantId, string instructionId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand(); q.CommandText =
                $"{InstructionSelect} WHERE i.tenant_id=$tenant AND i.id=$id;";
            Add(q, "$tenant", tenantId); Add(q, "$id", instructionId);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadInstruction(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<BoardInstructionRecord>> ListInstructionsAsync(
        string tenantId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<BoardInstructionRecord>>(async (c, t) =>
        {
            var values = new List<BoardInstructionRecord>(); await using var q = c.CreateCommand();
            q.CommandText = $"{InstructionSelect} WHERE i.tenant_id=$tenant " +
                "AND ($task IS NULL OR i.task_id=$task) AND ($after IS NULL OR i.id>$after) " +
                "ORDER BY i.id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$task", taskId);
            AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) values.Add(ReadInstruction(r)); return values;
        }, cancellationToken);

    public Task<BoardAttemptRecord?> GetAttemptAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand(); q.CommandText =
                $"{AttemptSelect} WHERE a.tenant_id=$tenant AND a.id=$id;";
            Add(q, "$tenant", tenantId); Add(q, "$id", attemptId);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadAttempt(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<BoardAttemptRecord>> ListAttemptsAsync(
        string tenantId, string? taskId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<BoardAttemptRecord>>(async (c, t) =>
        {
            var values = new List<BoardAttemptRecord>(); await using var q = c.CreateCommand();
            q.CommandText = $"{AttemptSelect} WHERE a.tenant_id=$tenant " +
                "AND ($task IS NULL OR a.task_id=$task) AND ($after IS NULL OR a.id>$after) " +
                "ORDER BY a.id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$task", taskId);
            AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) values.Add(ReadAttempt(r)); return values;
        }, cancellationToken);

    public Task<BoardAttemptEventRecord?> GetAttemptEventAsync(
        string tenantId, string eventId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand(); q.CommandText =
                $"{AttemptEventSelect} WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId); Add(q, "$id", eventId);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadAttemptEvent(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<BoardAttemptEventRecord>> ListAttemptEventsAsync(
        string tenantId, string? attemptId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<BoardAttemptEventRecord>>(async (c, t) =>
        {
            var values = new List<BoardAttemptEventRecord>(); await using var q = c.CreateCommand();
            q.CommandText = $"{AttemptEventSelect} WHERE tenant_id=$tenant " +
                "AND ($attempt IS NULL OR attempt_id=$attempt) AND ($after IS NULL OR id>$after) " +
                "ORDER BY id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$attempt", attemptId);
            AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) values.Add(ReadAttemptEvent(r)); return values;
        }, cancellationToken);

    private static async Task<BoardSolicitationRecord> CreateSolicitationCoreAsync(
        SqliteConnection c, BoardSolicitationCreateCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        if (!await ProjectExistsAsync(c, tx, command.TenantId, command.ProjectId, token))
            throw new WorkBoardReferenceNotFoundException("project");
        if (command.SupersedesId is not null && await ReadSolicitationAsync(
                c, tx, command.TenantId, command.SupersedesId, false, token) is null)
            throw new WorkBoardReferenceNotFoundException("supersedes");
        await InsertSolicitationAsync(c, tx, command.TenantId, command.Id, command.ProjectId,
            command.AuthorProfileId, command.Kind, command.Title, command.Body,
            command.SupersedesId, false, command.OccurredAt, token);
        var record = new BoardSolicitationRecord(command.TenantId, command.Id, command.ProjectId,
            command.AuthorProfileId, command.Kind, command.Title, command.Body, "open",
            command.SupersedesId, command.OccurredAt, false);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = command.ProjectId,
            solicitationId = command.Id,
            state = "open",
        }, JsonOptions);
        await AppendAuditAsync(c, tx, command.TenantId, "solicitation.created", payload,
            command.OccurredAt, token); await tx.CommitAsync(token); return record;
    }

    private static async Task<BoardDemandRecord> CreateDemandCoreAsync(
        SqliteConnection c, BoardDemandCreateCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        if (!await ProjectExistsAsync(c, tx, command.TenantId, command.ProjectId, token))
            throw new WorkBoardReferenceNotFoundException("project");
        var backing = command.SolicitationId ?? command.BackingSolicitationId;
        if (command.SolicitationId is null)
            await InsertSolicitationAsync(c, tx, command.TenantId, backing, command.ProjectId,
                command.AuthorProfileId, "request", command.Title, command.Description,
                null, true, command.OccurredAt, token);
        else if (await ReadSolicitationAsync(c, tx, command.TenantId, backing, false, token) is null)
            throw new WorkBoardReferenceNotFoundException("solicitation");
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            """
            INSERT INTO demands
                (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,created_at,
                 description,state,priority,source_solicitation_id,is_internal,phase_name)
            VALUES ($id,$tenant,$project,$backing,$title,$criteria,$at,$description,'open',$priority,
                    $source,0,$phase);
            """;
        Add(q, "$id", command.Id); Add(q, "$tenant", command.TenantId);
        Add(q, "$project", command.ProjectId); Add(q, "$backing", backing);
        Add(q, "$title", command.Title); Add(q, "$criteria", JsonSerializer.Serialize(
            new[] { command.Description }, JsonOptions)); Add(q, "$at", Store(command.OccurredAt));
        Add(q, "$description", command.Description); Add(q, "$priority", command.Priority);
        AddNullable(q, "$source", command.SolicitationId); AddNullable(q, "$phase", command.PhaseName);
        await q.ExecuteNonQueryAsync(token);
        var record = new BoardDemandRecord(command.TenantId, command.Id, command.ProjectId,
            command.SolicitationId, command.Title, command.Description, "open", command.Priority,
            command.OccurredAt, false, command.PhaseName); var payload = DemandPayload(record);
        await AppendAuditAsync(c, tx, command.TenantId, "demand.created", payload,
            command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "demand.created", payload,
            command.OccurredAt, token); await tx.CommitAsync(token); return record;
    }

    private static async Task<BoardTaskCreateResult> CreateTaskCoreAsync(
        SqliteConnection c, BoardTaskCreateCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        if (!await ProjectExistsAsync(c, tx, command.TenantId, command.ProjectId, token))
            throw new WorkBoardReferenceNotFoundException("project");
        var backingDemand = command.DemandId ?? command.BackingDemandId;
        var phaseName = command.PhaseName;
        string backingSolicitation;
        if (command.DemandId is null)
        {
            backingSolicitation = command.BackingSolicitationId;
            await InsertSolicitationAsync(c, tx, command.TenantId, backingSolicitation,
                command.ProjectId, command.AuthorProfileId, "request", command.Title,
                command.InstructionBody, null, true, command.OccurredAt, token);
            await InsertInternalDemandAsync(c, tx, command, backingSolicitation, token);
        }
        else
        {
            var demand = await ReadDemandAsync(c, tx, command.TenantId, backingDemand, false, token)
                ?? throw new WorkBoardReferenceNotFoundException("demand");
            if (demand.ProjectId != command.ProjectId)
                throw new WorkBoardReferenceNotFoundException("demand");
            phaseName ??= demand.PhaseName;
            backingSolicitation = await ReadBackingSolicitationIdAsync(c, tx, backingDemand, token);
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.InstructionBody)));
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            """
            INSERT INTO work_tasks
                (id,tenant_id,project_id,demand_id,title,risk_tier,weight,state,version,created_at,
                 updated_at,source_demand_id,board_state,priority,assignee_agent_id,due_at,phase_name)
            VALUES ($id,$tenant,$project,$backing,$title,$priority,1,'ready',1,$at,$at,$source,
                    'backlog',$priority,$assignee,$due,$phase);
            INSERT INTO instruction_versions
                (id,tenant_id,project_id,task_id,version,content,content_hash,created_at,
                 author_kind,author_id)
            VALUES ($instruction,$tenant,$project,$id,1,$body,$hash,$at,'chief',NULL);
            """;
        Add(q, "$id", command.Id); Add(q, "$tenant", command.TenantId);
        Add(q, "$project", command.ProjectId); Add(q, "$backing", backingDemand);
        Add(q, "$title", command.Title); Add(q, "$priority", command.Priority);
        Add(q, "$at", Store(command.OccurredAt)); AddNullable(q, "$source", command.DemandId);
        AddNullable(q, "$assignee", command.AssigneeAgentId);
        AddNullable(q, "$due", command.DueAt is null ? null : Store(command.DueAt.Value));
        AddNullable(q, "$phase", phaseName);
        Add(q, "$instruction", command.InstructionId); Add(q, "$body", command.InstructionBody);
        Add(q, "$hash", hash); await q.ExecuteNonQueryAsync(token);
        var task = new BoardTaskRecord(command.TenantId, command.Id, command.ProjectId,
            command.DemandId, command.Title, "backlog", command.Priority,
            command.AssigneeAgentId, null, 1, new BoardProgressRecord(0, 0, 0),
            command.OccurredAt, command.OccurredAt, command.DueAt, null, 1, "ready",
            backingSolicitation, backingDemand, phaseName);
        var instruction = new BoardInstructionRecord(command.TenantId, command.InstructionId,
            command.Id, 1, command.InstructionBody, "chief", null, command.OccurredAt);
        var payload = TaskPayload(task); await AppendAuditAsync(c, tx, command.TenantId,
            "task.created", payload, command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "task.created", payload,
            command.OccurredAt, token); await tx.CommitAsync(token);
        return new BoardTaskCreateResult(task, instruction);
    }

    private static async Task InsertSolicitationAsync(
        SqliteConnection c, SqliteTransaction tx, string tenant, string id, string project,
        string author, string kind, string title, string body, string? supersedes, bool internalRow,
        DateTimeOffset at, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            """
            INSERT INTO solicitations
                (id,tenant_id,project_id,user_id,content,created_at,kind,title,state,supersedes_id,is_internal)
            VALUES ($id,$tenant,$project,$author,$body,$at,$kind,$title,'open',$supersedes,$internal);
            """;
        Add(q, "$id", id); Add(q, "$tenant", tenant); Add(q, "$project", project);
        Add(q, "$author", author); Add(q, "$body", body); Add(q, "$at", Store(at));
        Add(q, "$kind", kind); Add(q, "$title", title); AddNullable(q, "$supersedes", supersedes);
        Add(q, "$internal", internalRow ? 1 : 0); await q.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertInternalDemandAsync(
        SqliteConnection c, SqliteTransaction tx, BoardTaskCreateCommand command,
        string solicitationId, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            """
            INSERT INTO demands
                (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,created_at,
                 description,state,priority,source_solicitation_id,is_internal,phase_name)
            VALUES ($id,$tenant,$project,$solicitation,$title,$criteria,$at,$description,'open',
                    $priority,NULL,1,$phase);
            """;
        Add(q, "$id", command.BackingDemandId); Add(q, "$tenant", command.TenantId);
        Add(q, "$project", command.ProjectId); Add(q, "$solicitation", solicitationId);
        Add(q, "$title", command.Title); Add(q, "$criteria", JsonSerializer.Serialize(
            new[] { command.InstructionBody }, JsonOptions)); Add(q, "$at", Store(command.OccurredAt));
        Add(q, "$description", command.InstructionBody); Add(q, "$priority", command.Priority);
        AddNullable(q, "$phase", command.PhaseName);
        await q.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> ReadBackingSolicitationIdAsync(
        SqliteConnection c, SqliteTransaction tx, string demandId, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT solicitation_id FROM demands WHERE id=$id;"; Add(q, "$id", demandId);
        return (string)(await q.ExecuteScalarAsync(token)
            ?? throw new WorkBoardReferenceNotFoundException("demand"));
    }

    private static async Task<bool> ProjectExistsAsync(SqliteConnection c, SqliteTransaction tx,
        string tenant, string project, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            "SELECT EXISTS(SELECT 1 FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL);";
        Add(q, "$tenant", tenant); Add(q, "$project", project);
        return Convert.ToInt64(await q.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<BoardSolicitationRecord?> ReadSolicitationAsync(SqliteConnection c,
        SqliteTransaction? tx, string tenant, string id, bool includeInternal, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            $"{SolicitationSelect} WHERE tenant_id=$tenant AND id=$id " +
            "AND ($include=1 OR is_internal=0);";
        Add(q, "$tenant", tenant); Add(q, "$id", id); Add(q, "$include", includeInternal ? 1 : 0);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadSolicitation(r) : null;
    }

    private static async Task<BoardDemandRecord?> ReadDemandAsync(SqliteConnection c,
        SqliteTransaction? tx, string tenant, string id, bool includeInternal, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            $"{DemandSelect} WHERE tenant_id=$tenant AND id=$id AND ($include=1 OR is_internal=0);";
        Add(q, "$tenant", tenant); Add(q, "$id", id); Add(q, "$include", includeInternal ? 1 : 0);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadDemand(r) : null;
    }

    private static async Task<BoardTaskRecord?> ReadTaskAsync(SqliteConnection c,
        SqliteTransaction? tx, string tenant, string id, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            $"{TaskSelect} WHERE t.tenant_id=$tenant AND t.id=$id;";
        Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadTask(r) : null;
    }

    private static BoardSolicitationRecord ReadSolicitation(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        Parse(r.GetString(9)), r.GetInt32(10) == 1);
    private static BoardDemandRecord ReadDemand(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), Parse(r.GetString(8)),
        r.GetInt32(9) == 1, r.IsDBNull(10) ? null : r.GetString(10));
    private static BoardTaskRecord ReadTask(SqliteDataReader r)
    {
        var internalState = r.GetString(17); var progress = internalState switch
        {
            "awaiting_review" => new BoardProgressRecord(100, 0, 0),
            "completed" => new BoardProgressRecord(100, 100, 100),
            _ => new BoardProgressRecord(0, 0, 0),
        };
        return new BoardTaskRecord(r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8), r.GetInt32(9), progress,
            Parse(r.GetString(10)), Parse(r.GetString(11)),
            r.IsDBNull(12) ? null : Parse(r.GetString(12)),
            r.IsDBNull(13) ? null : Parse(r.GetString(13)), r.GetInt64(14),
            internalState, r.GetString(15), r.GetString(16),
            r.IsDBNull(18) ? null : r.GetString(18));
    }
    private static BoardInstructionRecord ReadInstruction(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), Parse(r.GetString(7)));
    private static BoardAttemptRecord ReadAttempt(SqliteDataReader r)
    {
        var started = Parse(r.GetString(6));
        DateTimeOffset? finished = r.IsDBNull(7) ? null : Parse(r.GetString(7));
        var duration = r.IsDBNull(8) ? finished is null ? null : (long?)(finished.Value - started).TotalMilliseconds : r.GetInt64(8);
        var state = r.GetString(15);
        return new BoardAttemptRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
            state, r.GetString(5), started, finished, duration, r.GetDecimal(9), r.GetInt64(10),
            r.GetInt64(11), JsonSerializer.Deserialize<string[]>(r.GetString(12), JsonOptions) ?? [],
            r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14));
    }
    private static BoardAttemptEventRecord ReadAttemptEvent(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        Parse(r.GetString(5)));

    private static string DemandPayload(BoardDemandRecord d) => JsonSerializer.Serialize(new
    {
        projectId = d.ProjectId,
        demand = new { d.Id, d.ProjectId, d.SolicitationId, d.Title, d.Description, d.State, d.Priority, d.CreatedAt, d.PhaseName },
    }, JsonOptions);
    private static string TaskPayload(BoardTaskRecord t) => JsonSerializer.Serialize(new
    {
        projectId = t.ProjectId,
        task = new
        {
            t.Id,
            t.ProjectId,
            t.DemandId,
            t.Title,
            t.State,
            t.Priority,
            t.AssigneeAgentId,
            t.BlockedReason,
            t.InstructionVersion,
            progress = new { t.Progress.Executed, t.Progress.Validated, t.Progress.Approved },
            t.CreatedAt,
            t.UpdatedAt,
            t.DueAt,
            t.ArchivedAt,
            t.PhaseName,
        },
    }, JsonOptions);

    private static async Task AppendAuditAsync(SqliteConnection c, SqliteTransaction tx,
        string tenant, string type, string payload, DateTimeOffset at, CancellationToken token)
    {
        await using var tail = c.CreateCommand(); tail.Transaction = tx; tail.CommandText =
            "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
        Add(tail, "$tenant", tenant); await using var r = await tail.ExecuteReaderAsync(token);
        var exists = await r.ReadAsync(token); var sequence = exists ? r.GetInt64(0) + 1 : 1;
        var previous = exists ? r.GetString(1) : AuditLedgerHash.Genesis; await r.DisposeAsync();
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            """
            INSERT INTO audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);
            """;
        Add(q, "$id", UlidValue.New(at).ToString()); Add(q, "$tenant", tenant);
        Add(q, "$sequence", sequence); Add(q, "$previous", previous); Add(q, "$hash", hash);
        Add(q, "$type", type); Add(q, "$payload", payload); Add(q, "$at", Store(at));
        await q.ExecuteNonQueryAsync(token);
    }

    private static async Task AppendOutboxAsync(SqliteConnection c, SqliteTransaction tx,
        string tenant, string type, string payload, DateTimeOffset at, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
            "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,$type,$payload,$at);";
        Add(q, "$id", UlidValue.New(at).ToString()); Add(q, "$tenant", tenant);
        Add(q, "$type", type); Add(q, "$payload", payload); Add(q, "$at", Store(at));
        await q.ExecuteNonQueryAsync(token);
    }

    private const string SolicitationSelect =
        "SELECT tenant_id,id,project_id,user_id,kind,title,content,state,supersedes_id,created_at,is_internal FROM solicitations";
    private const string DemandSelect =
        "SELECT tenant_id,id,project_id,source_solicitation_id,title,description,state,priority,created_at,is_internal,phase_name FROM demands";
    private const string TaskSelect =
        """
        SELECT t.tenant_id,t.id,t.project_id,t.source_demand_id,t.title,t.board_state,t.priority,
               t.assignee_agent_id,t.blocked_reason,
               (SELECT MAX(version) FROM instruction_versions i WHERE i.task_id=t.id),
               t.created_at,t.updated_at,t.due_at,t.archived_at,t.version,
               d.solicitation_id,t.demand_id,t.state,t.phase_name
        FROM work_tasks t JOIN demands d ON d.id=t.demand_id
        """;
    private const string InstructionSelect =
        "SELECT i.tenant_id,i.id,i.task_id,i.version,i.content,i.author_kind,i.author_id,i.created_at FROM instruction_versions i";
    private const string AttemptSelect =
        """
        SELECT a.tenant_id,a.id,a.task_id,a.attempt_number,a.state,a.producer_agent_id,a.started_at,
               a.completed_at,a.duration_ms,a.cost_usd,a.tokens_input,a.tokens_output,
               COALESCE((SELECT json_group_array(reference) FROM work_evidence e WHERE e.attempt_id=a.id),'[]'),
               a.summary,a.failure_reason,a.operational_state FROM work_attempts a
        """;
    private const string AttemptEventSelect =
        "SELECT tenant_id,id,attempt_id,kind,content,occurred_at FROM attempt_events";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void AddTaskPageParameters(
        SqliteCommand command, string tenantId, BoardTaskPageQuery query)
    {
        Add(command, "$tenant", tenantId); AddNullable(command, "$project", query.ProjectId);
        AddNullable(command, "$demand", query.DemandId); AddNullable(command, "$search", query.Search);
        AddNullable(command, "$state", query.State); AddNullable(command, "$priority", query.Priority);
        AddNullable(command, "$agent", query.AssigneeAgentId); Add(command, "$archive", query.Archive);
        AddNullable(command, "$phase", query.PhaseName);
        AddNullable(command, "$since", query.UpdatedSince is null ? null : Store(query.UpdatedSince.Value));
    }
    private static void Add(SqliteCommand q, string name, object value) => q.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand q, string name, object? value) => q.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
