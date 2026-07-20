using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresLearningCandidateStore(NpgsqlDataSource dataSource) : ILearningCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<(LearningCandidateRecord Candidate, bool Deduplicated)> CreateAsync(
        LearningCandidateCreateCommand command, CancellationToken cancellationToken = default)
    {
        ValidateCreate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, tx, $"{command.TenantId}:{command.IdempotencyKey}", cancellationToken);
        var replay = await ReadInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash, cancellationToken);
        if (replay is not null)
        {
            var replayed = await ReadAsync(connection, tx, command.TenantId, replay.Value.CandidateId, cancellationToken)
                ?? throw new InvalidOperationException("Learning inbox references a missing candidate.");
            await tx.CommitAsync(cancellationToken); return (replayed, replay.Value.Deduplicated);
        }
        var existing = await ReadByFingerprintAsync(connection, tx, command, cancellationToken);
        if (existing is not null)
        {
            await AppendMetricAsync(connection, tx, existing, "deduplicated", command.OccurredAt, cancellationToken);
            await AppendInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash,
                existing.CandidateId, true, command.OccurredAt, cancellationToken);
            await tx.CommitAsync(cancellationToken); return (existing, true);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO harness.learning_candidates
                (tenant_id,organization_id,project_id,candidate_id,type,state,fingerprint,observation,
                 evidence_json,payload_json,actor_agent_id,actor_provider,actor_model,baseline_version,
                 proposed_version,created_at,updated_at,version)
                VALUES ($1,$2,$3,$4,$5,'candidate',$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$15,1);
                """;
            insert.Parameters.Add(Text(command.TenantId)); insert.Parameters.Add(Text(command.OrganizationId));
            insert.Parameters.Add(Text(command.ProjectId)); insert.Parameters.Add(Text(command.CandidateId));
            insert.Parameters.Add(Text(Type(command.Type))); insert.Parameters.Add(Text(command.Fingerprint));
            insert.Parameters.Add(Text(command.Observation)); insert.Parameters.Add(Json(JsonSerializer.Serialize(command.Evidence, JsonOptions)));
            insert.Parameters.Add(Json(JsonSerializer.Serialize(command.Payload, JsonOptions))); insert.Parameters.Add(Text(command.ActorAgentId));
            insert.Parameters.Add(Text(command.ActorProvider)); insert.Parameters.Add(Text(command.ActorModel));
            insert.Parameters.Add(Text(command.BaselineVersion)); insert.Parameters.Add(Text(command.ProposedVersion));
            insert.Parameters.Add(Timestamp(command.OccurredAt)); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var created = await ReadAsync(connection, tx, command.TenantId, command.CandidateId, cancellationToken)
            ?? throw new InvalidOperationException("Learning candidate insert did not produce a row.");
        await AppendHistoryAsync(connection, tx, created, created.State, null, command.ActorAgentId,
            "candidate_created", command.OccurredAt, cancellationToken);
        await AppendMetricAsync(connection, tx, created, "created", command.OccurredAt, cancellationToken);
        await AppendAuditAndOutboxAsync(connection, tx, created, "learning.candidateCreated", command.ActorAgentId,
            "Candidate created from observation and evidence.", command.OccurredAt, cancellationToken);
        await AppendInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash,
            created.CandidateId, false, command.OccurredAt, cancellationToken);
        await tx.CommitAsync(cancellationToken); return (created, false);
    }

    public async Task<LearningCandidateRecord> TransitionAsync(
        LearningCandidateTransitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, tx, $"{command.TenantId}:{command.CandidateId}", cancellationToken);
        await LockAsync(connection, tx, $"{command.TenantId}:{command.IdempotencyKey}", cancellationToken);
        var replay = await ReadInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash, cancellationToken);
        if (replay is not null)
        {
            var replayed = await ReadAsync(connection, tx, command.TenantId, replay.Value.CandidateId, cancellationToken)
                ?? throw new InvalidOperationException("Learning inbox references a missing candidate.");
            await tx.CommitAsync(cancellationToken); return replayed;
        }
        var current = await ReadAsync(connection, tx, command.TenantId, command.CandidateId, cancellationToken)
            ?? throw new KeyNotFoundException("Learning candidate does not exist.");
        var next = LearningCandidatePolicy.Next(current, command);
        var active = current.ActiveVersion; var previous = current.PreviousVersion;
        if (command.Action == LearningCandidateAction.Promote) { previous = current.BaselineVersion; active = current.ProposedVersion; }
        else if (command.Action == LearningCandidateAction.Rollback) active = current.PreviousVersion;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE harness.learning_candidates SET state=$1,evaluator_agent_id=COALESCE($2,evaluator_agent_id),
                    evaluator_provider=COALESCE($3,evaluator_provider),evaluator_model=COALESCE($4,evaluator_model),
                    evaluation_verdict=COALESCE($5,evaluation_verdict),shadow_result_json=COALESCE($6,shadow_result_json),
                    reviewer_profile_id=$7,decision_note=$8,active_version=$9,previous_version=$10,
                    updated_at=$11,version=version+1
                WHERE tenant_id=$12 AND candidate_id=$13 AND version=$14;
                """;
            update.Parameters.Add(Text(State(next))); update.Parameters.Add(Text(command.EvaluatorAgentId));
            update.Parameters.Add(Text(command.EvaluatorProvider)); update.Parameters.Add(Text(command.EvaluatorModel));
            update.Parameters.Add(Text(command.EvaluationVerdict));
            update.Parameters.Add(command.ShadowResult is null ? JsonNull() : Json(JsonSerializer.Serialize(command.ShadowResult, JsonOptions)));
            update.Parameters.Add(Text(command.ActorProfileId)); update.Parameters.Add(Text(command.Note));
            update.Parameters.Add(Text(active)); update.Parameters.Add(Text(previous)); update.Parameters.Add(Timestamp(command.OccurredAt));
            update.Parameters.Add(Text(command.TenantId)); update.Parameters.Add(Text(command.CandidateId));
            update.Parameters.Add(Bigint(command.ExpectedVersion));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new LearningCandidateConflictException("The candidate changed concurrently.");
        }
        var updated = await ReadAsync(connection, tx, command.TenantId, command.CandidateId, cancellationToken)
            ?? throw new InvalidOperationException("Learning candidate disappeared during transition.");
        await AppendHistoryAsync(connection, tx, updated, current.State, command.Action, command.ActorProfileId,
            command.Note, command.OccurredAt, cancellationToken);
        var metric = command.Action switch
        {
            LearningCandidateAction.Reject => "rejected",
            LearningCandidateAction.Approve => "approved",
            LearningCandidateAction.Promote => "promoted",
            LearningCandidateAction.Rollback => "rolled_back",
            _ => null
        };
        if (metric is not null) await AppendMetricAsync(connection, tx, updated, metric, command.OccurredAt, cancellationToken);
        var eventType = command.Action switch
        {
            LearningCandidateAction.Promote => "learning.candidatePromoted",
            LearningCandidateAction.Rollback => "learning.candidateRolledBack",
            _ => "learning.candidateStateChanged"
        };
        await AppendAuditAndOutboxAsync(connection, tx, updated, eventType, command.ActorProfileId,
            command.Note ?? command.Action.ToString(), command.OccurredAt, cancellationToken);
        await AppendInboxAsync(connection, tx, command.TenantId, command.IdempotencyKey, command.PayloadHash,
            updated.CandidateId, false, command.OccurredAt, cancellationToken);
        await tx.CommitAsync(cancellationToken); return updated;
    }

    public async Task<LearningCandidateRecord?> GetAsync(
        string tenantId, string candidateId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, candidateId, cancellationToken);
    }

    public async Task<LearningCandidatePage> ListAsync(
        string tenantId, string? organizationId, string? projectId, LearningCandidateType? type,
        LearningCandidateState? state, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string where = "tenant_id=$1 AND ($2 IS NULL OR organization_id=$2) AND ($3 IS NULL OR project_id=$3) " +
            "AND ($4 IS NULL OR type=$4) AND ($5 IS NULL OR state=$5)";
        await using var count = connection.CreateCommand(); count.CommandText = $"SELECT COUNT(*) FROM harness.learning_candidates WHERE {where};";
        AddFilters(count, tenantId, organizationId, projectId, type, state);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE {where} AND ($6 IS NULL OR candidate_id>$6) ORDER BY candidate_id LIMIT $7;";
        AddFilters(query, tenantId, organizationId, projectId, type, state); query.Parameters.Add(Text(cursor)); query.Parameters.Add(Integer(limit + 1));
        var rows = new List<LearningCandidateRecord>(); await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Map(reader));
        var more = rows.Count > limit; if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows, more ? rows[^1].CandidateId : null, total);
    }

    public async Task<IReadOnlyList<LearningCandidateHistoryRecord>> ListHistoryAsync(
        string tenantId, string candidateId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand(); query.CommandText =
            "SELECT event_id,candidate_id,from_state,to_state,action,actor_id,note,occurred_at,candidate_version " +
            "FROM harness.learning_candidate_history WHERE tenant_id=$1 AND candidate_id=$2 ORDER BY occurred_at,event_id;";
        query.Parameters.Add(Text(tenantId)); query.Parameters.Add(Text(candidateId));
        var rows = new List<LearningCandidateHistoryRecord>(); await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new(reader.GetString(0), reader.GetString(1),
            ParseState(reader.GetString(2)), ParseState(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseAction(reader.GetString(4)),
            reader.GetString(5), Null(reader, 6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt64(8)));
        return rows;
    }

    public async Task<LearningCandidateMetrics> GetMetricsAsync(
        string tenantId, string? organizationId, string? projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand(); query.CommandText = """
            SELECT COUNT(*) FILTER (WHERE kind='created'),COUNT(*) FILTER (WHERE kind='deduplicated'),
                   COUNT(*) FILTER (WHERE kind='rejected'),COUNT(*) FILTER (WHERE kind='approved'),
                   COUNT(*) FILTER (WHERE kind='promoted'),COUNT(*) FILTER (WHERE kind='rolled_back')
            FROM harness.learning_candidate_metrics WHERE tenant_id=$1 AND ($2 IS NULL OR organization_id=$2)
              AND ($3 IS NULL OR project_id=$3);
            """;
        query.Parameters.Add(Text(tenantId)); query.Parameters.Add(Text(organizationId)); query.Parameters.Add(Text(projectId));
        int created, dedup, rejected, approved, promoted, rollback;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken); created = reader.GetInt32(0); dedup = reader.GetInt32(1); rejected = reader.GetInt32(2);
            approved = reader.GetInt32(3); promoted = reader.GetInt32(4); rollback = reader.GetInt32(5);
        }
        await using var shadow = connection.CreateCommand(); shadow.CommandText = """
            SELECT COALESCE(AVG((shadow_result_json->>'firstPassSuccessDelta')::numeric),0),
                   COALESCE(AVG((shadow_result_json->>'repeatedErrorRateDelta')::numeric),0),
                   COALESCE(SUM((shadow_result_json->>'tokenImpact')::bigint),0),
                   COALESCE(AVG((shadow_result_json->>'costPerAcceptedTaskDelta')::numeric),0),
                   COALESCE(SUM(CASE WHEN state IN ('promoted','rolled_back','deprecated')
                       THEN (shadow_result_json->>'regressions')::integer ELSE 0 END),0)
            FROM harness.learning_candidates WHERE tenant_id=$1 AND ($2 IS NULL OR organization_id=$2)
              AND ($3 IS NULL OR project_id=$3) AND shadow_result_json IS NOT NULL;
            """;
        shadow.Parameters.Add(Text(tenantId)); shadow.Parameters.Add(Text(organizationId)); shadow.Parameters.Add(Text(projectId));
        await using var sr = await shadow.ExecuteReaderAsync(cancellationToken); await sr.ReadAsync(cancellationToken);
        return new(created, dedup, rejected, approved, promoted, rollback, sr.GetDecimal(0), sr.GetDecimal(1), sr.GetInt64(2), sr.GetDecimal(3), sr.GetInt32(4));
    }

    private static async Task<LearningCandidateRecord?> ReadAsync(NpgsqlConnection c, NpgsqlTransaction? tx,
        string tenant, string candidate, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"{Select} WHERE tenant_id=$1 AND candidate_id=$2;";
        q.Parameters.Add(Text(tenant)); q.Parameters.Add(Text(candidate)); await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? Map(r) : null;
    }
    private static async Task<LearningCandidateRecord?> ReadByFingerprintAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LearningCandidateCreateCommand value, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"{Select} WHERE tenant_id=$1 AND project_id=$2 AND type=$3 AND fingerprint=$4;";
        q.Parameters.Add(Text(value.TenantId)); q.Parameters.Add(Text(value.ProjectId)); q.Parameters.Add(Text(Type(value.Type))); q.Parameters.Add(Text(value.Fingerprint));
        await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? Map(r) : null;
    }

    private static async Task<(string CandidateId, bool Deduplicated)?> ReadInboxAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        string tenant, string key, string hash, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT message_hash,response_json FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        q.Parameters.Add(Text(tenant)); q.Parameters.Add(Text(key)); await using var r = await q.ExecuteReaderAsync(token); if (!await r.ReadAsync(token)) return null;
        if (!string.Equals(r.GetString(0), hash, StringComparison.Ordinal)) throw new LearningCandidateConflictException("The idempotency key was used with another payload.");
        using var json = JsonDocument.Parse(r.GetString(1)); return (json.RootElement.GetProperty("candidateId").GetString()!, json.RootElement.GetProperty("deduplicated").GetBoolean());
    }
    private static Task AppendInboxAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenant, string key, string hash, string candidate, bool dedup, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx,
        "INSERT INTO harness.inbox_messages(tenant_id,idempotency_key,message_hash,response_json,processed_at) VALUES($1,$2,$3,$4,$5);", token,
        Text(tenant), Text(key), Text(hash), Json(JsonSerializer.Serialize(new { candidateId = candidate, deduplicated = dedup }, JsonOptions)), Timestamp(at));
    private static Task AppendMetricAsync(NpgsqlConnection c, NpgsqlTransaction tx, LearningCandidateRecord v, string kind, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx,
        "INSERT INTO harness.learning_candidate_metrics(tenant_id,metric_id,organization_id,project_id,candidate_id,kind,occurred_at) VALUES($1,$2,$3,$4,$5,$6,$7);", token,
        Text(v.TenantId), Text(UlidValue.New(at).ToString()), Text(v.OrganizationId), Text(v.ProjectId), Text(v.CandidateId), Text(kind), Timestamp(at));
    private static Task AppendHistoryAsync(NpgsqlConnection c, NpgsqlTransaction tx, LearningCandidateRecord v, LearningCandidateState from, LearningCandidateAction? action, string actor, string? note, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx,
        "INSERT INTO harness.learning_candidate_history(tenant_id,event_id,candidate_id,from_state,to_state,action,actor_id,note,occurred_at,candidate_version) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10);", token,
        Text(v.TenantId), Text(UlidValue.New(at).ToString()), Text(v.CandidateId), Text(State(from)), Text(State(v.State)), Text(action?.ToString()), Text(actor), Text(note), Timestamp(at), Bigint(v.Version));
    private static async Task AppendAuditAndOutboxAsync(NpgsqlConnection c, NpgsqlTransaction tx, LearningCandidateRecord v, string eventType, string actor, string detail, DateTimeOffset at, CancellationToken token)
    {
        var auditId = UlidValue.New(at).ToString(); var payload = JsonSerializer.Serialize(new
        {
            projectId = v.ProjectId,
            candidateId = v.CandidateId,
            type = Type(v.Type),
            state = State(v.State),
            version = v.Version,
            auditEvent = new { id = auditId, actorKind = "user", actorId = actor, action = eventType, targetType = "learning-candidate", targetId = v.CandidateId, detail, occurredAt = at }
        }, JsonOptions);
        var (sequence, previous) = await ReadTailAsync(c, tx, v.TenantId, token); var hash = AuditLedgerHash.Compute(previous, v.TenantId, sequence, eventType, payload, at);
        await ExecuteAsync(c, tx, "INSERT INTO harness.audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8);", token,
          Text(auditId), Text(v.TenantId), Bigint(sequence), Text(previous), Text(hash), Text(eventType), Json(payload), Timestamp(at));
        await ExecuteAsync(c, tx, "INSERT INTO harness.outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($1,$2,$3,$4,$5);", token,
          Text(UlidValue.New(at).ToString()), Text(v.TenantId), Text("audit.eventAppended"), Json(payload), Timestamp(at));
    }
    private static async Task<(long, string)> ReadTailAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenant, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;";
        q.Parameters.Add(Text(tenant)); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? (r.GetInt64(0) + 1, r.GetString(1).TrimEnd()) : (1, AuditLedgerHash.Genesis);
    }
    private static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, CancellationToken token) => ExecuteAsync(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", token, Text(key));
    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken token, params NpgsqlParameter[] parameters)
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; q.Parameters.AddRange(parameters); await q.ExecuteNonQueryAsync(token); }

    private static LearningCandidateRecord Map(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), ParseType(r.GetString(4)), ParseState(r.GetString(5)), r.GetString(6), r.GetString(7),
        JsonSerializer.Deserialize<LearningEvidenceRecord[]>(r.GetFieldValue<string>(8), JsonOptions) ?? [], JsonSerializer.Deserialize<LearningCandidatePayload>(r.GetFieldValue<string>(9), JsonOptions)!, r.GetString(10), r.GetString(11), Null(r, 12), r.GetString(13), r.GetString(14),
        Null(r, 15), Null(r, 16), Null(r, 17), Null(r, 18), r.IsDBNull(19) ? null : JsonSerializer.Deserialize<LearningShadowResult>(r.GetFieldValue<string>(19), JsonOptions), Null(r, 20), Null(r, 21), Null(r, 22), Null(r, 23), r.GetFieldValue<DateTimeOffset>(24), r.GetFieldValue<DateTimeOffset>(25), r.GetInt64(26));
    private const string Select = "SELECT tenant_id,organization_id,project_id,candidate_id,type,state,fingerprint,observation,evidence_json::text,payload_json::text,actor_agent_id,actor_provider,actor_model,baseline_version,proposed_version,evaluator_agent_id,evaluator_provider,evaluator_model,evaluation_verdict,shadow_result_json::text,reviewer_profile_id,decision_note,active_version,previous_version,created_at,updated_at,version FROM harness.learning_candidates";
    private static void AddFilters(NpgsqlCommand q, string tenant, string? organization, string? project, LearningCandidateType? type, LearningCandidateState? state)
    { q.Parameters.Add(Text(tenant)); q.Parameters.Add(Text(organization)); q.Parameters.Add(Text(project)); q.Parameters.Add(Text(type is null ? null : Type(type.Value))); q.Parameters.Add(Text(state is null ? null : State(state.Value))); }
    private static void ValidateCreate(LearningCandidateCreateCommand c) { ArgumentNullException.ThrowIfNull(c); ArgumentException.ThrowIfNullOrWhiteSpace(c.TenantId); ArgumentException.ThrowIfNullOrWhiteSpace(c.OrganizationId); ArgumentException.ThrowIfNullOrWhiteSpace(c.ProjectId); ArgumentException.ThrowIfNullOrWhiteSpace(c.CandidateId); if (c.Evidence.Count == 0) throw new ArgumentException("Evidence is required."); }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Nullable Npgsql parameters require the untyped DBNull representation.")]
    private static NpgsqlParameter Text(string? value) => value is null
        ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DBNull.Value }
        : new NpgsqlParameter<string> { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = value };
    private static NpgsqlParameter JsonNull() => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = DBNull.Value };
    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value }; private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value }; private static string? Null(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string Type(LearningCandidateType v) => v switch { LearningCandidateType.Rule => "rule", LearningCandidateType.Skill => "skill", LearningCandidateType.PersonaRefinement => "persona_refinement", LearningCandidateType.WorkflowRefinement => "workflow_refinement", LearningCandidateType.ToolRoutingRecommendation => "tool_routing_recommendation", LearningCandidateType.DocumentationCorrection => "documentation_correction", LearningCandidateType.ProviderModelRoutingRecommendation => "provider_model_routing_recommendation", _ => throw new ArgumentOutOfRangeException(nameof(v)) };
    private static LearningCandidateType ParseType(string v) => v switch { "rule" => LearningCandidateType.Rule, "skill" => LearningCandidateType.Skill, "persona_refinement" => LearningCandidateType.PersonaRefinement, "workflow_refinement" => LearningCandidateType.WorkflowRefinement, "tool_routing_recommendation" => LearningCandidateType.ToolRoutingRecommendation, "documentation_correction" => LearningCandidateType.DocumentationCorrection, "provider_model_routing_recommendation" => LearningCandidateType.ProviderModelRoutingRecommendation, _ => throw new InvalidOperationException("Unknown learning candidate type.") };
    private static string State(LearningCandidateState v) => v switch { LearningCandidateState.Candidate => "candidate", LearningCandidateState.InReview => "in_review", LearningCandidateState.AwaitingEvaluation => "awaiting_evaluation", LearningCandidateState.Evaluated => "evaluated", LearningCandidateState.Shadow => "shadow", LearningCandidateState.Approved => "approved", LearningCandidateState.Rejected => "rejected", LearningCandidateState.Promoted => "promoted", LearningCandidateState.RolledBack => "rolled_back", LearningCandidateState.Deprecated => "deprecated", _ => throw new ArgumentOutOfRangeException(nameof(v)) };
    private static LearningCandidateState ParseState(string v) => v switch { "candidate" => LearningCandidateState.Candidate, "in_review" => LearningCandidateState.InReview, "awaiting_evaluation" => LearningCandidateState.AwaitingEvaluation, "evaluated" => LearningCandidateState.Evaluated, "shadow" => LearningCandidateState.Shadow, "approved" => LearningCandidateState.Approved, "rejected" => LearningCandidateState.Rejected, "promoted" => LearningCandidateState.Promoted, "rolled_back" => LearningCandidateState.RolledBack, "deprecated" => LearningCandidateState.Deprecated, _ => throw new InvalidOperationException("Unknown learning candidate state.") };
    private static LearningCandidateAction ParseAction(string value) => Enum.Parse<LearningCandidateAction>(value, false);
}
