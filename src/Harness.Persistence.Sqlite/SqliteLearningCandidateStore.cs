using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteLearningCandidateStore(SqliteWriteDispatcher dispatcher) : ILearningCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<(LearningCandidateRecord Candidate, bool Deduplicated)> CreateAsync(
        LearningCandidateCreateCommand command, CancellationToken cancellationToken = default)
    {
        ValidateCreate(command);
        return _dispatcher.ExecuteAsync((connection, token) => CreateCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<LearningCandidateRecord> TransitionAsync(
        LearningCandidateTransitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync((connection, token) => TransitionCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<LearningCandidateRecord?> GetAsync(
        string tenantId, string candidateId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ReadAsync(connection, null, tenantId, candidateId, token), cancellationToken);

    public Task<LearningCandidatePage> ListAsync(
        string tenantId, string? organizationId, string? projectId, LearningCandidateType? type,
        LearningCandidateState? state, string? cursor, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ListCoreAsync(
            connection, tenantId, organizationId, projectId, type, state, cursor, limit, token), cancellationToken);

    public Task<IReadOnlyList<LearningCandidateHistoryRecord>> ListHistoryAsync(
        string tenantId, string candidateId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<LearningCandidateHistoryRecord>>(
            (connection, token) => ListHistoryCoreAsync(connection, tenantId, candidateId, token), cancellationToken);

    public Task<LearningCandidateMetrics> GetMetricsAsync(
        string tenantId, string? organizationId, string? projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => GetMetricsCoreAsync(
            connection, tenantId, organizationId, projectId, token), cancellationToken);

    private static async Task<(LearningCandidateRecord Candidate, bool Deduplicated)> CreateCoreAsync(
        SqliteConnection connection, LearningCandidateCreateCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var replay = await ReadInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash, token);
        if (replay is not null)
        {
            var candidate = await ReadAsync(connection, tx, command.TenantId, replay.Value.CandidateId, token)
                ?? throw new InvalidOperationException("Learning inbox references a missing candidate.");
            await tx.CommitAsync(token);
            return (candidate, replay.Value.Deduplicated);
        }

        var existing = await ReadByFingerprintAsync(connection, tx, command, token);
        if (existing is not null)
        {
            await AppendMetricAsync(connection, tx, existing, "deduplicated", command.OccurredAt, token);
            await AppendInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey,
                command.PayloadHash, existing.CandidateId, true, command.OccurredAt, token);
            await tx.CommitAsync(token);
            return (existing, true);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO learning_candidates
                (tenant_id,organization_id,project_id,candidate_id,type,state,fingerprint,observation,
                 evidence_json,payload_json,actor_agent_id,actor_provider,actor_model,baseline_version,
                 proposed_version,created_at,updated_at,version)
                VALUES ($tenant,$organization,$project,$candidate,$type,'candidate',$fingerprint,$observation,
                        $evidence,$payload,$actor,$provider,$model,$baseline,$proposed,$at,$at,1);
                """;
            Add(insert, "$tenant", command.TenantId); Add(insert, "$organization", command.OrganizationId);
            Add(insert, "$project", command.ProjectId); Add(insert, "$candidate", command.CandidateId);
            Add(insert, "$type", Type(command.Type)); Add(insert, "$fingerprint", command.Fingerprint);
            Add(insert, "$observation", command.Observation);
            Add(insert, "$evidence", JsonSerializer.Serialize(command.Evidence, JsonOptions));
            Add(insert, "$payload", JsonSerializer.Serialize(command.Payload, JsonOptions));
            Add(insert, "$actor", command.ActorAgentId); Add(insert, "$provider", command.ActorProvider);
            Add(insert, "$model", command.ActorModel); Add(insert, "$baseline", command.BaselineVersion);
            Add(insert, "$proposed", command.ProposedVersion); Add(insert, "$at", Store(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(token);
        }
        var created = await ReadAsync(connection, tx, command.TenantId, command.CandidateId, token)
            ?? throw new InvalidOperationException("Learning candidate insert did not produce a row.");
        await AppendHistoryAsync(connection, tx, created, created.State, null, command.ActorAgentId,
            "candidate_created", command.OccurredAt, token);
        await AppendMetricAsync(connection, tx, created, "created", command.OccurredAt, token);
        await AppendAuditAndOutboxAsync(connection, tx, created, "learning.candidateCreated",
            "agent", command.ActorAgentId, "Candidate created from observation and evidence.", command.OccurredAt, token);
        await AppendInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey,
            command.PayloadHash, created.CandidateId, false, command.OccurredAt, token);
        await tx.CommitAsync(token);
        return (created, false);
    }

    private static async Task<LearningCandidateRecord> TransitionCoreAsync(
        SqliteConnection connection, LearningCandidateTransitionCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var replay = await ReadInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash, token);
        if (replay is not null)
        {
            var replayed = await ReadAsync(connection, tx, command.TenantId, replay.Value.CandidateId, token)
                ?? throw new InvalidOperationException("Learning inbox references a missing candidate.");
            await tx.CommitAsync(token);
            return replayed;
        }
        var current = await ReadAsync(connection, tx, command.TenantId, command.CandidateId, token)
            ?? throw new KeyNotFoundException("Learning candidate does not exist.");
        var next = LearningCandidatePolicy.Next(current, command);
        var active = current.ActiveVersion;
        var previous = current.PreviousVersion;
        if (command.Action == LearningCandidateAction.Promote)
        {
            previous = current.BaselineVersion;
            active = current.ProposedVersion;
        }
        else if (command.Action == LearningCandidateAction.Rollback)
        {
            active = current.PreviousVersion;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE learning_candidates SET state=$state,evaluator_agent_id=COALESCE($evaluator,evaluator_agent_id),
                    evaluator_provider=COALESCE($evaluationProvider,evaluator_provider),
                    evaluator_model=COALESCE($evaluationModel,evaluator_model),
                    evaluation_verdict=COALESCE($verdict,evaluation_verdict),
                    shadow_result_json=COALESCE($shadow,shadow_result_json),
                    reviewer_profile_id=$reviewer,decision_note=$note,active_version=$active,
                    previous_version=$previous,updated_at=$at,version=version+1
                WHERE tenant_id=$tenant AND candidate_id=$candidate AND version=$version;
                """;
            Add(update, "$state", State(next)); Add(update, "$evaluator", command.EvaluatorAgentId);
            Add(update, "$evaluationProvider", command.EvaluatorProvider); Add(update, "$evaluationModel", command.EvaluatorModel);
            Add(update, "$verdict", command.EvaluationVerdict);
            Add(update, "$shadow", command.ShadowResult is null ? null : JsonSerializer.Serialize(command.ShadowResult, JsonOptions));
            Add(update, "$reviewer", command.ActorProfileId); Add(update, "$note", command.Note);
            Add(update, "$active", active); Add(update, "$previous", previous); Add(update, "$at", Store(command.OccurredAt));
            Add(update, "$tenant", command.TenantId); Add(update, "$candidate", command.CandidateId);
            Add(update, "$version", command.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new LearningCandidateConflictException("The candidate changed concurrently.");
        }
        var updated = await ReadAsync(connection, tx, command.TenantId, command.CandidateId, token)!;
        await AppendHistoryAsync(connection, tx, updated!, current.State, command.Action,
            command.ActorProfileId, command.Note, command.OccurredAt, token);
        var metric = command.Action switch
        {
            LearningCandidateAction.Reject => "rejected",
            LearningCandidateAction.Approve => "approved",
            LearningCandidateAction.Promote => "promoted",
            LearningCandidateAction.Rollback => "rolled_back",
            _ => null,
        };
        if (metric is not null) await AppendMetricAsync(connection, tx, updated!, metric, command.OccurredAt, token);
        var eventType = command.Action switch
        {
            LearningCandidateAction.Promote => "learning.candidatePromoted",
            LearningCandidateAction.Rollback => "learning.candidateRolledBack",
            _ => "learning.candidateStateChanged",
        };
        await AppendAuditAndOutboxAsync(connection, tx, updated!, eventType, "user", command.ActorProfileId,
            command.Note ?? command.Action.ToString(), command.OccurredAt, token);
        await AppendInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey,
            command.PayloadHash, updated!.CandidateId, false, command.OccurredAt, token);
        await tx.CommitAsync(token);
        return updated;
    }

    private static async Task<LearningCandidatePage> ListCoreAsync(
        SqliteConnection connection, string tenantId, string? organizationId, string? projectId,
        LearningCandidateType? type, LearningCandidateState? state, string? cursor, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        const string where = "tenant_id=$tenant AND ($organization IS NULL OR organization_id=$organization) " +
            "AND ($project IS NULL OR project_id=$project) AND ($type IS NULL OR type=$type) " +
            "AND ($state IS NULL OR state=$state)";
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM learning_candidates WHERE {where};";
        AddFilters(count, tenantId, organizationId, projectId, type, state);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE {where} AND ($cursor IS NULL OR candidate_id>$cursor) " +
            "ORDER BY candidate_id LIMIT $take;";
        AddFilters(query, tenantId, organizationId, projectId, type, state);
        Add(query, "$cursor", cursor); Add(query, "$take", limit + 1);
        var rows = new List<LearningCandidateRecord>();
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(Map(reader));
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore ? rows[^1].CandidateId : null, total);
    }

    private static async Task<IReadOnlyList<LearningCandidateHistoryRecord>> ListHistoryCoreAsync(
        SqliteConnection connection, string tenantId, string candidateId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT event_id,candidate_id,from_state,to_state,action,actor_id,note,occurred_at,candidate_version " +
            "FROM learning_candidate_history WHERE tenant_id=$tenant AND candidate_id=$candidate ORDER BY occurred_at,event_id;";
        Add(query, "$tenant", tenantId); Add(query, "$candidate", candidateId);
        var rows = new List<LearningCandidateHistoryRecord>();
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(
            reader.GetString(0), reader.GetString(1), ParseState(reader.GetString(2)), ParseState(reader.GetString(3)),
            reader.IsDBNull(4) ? null : ParseAction(reader.GetString(4)), reader.GetString(5), Null(reader, 6),
            ParseDate(reader.GetString(7)), reader.GetInt64(8)));
        return rows;
    }

    private static async Task<LearningCandidateMetrics> GetMetricsCoreAsync(
        SqliteConnection connection, string tenantId, string? organizationId, string? projectId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT
              COALESCE(SUM(CASE WHEN m.kind='created' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN m.kind='deduplicated' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN m.kind='rejected' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN m.kind='approved' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN m.kind='promoted' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN m.kind='rolled_back' THEN 1 ELSE 0 END),0)
            FROM learning_candidate_metrics m
            WHERE m.tenant_id=$tenant AND ($organization IS NULL OR m.organization_id=$organization)
              AND ($project IS NULL OR m.project_id=$project);
            """;
        Add(query, "$tenant", tenantId); Add(query, "$organization", organizationId); Add(query, "$project", projectId);
        int created, dedup, rejected, approved, promoted, rollback;
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            await reader.ReadAsync(token);
            created = reader.GetInt32(0); dedup = reader.GetInt32(1); rejected = reader.GetInt32(2);
            approved = reader.GetInt32(3); promoted = reader.GetInt32(4); rollback = reader.GetInt32(5);
        }
        await using var shadow = connection.CreateCommand();
        shadow.CommandText = """
            SELECT COALESCE(AVG(CAST(json_extract(shadow_result_json,'$.firstPassSuccessDelta') AS REAL)),0),
                   COALESCE(AVG(CAST(json_extract(shadow_result_json,'$.repeatedErrorRateDelta') AS REAL)),0),
                   COALESCE(SUM(CAST(json_extract(shadow_result_json,'$.tokenImpact') AS INTEGER)),0),
                   COALESCE(AVG(CAST(json_extract(shadow_result_json,'$.costPerAcceptedTaskDelta') AS REAL)),0),
                   COALESCE(SUM(CASE WHEN state IN ('promoted','rolled_back','deprecated')
                       THEN CAST(json_extract(shadow_result_json,'$.regressions') AS INTEGER) ELSE 0 END),0)
            FROM learning_candidates WHERE tenant_id=$tenant
              AND ($organization IS NULL OR organization_id=$organization)
              AND ($project IS NULL OR project_id=$project) AND shadow_result_json IS NOT NULL;
            """;
        Add(shadow, "$tenant", tenantId); Add(shadow, "$organization", organizationId); Add(shadow, "$project", projectId);
        await using var shadowReader = await shadow.ExecuteReaderAsync(token); await shadowReader.ReadAsync(token);
        return new(created, dedup, rejected, approved, promoted, rollback,
            Convert.ToDecimal(shadowReader.GetDouble(0), CultureInfo.InvariantCulture),
            Convert.ToDecimal(shadowReader.GetDouble(1), CultureInfo.InvariantCulture), shadowReader.GetInt64(2),
            Convert.ToDecimal(shadowReader.GetDouble(3), CultureInfo.InvariantCulture), shadowReader.GetInt32(4));
    }

    private static async Task<LearningCandidateRecord?> ReadAsync(
        SqliteConnection connection, SqliteTransaction? tx, string tenantId, string candidateId, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = $"{Select} WHERE tenant_id=$tenant AND candidate_id=$candidate;";
        Add(query, "$tenant", tenantId); Add(query, "$candidate", candidateId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static async Task<LearningCandidateRecord?> ReadByFingerprintAsync(
        SqliteConnection connection, SqliteTransaction tx, LearningCandidateCreateCommand command, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = $"{Select} WHERE tenant_id=$tenant AND project_id=$project AND type=$type AND fingerprint=$fingerprint;";
        Add(query, "$tenant", command.TenantId); Add(query, "$project", command.ProjectId);
        Add(query, "$type", Type(command.Type)); Add(query, "$fingerprint", command.Fingerprint);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static async Task<(string CandidateId, bool Deduplicated)?> ReadInboxAsync(
        SqliteConnection connection, SqliteTransaction tx, string tenant, string key, string hash, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT message_hash,response_json FROM inbox_messages WHERE tenant_id=$tenant AND idempotency_key=$key;";
        Add(query, "$tenant", tenant); Add(query, "$key", key);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
            throw new LearningCandidateConflictException("The idempotency key was used with another payload.");
        using var json = JsonDocument.Parse(reader.GetString(1));
        return (json.RootElement.GetProperty("candidateId").GetString()!,
            json.RootElement.GetProperty("deduplicated").GetBoolean());
    }

    private static Task AppendInboxAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string key,
        string hash, string candidate, bool deduplicated, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx,
        "INSERT INTO inbox_messages(tenant_id,idempotency_key,message_hash,response_json,processed_at) VALUES($tenant,$key,$hash,$response,$at);",
        token, ("$tenant", tenant), ("$key", key), ("$hash", hash),
        ("$response", JsonSerializer.Serialize(new { candidateId = candidate, deduplicated }, JsonOptions)), ("$at", Store(at)));

    private static Task AppendMetricAsync(SqliteConnection c, SqliteTransaction tx, LearningCandidateRecord value,
        string kind, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx,
        "INSERT INTO learning_candidate_metrics(tenant_id,metric_id,organization_id,project_id,candidate_id,kind,occurred_at) " +
        "VALUES($tenant,$id,$organization,$project,$candidate,$kind,$at);", token,
        ("$tenant", value.TenantId), ("$id", UlidValue.New(at).ToString()), ("$organization", value.OrganizationId),
        ("$project", value.ProjectId), ("$candidate", value.CandidateId), ("$kind", kind), ("$at", Store(at)));

    private static Task AppendHistoryAsync(SqliteConnection c, SqliteTransaction tx, LearningCandidateRecord value,
        LearningCandidateState from, LearningCandidateAction? action, string actor, string? note,
        DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx,
        "INSERT INTO learning_candidate_history(tenant_id,event_id,candidate_id,from_state,to_state,action,actor_id,note,occurred_at,candidate_version) " +
        "VALUES($tenant,$id,$candidate,$from,$to,$action,$actor,$note,$at,$version);", token,
        ("$tenant", value.TenantId), ("$id", UlidValue.New(at).ToString()), ("$candidate", value.CandidateId),
        ("$from", State(from)), ("$to", State(value.State)), ("$action", action is null ? null : Action(action.Value)),
        ("$actor", actor), ("$note", note), ("$at", Store(at)), ("$version", value.Version));

    private static async Task AppendAuditAndOutboxAsync(SqliteConnection c, SqliteTransaction tx,
        LearningCandidateRecord value, string eventType, string actorKind, string actor, string detail,
        DateTimeOffset at, CancellationToken token)
    {
        var auditId = UlidValue.New(at).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = value.ProjectId,
            candidateId = value.CandidateId,
            type = Type(value.Type),
            state = State(value.State),
            version = value.Version,
            auditEvent = new
            {
                id = auditId,
                actorKind,
                actorId = actor,
                action = eventType,
                targetType = "learning-candidate",
                targetId = value.CandidateId,
                detail,
                occurredAt = at
            }
        }, JsonOptions);
        long sequence; string previous;
        await using (var tail = c.CreateCommand())
        {
            tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            Add(tail, "$tenant", value.TenantId); await using var reader = await tail.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); }
            else { sequence = 1; previous = AuditLedgerHash.Genesis; }
        }
        var hash = AuditLedgerHash.Compute(previous, value.TenantId, sequence, eventType, payload, at);
        await ExecuteAsync(c, tx, "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) " +
            "VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);", token,
            ("$id", auditId), ("$tenant", value.TenantId), ("$sequence", sequence), ("$previous", previous),
            ("$hash", hash), ("$type", eventType), ("$payload", payload), ("$at", Store(at)));
        await ExecuteAsync(c, tx, "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) " +
            "VALUES($id,$tenant,'audit.eventAppended',$payload,$at);", token, ("$id", UlidValue.New(at).ToString()),
            ("$tenant", value.TenantId), ("$payload", payload), ("$at", Store(at)));
    }

    private static LearningCandidateRecord Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), ParseType(r.GetString(4)), ParseState(r.GetString(5)),
        r.GetString(6), r.GetString(7), JsonSerializer.Deserialize<LearningEvidenceRecord[]>(r.GetString(8), JsonOptions) ?? [],
        JsonSerializer.Deserialize<LearningCandidatePayload>(r.GetString(9), JsonOptions)!, r.GetString(10), r.GetString(11), Null(r, 12),
        r.GetString(13), r.GetString(14), Null(r, 15), Null(r, 16), Null(r, 17), Null(r, 18),
        r.IsDBNull(19) ? null : JsonSerializer.Deserialize<LearningShadowResult>(r.GetString(19), JsonOptions),
        Null(r, 20), Null(r, 21), Null(r, 22), Null(r, 23), ParseDate(r.GetString(24)), ParseDate(r.GetString(25)), r.GetInt64(26));

    private const string Select = "SELECT tenant_id,organization_id,project_id,candidate_id,type,state,fingerprint,observation," +
        "evidence_json,payload_json,actor_agent_id,actor_provider,actor_model,baseline_version,proposed_version," +
        "evaluator_agent_id,evaluator_provider,evaluator_model,evaluation_verdict,shadow_result_json,reviewer_profile_id," +
        "decision_note,active_version,previous_version,created_at,updated_at,version FROM learning_candidates";

    private static void AddFilters(SqliteCommand command, string tenant, string? organization, string? project,
        LearningCandidateType? type, LearningCandidateState? state)
    {
        Add(command, "$tenant", tenant); Add(command, "$organization", organization); Add(command, "$project", project);
        Add(command, "$type", type is null ? null : Type(type.Value)); Add(command, "$state", state is null ? null : State(state.Value));
    }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token,
        params (string Name, object? Value)[] values)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var value in values) Add(command, value.Name, value.Value); await command.ExecuteNonQueryAsync(token);
    }
    private static void ValidateCreate(LearningCandidateCreateCommand c)
    {
        ArgumentNullException.ThrowIfNull(c); ArgumentException.ThrowIfNullOrWhiteSpace(c.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.OrganizationId); ArgumentException.ThrowIfNullOrWhiteSpace(c.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.CandidateId); ArgumentException.ThrowIfNullOrWhiteSpace(c.Fingerprint);
        if (c.Evidence.Count == 0) throw new ArgumentException("Evidence is required.");
    }
    private static void Add(SqliteCommand c, string name, object? value) => c.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? Null(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Type(LearningCandidateType value) => value switch
    {
        LearningCandidateType.Rule => "rule",
        LearningCandidateType.Skill => "skill",
        LearningCandidateType.PersonaRefinement => "persona_refinement",
        LearningCandidateType.WorkflowRefinement => "workflow_refinement",
        LearningCandidateType.ToolRoutingRecommendation => "tool_routing_recommendation",
        LearningCandidateType.DocumentationCorrection => "documentation_correction",
        LearningCandidateType.ProviderModelRoutingRecommendation => "provider_model_routing_recommendation",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static LearningCandidateType ParseType(string value) => value switch
    {
        "rule" => LearningCandidateType.Rule,
        "skill" => LearningCandidateType.Skill,
        "persona_refinement" => LearningCandidateType.PersonaRefinement,
        "workflow_refinement" => LearningCandidateType.WorkflowRefinement,
        "tool_routing_recommendation" => LearningCandidateType.ToolRoutingRecommendation,
        "documentation_correction" => LearningCandidateType.DocumentationCorrection,
        "provider_model_routing_recommendation" => LearningCandidateType.ProviderModelRoutingRecommendation,
        _ => throw new InvalidOperationException("Unknown learning candidate type.")
    };
    private static string State(LearningCandidateState value) => value switch
    {
        LearningCandidateState.Candidate => "candidate",
        LearningCandidateState.InReview => "in_review",
        LearningCandidateState.AwaitingEvaluation => "awaiting_evaluation",
        LearningCandidateState.Evaluated => "evaluated",
        LearningCandidateState.Shadow => "shadow",
        LearningCandidateState.Approved => "approved",
        LearningCandidateState.Rejected => "rejected",
        LearningCandidateState.Promoted => "promoted",
        LearningCandidateState.RolledBack => "rolled_back",
        LearningCandidateState.Deprecated => "deprecated",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static LearningCandidateState ParseState(string value) => value switch
    {
        "candidate" => LearningCandidateState.Candidate,
        "in_review" => LearningCandidateState.InReview,
        "awaiting_evaluation" => LearningCandidateState.AwaitingEvaluation,
        "evaluated" => LearningCandidateState.Evaluated,
        "shadow" => LearningCandidateState.Shadow,
        "approved" => LearningCandidateState.Approved,
        "rejected" => LearningCandidateState.Rejected,
        "promoted" => LearningCandidateState.Promoted,
        "rolled_back" => LearningCandidateState.RolledBack,
        "deprecated" => LearningCandidateState.Deprecated,
        _ => throw new InvalidOperationException("Unknown learning candidate state.")
    };
    private static string Action(LearningCandidateAction value) => value.ToString();
    private static LearningCandidateAction ParseAction(string value) => Enum.Parse<LearningCandidateAction>(value, false);
}
