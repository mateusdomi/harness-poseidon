using Harness.Persistence.Abstractions.Product;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>Paridade do ledger append-only de evidência no modo servidor.</summary>
public sealed class PostgresProductEvidenceSetStore(NpgsqlDataSource dataSource)
    : IProductEvidenceSetStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<ProductEvidenceSetRecord> AppendAsync(
        ProductEvidenceSetRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EvidenceSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.CommitSha);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO harness.product_evidence_sets " +
            "(tenant_id,evidence_set_id,project_id,workflow_run_id,task_id,attempt_id,commit_sha," +
            "profile_version,profile_fingerprint,modality,gate_decision,plan_json,items_json," +
            "findings_json,collectors,created_at) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16) " +
            "ON CONFLICT (tenant_id,evidence_set_id) DO NOTHING;";
        insert.Parameters.Add(Text(record.TenantId));
        insert.Parameters.Add(Text(record.EvidenceSetId));
        insert.Parameters.Add(Text(record.ProjectId));
        insert.Parameters.Add(Text(record.WorkflowRunId));
        insert.Parameters.Add(Text(record.TaskId));
        insert.Parameters.Add(Text(record.AttemptId));
        insert.Parameters.Add(Text(record.CommitSha));
        insert.Parameters.Add(Integer(record.ProfileVersion));
        insert.Parameters.Add(Text(record.ProfileFingerprint));
        insert.Parameters.Add(Text(record.Modality));
        insert.Parameters.Add(Text(record.GateDecision));
        insert.Parameters.Add(Json(record.PlanJson));
        insert.Parameters.Add(Json(record.ItemsJson));
        insert.Parameters.Add(Json(record.FindingsJson));
        insert.Parameters.Add(Text(record.Collectors));
        insert.Parameters.Add(Timestamp(record.CreatedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        return (await ReadAsync(connection, record.TenantId, record.EvidenceSetId, cancellationToken))!;
    }

    public async Task<ProductEvidenceSetRecord?> GetAsync(
        string tenantId, string evidenceSetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, tenantId, evidenceSetId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductEvidenceSetRecord>> ListAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND project_id=$2 " +
            "ORDER BY created_at DESC, evidence_set_id DESC LIMIT $3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        query.Parameters.Add(Integer(Math.Clamp(limit, 1, 500)));
        var rows = new List<ProductEvidenceSetRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static async Task<ProductEvidenceSetRecord?> ReadAsync(
        NpgsqlConnection connection, string tenantId, string evidenceSetId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND evidence_set_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(evidenceSetId));
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private const string Select =
        "SELECT tenant_id,evidence_set_id,project_id,workflow_run_id,task_id,attempt_id,commit_sha," +
        "profile_version,profile_fingerprint,modality,gate_decision,plan_json::text," +
        "items_json::text,findings_json::text,collectors,created_at " +
        "FROM harness.product_evidence_sets";

    private static ProductEvidenceSetRecord Map(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9),
        reader.GetString(10), reader.GetFieldValue<string>(11), reader.GetFieldValue<string>(12),
        reader.GetFieldValue<string>(13), reader.GetString(14),
        reader.GetFieldValue<DateTimeOffset>(15));

    /// <summary>Texto anulável: ausência vira NULL, nunca a string "null".</summary>
    private static NpgsqlParameter Text(string? value) => value is null
        ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DBNull.Value }
        : new NpgsqlParameter<string> { TypedValue = value };
    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };
    private static NpgsqlParameter<string> Json(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
}
