using Harness.Persistence.Abstractions.WorkChain;
using Npgsql;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL da intenção de merge (Fase 0C1/0C2). A exclusão mútua por repositório é do
/// BANCO — índice único parcial sobre o estado `merging` —, não de um semáforo de processo.
/// </summary>
public sealed class PostgresMergeIntentStore(NpgsqlDataSource dataSource) : IMergeIntentStore
{
    private const string Selection =
        "SELECT tenant_id,merge_intent_id,repository_id,project_id,card_id,attempt_id," +
        "source_branch,target_branch,expected_source_sha,expected_base_sha,state,owner_id," +
        "fencing_token,lease_expires_at,result_sha,board_settled,last_error,created_at,updated_at " +
        "FROM harness.merge_intents";

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<MergeIntentRecord> RequestAsync(
        MergeIntentRequestCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(async (c, t) =>
        {
            await using var tx = await c.BeginTransactionAsync(t);
            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            // Idempotente por TENTATIVA: um retry reusa a intenção em vez de abrir outra e
            // arriscar dois merges para o mesmo trabalho.
            insert.CommandText =
                """
                INSERT INTO harness.merge_intents
                    (tenant_id,merge_intent_id,repository_id,project_id,card_id,attempt_id,
                     source_branch,target_branch,expected_source_sha,expected_base_sha,state,
                     owner_id,fencing_token,lease_expires_at,result_sha,board_settled,last_error,
                     created_at,updated_at)
                VALUES (@tenant,@id,@repository,@project,@card,@attempt,@source,@target,@sourceSha,
                        @baseSha,'pending',NULL,0,NULL,NULL,false,NULL,@at,@at)
                ON CONFLICT (tenant_id,attempt_id) DO NOTHING;
                """;
            Add(insert, "@tenant", command.TenantId);
            Add(insert, "@id", command.MergeIntentId);
            Add(insert, "@repository", command.RepositoryId);
            Add(insert, "@project", command.ProjectId);
            Add(insert, "@card", command.CardId);
            Add(insert, "@attempt", command.AttemptId);
            Add(insert, "@source", command.SourceBranch);
            Add(insert, "@target", command.TargetBranch);
            Add(insert, "@sourceSha", command.ExpectedSourceSha);
            Add(insert, "@baseSha", command.ExpectedBaseSha);
            Add(insert, "@at", command.OccurredAt);
            await insert.ExecuteNonQueryAsync(t);

            // Compatibilidade de recuperação equivalente ao SQLite: somente a primeira falha
            // legada de raiz controlada é reaberta. Um conflito Git abortado não é redespachado.
            await using (var recover = c.CreateCommand())
            {
                recover.Transaction = tx;
                recover.CommandText =
                    "UPDATE harness.merge_intents SET state='pending',last_error=NULL,updated_at=@at " +
                    "WHERE tenant_id=@tenant AND attempt_id=@attempt AND state='aborted' " +
                    "AND last_error='ArgumentException' AND fencing_token=1;";
                Add(recover, "@at", command.OccurredAt);
                Add(recover, "@tenant", command.TenantId);
                Add(recover, "@attempt", command.AttemptId);
                await recover.ExecuteNonQueryAsync(t);
            }
            var record = await ReadByAttemptAsync(c, tx, command.TenantId, command.AttemptId, t)
                ?? throw new InvalidOperationException("The merge intent could not be read back.");
            await tx.CommitAsync(t);
            return record;
        }, cancellationToken);
    }

    public Task<MergeIntentRecord?> GetAsync(
        string tenantId, string mergeIntentId, CancellationToken cancellationToken = default) =>
        ExecuteAsync((c, t) => ReadAsync(c, null, tenantId, mergeIntentId, t), cancellationToken);

