using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>Fila durável de solicitações de agente em SQLite. Ver <see cref="IAgentRequestStore"/>.</summary>
public sealed class SqliteAgentRequestStore(SqliteWriteDispatcher dispatcher) : IAgentRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string Select =
        "SELECT tenant_id,request_id,project_id,task_id,attempt_id,kind,question,reason," +
        "options_json,recommended_option,evidence_json,requested_paths_json,blocking,state," +
        "answered_by,answer,answer_reason_code,fencing_token,created_at,updated_at,answered_at " +
        "FROM agent_requests";

    public Task<(AgentRequestRecord Request, bool Created)> OpenAsync(
        AgentRequestOpenCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                // Idempotência da PERGUNTA: o índice único parcial cobre solicitações abertas, mas
                // a leitura prévia evita gastar a exceção no caminho normal do reinício.
                var existing = await ReadOpenAsync(
                    connection, command.TenantId, command.TaskId, command.AttemptId,
                    command.Kind, command.Question, token);
                if (existing is not null)
                {
                    return (existing, false);
                }

                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT OR IGNORE INTO agent_requests " +
                    "(tenant_id,request_id,project_id,task_id,attempt_id,kind,question,reason," +
                    "options_json,recommended_option,evidence_json,requested_paths_json,blocking," +
                    "state,fencing_token,created_at,updated_at) VALUES " +
                    "($tenant,$id,$project,$task,$attempt,$kind,$question,$reason,$options," +
                    "$recommended,$evidence,$paths,$blocking,'open',$fencing,$at,$at);";
                Add(insert, "$tenant", command.TenantId);
                Add(insert, "$id", command.RequestId);
                Add(insert, "$project", command.ProjectId);
                Add(insert, "$task", command.TaskId);
                AddNullable(insert, "$attempt", command.AttemptId);
                Add(insert, "$kind", command.Kind);
                Add(insert, "$question", command.Question);
                Add(insert, "$reason", command.Reason);
                Add(insert, "$options", JsonSerializer.Serialize(command.Options ?? [], JsonOptions));
                AddNullable(insert, "$recommended", command.RecommendedOption);
                Add(insert, "$evidence", JsonSerializer.Serialize(command.Evidence ?? [], JsonOptions));
                Add(insert, "$paths", JsonSerializer.Serialize(command.RequestedPaths ?? [], JsonOptions));
                Add(insert, "$blocking", command.Blocking ? 1 : 0);
                Add(insert, "$fencing", command.FencingToken);
                Add(insert, "$at", Store(command.OccurredAt));
                var inserted = await insert.ExecuteNonQueryAsync(token);
                var stored = await ReadAsync(connection, command.TenantId, command.RequestId, token);
                return stored is null
                    ? ((await ReadOpenAsync(
                        connection, command.TenantId, command.TaskId, command.AttemptId,
                        command.Kind, command.Question, token))!, false)
                    : (stored, inserted == 1);
            },
            cancellationToken);
    }

    public Task<AgentRequestRecord?> AnswerAsync(
        AgentRequestAnswerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var update = connection.CreateCommand();
                // O fencing entra na CLÁUSULA: uma tentativa substituída não consome a resposta da
                // que perguntou, e a antiga não responde pela nova.
                update.CommandText =
                    "UPDATE agent_requests SET state=$state,answered_by=$by,answer=$answer," +
                    "answer_reason_code=$code,updated_at=$at,answered_at=$at " +
                    "WHERE tenant_id=$tenant AND request_id=$id AND state='open' AND fencing_token=$fencing;";
                Add(update, "$state", command.State);
                Add(update, "$by", command.AnsweredBy);
                Add(update, "$answer", command.Answer);
                Add(update, "$code", command.ReasonCode);
                Add(update, "$at", Store(command.OccurredAt));
                Add(update, "$tenant", command.TenantId);
                Add(update, "$id", command.RequestId);
                Add(update, "$fencing", command.FencingToken);
                return await update.ExecuteNonQueryAsync(token) == 1
                    ? await ReadAsync(connection, command.TenantId, command.RequestId, token)
                    : null;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<AgentRequestRecord>> ListOpenAsync(
        string tenantId, string? projectId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND state='open'" +
                    (projectId is null ? string.Empty : " AND project_id=$project") +
                    " ORDER BY created_at,request_id LIMIT $limit;";
                Add(query, "$tenant", tenantId);
                if (projectId is not null)
                {
                    Add(query, "$project", projectId);
                }

                Add(query, "$limit", limit);
                var rows = new List<AgentRequestRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(Map(reader));
                }

                return (IReadOnlyList<AgentRequestRecord>)rows;
            },
            cancellationToken);

    public Task<AgentRequestRecord?> GetAsync(
        string tenantId, string requestId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadAsync(connection, tenantId, requestId, token), cancellationToken);

    public Task<int> SupersedeForAttemptAsync(
        string tenantId, string attemptId, string reasonCode, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var update = connection.CreateCommand();
                update.CommandText =
                    "UPDATE agent_requests SET state='superseded',answer_reason_code=$code," +
                    "updated_at=$at WHERE tenant_id=$tenant AND attempt_id=$attempt AND state='open';";
                Add(update, "$code", reasonCode);
                Add(update, "$at", Store(occurredAt));
                Add(update, "$tenant", tenantId);
                Add(update, "$attempt", attemptId);
                return await update.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

    private static async Task<AgentRequestRecord?> ReadAsync(
        SqliteConnection connection, string tenantId, string requestId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"{Select} WHERE tenant_id=$tenant AND request_id=$id;";
        Add(query, "$tenant", tenantId);
        Add(query, "$id", requestId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static async Task<AgentRequestRecord?> ReadOpenAsync(
        SqliteConnection connection, string tenantId, string taskId, string? attemptId,
        string kind, string question, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.CommandText =
            $"{Select} WHERE tenant_id=$tenant AND task_id=$task AND state='open' AND kind=$kind " +
            "AND question=$question AND (attempt_id IS $attempt OR attempt_id=$attempt) LIMIT 1;";
        Add(query, "$tenant", tenantId);
        Add(query, "$task", taskId);
        Add(query, "$kind", kind);
        Add(query, "$question", question);
        AddNullable(query, "$attempt", attemptId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static AgentRequestRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7),
        JsonSerializer.Deserialize<string[]>(reader.GetString(8), JsonOptions) ?? [],
        reader.IsDBNull(9) ? null : reader.GetString(9),
        JsonSerializer.Deserialize<string[]>(reader.GetString(10), JsonOptions) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions) ?? [],
        reader.GetInt32(12) == 1, reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.GetInt64(17),
        DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
        reader.IsDBNull(20) ? null : DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture));

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
