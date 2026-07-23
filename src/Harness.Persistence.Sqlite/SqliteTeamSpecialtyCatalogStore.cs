using System.Globalization;
using Harness.Persistence.Abstractions.Agents;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Catálogo tenant-scoped de equipes/especialidades em SQLite. Espelha os padrões dos demais
/// stores de catálogo (paging por id, escritas serializadas pelo dispatcher, chaves ULID) e
/// aplica a guarda referencial no delete: uma entrada ainda referenciada não pode ser apagada.
/// </summary>
public sealed class SqliteTeamSpecialtyCatalogStore(SqliteWriteDispatcher dispatcher) : ITeamSpecialtyCatalogStore
{
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string TeamSelect = "SELECT id,tenant_id,team_key,name,description,created_at,updated_at FROM teams";
    private const string SpecialtySelect = "SELECT id,tenant_id,specialty_key,name,description,team_id,created_at,updated_at FROM specialties";

    public Task<IReadOnlyList<TeamRecord>> ListTeamsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<TeamRecord>>(async (connection, token) =>
        {
            var values = new List<TeamRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"{TeamSelect} WHERE tenant_id=$tenant AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(command, "$tenant", tenantId);
            Add(command, "$after", afterId is null ? DBNull.Value : afterId);
            Add(command, "$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadTeam(reader));
            return values;
        }, cancellationToken);

    public Task<TeamRecord?> GetTeamAsync(string tenantId, string teamId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{TeamSelect} WHERE tenant_id=$tenant AND id=$id;";
            Add(command, "$tenant", tenantId);
            Add(command, "$id", teamId);
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadTeam(reader) : null;
        }, cancellationToken);

    public Task<TeamRecord> CreateTeamAsync(TeamCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            await GuardTeamUniquenessAsync(connection, tx, command.TenantId, name, key, null, token);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO teams(id,tenant_id,team_key,name,description,created_at,updated_at) VALUES($id,$tenant,$key,$name,$description,$at,$at);";
                Add(insert, "$id", command.Id);
                Add(insert, "$tenant", command.TenantId);
                Add(insert, "$key", key);
                Add(insert, "$name", name);
                Add(insert, "$description", (object?)description ?? DBNull.Value);
                Add(insert, "$at", Store(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
            return (await GetTeamInConnectionAsync(connection, command.TenantId, command.Id, token))!;
        }, cancellationToken);

    public Task<TeamRecord> UpdateTeamAsync(TeamUpdateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            if (await GetTeamInConnectionAsync(connection, command.TenantId, command.Id, token) is null)
                throw new TeamSpecialtyCatalogNotFoundException("team");
            await GuardTeamUniquenessAsync(connection, tx, command.TenantId, name, key, command.Id, token);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE teams SET team_key=$key,name=$name,description=$description,updated_at=$at WHERE tenant_id=$tenant AND id=$id;";
                Add(update, "$key", key);
                Add(update, "$name", name);
                Add(update, "$description", (object?)description ?? DBNull.Value);
                Add(update, "$at", Store(command.OccurredAt));
                Add(update, "$tenant", command.TenantId);
                Add(update, "$id", command.Id);
                await update.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
            return (await GetTeamInConnectionAsync(connection, command.TenantId, command.Id, token))!;
        }, cancellationToken);

