using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Governance;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteGovernanceRuntimeStore(SqliteWriteDispatcher dispatcher)
    : IGovernanceRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<GovernanceTurnReceiptRecord> CreateReceiptAsync(
        GovernanceTurnReceiptCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync((connection, token) => CreateCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<GovernanceTurnReceiptRecord> CompleteReceiptAsync(
        GovernanceTurnReceiptCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync((connection, token) => CompleteCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<GovernanceTurnReceiptRecord?> GetReceiptAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ReadAsync(connection, tenantId, turnId, token), cancellationToken);

    public Task<IReadOnlyList<GovernanceTurnReceiptRecord>> ListReceiptsAsync(
        string tenantId,
        string? projectId,
        string? afterTurnId,
        int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<GovernanceTurnReceiptRecord>>(
            (connection, token) => ListCoreAsync(connection, tenantId, projectId, afterTurnId, limit, token),
            cancellationToken);

    public Task AppendMetricAsync(
        GovernanceMetricAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync((connection, token) => AppendMetricCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<IReadOnlyList<GovernanceMetricRecord>> ListMetricsAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<GovernanceMetricRecord>>(
            (connection, token) => ListMetricsCoreAsync(connection, tenantId, turnId, token),
            cancellationToken);

    private static async Task<GovernanceTurnReceiptRecord> CreateCoreAsync(
        SqliteConnection connection,
        GovernanceTurnReceiptCreateCommand command,
        CancellationToken token)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT OR IGNORE INTO governance_turn_receipts " +
            "(tenant_id,project_id,task_id,attempt_id,turn_id,agent_id,manifest_version,documents_json," +
            "estimated_tokens,actual_prompt_tokens,truncated_json,conflicts_json,cache_hits,provider,model," +
            "occurred_at,bundle_checksum,state,gate_result,version) " +
            "VALUES ($tenant,$project,$task,$attempt,$turn,$agent,$manifest,$documents,$estimated,NULL," +
            "$truncated,$conflicts,$hits,$provider,$model,$at,$checksum,'selected',NULL,1);";
        Add(insert, "$tenant", command.TenantId);
        Add(insert, "$project", command.ProjectId);
        Add(insert, "$task", command.TaskId);
        Add(insert, "$attempt", command.AttemptId);
        Add(insert, "$turn", command.TurnId);
        Add(insert, "$agent", command.AgentId);
        Add(insert, "$manifest", command.ManifestVersion);
        Add(insert, "$documents", JsonSerializer.Serialize(command.Documents, JsonOptions));
        Add(insert, "$estimated", command.EstimatedTokens);
        Add(insert, "$truncated", JsonSerializer.Serialize(command.Truncated, JsonOptions));
        Add(insert, "$conflicts", JsonSerializer.Serialize(command.Conflicts, JsonOptions));
        Add(insert, "$hits", command.CacheHits);
        Add(insert, "$provider", command.Provider);
        Add(insert, "$model", command.Model);
        Add(insert, "$at", Store(command.Timestamp));
        Add(insert, "$checksum", command.BundleChecksum);
        await insert.ExecuteNonQueryAsync(token);
        var receipt = await ReadAsync(connection, command.TenantId, command.TurnId, token)
            ?? throw new InvalidOperationException("Governance receipt insert did not produce a row.");
        if (!string.Equals(receipt.BundleChecksum, command.BundleChecksum, StringComparison.Ordinal))
        {
            throw new GovernanceRuntimeConflictException("A receipt with the same turn ID has a different bundle checksum.");
        }

        return receipt;
    }

    private static async Task<GovernanceTurnReceiptRecord> CompleteCoreAsync(
        SqliteConnection connection,
        GovernanceTurnReceiptCompleteCommand command,
        CancellationToken token)
    {
        var current = await ReadAsync(connection, command.TenantId, command.TurnId, token);
        if (current is null || current.Version != command.ExpectedVersion ||
            !GovernanceReceiptLifecycle.CanTransition(current.State, command.State))
        {
            throw new GovernanceRuntimeConflictException("Governance receipt transition is stale, missing or invalid.");
        }

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE governance_turn_receipts SET actual_prompt_tokens=$tokens,state=$state,gate_result=$gate," +
            "version=version+1 WHERE tenant_id=$tenant AND turn_id=$turn AND version=$version;";
        Add(update, "$tokens", command.ActualPromptTokens);
        Add(update, "$state", State(command.State));
        Add(update, "$gate", command.GateResult);
        Add(update, "$tenant", command.TenantId);
        Add(update, "$turn", command.TurnId);
        Add(update, "$version", command.ExpectedVersion);
        if (await update.ExecuteNonQueryAsync(token) != 1)
        {
            throw new GovernanceRuntimeConflictException("Governance receipt version is stale or missing.");
        }

        return (await ReadAsync(connection, command.TenantId, command.TurnId, token))!;
    }

    private static async Task<GovernanceTurnReceiptRecord?> ReadAsync(
        SqliteConnection connection,
        string tenantId,
        string turnId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{SelectReceipt} WHERE tenant_id=$tenant AND turn_id=$turn;";
        Add(query, "$tenant", tenantId);
        Add(query, "$turn", turnId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? MapReceipt(reader) : null;
    }

    private static async Task<IReadOnlyList<GovernanceTurnReceiptRecord>> ListCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string? projectId,
        string? afterTurnId,
        int limit,
        CancellationToken token)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var query = connection.CreateCommand();
        query.CommandText = $"{SelectReceipt} WHERE tenant_id=$tenant " +
            "AND ($project IS NULL OR project_id=$project) AND ($after IS NULL OR turn_id>$after) " +
            "ORDER BY turn_id LIMIT $limit;";
        Add(query, "$tenant", tenantId);
        Add(query, "$project", projectId);
        Add(query, "$after", afterTurnId);
        Add(query, "$limit", limit);
        var rows = new List<GovernanceTurnReceiptRecord>();
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(MapReceipt(reader));
        return rows;
    }

    private static async Task AppendMetricCoreAsync(
        SqliteConnection connection,
        GovernanceMetricAppendCommand command,
        CancellationToken token)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT OR IGNORE INTO governance_receipt_metrics " +
            "(tenant_id,project_id,turn_id,event_id,kind,document_id,rule_id,detail_code,token_count,occurred_at) " +
            "VALUES ($tenant,$project,$turn,$event,$kind,$document,$rule,$detail,$tokens,$at);";
        Add(insert, "$tenant", command.TenantId);
        Add(insert, "$project", command.ProjectId);
        Add(insert, "$turn", command.TurnId);
        Add(insert, "$event", command.EventId);
        Add(insert, "$kind", Kind(command.Kind));
        Add(insert, "$document", command.DocumentId);
        Add(insert, "$rule", command.RuleId);
        Add(insert, "$detail", command.DetailCode);
        Add(insert, "$tokens", command.TokenCount);
        Add(insert, "$at", Store(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(token);
    }

    private static async Task<IReadOnlyList<GovernanceMetricRecord>> ListMetricsCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string turnId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,project_id,turn_id,event_id,kind,document_id,rule_id,detail_code,token_count,occurred_at " +
            "FROM governance_receipt_metrics WHERE tenant_id=$tenant AND turn_id=$turn ORDER BY occurred_at,event_id;";
        Add(query, "$tenant", tenantId);
        Add(query, "$turn", turnId);
        var rows = new List<GovernanceMetricRecord>();
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new GovernanceMetricRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseKind(reader.GetString(4)), NullString(reader, 5), NullString(reader, 6),
                NullString(reader, 7), reader.IsDBNull(8) ? null : reader.GetInt32(8), Parse(reader.GetString(9))));
        }

        return rows;
    }

    private static GovernanceTurnReceiptRecord MapReceipt(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        JsonSerializer.Deserialize<GovernanceReceiptDocumentRecord[]>(reader.GetString(7), JsonOptions) ?? [],
        reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
        JsonSerializer.Deserialize<string[]>(reader.GetString(10), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions) ?? [],
        reader.GetInt32(12), reader.GetString(13), NullString(reader, 14), Parse(reader.GetString(15)),
        reader.GetString(16), ParseState(reader.GetString(17)), NullString(reader, 18), reader.GetInt64(19));

    private const string SelectReceipt =
        "SELECT tenant_id,project_id,task_id,attempt_id,turn_id,agent_id,manifest_version,documents_json," +
        "estimated_tokens,actual_prompt_tokens,truncated_json,conflicts_json,cache_hits,provider,model," +
        "occurred_at,bundle_checksum,state,gate_result,version FROM governance_turn_receipts";

    private static void Validate(GovernanceTurnReceiptCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.BundleChecksum);
        ArgumentNullException.ThrowIfNull(command.Documents);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? NullString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string State(GovernanceReceiptState value) => value switch
    {
        GovernanceReceiptState.Selected => "selected",
        GovernanceReceiptState.Delivered => "delivered",
        GovernanceReceiptState.Completed => "completed",
        GovernanceReceiptState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static GovernanceReceiptState ParseState(string value) => value switch
    {
        "selected" => GovernanceReceiptState.Selected,
        "delivered" => GovernanceReceiptState.Delivered,
        "completed" => GovernanceReceiptState.Completed,
        "failed" => GovernanceReceiptState.Failed,
        _ => throw new InvalidOperationException("Unknown governance receipt state."),
    };

    private static string Kind(GovernanceMetricKind value) => value switch
    {
        GovernanceMetricKind.Selected => "selected",
        GovernanceMetricKind.Delivered => "delivered",
        GovernanceMetricKind.OpenedByTool => "opened_by_tool",
        GovernanceMetricKind.RuleTriggered => "rule_triggered",
        GovernanceMetricKind.ViolationDetected => "violation_detected",
        GovernanceMetricKind.ItemTruncated => "item_truncated",
        GovernanceMetricKind.GateResult => "gate_result",
        GovernanceMetricKind.PatchApplied => "patch_applied",
        GovernanceMetricKind.PatchRejected => "patch_rejected",
        GovernanceMetricKind.EvaluatorVerdict => "evaluator_verdict",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static GovernanceMetricKind ParseKind(string value) => value switch
    {
        "selected" => GovernanceMetricKind.Selected,
        "delivered" => GovernanceMetricKind.Delivered,
        "opened_by_tool" => GovernanceMetricKind.OpenedByTool,
        "rule_triggered" => GovernanceMetricKind.RuleTriggered,
        "violation_detected" => GovernanceMetricKind.ViolationDetected,
        "item_truncated" => GovernanceMetricKind.ItemTruncated,
        "gate_result" => GovernanceMetricKind.GateResult,
        "patch_applied" => GovernanceMetricKind.PatchApplied,
        "patch_rejected" => GovernanceMetricKind.PatchRejected,
        "evaluator_verdict" => GovernanceMetricKind.EvaluatorVerdict,
        _ => throw new InvalidOperationException("Unknown governance metric kind."),
    };
}
