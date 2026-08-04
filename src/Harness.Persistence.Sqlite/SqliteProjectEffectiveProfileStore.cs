using Harness.Persistence.Abstractions.Product;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Histórico append-only do perfil efetivo. Nenhum caminho aqui faz UPDATE de conteúdo: a versão
/// anterior só muda de <c>status</c> para `superseded`, e o corpo dela permanece intacto para a
/// auditoria de um card antigo.
/// </summary>
public sealed class SqliteProjectEffectiveProfileStore(SqliteWriteDispatcher dispatcher)
    : IProjectEffectiveProfileStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ProjectEffectiveProfileSaveResult> SaveAsync(
        ProjectEffectiveProfileSaveCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProfileJson);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = await connection.BeginTransactionAsync(token);
                var current = await ReadCurrentAsync(
                    connection, (SqliteTransaction)transaction, command.TenantId, command.ProjectId, token);

                // Resolução que não mudou nada não é decisão nova. Criar uma versão idêntica
                // encheria o histórico de ruído e faria "mudou de versão" perder o significado.
                if (current is not null &&
                    string.Equals(current.Fingerprint, command.Fingerprint, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(token);
                    return new ProjectEffectiveProfileSaveResult(current, false);
                }

                if (current is not null)
                {
                    await using var supersede = connection.CreateCommand();
                    supersede.Transaction = (SqliteTransaction)transaction;
                    supersede.CommandText =
                        "UPDATE project_effective_profiles SET status='superseded' " +
                        "WHERE tenant_id=$tenant AND project_id=$project AND version=$version;";
                    Add(supersede, "$tenant", command.TenantId);
                    Add(supersede, "$project", command.ProjectId);
                    Add(supersede, "$version", current.Version);
                    await supersede.ExecuteNonQueryAsync(token);
                }

                var version = (current?.Version ?? 0) + 1;
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText =
                    "INSERT INTO project_effective_profiles " +
                    "(tenant_id,project_id,version,fingerprint,baseline_version,modality," +
                    "profile_json,resolved_at,resolved_by,status) " +
                    "VALUES ($tenant,$project,$version,$fingerprint,$baseline,$modality," +
                    "$profile,$at,$by,'active');";
                Add(insert, "$tenant", command.TenantId);
                Add(insert, "$project", command.ProjectId);
                Add(insert, "$version", version);
                Add(insert, "$fingerprint", command.Fingerprint);
                Add(insert, "$baseline", command.BaselineVersion);
                Add(insert, "$modality", command.Modality);
                Add(insert, "$profile", command.ProfileJson);
                Add(insert, "$at", command.ResolvedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                Add(insert, "$by", command.ResolvedBy);
                await insert.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);

                return new ProjectEffectiveProfileSaveResult(
                    new ProjectEffectiveProfileRecord(
                        command.TenantId, command.ProjectId, version, command.Fingerprint,
                        command.BaselineVersion, command.Modality, command.ProfileJson,
                        command.ResolvedAt, command.ResolvedBy, "active"),
                    true);
            },
            cancellationToken);
    }

    public Task<ProjectEffectiveProfileRecord?> GetCurrentAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadCurrentAsync(connection, null, tenantId, projectId, token),
            cancellationToken);

    public Task<ProjectEffectiveProfileRecord?> GetVersionAsync(
        string tenantId, string projectId, int version, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText = $"{Select} WHERE tenant_id=$tenant AND project_id=$project AND version=$version;";
                Add(query, "$tenant", tenantId);
                Add(query, "$project", projectId);
                Add(query, "$version", version);
                await using var reader = await query.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? Map(reader) : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<ProjectEffectiveProfileRecord>> ListAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    $"{Select} WHERE tenant_id=$tenant AND project_id=$project ORDER BY version DESC;";
                Add(query, "$tenant", tenantId);
                Add(query, "$project", projectId);
                var rows = new List<ProjectEffectiveProfileRecord>();
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(Map(reader));
                }

                return (IReadOnlyList<ProjectEffectiveProfileRecord>)rows;
            },
            cancellationToken);

    private static async Task<ProjectEffectiveProfileRecord?> ReadCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string projectId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{Select} WHERE tenant_id=$tenant AND project_id=$project ORDER BY version DESC LIMIT 1;";
        Add(query, "$tenant", tenantId);
        Add(query, "$project", projectId);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private const string Select =
        "SELECT tenant_id,project_id,version,fingerprint,baseline_version,modality,profile_json," +
        "resolved_at,resolved_by,status FROM project_effective_profiles";

    private static ProjectEffectiveProfileRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetString(8), reader.GetString(9));

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
