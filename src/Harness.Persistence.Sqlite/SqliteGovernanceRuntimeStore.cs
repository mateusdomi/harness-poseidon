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

    public Task<int> LinkEvidenceAsync(
        GovernanceReceiptEvidenceLinkCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => LinkEvidenceCoreAsync(connection, command, token), cancellationToken);
    }

    private static async Task<int> LinkEvidenceCoreAsync(
        SqliteConnection connection,
        GovernanceReceiptEvidenceLinkCommand command,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{SelectReceipt} WHERE tenant_id=$tenant AND attempt_id=$attempt;";
        Add(query, "$tenant", command.TenantId);
        Add(query, "$attempt", command.AttemptId);

        var receipts = new List<GovernanceTurnReceiptRecord>();
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                receipts.Add(MapReceipt(reader));
            }
        }

        var linked = 0;
        foreach (var receipt in receipts)
        {
            var context = (receipt.Context ?? new GovernanceReceiptContextRecord()) with
            {
                EvidenceSetId = command.EvidenceSetId,
                EvidenceCommitSha = command.EvidenceCommitSha,
                GateDecision = command.GateDecision,
            };

            await using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE governance_turn_receipts SET context_json=$context,version=version+1 " +
                "WHERE tenant_id=$tenant AND turn_id=$turn AND version=$version;";
            Add(update, "$context", JsonSerializer.Serialize(context, JsonOptions));
            Add(update, "$tenant", receipt.TenantId);
            Add(update, "$turn", receipt.TurnId);
            Add(update, "$version", receipt.Version);
            linked += await update.ExecuteNonQueryAsync(token);
        }

        return linked;
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

    public Task<ContextSnapshotRecord> CreateContextSnapshotAsync(
        ContextSnapshotCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateContextSnapshotCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<ContextSnapshotRecord?> GetContextSnapshotAsync(
        string tenantId,
        string snapshotId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadContextSnapshotAsync(connection, tenantId, snapshotId, token),
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
            "occurred_at,bundle_checksum,state,gate_result,version,context_json) " +
            "VALUES ($tenant,$project,$task,$attempt,$turn,$agent,$manifest,$documents,$estimated,NULL," +
            "$truncated,$conflicts,$hits,$provider,$model,$at,$checksum,'selected',NULL,1,$context);";
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
        Add(insert, "$context", command.Context is null
            ? null
            : JsonSerializer.Serialize(command.Context, JsonOptions));
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

    private static async Task<ContextSnapshotRecord> CreateContextSnapshotCoreAsync(
        SqliteConnection connection,
        ContextSnapshotCreateCommand command,
        CancellationToken token)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT OR IGNORE INTO context_snapshots " +
            "(tenant_id,snapshot_id,project_id,work_task_id,execution_id,manifest_version," +
            "bundle_manifest_ids_json,sources_json,assembled_context_hash,token_count,created_at) " +
            "VALUES ($tenant,$snapshot,$project,$task,$execution,$manifest,$bundles,$sources,$hash,$tokens,$at);";
        Add(insert, "$tenant", command.TenantId);
        Add(insert, "$snapshot", command.SnapshotId);
        Add(insert, "$project", command.ProjectId);
        Add(insert, "$task", command.WorkTaskId);
        Add(insert, "$execution", command.ExecutionId);
        Add(insert, "$manifest", command.ManifestVersion);
        Add(insert, "$bundles", JsonSerializer.Serialize(command.BundleManifestIds, JsonOptions));
        Add(insert, "$sources", JsonSerializer.Serialize(command.Sources, JsonOptions));
        Add(insert, "$hash", command.AssembledContextHash);
        Add(insert, "$tokens", command.TokenCount);
        Add(insert, "$at", Store(command.CreatedAt));
        await insert.ExecuteNonQueryAsync(token);

        var snapshot = await ReadContextSnapshotAsync(
            connection,
            command.TenantId,
            command.SnapshotId,
            token) ?? throw new InvalidOperationException("Context snapshot insert did not produce a row.");
        if (!string.Equals(snapshot.AssembledContextHash, command.AssembledContextHash, StringComparison.Ordinal))
        {
            throw new GovernanceRuntimeConflictException(
                "A context snapshot with the same ID has a different assembled context hash.");
        }

        return snapshot;
    }

    private static async Task<ContextSnapshotRecord?> ReadContextSnapshotAsync(
        SqliteConnection connection,
        string tenantId,
        string snapshotId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,snapshot_id,project_id,work_task_id,execution_id,manifest_version," +
            "bundle_manifest_ids_json,sources_json,assembled_context_hash,token_count,created_at " +
            "FROM context_snapshots WHERE tenant_id=$tenant AND snapshot_id=$snapshot;";
        Add(query, "$tenant", tenantId);
        Add(query, "$snapshot", snapshotId);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        return new ContextSnapshotRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions) ?? [],
            JsonSerializer.Deserialize<ContextSnapshotSourceRecord[]>(reader.GetString(7), JsonOptions) ?? [],
            reader.GetString(8),
            reader.GetInt32(9),
            Parse(reader.GetString(10)));
    }

    private static GovernanceTurnReceiptRecord MapReceipt(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        JsonSerializer.Deserialize<GovernanceReceiptDocumentRecord[]>(reader.GetString(7), JsonOptions) ?? [],
        reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
        JsonSerializer.Deserialize<string[]>(reader.GetString(10), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions) ?? [],
        reader.GetInt32(12), reader.GetString(13), NullString(reader, 14), Parse(reader.GetString(15)),
        reader.GetString(16), ParseState(reader.GetString(17)), NullString(reader, 18), reader.GetInt64(19),
        reader.IsDBNull(20)
            ? null
            : JsonSerializer.Deserialize<GovernanceReceiptContextRecord>(reader.GetString(20), JsonOptions));

    private const string SelectReceipt =
        "SELECT tenant_id,project_id,task_id,attempt_id,turn_id,agent_id,manifest_version,documents_json," +
        "estimated_tokens,actual_prompt_tokens,truncated_json,conflicts_json,cache_hits,provider,model," +
        "occurred_at,bundle_checksum,state,gate_result,version,context_json FROM governance_turn_receipts";

    private static void Validate(GovernanceTurnReceiptCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.BundleChecksum);
        ArgumentNullException.ThrowIfNull(command.Documents);
    }

    private static void Validate(ContextSnapshotCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ManifestVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AssembledContextHash);
        ArgumentNullException.ThrowIfNull(command.BundleManifestIds);
        ArgumentNullException.ThrowIfNull(command.Sources);
        if (command.TokenCount < 0) throw new ArgumentOutOfRangeException(nameof(command));
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
