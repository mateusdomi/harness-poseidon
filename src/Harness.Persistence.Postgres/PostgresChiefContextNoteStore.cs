using Harness.Persistence.Abstractions.Governance;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL das notas de contexto do Chief (PLAT-02). Append idempotente por
/// (tenant, project, conversation, source_item_id) via <c>ON CONFLICT DO NOTHING</c>: reprocessar o
/// mesmo turno não duplica notas.
/// </summary>
public sealed class PostgresChiefContextNoteStore(NpgsqlDataSource dataSource) : IChiefContextNoteStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<int> AppendAsync(
        ChiefContextNoteAppendCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        if (command.Notes.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = 0;
        foreach (var note in command.Notes)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO harness.chief_context_notes
                    (id,tenant_id,project_id,conversation_id,turn_id,source_item_id,role,content,token_estimate,sequence,created_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                ON CONFLICT (tenant_id,project_id,conversation_id,source_item_id) DO NOTHING;
                """;
            insert.Parameters.Add(Text(note.NoteId));
            insert.Parameters.Add(Text(command.TenantId));
            insert.Parameters.Add(Text(command.ProjectId));
            insert.Parameters.Add(Text(command.ConversationId));
            insert.Parameters.Add(Text(command.TurnId));
            insert.Parameters.Add(Text(note.SourceItemId));
            insert.Parameters.Add(Text(note.Role));
            insert.Parameters.Add(Text(note.Content));
            insert.Parameters.Add(Integer(note.TokenEstimate));
            insert.Parameters.Add(Bigint(note.Sequence));
            insert.Parameters.Add(Timestamp(command.OccurredAt));
            inserted += await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<ChiefContextNoteRecord>> ListAsync(
        string tenantId, string projectId, string? conversationId, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT tenant_id,project_id,conversation_id,turn_id,id,source_item_id,role,content,token_estimate,sequence,created_at " +
            "FROM harness.chief_context_notes WHERE tenant_id=$1 AND project_id=$2 " +
            // Paridade com o SQLite: o limite corta as notas mais ANTIGAS, não as mais recentes.
            // Busca decrescente e inversão antes de devolver, mantendo a ordem cronológica para
            // quem monta o contexto.
            "AND ($3 IS NULL OR conversation_id=$3) ORDER BY sequence DESC,id DESC LIMIT $4;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        query.Parameters.Add(Text(conversationId));
        query.Parameters.Add(Integer(limit));
        var rows = new List<ChiefContextNoteRecord>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ChiefContextNoteRecord(
                reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), Null(reader, 2), Null(reader, 3),
                reader.GetString(4).TrimEnd(), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10)));
        }

        rows.Reverse();
        return rows;
    }

    private static void Validate(ChiefContextNoteAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentNullException.ThrowIfNull(command.Notes);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1859", Justification = "Nullable Npgsql parameters require the untyped DBNull representation.")]
    private static NpgsqlParameter Text(string? value) => value is null
        ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DBNull.Value }
        : new NpgsqlParameter<string> { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };

    private static string? Null(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index).TrimEnd();
}
