using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Sqlite;

public sealed class SqlitePrototypeStore(SqliteWriteDispatcher dispatcher) : IPrototypeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Sources = ["upload", "url", "generated"];
    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal) { ["draft"] = ["generating", "ready", "archived"], ["generating"] = ["draft", "ready", "archived"], ["ready"] = ["published", "archived"], ["published"] = ["archived"], ["archived"] = [] };
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<PrototypeRecord>> ListPrototypesAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, projectId, afterId, limit, PrototypeSelect, ReadPrototype, cancellationToken);
    public Task<PrototypeRecord?> GetPrototypeAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, id, PrototypeSelect, ReadPrototype, cancellationToken);
    public Task<IReadOnlyList<VisualReferenceRecord>> ListReferencesAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, projectId, afterId, limit, ReferenceSelect, ReadReference, cancellationToken);
    public Task<VisualReferenceRecord?> GetReferenceAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, id, ReferenceSelect, ReadReference, cancellationToken);
    public Task<PrototypeRecord> CreatePrototypeAsync(PrototypeCreateCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync((c, t) => CreatePrototypeCoreAsync(c, command, t), cancellationToken);
    public Task<VisualReferenceRecord> CreateReferenceAsync(VisualReferenceCreateCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync((c, t) => CreateReferenceCoreAsync(c, command, t), cancellationToken);
    public Task<PrototypeRecord> TransitionPrototypeAsync(PrototypeTransitionCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync((c, t) => TransitionCoreAsync(c, command, t), cancellationToken);
    public Task DeletePrototypeAsync(PrototypeDeleteCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync<object?>(async (c, t) => { await DeleteCoreAsync(c, command, "prototypes", "prototype.deleted", t); return null; }, cancellationToken);
    public Task DeleteReferenceAsync(PrototypeDeleteCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync<object?>(async (c, t) => { await DeleteCoreAsync(c, command, "visual_references", "visualReference.deleted", t); return null; }, cancellationToken);

    private Task<IReadOnlyList<T>> ListAsync<T>(string tenant, string? project, string? after, int limit, string select, Func<SqliteDataReader, T> read, CancellationToken token) => _dispatcher.ExecuteAsync<IReadOnlyList<T>>(async (c, ct) => { var rows = new List<T>(); await using var q = c.CreateCommand(); q.CommandText = $"{select} WHERE tenant_id=$tenant AND deleted_at IS NULL AND ($project IS NULL OR project_id=$project) AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;"; Add(q, "$tenant", tenant); AddNullable(q, "$project", project); AddNullable(q, "$after", after); Add(q, "$limit", limit); await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) rows.Add(read(r)); return rows; }, token);
    private Task<T?> GetAsync<T>(string tenant, string id, string select, Func<SqliteDataReader, T> read, CancellationToken token) where T : class => _dispatcher.ExecuteAsync(async (c, ct) => { await using var q = c.CreateCommand(); q.CommandText = $"{select} WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL;"; Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? read(r) : null; }, token);

    private static async Task<PrototypeRecord> CreatePrototypeCoreAsync(SqliteConnection c, PrototypeCreateCommand command, CancellationToken token)
    {
        ValidateId(command.Id); ValidateId(command.ProjectId); if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 200 || command.Description?.Length > 4000 || command.SourceDocumentId is not null && !UlidValue.TryParse(command.SourceDocumentId, out _)) throw new PrototypeValidationException("Prototype is invalid.");
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token); await RequireEnabledProjectAsync(c, tx, command.TenantId, command.ProjectId, token);
        if (command.SourceDocumentId is not null && !await ReferenceExistsAsync(c, tx, "documents", command.TenantId, command.ProjectId, command.SourceDocumentId, token)) throw new PrototypeNotFoundException("source_document");
        await ExecuteAsync(c, tx, "INSERT INTO prototypes(tenant_id,id,project_id,name,description,state,source_document_id,created_at,updated_at) VALUES($tenant,$id,$project,$name,$description,'draft',$document,$at,$at);", token, ("$tenant", command.TenantId), ("$id", command.Id), ("$project", command.ProjectId), ("$name", command.Name.Trim()), ("$description", command.Description?.Trim() ?? ""), ("$document", command.SourceDocumentId ?? (object)DBNull.Value), ("$at", Store(command.OccurredAt)));
        var value = new PrototypeRecord(command.Id, command.ProjectId, command.Name.Trim(), command.Description?.Trim() ?? "", "draft", null, null, command.SourceDocumentId, command.OccurredAt, command.OccurredAt);
        await AppendAsync(c, tx, command.TenantId, "prototype.created", JsonSerializer.Serialize(new { projectId = command.ProjectId, prototype = value }, JsonOptions), command.OccurredAt, true, token); await tx.CommitAsync(token); return value;
    }

    private static async Task<VisualReferenceRecord> CreateReferenceCoreAsync(SqliteConnection c, VisualReferenceCreateCommand command, CancellationToken token)
    {
        ValidateId(command.Id); ValidateId(command.ProjectId); if (command.PrototypeId is not null) ValidateId(command.PrototypeId); if (string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 200 || string.IsNullOrWhiteSpace(command.ImageUrl) || command.ImageUrl.Length > 2048 || !Sources.Contains(command.Source) || command.Tags is { Count: > 50 } || command.Tags?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100) == true) throw new PrototypeValidationException("Visual reference is invalid.");
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token); await RequireEnabledProjectAsync(c, tx, command.TenantId, command.ProjectId, token);
        if (command.PrototypeId is not null && !await ReferenceExistsAsync(c, tx, "prototypes", command.TenantId, command.ProjectId, command.PrototypeId, token)) throw new PrototypeNotFoundException("prototype");
        var tags = (command.Tags ?? []).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray(); await ExecuteAsync(c, tx, "INSERT INTO visual_references(tenant_id,id,project_id,prototype_id,title,image_url,source,tags_json,created_at) VALUES($tenant,$id,$project,$prototype,$title,$image,$source,$tags,$at);", token, ("$tenant", command.TenantId), ("$id", command.Id), ("$project", command.ProjectId), ("$prototype", command.PrototypeId ?? (object)DBNull.Value), ("$title", command.Title.Trim()), ("$image", command.ImageUrl.Trim()), ("$source", command.Source), ("$tags", JsonSerializer.Serialize(tags, JsonOptions)), ("$at", Store(command.OccurredAt)));
        var value = new VisualReferenceRecord(command.Id, command.ProjectId, command.PrototypeId, command.Title.Trim(), command.ImageUrl.Trim(), command.Source, tags, command.OccurredAt); await AppendAsync(c, tx, command.TenantId, "visualReference.created", JsonSerializer.Serialize(new { projectId = command.ProjectId, visualReference = value }, JsonOptions), command.OccurredAt, false, token); await tx.CommitAsync(token); return value;
    }

    private static async Task<PrototypeRecord> TransitionCoreAsync(SqliteConnection c, PrototypeTransitionCommand command, CancellationToken token)
    {
        ValidateId(command.Id); await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token); var current = await ReadPrototypeAsync(c, tx, command.TenantId, command.Id, token) ?? throw new PrototypeNotFoundException("prototype"); if (!Transitions.TryGetValue(current.State, out var allowed) || !allowed.Contains(command.State, StringComparer.Ordinal)) throw new PrototypeValidationException("Prototype transition is invalid.");
        var url = command.Url ?? current.Url; var thumb = command.ThumbnailUrl ?? current.ThumbnailUrl; if (url?.Length > 2048 || thumb?.Length > 2048 || command.State == "published" && string.IsNullOrWhiteSpace(url)) throw new PrototypeValidationException("Published prototype requires a URL.");
        await ExecuteAsync(c, tx, "UPDATE prototypes SET state=$state,url=$url,thumbnail_url=$thumbnail,updated_at=$at WHERE tenant_id=$tenant AND id=$id;", token, ("$state", command.State), ("$url", url ?? (object)DBNull.Value), ("$thumbnail", thumb ?? (object)DBNull.Value), ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", command.Id));
        var value = current with { State = command.State, Url = url, ThumbnailUrl = thumb, UpdatedAt = command.OccurredAt }; await AppendAsync(c, tx, command.TenantId, "prototype.stateChanged", JsonSerializer.Serialize(new { projectId = current.ProjectId, prototypeId = current.Id, from = current.State, to = command.State }, JsonOptions), command.OccurredAt, true, token); await tx.CommitAsync(token); return value;
    }

    private static async Task DeleteCoreAsync(SqliteConnection c, PrototypeDeleteCommand command, string table, string eventType, CancellationToken token)
    {
        ValidateId(command.Id); await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token); string project; await using (var read = c.CreateCommand()) { read.Transaction = tx; read.CommandText = $"SELECT project_id FROM {table} WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL;"; Add(read, "$tenant", command.TenantId); Add(read, "$id", command.Id); project = await read.ExecuteScalarAsync(token) as string ?? throw new PrototypeNotFoundException(table == "prototypes" ? "prototype" : "visual_reference"); }
        await ExecuteAsync(c, tx, $"UPDATE {table} SET deleted_at=$at{(table == "prototypes" ? ",state='archived',updated_at=$at" : "")} WHERE tenant_id=$tenant AND id=$id;", token, ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", command.Id)); await AppendAsync(c, tx, command.TenantId, eventType, JsonSerializer.Serialize(new { projectId = project, id = command.Id }, JsonOptions), command.OccurredAt, false, token); await tx.CommitAsync(token);
    }

    private static async Task RequireEnabledProjectAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string project, CancellationToken token) { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT prototyping_mode FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL;"; Add(q, "$tenant", tenant); Add(q, "$project", project); var mode = await q.ExecuteScalarAsync(token) as string ?? throw new PrototypeNotFoundException("project"); if (mode == "notApplicable") throw new PrototypeValidationException("Prototyping is waived for this project."); }
    private static async Task<bool> ReferenceExistsAsync(SqliteConnection c, SqliteTransaction tx, string table, string tenant, string project, string id, CancellationToken token) { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE tenant_id=$tenant AND project_id=$project AND id=$id{(table == "prototypes" ? " AND deleted_at IS NULL" : "")});"; Add(q, "$tenant", tenant); Add(q, "$project", project); Add(q, "$id", id); return Convert.ToInt32(await q.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1; }
    private static async Task<PrototypeRecord?> ReadPrototypeAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string id, CancellationToken token) { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"{PrototypeSelect} WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL;"; Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? ReadPrototype(r) : null; }
    private static async Task AppendAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, bool outbox, CancellationToken token)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload); long seq; string prev; await using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(q, "$tenant", tenant); await using var r = await q.ExecuteReaderAsync(token); if (await r.ReadAsync(token)) { seq = r.GetInt64(0) + 1; prev = r.GetString(1); } else { seq = 1; prev = AuditLedgerHash.Genesis; } }
        var hash = AuditLedgerHash.Compute(prev, tenant, seq, type, payload, at); await ExecuteAsync(c, tx, "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$seq,$prev,$hash,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$seq", seq), ("$prev", prev), ("$hash", hash), ("$type", type), ("$payload", payload), ("$at", Store(at))); if (outbox) await ExecuteAsync(c, tx, "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($id,$tenant,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$type", type), ("$payload", payload), ("$at", Store(at)));
    }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string, object)[] values) { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var (name, value) in values) Add(q, name, value); await q.ExecuteNonQueryAsync(token); }
    private static PrototypeRecord ReadPrototype(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), Parse(r.GetString(8)), Parse(r.GetString(9)));
    private static VisualReferenceRecord ReadReference(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), JsonSerializer.Deserialize<string[]>(r.GetString(6), JsonOptions) ?? [], Parse(r.GetString(7)));
    private static void ValidateId(string id) { if (!UlidValue.TryParse(id, out _)) throw new PrototypeValidationException("ID must be a ULID."); }
    private const string PrototypeSelect = "SELECT id,project_id,name,description,state,url,thumbnail_url,source_document_id,created_at,updated_at FROM prototypes";
    private const string ReferenceSelect = "SELECT id,project_id,prototype_id,title,image_url,source,tags_json,created_at FROM visual_references";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand q, string name, object value) => q.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand q, string name, object? value) => q.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
