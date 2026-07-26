using System.Globalization;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Providers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteModelInvocationStore(SqliteWriteDispatcher dispatcher) : IModelInvocationStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task RecordInvocationAsync(
        ModelInvocationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO model_invocations (
                    id, tenant_id, project_id, work_task_id, attempt_id,
                    provider, model, account_alias, input_tokens, output_tokens,
                    estimated_cost_usd, duration_ms, outcome, invoked_at
                ) VALUES (
                    $id, $tenantId, $projectId, $workTaskId, $attemptId,
                    $provider, $model, $accountAlias, $inputTokens, $outputTokens,
                    $estimatedCostUsd, $durationMs, $outcome, $invokedAt
                );
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$tenantId", record.TenantId);
            command.Parameters.AddWithValue("$projectId", record.ProjectId);
            command.Parameters.AddWithValue("$workTaskId", record.WorkTaskId);
            command.Parameters.AddWithValue("$attemptId", record.AttemptId);
            command.Parameters.AddWithValue("$provider", record.Provider);
            command.Parameters.AddWithValue("$model", record.Model);
            command.Parameters.AddWithValue("$accountAlias", record.AccountAlias);
            command.Parameters.AddWithValue("$inputTokens", record.InputTokens);
            command.Parameters.AddWithValue("$outputTokens", record.OutputTokens);
            command.Parameters.AddWithValue("$estimatedCostUsd", (double)record.EstimatedCostUsd);
            command.Parameters.AddWithValue("$durationMs", record.DurationMs);
            command.Parameters.AddWithValue("$outcome", record.Outcome);
            command.Parameters.AddWithValue("$invokedAt", record.InvokedAt.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ModelInvocationRecord>> GetTaskInvocationsAsync(
        string tenantId, string workTaskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(workTaskId);

        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, tenant_id, project_id, work_task_id, attempt_id,
                       provider, model, account_alias, input_tokens, output_tokens,
                       estimated_cost_usd, duration_ms, outcome, invoked_at
                FROM model_invocations
                WHERE tenant_id = $tenantId AND work_task_id = $workTaskId
                ORDER BY invoked_at ASC;
                """;
            command.Parameters.AddWithValue("$tenantId", tenantId);
            command.Parameters.AddWithValue("$workTaskId", workTaskId);

            var items = new List<ModelInvocationRecord>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new ModelInvocationRecord(
                    Id: reader.GetString(0),
                    TenantId: reader.GetString(1),
                    ProjectId: reader.GetString(2),
                    WorkTaskId: reader.GetString(3),
                    AttemptId: reader.GetString(4),
                    Provider: reader.GetString(5),
                    Model: reader.GetString(6),
                    AccountAlias: reader.GetString(7),
                    InputTokens: reader.GetInt32(8),
                    OutputTokens: reader.GetInt32(9),
                    EstimatedCostUsd: Convert.ToDecimal(reader.GetDouble(10), CultureInfo.InvariantCulture),
                    DurationMs: reader.GetInt64(11),
                    Outcome: reader.GetString(12),
                    InvokedAt: DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture)
                ));
            }
            return (IReadOnlyList<ModelInvocationRecord>)items;
        }, cancellationToken);
    }

    public Task<decimal> GetTotalCostAsync(
        string tenantId, string? projectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            if (string.IsNullOrEmpty(projectId))
            {
                command.CommandText = "SELECT COALESCE(SUM(estimated_cost_usd), 0.0) FROM model_invocations WHERE tenant_id = $tenantId;";
            }
            else
            {
                command.CommandText = "SELECT COALESCE(SUM(estimated_cost_usd), 0.0) FROM model_invocations WHERE tenant_id = $tenantId AND project_id = $projectId;";
                command.Parameters.AddWithValue("$projectId", projectId);
            }
            command.Parameters.AddWithValue("$tenantId", tenantId);

            var result = await command.ExecuteScalarAsync(token);
            return Convert.ToDecimal(result ?? 0.0, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }
}
