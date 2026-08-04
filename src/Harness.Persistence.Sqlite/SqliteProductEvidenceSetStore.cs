using System.Globalization;
using Harness.Persistence.Abstractions.Product;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>Ledger append-only dos conjuntos de evidência. Nenhum caminho aqui faz UPDATE.</summary>
public sealed class SqliteProductEvidenceSetStore(SqliteWriteDispatcher dispatcher)
    : IProductEvidenceSetStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ProductEvidenceSetRecord> AppendAsync(
        ProductEvidenceSetRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EvidenceSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.CommitSha);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var insert = connection.CreateCommand();

                // INSERT OR IGNORE: repetir a mesma avaliação não duplica nem sobrescreve. O que é
                // idempotente é o direito de repetir; o conteúdo permanece o do primeiro registro.
                insert.CommandText =
                    "INSERT OR IGNORE INTO product_evidence_sets " +
                    "(tenant_id,evidence_set_id,project_id,workflow_run_id,task_id,attempt_id," +
                    "commit_sha,profile_version,profile_fingerprint,modality,gate_decision," +
                    "plan_json,items_json,findings_json,collectors,created_at) " +
                    "VALUES ($tenant,$id,$project,$run,$task,$attempt,$commit,$version," +
                    "$fingerprint,$modality,$decision,$plan,$items,$findings,$collectors,$at);";
                Add(insert, "$tenant", record.TenantId);
                Add(insert, "$id", record.EvidenceSetId);
                Add(insert, "$project", record.ProjectId);
                Add(insert, "$run", record.WorkflowRunId);
                Add(insert, "$task", record.TaskId);
                Add(insert, "$attempt", record.AttemptId);
                Add(insert, "$commit", record.CommitSha);
                Add(insert, "$version", record.ProfileVersion);
                Add(insert, "$fingerprint", record.ProfileFingerprint);
                Add(insert, "$modality", record.Modality);
                Add(insert, "$decision", record.GateDecision);
                Add(insert, "$plan", record.PlanJson);
                Add(insert, "$items", record.ItemsJson);
                Add(insert, "$findings", record.FindingsJson);
                Add(insert, "$collectors", record.Collectors);
                Add(insert, "$at", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(token);

                return await ReadAsync(connection, record.TenantId, record.EvidenceSetId, token)
                    ?? throw new InvalidOperationException("Evidence set insert did not produce a row.");
            },
            cancellationToken);
    }

    public Task<ProductEvidenceSetRecord?> GetAsync(
        string tenantId, string evidenceSetId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadAsync(connection, tenantId, evidenceSetId, token),
            cancellationToken);

    public Task<IReadOnlyList<ProductEvidenceSetRecord>> ListAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND project_id=$project " +
                    "ORDER BY created_at DESC, evidence_set_id DESC LIMIT $limit;";
                Add(query, "$tenant", tenantId);
                Add(query, "$project", projectId);
                Add(query, "$limit", Math.Clamp(limit, 1, 500));
                var rows = new List<ProductEvidenceSetRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(Map(reader));
                }

                return (IReadOnlyList<ProductEvidenceSetRecord>)rows;
            },
            cancellationToken);

    private static async Task<ProductEvidenceSetRecord?> ReadAsync(
        SqliteConnection connection, string tenantId, string evidenceSetId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$tenant AND evidence_set_id=$id;";
        Add(query, "$tenant", tenantId);
        Add(query, "$id", evidenceSetId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private const string Select =
        "SELECT tenant_id,evidence_set_id,project_id,workflow_run_id,task_id,attempt_id,commit_sha," +
        "profile_version,profile_fingerprint,modality,gate_decision,plan_json,items_json," +
        "findings_json,collectors,created_at FROM product_evidence_sets";

    private static ProductEvidenceSetRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13),
        reader.GetString(14),
        DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture));

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
