using System.Text.Json;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Plano de fase materializado em PostgreSQL. Paridade estrita com
/// <c>SqlitePhaseObligationStore</c>; ver <see cref="IPhaseObligationStore"/> para a intenção.
/// </summary>
public sealed class PostgresPhaseObligationStore(NpgsqlDataSource dataSource) : IPhaseObligationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<int> EnsurePlanAsync(
        PhaseObligationPlanCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var created = 0;
        var tick = 0;
        foreach (var obligation in command.Obligations)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO harness.phase_obligations
                (tenant_id,obligation_id,project_id,run_id,phase_key,obligation_key,plan_version,
                 kind,description,required,weight,source,completion_criteria,card_id,objective_key,
                 artifact_ref,state,evidence_json,reason,created_at,updated_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,'pending','[]'::jsonb,$17,$18,$18)
                ON CONFLICT DO NOTHING;
                """;
            var id = UlidValue.New(command.OccurredAt.AddMilliseconds(tick++)).ToString();
            insert.Parameters.Add(Text(command.TenantId));
            insert.Parameters.Add(Text(id));
            insert.Parameters.Add(Text(command.ProjectId));
            insert.Parameters.Add(Text(command.RunId));
            insert.Parameters.Add(Text(command.PhaseKey));
            insert.Parameters.Add(Text(obligation.ObligationKey));
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = command.PlanVersion });
            insert.Parameters.Add(Text(obligation.Kind));
            insert.Parameters.Add(Text(obligation.Description));
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = obligation.Required });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = obligation.Weight });
            insert.Parameters.Add(Text(obligation.Source));
            insert.Parameters.Add(Nullable(obligation.CompletionCriteria));
            insert.Parameters.Add(Nullable(obligation.CardId));
            insert.Parameters.Add(Nullable(obligation.ObjectiveKey));
            insert.Parameters.Add(Nullable(obligation.ArtifactRef));
            insert.Parameters.Add(Nullable(obligation.Reason));
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = command.OccurredAt });
            created += await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<PhaseObligationRecord>> ListCurrentAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default)
    {
        var version = await CurrentPlanVersionAsync(tenantId, runId, phaseKey, cancellationToken);
        return version == 0 ? [] : await ListAsync(tenantId, runId, phaseKey, version, cancellationToken);
    }

    public Task<IReadOnlyList<PhaseObligationRecord>> ListAllVersionsAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default) =>
        ListAsync(tenantId, runId, phaseKey, null, cancellationToken);

    public async Task<int> CurrentPlanVersionAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT COALESCE(MAX(plan_version),0) FROM harness.phase_obligations " +
            "WHERE tenant_id=$1 AND run_id=$2 AND phase_key=$3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(runId));
        query.Parameters.Add(Text(phaseKey));
        var value = await query.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> UpdateStateAsync(
        PhaseObligationStateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.Equals(command.State, "cancelled", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new PhaseObligationValidationException(
                "Cancelling a phase obligation requires a recorded reason.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE harness.phase_obligations
            SET state=$1,evidence_json=$2::jsonb,reason=COALESCE($3,reason),updated_at=$4
            WHERE tenant_id=$5 AND obligation_id=$6;
            """;
        update.Parameters.Add(Text(command.State));
        update.Parameters.Add(Text(JsonSerializer.Serialize(command.Evidence ?? [], JsonOptions)));
        update.Parameters.Add(Nullable(command.Reason));
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = command.OccurredAt });
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.ObligationId));
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<IReadOnlyList<PhaseObligationRecord>> ListAsync(
        string tenantId, string runId, string phaseKey, int? planVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,obligation_id,project_id,run_id,phase_key,obligation_key,plan_version," +
            "kind,description,required,weight,source,completion_criteria,card_id,objective_key," +
            "artifact_ref,state,evidence_json,reason,created_at,updated_at FROM harness.phase_obligations " +
            "WHERE tenant_id=$1 AND run_id=$2 AND phase_key=$3" +
            (planVersion is null ? string.Empty : " AND plan_version=$4") +
            " ORDER BY plan_version, obligation_key;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(runId));
        query.Parameters.Add(Text(phaseKey));
        if (planVersion is { } version)
        {
            query.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = version });
        }

        var rows = new List<PhaseObligationRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PhaseObligationRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
                reader.GetString(8), reader.GetBoolean(9), reader.GetDouble(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetString(16),
                JsonSerializer.Deserialize<string[]>(reader.GetString(17), JsonOptions) ?? [],
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.GetFieldValue<DateTimeOffset>(19),
                reader.GetFieldValue<DateTimeOffset>(20)));
        }

        return rows;
    }

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
}
