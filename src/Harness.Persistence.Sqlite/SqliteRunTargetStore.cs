using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteRunTargetStore(SqliteWriteDispatcher dispatcher) : IRunTargetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> States = ["running", "stopped", "unknown"];
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<RunTargetRecord>> SynchronizeAsync(RunTargetSynchronizationCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<RunTargetRecord>>((connection, token) => SynchronizeCoreAsync(connection, command, token), cancellationToken);
    public Task<IReadOnlyList<RunTargetRecord>> ListAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<RunTargetRecord>>((connection, token) => ListCoreAsync(connection, tenantId, projectId, afterId, limit, token), cancellationToken);
    public Task<RunTargetRecord?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => GetCoreAsync(connection, tenantId, id, token), cancellationToken);
    public Task<RunTargetLaunchRecord?> GetLaunchAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => GetLaunchCoreAsync(connection, tenantId, id, token), cancellationToken);
    public Task<RunTargetRecord> SetStateAsync(RunTargetStateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => SetStateCoreAsync(connection, command, token), cancellationToken);
    public Task AppendLogAsync(RunTargetLogCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<object?>(async (connection, token) => { await AppendLogCoreAsync(connection, null, command, token); return null; }, cancellationToken);
    public Task<int> CleanupAsync(RunTargetCleanupCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => CleanupCoreAsync(connection, command, token), cancellationToken);
    public Task MarkCheckedAsync(string tenantId, string id, DateTimeOffset checkedAt, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_targets SET last_check_at=$at WHERE tenant_id=$tenant AND id=$id;";
            Add(update, "$at", Store(checkedAt)); Add(update, "$tenant", tenantId); Add(update, "$id", id);
            await update.ExecuteNonQueryAsync(token); return null;
        }, cancellationToken);

    private static async Task<IReadOnlyList<RunTargetRecord>> SynchronizeCoreAsync(SqliteConnection connection, RunTargetSynchronizationCommand command, CancellationToken token)
    {
        if (!UlidValue.TryParse(command.ProjectId, out _) || command.Definitions.Count > 100) throw new RunTargetValidationException("Run target synchronization is invalid.");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        if (!await ProjectExistsAsync(connection, transaction, command.TenantId, command.ProjectId, token)) throw new RunTargetNotFoundException("project");
        var offset = 0;
        foreach (var definition in command.Definitions)
        {
            ValidateDefinition(definition); var id = UlidValue.New(command.OccurredAt.AddTicks(offset++)).ToString();
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO run_targets
                    (tenant_id,id,project_id,fingerprint,name,kind,url,port,state,working_directory,
                     executable,arguments_json,environment_json,detected_at,last_check_at,user_facing)
                VALUES ($tenant,$id,$project,$fingerprint,$name,$kind,$url,$port,'stopped',$working,
                        $executable,$arguments,$environment,$at,$at,$userFacing)
                ON CONFLICT(tenant_id,project_id,fingerprint) DO UPDATE SET
                    name=excluded.name,kind=excluded.kind,
                    working_directory=excluded.working_directory,executable=excluded.executable,
                    last_check_at=excluded.last_check_at,user_facing=excluded.user_facing;
                """, token,
                ("$tenant", command.TenantId), ("$id", id), ("$project", command.ProjectId),
                ("$fingerprint", definition.Fingerprint), ("$name", definition.Name), ("$kind", definition.Kind),
                ("$url", definition.Url ?? (object)DBNull.Value), ("$port", definition.Port ?? (object)DBNull.Value),
                ("$working", definition.WorkingDirectory), ("$executable", definition.Executable),
                ("$arguments", JsonSerializer.Serialize(definition.Arguments, JsonOptions)),
                ("$environment", JsonSerializer.Serialize(definition.Environment, JsonOptions)), ("$at", Store(command.OccurredAt)),
                ("$userFacing", definition.UserFacing ? 1 : 0));
        }
        await transaction.CommitAsync(token);
        return await ListCoreAsync(connection, command.TenantId, command.ProjectId, null, 200, token);
    }

    private static async Task<IReadOnlyList<RunTargetRecord>> ListCoreAsync(SqliteConnection connection, string tenant, string? project, string? after, int limit, CancellationToken token)
    {
        var values = new List<RunTargetRecord>(); await using var query = connection.CreateCommand(); query.CommandText = $"{SelectPublic} WHERE tenant_id=$tenant AND ($project IS NULL OR project_id=$project) AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
        Add(query, "$tenant", tenant); AddNullable(query, "$project", project); AddNullable(query, "$after", after); Add(query, "$limit", limit); await using var reader = await query.ExecuteReaderAsync(token); while (await reader.ReadAsync(token)) values.Add(ReadPublic(reader)); return values;
    }

    private static async Task<RunTargetRecord?> GetCoreAsync(SqliteConnection connection, string tenant, string id, CancellationToken token)
    { await using var query = connection.CreateCommand(); query.CommandText = $"{SelectPublic} WHERE tenant_id=$tenant AND id=$id;"; Add(query, "$tenant", tenant); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadPublic(reader) : null; }

    private static async Task<RunTargetLaunchRecord?> GetLaunchCoreAsync(SqliteConnection connection, string tenant, string id, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.CommandText = $"{SelectLaunch} WHERE tenant_id=$tenant AND id=$id;"; Add(query, "$tenant", tenant); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null; var target = ReadPublic(reader); return new(target, reader.GetString(10), reader.GetString(11), JsonSerializer.Deserialize<string[]>(reader.GetString(12), JsonOptions) ?? [], JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(13), JsonOptions) ?? new(StringComparer.Ordinal));
    }

    private static async Task<RunTargetRecord> SetStateCoreAsync(SqliteConnection connection, RunTargetStateCommand command, CancellationToken token)
    {
        if (!States.Contains(command.State) || string.IsNullOrWhiteSpace(command.LogLine) || command.LogLine.Length > 4_000) throw new RunTargetValidationException("Run target state is invalid.");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token); var current = await ReadInTransactionAsync(connection, transaction, command.TenantId, command.Id, token) ?? throw new RunTargetNotFoundException("run_target");
        await ExecuteAsync(connection, transaction, "UPDATE run_targets SET state=$state,last_check_at=$at WHERE tenant_id=$tenant AND id=$id;", token, ("$state", command.State), ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", command.Id));
        var result = current with { State = command.State, LastCheckAt = command.OccurredAt };
        var auditId = UlidValue.New(command.OccurredAt).ToString(); var auditPayload = JsonSerializer.Serialize(new { projectId = current.ProjectId, auditEvent = new { id = auditId, actorKind = "user", actorId = command.ActorProfileId, action = "runTarget.stateChanged", targetType = "run-target", targetId = command.Id, detail = $"State changed from {current.State} to {command.State}.", occurredAt = command.OccurredAt } }, JsonOptions);
        await AppendLedgerAsync(connection, transaction, command.TenantId, "runTarget.stateChanged", auditPayload, command.OccurredAt, token); await AppendOutboxAsync(connection, transaction, command.TenantId, "audit.eventAppended", auditPayload, command.OccurredAt, token); await AppendLogCoreAsync(connection, transaction, new(command.TenantId, current.ProjectId, command.LogLine.Trim(), command.OccurredAt), token); await transaction.CommitAsync(token); return result;
    }

    private static async Task AppendLogCoreAsync(SqliteConnection connection, SqliteTransaction? transaction, RunTargetLogCommand command, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command.Line) || command.Line.Length > 4_000) return;
        var payload = JsonSerializer.Serialize(new { projectId = command.ProjectId, runId = (string?)null, attemptId = (string?)null, line = command.Line }, JsonOptions);
        if (transaction is null) { await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token); await AppendOutboxAsync(connection, tx, command.TenantId, "run.logAppended", payload, command.OccurredAt, token); await tx.CommitAsync(token); }
        else await AppendOutboxAsync(connection, transaction, command.TenantId, "run.logAppended", payload, command.OccurredAt, token);
    }

    private static async Task<int> CleanupCoreAsync(SqliteConnection connection, RunTargetCleanupCommand command, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token); await using var update = connection.CreateCommand(); update.Transaction = transaction; update.CommandText = "UPDATE run_targets SET state='stopped',last_check_at=$at WHERE tenant_id=$tenant AND project_id=$project AND state<>'stopped';"; Add(update, "$at", Store(command.OccurredAt)); Add(update, "$tenant", command.TenantId); Add(update, "$project", command.ProjectId); var changed = await update.ExecuteNonQueryAsync(token);
        var auditId = UlidValue.New(command.OccurredAt).ToString(); var payload = JsonSerializer.Serialize(new { projectId = command.ProjectId, auditEvent = new { id = auditId, actorKind = "user", actorId = command.ActorProfileId, action = "run.environmentCleaned", targetType = "project", targetId = command.ProjectId, detail = $"Stopped {changed} managed run target(s).", occurredAt = command.OccurredAt } }, JsonOptions); await AppendLedgerAsync(connection, transaction, command.TenantId, "run.environmentCleaned", payload, command.OccurredAt, token); await AppendOutboxAsync(connection, transaction, command.TenantId, "audit.eventAppended", payload, command.OccurredAt, token); await AppendLogCoreAsync(connection, transaction, new(command.TenantId, command.ProjectId, $"Cleanup completed: {changed} managed service(s) stopped.", command.OccurredAt), token); await transaction.CommitAsync(token); return changed;
    }

    private static async Task<RunTargetRecord?> ReadInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string tenant, string id, CancellationToken token) { await using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = $"{SelectPublic} WHERE tenant_id=$tenant AND id=$id;"; Add(query, "$tenant", tenant); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadPublic(reader) : null; }
    private static async Task<bool> ProjectExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string tenant, string project, CancellationToken token) { await using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = "SELECT EXISTS(SELECT 1 FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL);"; Add(query, "$tenant", tenant); Add(query, "$project", project); return Convert.ToInt32(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1; }
    private static void ValidateDefinition(RunTargetDefinition value) { if (value.Fingerprint.Length != 64 || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 200 || value.Kind is not ("http" or "tcp" or "process") || value.Port is <= 0 or > 65535 || string.IsNullOrWhiteSpace(value.WorkingDirectory) || string.IsNullOrWhiteSpace(value.Executable) || value.Arguments.Count > 100 || value.Environment.Count > 100) throw new RunTargetValidationException("Detected run target is invalid."); }
    private static RunTargetRecord ReadPublic(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetString(6), Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)), reader.GetInt32(7) == 1);
    private static async Task AppendLedgerAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token) { long seq; string prev; await using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(q, "$tenant", tenant); await using var r = await q.ExecuteReaderAsync(token); if (await r.ReadAsync(token)) { seq = r.GetInt64(0) + 1; prev = r.GetString(1); } else { seq = 1; prev = AuditLedgerHash.Genesis; } } var hash = AuditLedgerHash.Compute(prev, tenant, seq, type, payload, at); await ExecuteAsync(c, tx, "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$sequence", seq), ("$previous", prev), ("$hash", hash), ("$type", type), ("$payload", payload), ("$at", Store(at))); }
    private static Task AppendOutboxAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx, "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($id,$tenant,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$type", type), ("$payload", payload), ("$at", Store(at)));
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string, object)[] values) { await using var query = c.CreateCommand(); query.Transaction = tx; query.CommandText = sql; foreach (var (name, value) in values) Add(query, name, value); await query.ExecuteNonQueryAsync(token); }
    private const string SelectPublic = "SELECT id,project_id,name,kind,url,port,state,user_facing,detected_at,last_check_at FROM run_targets";
    private const string SelectLaunch = "SELECT id,project_id,name,kind,url,port,state,user_facing,detected_at,last_check_at,working_directory,executable,arguments_json,environment_json FROM run_targets";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
