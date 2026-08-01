using Harness.Persistence.Abstractions.Agents;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresChiefTurnIntentStore(NpgsqlDataSource dataSource)
    : IChiefTurnIntentStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task RecordAsync(
        ChiefTurnIntentRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Paridade semântica com o SQLite: idempotente por (tenant, turn).
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO harness.chief_turn_intents
                (tenant_id, project_id, turn_id, intent, confidence,
                 demands_dropped, team_actions_dropped, duration_ms, occurred_at)
            VALUES (@tenant, @project, @turn, @intent, @confidence,
                    @demands, @teamActions, @duration, @at)
            ON CONFLICT (tenant_id, turn_id) DO NOTHING;
            """);
        command.Parameters.AddWithValue("@tenant", record.TenantId);
        command.Parameters.AddWithValue("@project", record.ProjectId);
        command.Parameters.AddWithValue("@turn", record.TurnId);
        command.Parameters.AddWithValue("@intent", record.Intent);
        command.Parameters.AddWithValue("@confidence", record.Confidence);
        command.Parameters.AddWithValue("@demands", record.DemandsDropped);
        command.Parameters.AddWithValue("@teamActions", record.TeamActionsDropped);
        command.Parameters.AddWithValue("@duration", record.DurationMs);
        command.Parameters.AddWithValue("@at", record.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChiefTurnIntentRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var command = _dataSource.CreateCommand("""
            SELECT tenant_id, project_id, turn_id, intent, confidence,
                   demands_dropped, team_actions_dropped, duration_ms, occurred_at
            FROM harness.chief_turn_intents
            WHERE tenant_id = @tenant AND project_id = @project
            ORDER BY occurred_at DESC
            LIMIT @limit;
            """);
        command.Parameters.AddWithValue("@tenant", tenantId);
        command.Parameters.AddWithValue("@project", projectId);
        command.Parameters.AddWithValue("@limit", limit);

        var items = new List<ChiefTurnIntentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ChiefTurnIntentRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDouble(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return items;
    }
}
