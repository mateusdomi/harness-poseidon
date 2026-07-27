using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Plano de fase materializado em SQLite. Ver <see cref="IPhaseObligationStore"/> para a intenção.
/// </summary>
public sealed class SqlitePhaseObligationStore(SqliteWriteDispatcher dispatcher) : IPhaseObligationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<int> EnsurePlanAsync(
        PhaseObligationPlanCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => EnsurePlanCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<IReadOnlyList<PhaseObligationRecord>> ListCurrentAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                var version = await ReadCurrentVersionAsync(connection, tenantId, runId, phaseKey, token);
                return version == 0
                    ? (IReadOnlyList<PhaseObligationRecord>)[]
                    : await ListCoreAsync(connection, tenantId, runId, phaseKey, version, token);
            },
            cancellationToken);

    public Task<IReadOnlyList<PhaseObligationRecord>> ListAllVersionsAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ListCoreAsync(connection, tenantId, runId, phaseKey, null, token),
            cancellationToken);

    public Task<int> CurrentPlanVersionAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadCurrentVersionAsync(connection, tenantId, runId, phaseKey, token),
            cancellationToken);

    public Task<bool> UpdateStateAsync(
        PhaseObligationStateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.Equals(command.State, "cancelled", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(command.Reason))
        {
            // Cancelar a obrigação que falhou é o caminho curto para fabricar 100%. Exigir a
            // justificativa aqui — na fronteira durável — impede que qualquer chamador o faça em
            // silêncio, inclusive a própria chefe.
            throw new PhaseObligationValidationException(
                "Cancelling a phase obligation requires a recorded reason.");
        }

        return _dispatcher.ExecuteAsync(
            (connection, token) => UpdateStateCoreAsync(connection, command, token), cancellationToken);
    }

    private static async Task<int> EnsurePlanCoreAsync(
        SqliteConnection connection, PhaseObligationPlanCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var created = 0;
        var tick = 0;
        foreach (var obligation in command.Obligations)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            // INSERT OR IGNORE: reexecutar a materialização da fase não recria nem sobrescreve.
            // O ESTADO da obrigação pertence ao runtime; o plano só declara que ela existe.
            insert.CommandText =
                "INSERT OR IGNORE INTO phase_obligations " +
                "(tenant_id,obligation_id,project_id,run_id,phase_key,obligation_key,plan_version," +
                "kind,description,required,weight,source,completion_criteria,card_id,objective_key," +
                "artifact_ref,state,evidence_json,reason,created_at,updated_at) VALUES " +
                "($tenant,$id,$project,$run,$phase,$key,$version,$kind,$description,$required,$weight," +
                "$source,$criteria,$card,$objective,$artifact,'pending','[]',$reason,$at,$at);";
            var id = UlidValue.New(command.OccurredAt.AddMilliseconds(tick++)).ToString();
            Add(insert, "$tenant", command.TenantId);
            Add(insert, "$id", id);
            Add(insert, "$project", command.ProjectId);
            Add(insert, "$run", command.RunId);
            Add(insert, "$phase", command.PhaseKey);
            Add(insert, "$key", obligation.ObligationKey);
            Add(insert, "$version", command.PlanVersion);
            Add(insert, "$kind", obligation.Kind);
            Add(insert, "$description", obligation.Description);
            Add(insert, "$required", obligation.Required ? 1 : 0);
            Add(insert, "$weight", obligation.Weight);
            Add(insert, "$source", obligation.Source);
            AddNullable(insert, "$criteria", obligation.CompletionCriteria);
            AddNullable(insert, "$card", obligation.CardId);
            AddNullable(insert, "$objective", obligation.ObjectiveKey);
            AddNullable(insert, "$artifact", obligation.ArtifactRef);
            AddNullable(insert, "$reason", obligation.Reason);
            Add(insert, "$at", Store(command.OccurredAt));
            created += await insert.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
        return created;
    }

    private static async Task<int> ReadCurrentVersionAsync(
        SqliteConnection connection, string tenantId, string runId, string phaseKey, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT COALESCE(MAX(plan_version),0) FROM phase_obligations " +
            "WHERE tenant_id=$tenant AND run_id=$run AND phase_key=$phase;";
        Add(query, "$tenant", tenantId);
        Add(query, "$run", runId);
        Add(query, "$phase", phaseKey);
        var value = await query.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<PhaseObligationRecord>> ListCoreAsync(
        SqliteConnection connection, string tenantId, string runId, string phaseKey,
        int? planVersion, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,obligation_id,project_id,run_id,phase_key,obligation_key,plan_version," +
            "kind,description,required,weight,source,completion_criteria,card_id,objective_key," +
            "artifact_ref,state,evidence_json,reason,created_at,updated_at FROM phase_obligations " +
            "WHERE tenant_id=$tenant AND run_id=$run AND phase_key=$phase" +
            (planVersion is null ? string.Empty : " AND plan_version=$version") +
            " ORDER BY plan_version, obligation_key;";
        Add(query, "$tenant", tenantId);
        Add(query, "$run", runId);
        Add(query, "$phase", phaseKey);
        if (planVersion is { } version)
        {
            Add(query, "$version", version);
        }

        var rows = new List<PhaseObligationRecord>();
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static async Task<bool> UpdateStateCoreAsync(
        SqliteConnection connection, PhaseObligationStateCommand command, CancellationToken token)
    {
        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE phase_obligations SET state=$state,evidence_json=$evidence," +
            "reason=COALESCE($reason,reason),updated_at=$at " +
            "WHERE tenant_id=$tenant AND obligation_id=$id;";
        Add(update, "$state", command.State);
        Add(update, "$evidence", JsonSerializer.Serialize(command.Evidence ?? [], JsonOptions));
        AddNullable(update, "$reason", command.Reason);
        Add(update, "$at", Store(command.OccurredAt));
        Add(update, "$tenant", command.TenantId);
        Add(update, "$id", command.ObligationId);
        return await update.ExecuteNonQueryAsync(token) == 1;
    }

    private static PhaseObligationRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt32(9) == 1,
        reader.GetDouble(10),
        reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.GetString(16),
        JsonSerializer.Deserialize<string[]>(reader.GetString(17), JsonOptions) ?? [],
        reader.IsDBNull(18) ? null : reader.GetString(18),
        DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture));

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
