using System.Globalization;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Providers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresModelInvocationStore(NpgsqlDataSource dataSource) : IModelInvocationStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task RecordInvocationAsync(
        ModelInvocationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var command = _dataSource.CreateCommand("""
            INSERT INTO model_invocations (
                id, tenant_id, project_id, work_task_id, attempt_id,
                provider, model, account_alias, input_tokens, output_tokens,
                estimated_cost_usd, duration_ms, outcome, invoked_at
            ) VALUES (
                @id, @tenantId, @projectId, @workTaskId, @attemptId,
                @provider, @model, @accountAlias, @inputTokens, @outputTokens,
                @estimatedCostUsd, @durationMs, @outcome, @invokedAt
            );
            """);

        command.Parameters.AddWithValue("@id", record.Id);
        command.Parameters.AddWithValue("@tenantId", record.TenantId);
        command.Parameters.AddWithValue("@projectId", record.ProjectId);
        command.Parameters.AddWithValue("@workTaskId", record.WorkTaskId);
        command.Parameters.AddWithValue("@attemptId", record.AttemptId);
        command.Parameters.AddWithValue("@provider", record.Provider);
        command.Parameters.AddWithValue("@model", record.Model);
        command.Parameters.AddWithValue("@accountAlias", record.AccountAlias);
        command.Parameters.AddWithValue("@inputTokens", record.InputTokens);
        command.Parameters.AddWithValue("@outputTokens", record.OutputTokens);
        command.Parameters.AddWithValue("@estimatedCostUsd", record.EstimatedCostUsd);
        command.Parameters.AddWithValue("@durationMs", record.DurationMs);
        command.Parameters.AddWithValue("@outcome", record.Outcome);
        command.Parameters.AddWithValue("@invokedAt", record.InvokedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModelInvocationRecord>> GetTaskInvocationsAsync(
        string tenantId, string workTaskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(workTaskId);

        await using var command = _dataSource.CreateCommand("""
            SELECT id, tenant_id, project_id, work_task_id, attempt_id,
                   provider, model, account_alias, input_tokens, output_tokens,
                   estimated_cost_usd, duration_ms, outcome, invoked_at
            FROM model_invocations
            WHERE tenant_id = @tenantId AND work_task_id = @workTaskId
            ORDER BY invoked_at ASC;
            """);

        command.Parameters.AddWithValue("@tenantId", tenantId);
        command.Parameters.AddWithValue("@workTaskId", workTaskId);

        var items = new List<ModelInvocationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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
                EstimatedCostUsd: reader.GetDecimal(10),
                DurationMs: reader.GetInt64(11),
                Outcome: reader.GetString(12),
                InvokedAt: reader.GetFieldValue<DateTimeOffset>(13)
            ));
        }

        return items;
    }

    public async Task<decimal> GetTotalCostAsync(
        string tenantId, string? projectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        NpgsqlCommand command;
        if (string.IsNullOrEmpty(projectId))
        {
            command = _dataSource.CreateCommand("SELECT COALESCE(SUM(estimated_cost_usd), 0.0) FROM model_invocations WHERE tenant_id = @tenantId;");
        }
        else
        {
            command = _dataSource.CreateCommand("SELECT COALESCE(SUM(estimated_cost_usd), 0.0) FROM model_invocations WHERE tenant_id = @tenantId AND project_id = @projectId;");
            command.Parameters.AddWithValue("@projectId", projectId);
        }
        command.Parameters.AddWithValue("@tenantId", tenantId);

        await using (command)
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToDecimal(result ?? 0.0m, CultureInfo.InvariantCulture);
        }
    }
}
