using System.Text.Json;
using Harness.Persistence.Abstractions.Governance;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresGovernanceRuntimeStore(NpgsqlDataSource dataSource)
    : IGovernanceRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<GovernanceTurnReceiptRecord> CreateReceiptAsync(
        GovernanceTurnReceiptCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public async Task<GovernanceTurnReceiptRecord> CompleteReceiptAsync(
        GovernanceTurnReceiptCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var current = await ReadAsync(connection, command.TenantId, command.TurnId, cancellationToken);
        if (current is null || current.Version != command.ExpectedVersion ||
            !GovernanceReceiptLifecycle.CanTransition(current.State, command.State))
        {
            throw new GovernanceRuntimeConflictException("Governance receipt transition is stale, missing or invalid.");
        }

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE harness.governance_turn_receipts SET actual_prompt_tokens=$1,state=$2,gate_result=$3," +
            "version=version+1 WHERE tenant_id=$4 AND turn_id=$5 AND version=$6;";
        update.Parameters.Add(Integer(command.ActualPromptTokens));
        update.Parameters.Add(Text(State(command.State)));
        update.Parameters.Add(Text(command.GateResult));
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.TurnId));
        update.Parameters.Add(Bigint(command.ExpectedVersion));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new GovernanceRuntimeConflictException("Governance receipt version is stale or missing.");
        }

        return (await ReadAsync(connection, command.TenantId, command.TurnId, cancellationToken))!;
    }

    public async Task<GovernanceTurnReceiptRecord?> GetReceiptAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, tenantId, turnId, cancellationToken);
    }

    public async Task<IReadOnlyList<GovernanceTurnReceiptRecord>> ListReceiptsAsync(
        string tenantId,
        string? projectId,
        string? afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"{SelectReceipt} WHERE tenant_id=$1 AND ($2 IS NULL OR project_id=$2) " +
            "AND ($3 IS NULL OR turn_id>$3) ORDER BY turn_id LIMIT $4;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        query.Parameters.Add(Text(afterTurnId));
        query.Parameters.Add(Integer(limit));
        var rows = new List<GovernanceTurnReceiptRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(MapReceipt(reader));
        return rows;
    }

    public async Task AppendMetricAsync(
        GovernanceMetricAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO harness.governance_receipt_metrics " +
            "(tenant_id,project_id,turn_id,event_id,kind,document_id,rule_id,detail_code,token_count,occurred_at) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT (tenant_id,event_id) DO NOTHING;";
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Text(command.TurnId));
        insert.Parameters.Add(Text(command.EventId));
        insert.Parameters.Add(Text(Kind(command.Kind)));
        insert.Parameters.Add(Text(command.DocumentId));
        insert.Parameters.Add(Text(command.RuleId));
        insert.Parameters.Add(Text(command.DetailCode));
        insert.Parameters.Add(Integer(command.TokenCount));
        insert.Parameters.Add(Timestamp(command.OccurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GovernanceMetricRecord>> ListMetricsAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,project_id,turn_id,event_id,kind,document_id,rule_id,detail_code,token_count,occurred_at " +
            "FROM harness.governance_receipt_metrics WHERE tenant_id=$1 AND turn_id=$2 ORDER BY occurred_at,event_id;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(turnId));
        var rows = new List<GovernanceMetricRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GovernanceMetricRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseKind(reader.GetString(4)), NullString(reader, 5), NullString(reader, 6),
                NullString(reader, 7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return rows;
    }

    private async Task<GovernanceTurnReceiptRecord> CreateCoreAsync(
        GovernanceTurnReceiptCreateCommand command,
        CancellationToken token)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(token);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO harness.governance_turn_receipts " +
            "(tenant_id,project_id,task_id,attempt_id,turn_id,agent_id,manifest_version,documents_json," +
            "estimated_tokens,actual_prompt_tokens,truncated_json,conflicts_json,cache_hits,provider,model," +
            "occurred_at,bundle_checksum,state,gate_result,version) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,NULL,$10,$11,$12,$13,$14,$15,$16,'selected',NULL,1) " +
            "ON CONFLICT (tenant_id,turn_id) DO NOTHING;";
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Text(command.TaskId));
        insert.Parameters.Add(Text(command.AttemptId));
        insert.Parameters.Add(Text(command.TurnId));
        insert.Parameters.Add(Text(command.AgentId));
        insert.Parameters.Add(Text(command.ManifestVersion));
        insert.Parameters.Add(Json(JsonSerializer.Serialize(command.Documents, JsonOptions)));
        insert.Parameters.Add(Integer(command.EstimatedTokens));
        insert.Parameters.Add(Json(JsonSerializer.Serialize(command.Truncated, JsonOptions)));
        insert.Parameters.Add(Json(JsonSerializer.Serialize(command.Conflicts, JsonOptions)));
        insert.Parameters.Add(Integer(command.CacheHits));
        insert.Parameters.Add(Text(command.Provider));
        insert.Parameters.Add(Text(command.Model));
        insert.Parameters.Add(Timestamp(command.Timestamp));
        insert.Parameters.Add(Text(command.BundleChecksum));
        await insert.ExecuteNonQueryAsync(token);
        var receipt = await ReadAsync(connection, command.TenantId, command.TurnId, token)
            ?? throw new InvalidOperationException("Governance receipt insert did not produce a row.");
        if (!string.Equals(receipt.BundleChecksum, command.BundleChecksum, StringComparison.Ordinal))
        {
            throw new GovernanceRuntimeConflictException("A receipt with the same turn ID has a different bundle checksum.");
        }

        return receipt;
    }

    private static async Task<GovernanceTurnReceiptRecord?> ReadAsync(
        NpgsqlConnection connection,
        string tenantId,
        string turnId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{SelectReceipt} WHERE tenant_id=$1 AND turn_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(turnId));
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? MapReceipt(reader) : null;
    }

    private static GovernanceTurnReceiptRecord MapReceipt(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        JsonSerializer.Deserialize<GovernanceReceiptDocumentRecord[]>(reader.GetFieldValue<string>(7), JsonOptions) ?? [],
        reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
        JsonSerializer.Deserialize<string[]>(reader.GetFieldValue<string>(10), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetFieldValue<string>(11), JsonOptions) ?? [],
        reader.GetInt32(12), reader.GetString(13), NullString(reader, 14), reader.GetFieldValue<DateTimeOffset>(15),
        reader.GetString(16), ParseState(reader.GetString(17)), NullString(reader, 18), reader.GetInt64(19));

    private const string SelectReceipt =
        "SELECT tenant_id,project_id,task_id,attempt_id,turn_id,agent_id,manifest_version,documents_json::text," +
        "estimated_tokens,actual_prompt_tokens,truncated_json::text,conflicts_json::text,cache_hits,provider,model," +
        "occurred_at,bundle_checksum,state,gate_result,version FROM harness.governance_turn_receipts";

    private static void Validate(GovernanceTurnReceiptCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.BundleChecksum);
        ArgumentNullException.ThrowIfNull(command.Documents);
    }

    private static NpgsqlParameter Text(string? value) => value is null
        ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DBNull.Value }
        : new NpgsqlParameter<string> { TypedValue = value };
    private static NpgsqlParameter Integer(int? value) => value.HasValue
        ? new NpgsqlParameter<int> { TypedValue = value.Value }
        : new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = DBNull.Value };
    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
    private static string? NullString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

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
