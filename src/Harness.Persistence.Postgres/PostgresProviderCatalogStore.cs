using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresProviderCatalogStore(NpgsqlDataSource dataSource) : IProviderCatalogStore
{
    private const string ProviderSelect =
        "SELECT id,kind,name,base_url,enabled FROM harness.providers";

    private const string AccountSelect =
        "SELECT id,provider_id,label,state,quota_limit_usd,quota_used_usd FROM harness.provider_accounts";

    private const string ModelSelect =
        "SELECT id,provider_id,model_name,display_name,capabilities_json::text," +
        "context_window,cost_input,cost_output,enabled FROM harness.provider_models";

    private const string RoutingSelect =
        "SELECT id,project_id,name,rules_json::text,active FROM harness.routing_policies";

    private const string BudgetSelect =
        "SELECT id,scope,scope_id,period,limit_usd,spent_usd,alert_threshold_pct FROM harness.budgets";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] SeedStatements =
    [
        """
        INSERT INTO harness.providers (tenant_id,id,kind,name,base_url,enabled) VALUES
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FG1','openai','OpenAI','https://api.openai.com/v1',true),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FG2','anthropic','Anthropic','https://api.anthropic.com',true),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FG3','ollama','Ollama','http://127.0.0.1:11434',false)
        ON CONFLICT DO NOTHING;
        """,
        """
        INSERT INTO harness.provider_accounts (tenant_id,id,provider_id,label,state,credential_reference,quota_limit_usd,quota_used_usd) VALUES
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FH1','01ARZ3NDEKTSV4RRFFQ69G5FG1','OpenAI account','active','keychain://harness/openai',100,0),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FH2','01ARZ3NDEKTSV4RRFFQ69G5FG2','Anthropic account','disabled','keychain://harness/anthropic',100,0)
        ON CONFLICT DO NOTHING;
        """,
        """
        INSERT INTO harness.provider_models (tenant_id,id,provider_id,model_name,display_name,capabilities_json,context_window,cost_input,cost_output,enabled) VALUES
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FJ1','01ARZ3NDEKTSV4RRFFQ69G5FG1','gpt-5','GPT-5','["chat","code","vision"]',400000,0.00125,0.010,true),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FJ2','01ARZ3NDEKTSV4RRFFQ69G5FG1','gpt-5-codex','GPT-5 Codex','["chat","code"]',400000,0.00125,0.010,true),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FJ3','01ARZ3NDEKTSV4RRFFQ69G5FG2','claude-sonnet','Claude Sonnet','["chat","code","vision"]',200000,0.003,0.015,true),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FJ4','01ARZ3NDEKTSV4RRFFQ69G5FG3','local-code','Local code model','["chat","code"]',32768,NULL,NULL,false)
        ON CONFLICT DO NOTHING;
        """,
        """
        INSERT INTO harness.routing_policies (tenant_id,id,project_id,name,rules_json,active) VALUES
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FK1',NULL,'Default safe routing','[{"taskKind":"code","preferredModelId":"01ARZ3NDEKTSV4RRFFQ69G5FJ2","fallbackModelIds":["01ARZ3NDEKTSV4RRFFQ69G5FJ1"],"maxCostPerAttemptUsd":10.0},{"taskKind":null,"preferredModelId":"01ARZ3NDEKTSV4RRFFQ69G5FJ1","fallbackModelIds":[],"maxCostPerAttemptUsd":5.0}]',true)
        ON CONFLICT DO NOTHING;
        """,
        """
        INSERT INTO harness.budgets (tenant_id,id,scope,scope_id,period,limit_usd,spent_usd,alert_threshold_pct) VALUES
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FM1','global',NULL,'monthly',200,0,80),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FM2','account','01ARZ3NDEKTSV4RRFFQ69G5FH1','monthly',100,0,80),
          ($1,'01ARZ3NDEKTSV4RRFFQ69G5FM3','account','01ARZ3NDEKTSV4RRFFQ69G5FH2','monthly',100,0,80)
        ON CONFLICT DO NOTHING;
        """,
    ];

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, ProviderSelect, afterId, limit, ReadProvider, cancellationToken);

    public Task<ProviderRecord?> GetProviderAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetCoreAsync(tenantId, ProviderSelect, id, ReadProvider, cancellationToken);

    public Task<IReadOnlyList<AccountRecord>> ListAccountsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, AccountSelect, afterId, limit, ReadAccount, cancellationToken);

    public Task<AccountRecord?> GetAccountAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetCoreAsync(tenantId, AccountSelect, id, ReadAccount, cancellationToken);

    public Task<IReadOnlyList<ModelRecord>> ListModelsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, ModelSelect, afterId, limit, ReadModel, cancellationToken);

    public Task<ModelRecord?> GetModelAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetCoreAsync(tenantId, ModelSelect, id, ReadModel, cancellationToken);

    public Task<IReadOnlyList<RoutingPolicyRecord>> ListRoutingPoliciesAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, RoutingSelect, afterId, limit, ReadRouting, cancellationToken);

    public Task<RoutingPolicyRecord?> GetRoutingPolicyAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetCoreAsync(tenantId, RoutingSelect, id, ReadRouting, cancellationToken);

    public Task<IReadOnlyList<BudgetRecord>> ListBudgetsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, BudgetSelect, afterId, limit, ReadBudget, cancellationToken);

    public Task<BudgetRecord?> GetBudgetAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        GetCoreAsync(tenantId, BudgetSelect, id, ReadBudget, cancellationToken);

    public Task<ProviderCatalogRecord> UpdateAsync(
        ProviderCatalogUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateCoreAsync(command, cancellationToken);
    }

    public Task<IReadOnlyList<ModelRecord>> SyncAsync(
        ProviderCatalogSyncCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SyncCoreAsync(command, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string tenantId,
        string select,
        string? afterId,
        int limit,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureAsync(connection, tenantId, cancellationToken);
        var values = new List<T>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"{select} WHERE tenant_id=$1 AND ($2::text IS NULL OR id>$2) ORDER BY id LIMIT $3;";
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(read(reader));
        }

        return values;
    }

    private async Task<T?> GetCoreAsync<T>(
        string tenantId,
        string select,
        string id,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureAsync(connection, tenantId, cancellationToken);
        return await ReadOneAsync(connection, null, tenantId, select, id, read, cancellationToken);
    }

    private static async Task EnsureAsync(
        NpgsqlConnection connection,
        string tenantId,
        CancellationToken cancellationToken)
    {
        foreach (var statement in SeedStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.Parameters.Add(Text(tenantId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<ProviderCatalogRecord> UpdateCoreAsync(
        ProviderCatalogUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureAsync(connection, command.TenantId, cancellationToken);
        using var patch = JsonDocument.Parse(command.PatchJson);
        if (patch.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ProviderCatalogValidationException("Patch must be an object.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{command.TenantId}"));
        ProviderCatalogRecord result = command.Resource switch
        {
            "providers" => await UpdateProviderAsync(
                connection, transaction, command, patch.RootElement, cancellationToken),
            "accounts" => await UpdateAccountAsync(
                connection, transaction, command, patch.RootElement, cancellationToken),
            "models" => await UpdateModelAsync(
                connection, transaction, command, patch.RootElement, cancellationToken),
            "routing-policies" => await UpdateRoutingAsync(
                connection, transaction, command, patch.RootElement, cancellationToken),
            "budgets" => await UpdateBudgetAsync(
                connection, transaction, command, patch.RootElement, cancellationToken),
            _ => throw new ProviderCatalogValidationException("Provider catalog resource is invalid."),
        };
        var payload = AuditPayload(command, $"{command.Resource}/{command.Id} updated.");
        await AppendLedgerAsync(
            connection, transaction, command.TenantId, "providerCatalog.updated",
            payload, command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "audit.eventAppended",
            payload, command.OccurredAt, cancellationToken);
        if (result is AccountRecord account)
        {
            await AppendOutboxAsync(
                connection,
                transaction,
                command.TenantId,
                "quota.updated",
                JsonSerializer.Serialize(
                    new
                    {
                        accountId = account.Id,
                        budgetId = (string?)null,
                        usedUsd = account.QuotaUsedUsd,
                        limitUsd = account.QuotaLimitUsd,
                    },
                    JsonOptions),
                command.OccurredAt,
                cancellationToken);
        }
        else if (result is BudgetRecord budget)
        {
            await AppendOutboxAsync(
                connection,
                transaction,
                command.TenantId,
                "quota.updated",
                JsonSerializer.Serialize(
                    new
                    {
                        accountId = budget.Scope == "account" ? budget.ScopeId : null,
                        budgetId = budget.Id,
                        usedUsd = budget.SpentUsd,
                        limitUsd = budget.LimitUsd,
                    },
                    JsonOptions),
                command.OccurredAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<ProviderRecord> UpdateProviderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProviderCatalogUpdateCommand command,
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        var current = await ReadOneAsync(
            connection, transaction, command.TenantId, ProviderSelect, command.Id,
            ReadProvider, cancellationToken)
            ?? throw new ProviderCatalogNotFoundException("provider");
        var name = ReadString(patch, "name", current.Name, false);
        var baseUrl = ReadNullableString(patch, "baseUrl", current.BaseUrl);
        var enabled = ReadBool(patch, "enabled", current.Enabled);
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.providers SET name=$1,base_url=$2,enabled=$3 WHERE tenant_id=$4 AND id=$5;",
            cancellationToken,
            Text(name),
            NullableText(baseUrl),
            Boolean(enabled),
            Text(command.TenantId),
            Text(command.Id));
        return current with { Name = name, BaseUrl = baseUrl, Enabled = enabled };
    }

    private static async Task<ModelRecord> UpdateModelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProviderCatalogUpdateCommand command,
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        var current = await ReadOneAsync(
            connection, transaction, command.TenantId, ModelSelect, command.Id,
            ReadModel, cancellationToken)
            ?? throw new ProviderCatalogNotFoundException("model");
        var name = ReadString(patch, "displayName", current.DisplayName, false);
        var enabled = ReadBool(patch, "enabled", current.Enabled);
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.provider_models SET display_name=$1,enabled=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text(name),
            Boolean(enabled),
            Text(command.TenantId),
            Text(command.Id));
        return current with { DisplayName = name, Enabled = enabled };
    }

    private static async Task<AccountRecord> UpdateAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProviderCatalogUpdateCommand command,
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        var current = await ReadOneAsync(
            connection, transaction, command.TenantId, AccountSelect, command.Id,
            ReadAccount, cancellationToken)
            ?? throw new ProviderCatalogNotFoundException("account");
        var label = ReadString(patch, "label", current.Label, false);
        var state = ReadString(patch, "state", current.State, false);
        var quotaLimit = ReadNullableDecimal(patch, "quotaLimitUsd", current.QuotaLimitUsd);
        if (state is not ("active" or "disabled" or "quotaExceeded"))
        {
            throw new ProviderCatalogValidationException("Account state is invalid.");
        }

        if (quotaLimit < 0)
        {
            throw new ProviderCatalogValidationException("Account quota limit cannot be negative.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.provider_accounts SET label=$1,state=$2,quota_limit_usd=$3 WHERE tenant_id=$4 AND id=$5;",
            cancellationToken,
            Text(label),
            Text(state),
            quotaLimit is null ? NullableNumeric() : Numeric(quotaLimit.Value),
            Text(command.TenantId),
            Text(command.Id));
        return current with { Label = label, State = state, QuotaLimitUsd = quotaLimit };
    }

    private static async Task<RoutingPolicyRecord> UpdateRoutingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProviderCatalogUpdateCommand command,
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        var current = await ReadOneAsync(
            connection, transaction, command.TenantId, RoutingSelect, command.Id,
            ReadRouting, cancellationToken)
            ?? throw new ProviderCatalogNotFoundException("routing_policy");
        var name = ReadString(patch, "name", current.Name, false);
        var active = ReadBool(patch, "active", current.Active);
        var rules = current.Rules;
        if (patch.TryGetProperty("rules", out var rulesNode) && rulesNode.ValueKind != JsonValueKind.Null)
        {
            rules = JsonSerializer.Deserialize<RoutingRuleRecord[]>(rulesNode.GetRawText(), JsonOptions)
                ?? throw new ProviderCatalogValidationException("Routing rules are invalid.");
            if (rules.Count == 0)
            {
                throw new ProviderCatalogValidationException("At least one routing rule is required.");
            }

            foreach (var rule in rules)
            {
                if (!UlidValue.TryParse(rule.PreferredModelId, out _) ||
                    rule.FallbackModelIds.Any(x => !UlidValue.TryParse(x, out _)) ||
                    rule.MaxCostPerAttemptUsd is <= 0)
                {
                    throw new ProviderCatalogValidationException("Routing rule is invalid.");
                }
            }
        }

        var json = JsonSerializer.Serialize(rules, JsonOptions);
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.routing_policies SET name=$1,rules_json=$2,active=$3 WHERE tenant_id=$4 AND id=$5;",
            cancellationToken,
            Text(name),
            Json(json),
            Boolean(active),
            Text(command.TenantId),
            Text(command.Id));
        return current with { Name = name, Rules = rules, Active = active };
    }

    private static async Task<BudgetRecord> UpdateBudgetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProviderCatalogUpdateCommand command,
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        var current = await ReadOneAsync(
            connection, transaction, command.TenantId, BudgetSelect, command.Id,
            ReadBudget, cancellationToken)
            ?? throw new ProviderCatalogNotFoundException("budget");
        var limit = ReadDecimal(patch, "limitUsd", current.LimitUsd);
        var threshold = ReadDecimal(patch, "alertThresholdPct", current.AlertThresholdPct);
        if (limit < 0 || threshold is < 0 or > 100)
        {
            throw new ProviderCatalogValidationException("Budget values are outside their allowed ranges.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.budgets SET limit_usd=$1,alert_threshold_pct=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Numeric(limit),
            Numeric(threshold),
            Text(command.TenantId),
            Text(command.Id));
        return current with { LimitUsd = limit, AlertThresholdPct = threshold };
    }

    private async Task<IReadOnlyList<ModelRecord>> SyncCoreAsync(
        ProviderCatalogSyncCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureAsync(connection, command.TenantId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{command.TenantId}"));
        if (await ReadOneAsync(
            connection, transaction, command.TenantId, ProviderSelect, command.ProviderId,
            ReadProvider, cancellationToken) is null)
        {
            throw new ProviderCatalogNotFoundException("provider");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.providers SET last_synced_at=$1 WHERE tenant_id=$2 AND id=$3;",
            cancellationToken,
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.ProviderId));
        var models = await ReadManyAsync(
            connection, transaction, command.TenantId,
            $"{ModelSelect} WHERE tenant_id=$1 AND provider_id=$2 ORDER BY id;",
            command.ProviderId, ReadModel, cancellationToken);
        var accounts = await ReadManyAsync(
            connection, transaction, command.TenantId,
            $"{AccountSelect} WHERE tenant_id=$1 AND provider_id=$2 ORDER BY id;",
            command.ProviderId, ReadAccount, cancellationToken);
        foreach (var account in accounts)
        {
            await AppendOutboxAsync(
                connection,
                transaction,
                command.TenantId,
                "quota.updated",
                JsonSerializer.Serialize(
                    new
                    {
                        accountId = account.Id,
                        budgetId = (string?)null,
                        usedUsd = account.QuotaUsedUsd,
                        limitUsd = account.QuotaLimitUsd,
                    },
                    JsonOptions),
                command.OccurredAt,
                cancellationToken);
        }

        var payload = AuditPayload(
            new(command.TenantId, command.ActorProfileId, "providers", command.ProviderId, "{}", command.OccurredAt),
            $"Provider catalog synchronized: {models.Count} model(s).");
        await AppendLedgerAsync(
            connection, transaction, command.TenantId, "provider.catalogSynced",
            payload, command.OccurredAt, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, command.TenantId, "audit.eventAppended",
            payload, command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return models;
    }

    private static string AuditPayload(ProviderCatalogUpdateCommand command, string detail)
    {
        var id = UlidValue.New(command.OccurredAt).ToString();
        return JsonSerializer.Serialize(
            new
            {
                auditEvent = new
                {
                    id,
                    actorKind = "user",
                    actorId = command.ActorProfileId,
                    action = command.Resource == "providers" && command.PatchJson == "{}"
                        ? "provider.catalogSynced"
                        : "providerCatalog.updated",
                    targetType = command.Resource,
                    targetId = command.Id,
                    detail,
                    occurredAt = command.OccurredAt,
                },
            },
            JsonOptions);
    }

    private static string ReadString(JsonElement patch, string key, string current, bool nullable)
    {
        if (!patch.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind != JsonValueKind.String ||
            (!nullable && string.IsNullOrWhiteSpace(node.GetString())))
        {
            throw new ProviderCatalogValidationException($"{key} is invalid.");
        }

        return node.GetString()!.Trim();
    }

    private static string? ReadNullableString(JsonElement patch, string key, string? current)
    {
        if (!patch.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(node.GetString()))
        {
            throw new ProviderCatalogValidationException($"{key} is invalid.");
        }

        return node.GetString()!.Trim();
    }

    private static bool ReadBool(JsonElement patch, string key, bool current)
    {
        if (!patch.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ProviderCatalogValidationException($"{key} is invalid.");
        }

        return node.GetBoolean();
    }

    private static decimal ReadDecimal(JsonElement patch, string key, decimal current)
    {
        if (!patch.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (node.ValueKind != JsonValueKind.Number || !node.TryGetDecimal(out var value))
        {
            throw new ProviderCatalogValidationException($"{key} is invalid.");
        }

        return value;
    }

    private static decimal? ReadNullableDecimal(JsonElement patch, string key, decimal? current)
    {
        if (!patch.TryGetProperty(key, out var node))
        {
            return current;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Number || !node.TryGetDecimal(out var value))
        {
            throw new ProviderCatalogValidationException($"{key} is invalid.");
        }

        return value;
    }

    private static async Task<T?> ReadOneAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string select,
        string id,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{select} WHERE tenant_id=$1 AND id=$2;";
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : null;
    }

    private static async Task<IReadOnlyList<T>> ReadManyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string sql,
        string providerId,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        var values = new List<T>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(providerId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(read(reader));
        }

        return values;
    }

    private static async Task AppendLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var (sequence, previous) = await ReadLedgerTailAsync(
            connection, transaction, tenantId, cancellationToken);
        var hash = AuditLedgerHash.Compute(
            previous, tenantId, sequence, eventType, payload, occurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Bigint(sequence),
            Text(previous),
            Text(hash),
            Text(eventType),
            Json(payload),
            Timestamp(occurredAt));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Text(eventType),
            Json(payload),
            Timestamp(occurredAt));

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProviderRecord ReadProvider(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4));

    private static AccountRecord ReadAccount(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            reader.GetDecimal(5));

    private static ModelRecord ReadModel(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2),
            reader.GetString(3),
            JsonSerializer.Deserialize<string[]>(reader.GetString(4), JsonOptions) ?? [],
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.GetBoolean(8));

    private static RoutingPolicyRecord ReadRouting(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.IsDBNull(1) ? null : reader.GetString(1).TrimEnd(),
            reader.GetString(2),
            JsonSerializer.Deserialize<RoutingRuleRecord[]>(reader.GetString(3), JsonOptions) ?? [],
            reader.GetBoolean(4));

    private static BudgetRecord ReadBudget(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2).TrimEnd(),
            reader.GetString(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6));

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter<decimal> Numeric(decimal value) => new() { TypedValue = value };

    private static NpgsqlParameter NullableNumeric() => new()
    {
        NpgsqlDbType = NpgsqlDbType.Numeric,
        Value = DBNull.Value,
    };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
