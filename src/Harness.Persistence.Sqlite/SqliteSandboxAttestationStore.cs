using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Execution;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite da attestation de sandbox (Fase 0B1).
/// </summary>
public sealed class SqliteSandboxAttestationStore(SqliteWriteDispatcher dispatcher)
    : ISandboxAttestationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<SandboxAttestationRecord> SaveAsync(
        SandboxAttestationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(t);
            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            // A PRIMEIRA emissão é a que vale: uma segunda não pode "melhorar" a avaliação de uma
            // execução em curso — seria a porta para atestar depois o que não foi contido antes.
            insert.CommandText =
                """
                INSERT OR IGNORE INTO sandbox_attestations
                    (tenant_id,attempt_id,project_id,provider,provider_version,sandbox_identity,
                     mounts_json,network_policy,root_filesystem_read_only,worktree_isolated,
                     egress_restricted,resource_limits_applied,verified,verification_detail,
                     configuration_hash,issued_at)
                VALUES ($tenant,$attempt,$project,$provider,$version,$identity,$mounts,$network,
                        $rootReadOnly,$worktree,$egress,$limits,$verified,$detail,$hash,$at);
                """;
            Bind(insert, record);
            await insert.ExecuteNonQueryAsync(t);
            var stored = await ReadAsync(c, tx, record.TenantId, record.AttemptId, t)
                ?? throw new InvalidOperationException(
                    "The persisted sandbox attestation could not be read back.");
            await tx.CommitAsync(t);
            return stored;
        }, cancellationToken);
    }

    public Task<SandboxAttestationRecord?> GetAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => ReadAsync(c, null, tenantId, attemptId, t), cancellationToken);

    private static async Task<SandboxAttestationRecord?> ReadAsync(
        SqliteConnection c, SqliteTransaction? tx, string tenantId, string attemptId,
        CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT tenant_id,attempt_id,project_id,provider,provider_version,sandbox_identity," +
            "mounts_json,network_policy,root_filesystem_read_only,worktree_isolated," +
            "egress_restricted,resource_limits_applied,verified,verification_detail," +
            "configuration_hash,issued_at FROM sandbox_attestations " +
            "WHERE tenant_id=$tenant AND attempt_id=$attempt;";
        Add(q, "$tenant", tenantId);
        Add(q, "$attempt", attemptId);
        await using var r = await q.ExecuteReaderAsync(token);
        return await r.ReadAsync(token)
            ? new SandboxAttestationRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5),
                JsonSerializer.Deserialize<string[]>(r.GetString(6), JsonOptions) ?? [],
                r.GetString(7), r.GetInt32(8) == 1, r.GetInt32(9) == 1, r.GetInt32(10) == 1,
                r.GetInt32(11) == 1, r.GetInt32(12) == 1, r.GetString(13), r.GetString(14),
                Parse(r.GetString(15)))
            : null;
    }

    private static void Bind(SqliteCommand command, SandboxAttestationRecord record)
    {
        Add(command, "$tenant", record.TenantId);
        Add(command, "$attempt", record.AttemptId);
        Add(command, "$project", record.ProjectId);
        Add(command, "$provider", record.Provider);
        Add(command, "$version", record.ProviderVersion);
        Add(command, "$identity", record.SandboxIdentity);
        Add(command, "$mounts", JsonSerializer.Serialize(record.Mounts, JsonOptions));
        Add(command, "$network", record.NetworkPolicy);
        Add(command, "$rootReadOnly", record.RootFilesystemReadOnly ? 1 : 0);
        Add(command, "$worktree", record.WorktreeIsolated ? 1 : 0);
        Add(command, "$egress", record.EgressRestricted ? 1 : 0);
        Add(command, "$limits", record.ResourceLimitsApplied ? 1 : 0);
        Add(command, "$verified", record.Verified ? 1 : 0);
        Add(command, "$detail", record.VerificationDetail);
        Add(command, "$hash", record.ConfigurationHash);
        Add(command, "$at", Store(record.IssuedAt));
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
