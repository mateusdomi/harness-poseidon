using System.Globalization;
using Harness.Persistence.Abstractions.Coordination;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteChiefLoopStateStore(SqliteWriteDispatcher dispatcher) : IChiefLoopStateStore
{
    public Task<IReadOnlyList<ChiefLoopStateEntry>> LoadAsync(
        string tenantId, CancellationToken cancellationToken = default) =>
        dispatcher.ExecuteAsync<IReadOnlyList<ChiefLoopStateEntry>>(async (connection, token) =>
        {
            var values = new List<ChiefLoopStateEntry>();
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT kind, entry_id, not_before, counter FROM chief_loop_state " +
                "WHERE tenant_id=$tenant;";
            query.Parameters.AddWithValue("$tenant", tenantId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                values.Add(new ChiefLoopStateEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2)
                        ? null
                        : DateTimeOffset.Parse(
                            reader.GetString(2), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                    reader.GetInt32(3)));
            }

            return values;
        }, cancellationToken);

    public Task UpsertAsync(
        string tenantId, ChiefLoopStateEntry entry, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var upsert = connection.CreateCommand();
            upsert.CommandText =
                "INSERT INTO chief_loop_state (tenant_id, kind, entry_id, not_before, counter, updated_at) " +
                "VALUES ($tenant, $kind, $entry, $notBefore, $counter, $at) " +
                "ON CONFLICT(tenant_id, kind, entry_id) DO UPDATE SET " +
                "not_before=excluded.not_before, counter=excluded.counter, updated_at=excluded.updated_at;";
            upsert.Parameters.AddWithValue("$tenant", tenantId);
            upsert.Parameters.AddWithValue("$kind", entry.Kind);
            upsert.Parameters.AddWithValue("$entry", entry.EntryId);
            upsert.Parameters.AddWithValue("$notBefore", entry.NotBefore is { } value
                ? value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
            upsert.Parameters.AddWithValue("$counter", entry.Counter);
            upsert.Parameters.AddWithValue(
                "$at", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return await upsert.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task RemoveAsync(
        string tenantId, string kind, string entryId,
        CancellationToken cancellationToken = default) =>
        dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText =
                "DELETE FROM chief_loop_state WHERE tenant_id=$tenant AND kind=$kind AND entry_id=$entry;";
            delete.Parameters.AddWithValue("$tenant", tenantId);
            delete.Parameters.AddWithValue("$kind", kind);
            delete.Parameters.AddWithValue("$entry", entryId);
            return await delete.ExecuteNonQueryAsync(token);
        }, cancellationToken);
}
