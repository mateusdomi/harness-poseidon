using System.Globalization;
using Harness.Persistence.Abstractions.Agents;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteChiefTurnIntentStore(SqliteWriteDispatcher dispatcher)
    : IChiefTurnIntentStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task RecordAsync(ChiefTurnIntentRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            // Idempotente por (tenant, turn): reprocessar um turno após queda não pode inflar a
            // contagem da intenção e fazer parecer que o dono conversa mais do que conversa.
            command.CommandText = """
                INSERT INTO chief_turn_intents
                    (tenant_id, project_id, turn_id, intent, confidence,
                     demands_dropped, team_actions_dropped, duration_ms, occurred_at)
                VALUES ($tenant, $project, $turn, $intent, $confidence,
                        $demands, $teamActions, $duration, $at)
                ON CONFLICT (tenant_id, turn_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$tenant", record.TenantId);
            command.Parameters.AddWithValue("$project", record.ProjectId);
            command.Parameters.AddWithValue("$turn", record.TurnId);
            command.Parameters.AddWithValue("$intent", record.Intent);
            command.Parameters.AddWithValue("$confidence", record.Confidence);
            command.Parameters.AddWithValue("$demands", record.DemandsDropped);
            command.Parameters.AddWithValue("$teamActions", record.TeamActionsDropped);
            command.Parameters.AddWithValue("$duration", record.DurationMs);
            command.Parameters.AddWithValue(
                "$at", record.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ChiefTurnIntentRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return _dispatcher.ExecuteAsync<IReadOnlyList<ChiefTurnIntentRecord>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tenant_id, project_id, turn_id, intent, confidence,
                       demands_dropped, team_actions_dropped, duration_ms, occurred_at
                FROM chief_turn_intents
                WHERE tenant_id = $tenant AND project_id = $project
                ORDER BY occurred_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$limit", limit);

            var items = new List<ChiefTurnIntentRecord>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new ChiefTurnIntentRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetDouble(4),
                    reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7),
                    DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
            }

            return items;
        }, cancellationToken);
    }
}
