using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>Fila durável de solicitações de agente em PostgreSQL. Paridade com o SQLite.</summary>
public sealed class PostgresAgentRequestStore(NpgsqlDataSource dataSource) : IAgentRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string Select =
        "SELECT tenant_id,request_id,project_id,task_id,attempt_id,kind,question,reason," +
        "options_json::text,recommended_option,evidence_json::text,requested_paths_json::text," +
        "blocking,state,answered_by,answer,answer_reason_code,fencing_token,created_at,updated_at," +
        "answered_at FROM harness.agent_requests";

    public async Task<(AgentRequestRecord Request, bool Created)> OpenAsync(
        AgentRequestOpenCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var existing = await ReadOpenAsync(connection, command, cancellationToken);
        if (existing is not null)
        {
            return (existing, false);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO harness.agent_requests
            (tenant_id,request_id,project_id,task_id,attempt_id,kind,question,reason,
             options_json,recommended_option,evidence_json,requested_paths_json,blocking,
             state,fencing_token,created_at,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10,$11::jsonb,$12::jsonb,$13,'open',$14,$15,$15)
            ON CONFLICT DO NOTHING;
            """;
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.RequestId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Text(command.TaskId));
        insert.Parameters.Add(Nullable(command.AttemptId));
        insert.Parameters.Add(Text(command.Kind));
        insert.Parameters.Add(Text(command.Question));
        insert.Parameters.Add(Text(command.Reason));
        insert.Parameters.Add(Text(JsonSerializer.Serialize(command.Options ?? [], JsonOptions)));
        insert.Parameters.Add(Nullable(command.RecommendedOption));
        insert.Parameters.Add(Text(JsonSerializer.Serialize(command.Evidence ?? [], JsonOptions)));
        insert.Parameters.Add(Text(JsonSerializer.Serialize(command.RequestedPaths ?? [], JsonOptions)));
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = command.Blocking });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = command.FencingToken });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = command.OccurredAt });
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        var stored = await ReadAsync(connection, command.TenantId, command.RequestId, cancellationToken);
        return stored is null
            ? ((await ReadOpenAsync(connection, command, cancellationToken))!, false)
            : (stored, inserted == 1);
    }

    public async Task<AgentRequestRecord?> AnswerAsync(
        AgentRequestAnswerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE harness.agent_requests
            SET state=$1,answered_by=$2,answer=$3,answer_reason_code=$4,updated_at=$5,answered_at=$5
            WHERE tenant_id=$6 AND request_id=$7 AND state='open' AND fencing_token=$8;
            """;
        update.Parameters.Add(Text(command.State));
        update.Parameters.Add(Text(command.AnsweredBy));
        update.Parameters.Add(Text(command.Answer));
        update.Parameters.Add(Text(command.ReasonCode));
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = command.OccurredAt });
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.RequestId));
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = command.FencingToken });
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1
            ? await ReadAsync(connection, command.TenantId, command.RequestId, cancellationToken)
            : null;
    }

    public async Task<IReadOnlyList<AgentRequestRecord>> ListOpenAsync(
        string tenantId, string? projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND state='open'" +
            (projectId is null ? string.Empty : " AND project_id=$3") +
            " ORDER BY created_at,request_id LIMIT $2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit });
        if (projectId is not null)
        {
            query.Parameters.Add(Text(projectId));
        }

        var rows = new List<AgentRequestRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public async Task<AgentRequestRecord?> GetAsync(
        string tenantId, string requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, tenantId, requestId, cancellationToken);
    }

    public async Task<int> SupersedeForAttemptAsync(
        string tenantId, string attemptId, string reasonCode, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE harness.agent_requests SET state='superseded',answer_reason_code=$1,updated_at=$2
            WHERE tenant_id=$3 AND attempt_id=$4 AND state='open';
            """;
        update.Parameters.Add(Text(reasonCode));
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = occurredAt });
        update.Parameters.Add(Text(tenantId));
        update.Parameters.Add(Text(attemptId));
        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AgentRequestRecord?> ReadAsync(
        NpgsqlConnection connection, string tenantId, string requestId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$1 AND request_id=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(requestId));
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static async Task<AgentRequestRecord?> ReadOpenAsync(
        NpgsqlConnection connection, AgentRequestOpenCommand command, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$1 AND task_id=$2 AND state='open' AND kind=$3 " +
            "AND question=$4 AND attempt_id IS NOT DISTINCT FROM $5 LIMIT 1;";
        query.Parameters.Add(Text(command.TenantId));
        query.Parameters.Add(Text(command.TaskId));
        query.Parameters.Add(Text(command.Kind));
        query.Parameters.Add(Text(command.Question));
        query.Parameters.Add(Nullable(command.AttemptId));
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static AgentRequestRecord Map(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7),
        JsonSerializer.Deserialize<string[]>(reader.GetString(8), JsonOptions) ?? [],
        reader.IsDBNull(9) ? null : reader.GetString(9),
        JsonSerializer.Deserialize<string[]>(reader.GetString(10), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions) ?? [],
        reader.GetBoolean(12), reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.GetInt64(17),
        reader.GetFieldValue<DateTimeOffset>(18),
        reader.GetFieldValue<DateTimeOffset>(19),
        reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20));

    private static NpgsqlParameter Text(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
}
