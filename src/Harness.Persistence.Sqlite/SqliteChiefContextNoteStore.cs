using System.Globalization;
using Harness.Persistence.Abstractions.Governance;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite das notas de contexto do Chief (PLAT-02). Append idempotente por
/// (tenant, project, conversation, source_item_id) via <c>INSERT OR IGNORE</c>: reprocessar o mesmo
/// turno não duplica notas.
/// </summary>
public sealed class SqliteChiefContextNoteStore(SqliteWriteDispatcher dispatcher) : IChiefContextNoteStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<int> AppendAsync(ChiefContextNoteAppendCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync((connection, token) => AppendCoreAsync(connection, command, token), cancellationToken);
    }

    public Task<IReadOnlyList<ChiefContextNoteRecord>> ListAsync(
        string tenantId, string projectId, string? conversationId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<ChiefContextNoteRecord>>(
            (connection, token) => ListCoreAsync(connection, tenantId, projectId, conversationId, limit, token),
            cancellationToken);

    private static async Task<int> AppendCoreAsync(
        SqliteConnection connection, ChiefContextNoteAppendCommand command, CancellationToken token)
    {
        if (command.Notes.Count == 0)
        {
            return 0;
        }

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var inserted = 0;
        foreach (var note in command.Notes)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO chief_context_notes
                    (id,tenant_id,project_id,conversation_id,turn_id,source_item_id,role,content,token_estimate,sequence,created_at)
                VALUES ($id,$tenant,$project,$conversation,$turn,$source,$role,$content,$tokens,$sequence,$at);
                """;
            Add(insert, "$id", note.NoteId);
            Add(insert, "$tenant", command.TenantId);
            Add(insert, "$project", command.ProjectId);
            Add(insert, "$conversation", command.ConversationId);
            Add(insert, "$turn", command.TurnId);
            Add(insert, "$source", note.SourceItemId);
            Add(insert, "$role", note.Role);
            Add(insert, "$content", note.Content);
            Add(insert, "$tokens", note.TokenEstimate);
            Add(insert, "$sequence", note.Sequence);
            Add(insert, "$at", Store(command.OccurredAt));
            inserted += await insert.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
        return inserted;
    }

    private static async Task<IReadOnlyList<ChiefContextNoteRecord>> ListCoreAsync(
        SqliteConnection connection, string tenantId, string projectId, string? conversationId,
        int limit, CancellationToken token)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,project_id,conversation_id,turn_id,id,source_item_id,role,content,token_estimate,sequence,created_at " +
            "FROM chief_context_notes WHERE tenant_id=$tenant AND project_id=$project " +
            "AND ($conversation IS NULL OR conversation_id=$conversation) " +
            "ORDER BY sequence,id LIMIT $limit;";
        Add(query, "$tenant", tenantId);
        Add(query, "$project", projectId);
        Add(query, "$conversation", conversationId);
        Add(query, "$limit", limit);
        var rows = new List<ChiefContextNoteRecord>();
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new ChiefContextNoteRecord(
                reader.GetString(0), reader.GetString(1), Null(reader, 2), Null(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetInt64(9), ParseDate(reader.GetString(10))));
        }

        return rows;
    }

    private static void Validate(ChiefContextNoteAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentNullException.ThrowIfNull(command.Notes);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? Null(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
