using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteProviderCatalogStore(SqliteWriteDispatcher dispatcher) : IProviderCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, ProviderSelect, afterId, limit, ReadProvider, cancellationToken);
    public Task<ProviderRecord?> GetProviderAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, ProviderSelect, id, ReadProvider, cancellationToken);
    public Task<IReadOnlyList<AccountRecord>> ListAccountsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, AccountSelect, afterId, limit, ReadAccount, cancellationToken);
    public Task<AccountRecord?> GetAccountAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, AccountSelect, id, ReadAccount, cancellationToken);
    public Task<IReadOnlyList<ModelRecord>> ListModelsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, ModelSelect, afterId, limit, ReadModel, cancellationToken);
    public Task<ModelRecord?> GetModelAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, ModelSelect, id, ReadModel, cancellationToken);
    public Task<IReadOnlyList<RoutingPolicyRecord>> ListRoutingPoliciesAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, RoutingSelect, afterId, limit, ReadRouting, cancellationToken);
    public Task<RoutingPolicyRecord?> GetRoutingPolicyAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, RoutingSelect, id, ReadRouting, cancellationToken);
    public Task<IReadOnlyList<BudgetRecord>> ListBudgetsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => ListAsync(tenantId, BudgetSelect, afterId, limit, ReadBudget, cancellationToken);
    public Task<BudgetRecord?> GetBudgetAsync(string tenantId, string id, CancellationToken cancellationToken = default) => GetAsync(tenantId, BudgetSelect, id, ReadBudget, cancellationToken);
    public Task<ProviderCatalogRecord> UpdateAsync(ProviderCatalogUpdateCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync((c, t) => UpdateCoreAsync(c, command, t), cancellationToken);
    public Task<IReadOnlyList<ModelRecord>> SyncAsync(ProviderCatalogSyncCommand command, CancellationToken cancellationToken = default) => _dispatcher.ExecuteAsync<IReadOnlyList<ModelRecord>>((c, t) => SyncCoreAsync(c, command, t), cancellationToken);

    private Task<IReadOnlyList<T>> ListAsync<T>(string tenant, string select, string? after, int limit, Func<SqliteDataReader, T> read, CancellationToken cancellationToken) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<T>>(async (c, token) =>
        {
            await EnsureAsync(c, tenant, token); var values = new List<T>(); await using var q = c.CreateCommand();
            q.CommandText = $"{select} WHERE tenant_id=$tenant AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(q, "$tenant", tenant); AddNullable(q, "$after", after); Add(q, "$limit", limit); await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) values.Add(read(r)); return values;
        }, cancellationToken);

    private Task<T?> GetAsync<T>(string tenant, string select, string id, Func<SqliteDataReader, T> read, CancellationToken cancellationToken) where T : class =>
        _dispatcher.ExecuteAsync(async (c, token) =>
        {
            await EnsureAsync(c, tenant, token); return await ReadOneAsync(c, null, tenant, select, id, read, token);
        }, cancellationToken);

    private static async Task EnsureAsync(SqliteConnection c, string tenant, CancellationToken token)
    {
        await using var q = c.CreateCommand(); q.CommandText = """
            INSERT OR IGNORE INTO providers(tenant_id,id,kind,name,base_url,enabled) VALUES
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FG1','openai','OpenAI','https://api.openai.com/v1',1),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FG2','anthropic','Anthropic','https://api.anthropic.com',1),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FG3','ollama','Ollama','http://127.0.0.1:11434',0);
            INSERT OR IGNORE INTO provider_accounts(tenant_id,id,provider_id,label,state,credential_reference,quota_limit_usd,quota_used_usd) VALUES
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FH1','01ARZ3NDEKTSV4RRFFQ69G5FG1','OpenAI account','active','keychain://harness/openai',100,0),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FH2','01ARZ3NDEKTSV4RRFFQ69G5FG2','Anthropic account','disabled','keychain://harness/anthropic',100,0);
            INSERT OR IGNORE INTO provider_models(tenant_id,id,provider_id,model_name,display_name,capabilities_json,context_window,cost_input,cost_output,enabled) VALUES
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FJ1','01ARZ3NDEKTSV4RRFFQ69G5FG1','gpt-5','GPT-5','["chat","code","vision"]',400000,0.00125,0.010,1),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FJ2','01ARZ3NDEKTSV4RRFFQ69G5FG1','gpt-5-codex','GPT-5 Codex','["chat","code"]',400000,0.00125,0.010,1),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FJ3','01ARZ3NDEKTSV4RRFFQ69G5FG2','claude-sonnet','Claude Sonnet','["chat","code","vision"]',200000,0.003,0.015,1),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FJ4','01ARZ3NDEKTSV4RRFFQ69G5FG3','local-code','Local code model','["chat","code"]',32768,NULL,NULL,0);
            INSERT OR IGNORE INTO routing_policies(tenant_id,id,project_id,name,rules_json,active) VALUES
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FK1',NULL,'Default safe routing','[{"taskKind":"code","preferredModelId":"01ARZ3NDEKTSV4RRFFQ69G5FJ2","fallbackModelIds":["01ARZ3NDEKTSV4RRFFQ69G5FJ1"],"maxCostPerAttemptUsd":10.0},{"taskKind":null,"preferredModelId":"01ARZ3NDEKTSV4RRFFQ69G5FJ1","fallbackModelIds":[],"maxCostPerAttemptUsd":5.0}]',1);
            INSERT OR IGNORE INTO budgets(tenant_id,id,scope,scope_id,period,limit_usd,spent_usd,alert_threshold_pct) VALUES
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FM1','global',NULL,'monthly',200,0,80),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FM2','account','01ARZ3NDEKTSV4RRFFQ69G5FH1','monthly',100,0,80),
              ($tenant,'01ARZ3NDEKTSV4RRFFQ69G5FM3','account','01ARZ3NDEKTSV4RRFFQ69G5FH2','monthly',100,0,80);
            """;
        Add(q, "$tenant", tenant); await q.ExecuteNonQueryAsync(token);
    }

    private static async Task<ProviderCatalogRecord> UpdateCoreAsync(SqliteConnection c, ProviderCatalogUpdateCommand command, CancellationToken token)
    {
        await EnsureAsync(c, command.TenantId, token); using var patch = JsonDocument.Parse(command.PatchJson);
        if (patch.RootElement.ValueKind != JsonValueKind.Object) throw new ProviderCatalogValidationException("Patch must be an object.");
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        ProviderCatalogRecord result = command.Resource switch
        {
            "providers" => await UpdateProviderAsync(c, tx, command, patch.RootElement, token),
            "accounts" => await UpdateAccountAsync(c, tx, command, patch.RootElement, token),
            "models" => await UpdateModelAsync(c, tx, command, patch.RootElement, token),
            "routing-policies" => await UpdateRoutingAsync(c, tx, command, patch.RootElement, token),
            "budgets" => await UpdateBudgetAsync(c, tx, command, patch.RootElement, token),
            _ => throw new ProviderCatalogValidationException("Provider catalog resource is invalid."),
        };
        var payload = AuditPayload(command, $"{command.Resource}/{command.Id} updated.");
        await AppendLedgerAsync(c, tx, command.TenantId, "providerCatalog.updated", payload, command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "audit.eventAppended", payload, command.OccurredAt, token);
        if (result is AccountRecord account)
            await AppendOutboxAsync(c, tx, command.TenantId, "quota.updated", JsonSerializer.Serialize(new { accountId = account.Id, budgetId = (string?)null, usedUsd = account.QuotaUsedUsd, limitUsd = account.QuotaLimitUsd }, JsonOptions), command.OccurredAt, token);
        else if (result is BudgetRecord budget)
            await AppendOutboxAsync(c, tx, command.TenantId, "quota.updated", JsonSerializer.Serialize(new { accountId = budget.Scope == "account" ? budget.ScopeId : null, budgetId = budget.Id, usedUsd = budget.SpentUsd, limitUsd = budget.LimitUsd }, JsonOptions), command.OccurredAt, token);
        await tx.CommitAsync(token); return result;
    }

    private static async Task<ProviderRecord> UpdateProviderAsync(SqliteConnection c, SqliteTransaction tx, ProviderCatalogUpdateCommand cmd, JsonElement p, CancellationToken token)
    {
        var current = await ReadOneAsync(c, tx, cmd.TenantId, ProviderSelect, cmd.Id, ReadProvider, token) ?? throw new ProviderCatalogNotFoundException("provider");
        var name = ReadString(p, "name", current.Name, false); var baseUrl = ReadNullableString(p, "baseUrl", current.BaseUrl); var enabled = ReadBool(p, "enabled", current.Enabled);
        await ExecuteAsync(c, tx, "UPDATE providers SET name=$name,base_url=$url,enabled=$enabled WHERE tenant_id=$tenant AND id=$id;", token,
            ("$name", name), ("$url", baseUrl ?? (object)DBNull.Value), ("$enabled", enabled ? 1 : 0), ("$tenant", cmd.TenantId), ("$id", cmd.Id));
        return current with { Name = name, BaseUrl = baseUrl, Enabled = enabled };
    }

    private static async Task<ModelRecord> UpdateModelAsync(SqliteConnection c, SqliteTransaction tx, ProviderCatalogUpdateCommand cmd, JsonElement p, CancellationToken token)
    {
        var current = await ReadOneAsync(c, tx, cmd.TenantId, ModelSelect, cmd.Id, ReadModel, token) ?? throw new ProviderCatalogNotFoundException("model");
        var name = ReadString(p, "displayName", current.DisplayName, false); var enabled = ReadBool(p, "enabled", current.Enabled);
        await ExecuteAsync(c, tx, "UPDATE provider_models SET display_name=$name,enabled=$enabled WHERE tenant_id=$tenant AND id=$id;", token,
            ("$name", name), ("$enabled", enabled ? 1 : 0), ("$tenant", cmd.TenantId), ("$id", cmd.Id)); return current with { DisplayName = name, Enabled = enabled };
    }

    private static async Task<AccountRecord> UpdateAccountAsync(SqliteConnection c, SqliteTransaction tx, ProviderCatalogUpdateCommand cmd, JsonElement p, CancellationToken token)
    {
        var current = await ReadOneAsync(c, tx, cmd.TenantId, AccountSelect, cmd.Id, ReadAccount, token) ?? throw new ProviderCatalogNotFoundException("account");
        var label = ReadString(p, "label", current.Label, false); var state = ReadString(p, "state", current.State, false); var quotaLimit = ReadNullableDecimal(p, "quotaLimitUsd", current.QuotaLimitUsd);
        if (state is not ("active" or "disabled" or "quotaExceeded")) throw new ProviderCatalogValidationException("Account state is invalid.");
        if (quotaLimit < 0) throw new ProviderCatalogValidationException("Account quota limit cannot be negative.");
        await ExecuteAsync(c, tx, "UPDATE provider_accounts SET label=$label,state=$state,quota_limit_usd=$limit WHERE tenant_id=$tenant AND id=$id;", token,
            ("$label", label), ("$state", state), ("$limit", quotaLimit ?? (object)DBNull.Value), ("$tenant", cmd.TenantId), ("$id", cmd.Id));
        return current with { Label = label, State = state, QuotaLimitUsd = quotaLimit };
    }

    private static async Task<RoutingPolicyRecord> UpdateRoutingAsync(SqliteConnection c, SqliteTransaction tx, ProviderCatalogUpdateCommand cmd, JsonElement p, CancellationToken token)
    {
        var current = await ReadOneAsync(c, tx, cmd.TenantId, RoutingSelect, cmd.Id, ReadRouting, token) ?? throw new ProviderCatalogNotFoundException("routing_policy");
        var name = ReadString(p, "name", current.Name, false); var active = ReadBool(p, "active", current.Active); var rules = current.Rules;
        if (p.TryGetProperty("rules", out var rulesNode) && rulesNode.ValueKind != JsonValueKind.Null)
        {
            rules = JsonSerializer.Deserialize<RoutingRuleRecord[]>(rulesNode.GetRawText(), JsonOptions) ?? throw new ProviderCatalogValidationException("Routing rules are invalid.");
            if (rules.Count == 0) throw new ProviderCatalogValidationException("At least one routing rule is required.");
            foreach (var rule in rules)
            {
                if (!UlidValue.TryParse(rule.PreferredModelId, out _) || rule.FallbackModelIds.Any(x => !UlidValue.TryParse(x, out _)) || rule.MaxCostPerAttemptUsd is <= 0)
                    throw new ProviderCatalogValidationException("Routing rule is invalid.");
            }
        }
        var json = JsonSerializer.Serialize(rules, JsonOptions);
        await ExecuteAsync(c, tx, "UPDATE routing_policies SET name=$name,rules_json=$rules,active=$active WHERE tenant_id=$tenant AND id=$id;", token,
            ("$name", name), ("$rules", json), ("$active", active ? 1 : 0), ("$tenant", cmd.TenantId), ("$id", cmd.Id)); return current with { Name = name, Rules = rules, Active = active };
    }

    private static async Task<BudgetRecord> UpdateBudgetAsync(SqliteConnection c, SqliteTransaction tx, ProviderCatalogUpdateCommand cmd, JsonElement p, CancellationToken token)
    {
        var current = await ReadOneAsync(c, tx, cmd.TenantId, BudgetSelect, cmd.Id, ReadBudget, token) ?? throw new ProviderCatalogNotFoundException("budget");
        var limit = ReadDecimal(p, "limitUsd", current.LimitUsd); var threshold = ReadDecimal(p, "alertThresholdPct", current.AlertThresholdPct);
        if (limit < 0 || threshold is < 0 or > 100) throw new ProviderCatalogValidationException("Budget values are outside their allowed ranges.");
        await ExecuteAsync(c, tx, "UPDATE budgets SET limit_usd=$limit,alert_threshold_pct=$threshold WHERE tenant_id=$tenant AND id=$id;", token,
            ("$limit", limit), ("$threshold", threshold), ("$tenant", cmd.TenantId), ("$id", cmd.Id)); return current with { LimitUsd = limit, AlertThresholdPct = threshold };
    }

    private static async Task<IReadOnlyList<ModelRecord>> SyncCoreAsync(SqliteConnection c, ProviderCatalogSyncCommand command, CancellationToken token)
    {
        await EnsureAsync(c, command.TenantId, token); await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        if (await ReadOneAsync(c, tx, command.TenantId, ProviderSelect, command.ProviderId, ReadProvider, token) is null) throw new ProviderCatalogNotFoundException("provider");
        await ExecuteAsync(c, tx, "UPDATE providers SET last_synced_at=$at WHERE tenant_id=$tenant AND id=$id;", token,
            ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", command.ProviderId));
        var models = await ReadManyAsync(c, tx, command.TenantId, $"{ModelSelect} WHERE tenant_id=$tenant AND provider_id=$provider ORDER BY id", q => Add(q, "$provider", command.ProviderId), ReadModel, token);
        var accounts = await ReadManyAsync(c, tx, command.TenantId, $"{AccountSelect} WHERE tenant_id=$tenant AND provider_id=$provider ORDER BY id", q => Add(q, "$provider", command.ProviderId), ReadAccount, token);
        foreach (var account in accounts) await AppendOutboxAsync(c, tx, command.TenantId, "quota.updated",
            JsonSerializer.Serialize(new { accountId = account.Id, budgetId = (string?)null, usedUsd = account.QuotaUsedUsd, limitUsd = account.QuotaLimitUsd }, JsonOptions), command.OccurredAt, token);
        var payload = AuditPayload(new(command.TenantId, command.ActorProfileId, "providers", command.ProviderId, "{}", command.OccurredAt), $"Provider catalog synchronized: {models.Count} model(s).");
        await AppendLedgerAsync(c, tx, command.TenantId, "provider.catalogSynced", payload, command.OccurredAt, token);
        await AppendOutboxAsync(c, tx, command.TenantId, "audit.eventAppended", payload, command.OccurredAt, token);
        await tx.CommitAsync(token); return models;
    }

    private static string AuditPayload(ProviderCatalogUpdateCommand c, string detail)
    {
        var id = UlidValue.New(c.OccurredAt).ToString(); return JsonSerializer.Serialize(new { auditEvent = new { id, actorKind = "user", actorId = c.ActorProfileId, action = c.Resource == "providers" && c.PatchJson == "{}" ? "provider.catalogSynced" : "providerCatalog.updated", targetType = c.Resource, targetId = c.Id, detail, occurredAt = c.OccurredAt } }, JsonOptions);
    }
    private static string ReadString(JsonElement p, string key, string current, bool nullable) { if (!p.TryGetProperty(key, out var n) || n.ValueKind == JsonValueKind.Null) return current; if (n.ValueKind != JsonValueKind.String || (!nullable && string.IsNullOrWhiteSpace(n.GetString()))) throw new ProviderCatalogValidationException($"{key} is invalid."); return n.GetString()!.Trim(); }
    private static string? ReadNullableString(JsonElement p, string key, string? current) { if (!p.TryGetProperty(key, out var n)) return current; if (n.ValueKind == JsonValueKind.Null) return null; if (n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())) throw new ProviderCatalogValidationException($"{key} is invalid."); return n.GetString()!.Trim(); }
    private static bool ReadBool(JsonElement p, string key, bool current) { if (!p.TryGetProperty(key, out var n) || n.ValueKind == JsonValueKind.Null) return current; if (n.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ProviderCatalogValidationException($"{key} is invalid."); return n.GetBoolean(); }
    private static decimal ReadDecimal(JsonElement p, string key, decimal current) { if (!p.TryGetProperty(key, out var n) || n.ValueKind == JsonValueKind.Null) return current; if (n.ValueKind != JsonValueKind.Number || !n.TryGetDecimal(out var value)) throw new ProviderCatalogValidationException($"{key} is invalid."); return value; }
    private static decimal? ReadNullableDecimal(JsonElement p, string key, decimal? current) { if (!p.TryGetProperty(key, out var n) || n.ValueKind == JsonValueKind.Null) return current; if (n.ValueKind != JsonValueKind.Number || !n.TryGetDecimal(out var value)) throw new ProviderCatalogValidationException($"{key} is invalid."); return value; }

    private static async Task<T?> ReadOneAsync<T>(SqliteConnection c, SqliteTransaction? tx, string tenant, string select, string id, Func<SqliteDataReader, T> read, CancellationToken token) where T : class
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = $"{select} WHERE tenant_id=$tenant AND id=$id;"; Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? read(r) : null; }
    private static async Task<IReadOnlyList<T>> ReadManyAsync<T>(SqliteConnection c, SqliteTransaction tx, string tenant, string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> read, CancellationToken token)
    { var values = new List<T>(); await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; Add(q, "$tenant", tenant); bind(q); await using var r = await q.ExecuteReaderAsync(token); while (await r.ReadAsync(token)) values.Add(read(r)); return values; }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string, object)[] values)
    { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var (name, value) in values) Add(q, name, value); await q.ExecuteNonQueryAsync(token); }
    private static async Task AppendLedgerAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token)
    { long seq; string prev; await using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(q, "$tenant", tenant); await using var r = await q.ExecuteReaderAsync(token); if (await r.ReadAsync(token)) { seq = r.GetInt64(0) + 1; prev = r.GetString(1); } else { seq = 1; prev = AuditLedgerHash.Genesis; } } var hash = AuditLedgerHash.Compute(prev, tenant, seq, type, payload, at); await ExecuteAsync(c, tx, "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$seq,$prev,$hash,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$seq", seq), ("$prev", prev), ("$hash", hash), ("$type", type), ("$payload", payload), ("$at", Store(at))); }
    private static Task AppendOutboxAsync(SqliteConnection c, SqliteTransaction tx, string tenant, string type, string payload, DateTimeOffset at, CancellationToken token) => ExecuteAsync(c, tx, "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($id,$tenant,$type,$payload,$at);", token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$type", type), ("$payload", payload), ("$at", Store(at)));

    private static ProviderRecord ReadProvider(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) == 1);
    private static AccountRecord ReadAccount(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.GetDecimal(5));
    private static ModelRecord ReadModel(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), JsonSerializer.Deserialize<string[]>(r.GetString(4), JsonOptions) ?? [], r.GetInt32(5), r.IsDBNull(6) ? null : r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetInt32(8) == 1);
    private static RoutingPolicyRecord ReadRouting(SqliteDataReader r) => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<RoutingRuleRecord[]>(r.GetString(3), JsonOptions) ?? [], r.GetInt32(4) == 1);
    private static BudgetRecord ReadBudget(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6));
    private const string ProviderSelect = "SELECT id,kind,name,base_url,enabled FROM providers";
    private const string AccountSelect = "SELECT id,provider_id,label,state,quota_limit_usd,quota_used_usd FROM provider_accounts";
    private const string ModelSelect = "SELECT id,provider_id,model_name,display_name,capabilities_json,context_window,cost_input,cost_output,enabled FROM provider_models";
    private const string RoutingSelect = "SELECT id,project_id,name,rules_json,active FROM routing_policies";
    private const string BudgetSelect = "SELECT id,scope,scope_id,period,limit_usd,spent_usd,alert_threshold_pct FROM budgets";
    private static string Store(DateTimeOffset v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand q, string n, object v) => q.Parameters.AddWithValue(n, v);
    private static void AddNullable(SqliteCommand q, string n, object? v) => q.Parameters.AddWithValue(n, v ?? DBNull.Value);
}