    public Task<MergeIntentRecord?> TryBeginAsync(
        MergeIntentBeginCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(async (c, t) =>
        {
            await using var tx = await c.BeginTransactionAsync(t);
            await using var update = c.CreateCommand();
            update.Transaction = tx;
            // O índice único parcial recusa um segundo `merging` no MESMO repositório: a
            // coordenação é do banco, então vale entre processos e entre Hosts.
            update.CommandText =
                """
                UPDATE harness.merge_intents
                SET state='merging',owner_id=@owner,fencing_token=fencing_token+1,
                    lease_expires_at=@expires,updated_at=@now
                WHERE tenant_id=@tenant AND merge_intent_id=@id
                  AND (state='pending' OR state='failed'
                       OR (state='merging' AND (lease_expires_at IS NULL OR lease_expires_at<=@now)));
                """;
            Add(update, "@owner", command.OwnerId);
            Add(update, "@expires", command.Now.Add(command.LeaseDuration));
            Add(update, "@now", command.Now);
            Add(update, "@tenant", command.TenantId);
            Add(update, "@id", command.MergeIntentId);
            int affected;
            try
            {
                affected = await update.ExecuteNonQueryAsync(t);
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                // Outro merge ativo NESTE repositório. Recusar é o comportamento correto: dois
                // merges simultâneos no mesmo repositório é exatamente o que BR-003 descreve.
                await tx.CommitAsync(t);
                return null;
            }

            if (affected != 1)
            {
                await tx.CommitAsync(t);
                return null;
            }

            var record = await ReadAsync(c, tx, command.TenantId, command.MergeIntentId, t);
            await tx.CommitAsync(t);
            return record;
        }, cancellationToken);
    }

    public Task<bool> TryRecordMergedAsync(
        MergeIntentResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                """
                UPDATE harness.merge_intents
                SET state='merged',result_sha=@sha,last_error=NULL,lease_expires_at=NULL,updated_at=@at
                WHERE tenant_id=@tenant AND merge_intent_id=@id AND state='merging'
                  AND owner_id=@owner AND fencing_token=@fencing;
                """;
            Add(q, "@sha", command.ResultSha);
            Add(q, "@at", command.OccurredAt);
            Add(q, "@tenant", command.TenantId);
            Add(q, "@id", command.MergeIntentId);
            Add(q, "@owner", command.OwnerId);
            Add(q, "@fencing", command.FencingToken);
            return await q.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);
    }

    public Task<bool> TrySettleBoardAsync(
        string tenantId, string mergeIntentId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "UPDATE harness.merge_intents SET board_settled=true,updated_at=@at " +
                "WHERE tenant_id=@tenant AND merge_intent_id=@id AND state='merged';";
            Add(q, "@at", occurredAt);
            Add(q, "@tenant", tenantId);
            Add(q, "@id", mergeIntentId);
            return await q.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);

    public Task<bool> TryFailAsync(
        MergeIntentFailCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                """
                UPDATE harness.merge_intents
                SET state=@state,last_error=@error,lease_expires_at=NULL,updated_at=@at
                WHERE tenant_id=@tenant AND merge_intent_id=@id AND state='merging'
                  AND owner_id=@owner AND fencing_token=@fencing;
                """;
            Add(q, "@state", command.Aborted ? MergeIntentState.Aborted : MergeIntentState.Failed);
            Add(q, "@error", command.ErrorCode);
            Add(q, "@at", command.OccurredAt);
            Add(q, "@tenant", command.TenantId);
            Add(q, "@id", command.MergeIntentId);
            Add(q, "@owner", command.OwnerId);
            Add(q, "@fencing", command.FencingToken);
            return await q.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<MergeIntentRecord>> ListUnsettledAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ExecuteAsync<IReadOnlyList<MergeIntentRecord>>(async (c, t) =>
        {
            var values = new List<MergeIntentRecord>();
            await using var q = c.CreateCommand();
            q.CommandText =
                $"{Selection} WHERE state IN ('pending','merging') " +
                "OR (state='merged' AND board_settled=false) ORDER BY updated_at LIMIT @limit;";
            Add(q, "@limit", limit);
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                values.Add(Read(r));
            }

            return values;
        }, cancellationToken);
    }


    /// <summary>Abre conexão e executa — o equivalente ao dispatcher serializado do SQLite.</summary>
    private async Task<T> ExecuteAsync<T>(
        Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await work(connection, cancellationToken);
    }

    private static async Task<MergeIntentRecord?> ReadAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, string tenantId, string id, CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = $"{Selection} WHERE tenant_id=@tenant AND merge_intent_id=@id;";
        Add(q, "@tenant", tenantId);
        Add(q, "@id", id);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? Read(r) : null;
    }

    private static async Task<MergeIntentRecord?> ReadByAttemptAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, string tenantId, string attemptId,
        CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = $"{Selection} WHERE tenant_id=@tenant AND attempt_id=@attempt;";
        Add(q, "@tenant", tenantId);
        Add(q, "@attempt", attemptId);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? Read(r) : null;
    }

    private static MergeIntentRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt64(12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
        r.IsDBNull(14) ? null : r.GetString(14), r.GetBoolean(15),
        r.IsDBNull(16) ? null : r.GetString(16), r.GetFieldValue<DateTimeOffset>(17), r.GetFieldValue<DateTimeOffset>(18));

    private static void Add(NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