    public Task DeleteTeamAsync(TeamDeleteCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            string? name;
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = tx;
                query.CommandText = "SELECT name FROM teams WHERE tenant_id=$tenant AND id=$id;";
                Add(query, "$tenant", command.TenantId);
                Add(query, "$id", command.Id);
                name = (string?)await query.ExecuteScalarAsync(token);
            }
            if (name is null) throw new TeamSpecialtyCatalogNotFoundException("team");
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT EXISTS(SELECT 1 FROM specialties WHERE tenant_id=$tenant AND team_id=$id)," +
                    "EXISTS(SELECT 1 FROM agent_definitions WHERE tenant_id=$tenant AND team=$name);";
                Add(check, "$tenant", command.TenantId);
                Add(check, "$id", command.Id);
                Add(check, "$name", name);
                await using var reader = await check.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
                if (reader.GetInt64(0) != 0) throw new TeamSpecialtyCatalogConflictException("A team referenced by a specialty cannot be deleted.");
                if (reader.GetInt64(1) != 0) throw new TeamSpecialtyCatalogConflictException("A team referenced by an agent definition cannot be deleted.");
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM teams WHERE tenant_id=$tenant AND id=$id;";
                Add(delete, "$tenant", command.TenantId);
                Add(delete, "$id", command.Id);
                await delete.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
        }, cancellationToken);

    public Task<IReadOnlyList<SpecialtyRecord>> ListSpecialtiesAsync(string tenantId, string? teamId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<SpecialtyRecord>>(async (connection, token) =>
        {
            var values = new List<SpecialtyRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"{SpecialtySelect} WHERE tenant_id=$tenant AND ($team IS NULL OR team_id=$team) AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(command, "$tenant", tenantId);
            Add(command, "$team", teamId is null ? DBNull.Value : teamId);
            Add(command, "$after", afterId is null ? DBNull.Value : afterId);
            Add(command, "$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(ReadSpecialty(reader));
            return values;
        }, cancellationToken);

    public Task<SpecialtyRecord?> GetSpecialtyAsync(string tenantId, string specialtyId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{SpecialtySelect} WHERE tenant_id=$tenant AND id=$id;";
            Add(command, "$tenant", tenantId);
            Add(command, "$id", specialtyId);
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadSpecialty(reader) : null;
        }, cancellationToken);

    public Task<SpecialtyRecord> CreateSpecialtyAsync(SpecialtyCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            await GuardSpecialtyTeamAsync(connection, tx, command.TenantId, command.TeamId, token);
            await GuardSpecialtyUniquenessAsync(connection, tx, command.TenantId, name, key, null, token);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO specialties(id,tenant_id,specialty_key,name,description,team_id,created_at,updated_at) VALUES($id,$tenant,$key,$name,$description,$team,$at,$at);";
                Add(insert, "$id", command.Id);
                Add(insert, "$tenant", command.TenantId);
                Add(insert, "$key", key);
                Add(insert, "$name", name);
                Add(insert, "$description", (object?)description ?? DBNull.Value);
                Add(insert, "$team", (object?)command.TeamId ?? DBNull.Value);
                Add(insert, "$at", Store(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
            return (await GetSpecialtyInConnectionAsync(connection, command.TenantId, command.Id, token))!;
        }, cancellationToken);

    public Task<SpecialtyRecord> UpdateSpecialtyAsync(SpecialtyUpdateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            if (await GetSpecialtyInConnectionAsync(connection, command.TenantId, command.Id, token) is null)
                throw new TeamSpecialtyCatalogNotFoundException("specialty");
            await GuardSpecialtyTeamAsync(connection, tx, command.TenantId, command.TeamId, token);
            await GuardSpecialtyUniquenessAsync(connection, tx, command.TenantId, name, key, command.Id, token);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE specialties SET specialty_key=$key,name=$name,description=$description,team_id=$team,updated_at=$at WHERE tenant_id=$tenant AND id=$id;";
                Add(update, "$key", key);
                Add(update, "$name", name);
                Add(update, "$description", (object?)description ?? DBNull.Value);
                Add(update, "$team", (object?)command.TeamId ?? DBNull.Value);
                Add(update, "$at", Store(command.OccurredAt));
                Add(update, "$tenant", command.TenantId);
                Add(update, "$id", command.Id);
                await update.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
            return (await GetSpecialtyInConnectionAsync(connection, command.TenantId, command.Id, token))!;
        }, cancellationToken);

    public Task DeleteSpecialtyAsync(SpecialtyDeleteCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            string? name;
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = tx;
                query.CommandText = "SELECT name FROM specialties WHERE tenant_id=$tenant AND id=$id;";
                Add(query, "$tenant", command.TenantId);
                Add(query, "$id", command.Id);
                name = (string?)await query.ExecuteScalarAsync(token);
            }
            if (name is null) throw new TeamSpecialtyCatalogNotFoundException("specialty");
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_definitions WHERE tenant_id=$tenant AND specialty=$name);";
                Add(check, "$tenant", command.TenantId);
                Add(check, "$name", name);
                if (Convert.ToInt64(await check.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
                    throw new TeamSpecialtyCatalogConflictException("A specialty referenced by an agent definition cannot be deleted.");
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM specialties WHERE tenant_id=$tenant AND id=$id;";
                Add(delete, "$tenant", command.TenantId);
                Add(delete, "$id", command.Id);
                await delete.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
        }, cancellationToken);

    private static async Task GuardTeamUniquenessAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string name, string key, string? excludeId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM teams WHERE tenant_id=$tenant AND ($exclude IS NULL OR id<>$exclude) AND (lower(name)=lower($name) OR team_key=$key));";
        Add(command, "$tenant", tenant);
        Add(command, "$exclude", (object?)excludeId ?? DBNull.Value);
        Add(command, "$name", name);
        Add(command, "$key", key);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
            throw new TeamSpecialtyCatalogConflictException("A team with the same name or key already exists.");
    }

    private static async Task GuardSpecialtyUniquenessAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string name, string key, string? excludeId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM specialties WHERE tenant_id=$tenant AND ($exclude IS NULL OR id<>$exclude) AND (lower(name)=lower($name) OR specialty_key=$key));";
        Add(command, "$tenant", tenant);
        Add(command, "$exclude", (object?)excludeId ?? DBNull.Value);
        Add(command, "$name", name);
        Add(command, "$key", key);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
            throw new TeamSpecialtyCatalogConflictException("A specialty with the same name or key already exists.");
    }

    private static async Task GuardSpecialtyTeamAsync(SqliteConnection connection, SqliteTransaction tx, string tenant, string? teamId, CancellationToken token)
    {
        if (teamId is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM teams WHERE tenant_id=$tenant AND id=$id);";
        Add(command, "$tenant", tenant);
        Add(command, "$id", teamId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0)
            throw new TeamSpecialtyCatalogValidationException("The referenced team was not found.");
    }

    private static async Task<TeamRecord?> GetTeamInConnectionAsync(SqliteConnection connection, string tenant, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{TeamSelect} WHERE tenant_id=$tenant AND id=$id;";
        Add(command, "$tenant", tenant);
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadTeam(reader) : null;
    }

    private static async Task<SpecialtyRecord?> GetSpecialtyInConnectionAsync(SqliteConnection connection, string tenant, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SpecialtySelect} WHERE tenant_id=$tenant AND id=$id;";
        Add(command, "$tenant", tenant);
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadSpecialty(reader) : null;
    }

    private static (string Name, string Key, string? Description) Normalize(string name, string? key, string? description)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 200)
            throw new TeamSpecialtyCatalogValidationException("Name is required and must be at most 200 characters.");
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription is { Length: > 2000 })
            throw new TeamSpecialtyCatalogValidationException("Description must be at most 2000 characters.");
        var resolvedKey = string.IsNullOrWhiteSpace(key) ? AgentKeyGenerator.Derive(trimmedName) : key.Trim();
        if (resolvedKey.Length is 0 or > 100 || resolvedKey.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new TeamSpecialtyCatalogValidationException("Key must be a lowercase slug of at most 100 characters.");
        return (trimmedName, resolvedKey, trimmedDescription);
    }

    private static TeamRecord ReadTeam(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), Parse(reader.GetString(5)), Parse(reader.GetString(6)));

    private static SpecialtyRecord ReadSpecialty(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        Parse(reader.GetString(6)), Parse(reader.GetString(7)));

    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
}
