using Harness.Persistence.Abstractions.Agents;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Catálogo tenant-scoped de equipes/especialidades em PostgreSQL. Equivalente ao store
/// SQLite: paging por id, guarda referencial no delete e unicidade de nome/chave por tenant.
/// </summary>
public sealed class PostgresTeamSpecialtyCatalogStore(NpgsqlDataSource dataSource) : ITeamSpecialtyCatalogStore
{
    private const string TeamSelect = "SELECT id,tenant_id,team_key,name,description,created_at,updated_at FROM harness.teams";
    private const string SpecialtySelect = "SELECT id,tenant_id,specialty_key,name,description,team_id,created_at,updated_at FROM harness.specialties";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<TeamRecord>> ListTeamsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        var values = new List<TeamRecord>();
        await using var command = _dataSource.CreateCommand($"{TeamSelect} WHERE tenant_id=$1 AND ($2 IS NULL OR id>$2) ORDER BY id LIMIT $3;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(ReadTeam(reader));
        return values;
    }

    public async Task<TeamRecord?> GetTeamAsync(string tenantId, string teamId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"{TeamSelect} WHERE tenant_id=$1 AND id=$2;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(teamId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTeam(reader) : null;
    }

    public async Task<TeamRecord> CreateTeamAsync(TeamCreateCommand command, CancellationToken cancellationToken = default)
    {
        var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await GuardTeamUniquenessAsync(connection, tx, command.TenantId, name, key, null, cancellationToken);
        await ExecuteAsync(connection, tx,
            "INSERT INTO harness.teams(id,tenant_id,team_key,name,description,created_at,updated_at) VALUES($1,$2,$3,$4,$5,$6,$6);",
            cancellationToken, Text(command.Id), Text(command.TenantId), Text(key), Text(name), NullableText(description), Timestamp(command.OccurredAt));
        await tx.CommitAsync(cancellationToken);
        return (await GetTeamAsync(command.TenantId, command.Id, cancellationToken))!;
    }

    public async Task<TeamRecord> UpdateTeamAsync(TeamUpdateCommand command, CancellationToken cancellationToken = default)
    {
        var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        if (await ReadNameAsync(connection, tx, "harness.teams", command.TenantId, command.Id, cancellationToken) is null)
            throw new TeamSpecialtyCatalogNotFoundException("team");
        await GuardTeamUniquenessAsync(connection, tx, command.TenantId, name, key, command.Id, cancellationToken);
        await ExecuteAsync(connection, tx,
            "UPDATE harness.teams SET team_key=$1,name=$2,description=$3,updated_at=$4 WHERE tenant_id=$5 AND id=$6;",
            cancellationToken, Text(key), Text(name), NullableText(description), Timestamp(command.OccurredAt), Text(command.TenantId), Text(command.Id));
        await tx.CommitAsync(cancellationToken);
        return (await GetTeamAsync(command.TenantId, command.Id, cancellationToken))!;
    }

    public async Task DeleteTeamAsync(TeamDeleteCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var name = await ReadNameAsync(connection, tx, "harness.teams", command.TenantId, command.Id, cancellationToken)
            ?? throw new TeamSpecialtyCatalogNotFoundException("team");
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.specialties WHERE tenant_id=$1 AND team_id=$2)," +
                "EXISTS(SELECT 1 FROM harness.agent_definitions WHERE tenant_id=$1 AND team=$3);";
            check.Parameters.Add(Text(command.TenantId));
            check.Parameters.Add(Text(command.Id));
            check.Parameters.Add(Text(name));
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetBoolean(0)) throw new TeamSpecialtyCatalogConflictException("A team referenced by a specialty cannot be deleted.");
            if (reader.GetBoolean(1)) throw new TeamSpecialtyCatalogConflictException("A team referenced by an agent definition cannot be deleted.");
        }
        await ExecuteAsync(connection, tx, "DELETE FROM harness.teams WHERE tenant_id=$1 AND id=$2;",
            cancellationToken, Text(command.TenantId), Text(command.Id));
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SpecialtyRecord>> ListSpecialtiesAsync(string tenantId, string? teamId, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        var values = new List<SpecialtyRecord>();
        await using var command = _dataSource.CreateCommand($"{SpecialtySelect} WHERE tenant_id=$1 AND ($2 IS NULL OR team_id=$2) AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(NullableText(teamId));
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(ReadSpecialty(reader));
        return values;
    }

    public async Task<SpecialtyRecord?> GetSpecialtyAsync(string tenantId, string specialtyId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"{SpecialtySelect} WHERE tenant_id=$1 AND id=$2;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(specialtyId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSpecialty(reader) : null;
    }

    public async Task<SpecialtyRecord> CreateSpecialtyAsync(SpecialtyCreateCommand command, CancellationToken cancellationToken = default)
    {
        var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await GuardSpecialtyTeamAsync(connection, tx, command.TenantId, command.TeamId, cancellationToken);
        await GuardSpecialtyUniquenessAsync(connection, tx, command.TenantId, name, key, null, cancellationToken);
        await ExecuteAsync(connection, tx,
            "INSERT INTO harness.specialties(id,tenant_id,specialty_key,name,description,team_id,created_at,updated_at) VALUES($1,$2,$3,$4,$5,$6,$7,$7);",
            cancellationToken, Text(command.Id), Text(command.TenantId), Text(key), Text(name), NullableText(description), NullableText(command.TeamId), Timestamp(command.OccurredAt));
        await tx.CommitAsync(cancellationToken);
        return (await GetSpecialtyAsync(command.TenantId, command.Id, cancellationToken))!;
    }

    public async Task<SpecialtyRecord> UpdateSpecialtyAsync(SpecialtyUpdateCommand command, CancellationToken cancellationToken = default)
    {
        var (name, key, description) = Normalize(command.Name, command.Key, command.Description);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        if (await ReadNameAsync(connection, tx, "harness.specialties", command.TenantId, command.Id, cancellationToken) is null)
            throw new TeamSpecialtyCatalogNotFoundException("specialty");
        await GuardSpecialtyTeamAsync(connection, tx, command.TenantId, command.TeamId, cancellationToken);
        await GuardSpecialtyUniquenessAsync(connection, tx, command.TenantId, name, key, command.Id, cancellationToken);
        await ExecuteAsync(connection, tx,
            "UPDATE harness.specialties SET specialty_key=$1,name=$2,description=$3,team_id=$4,updated_at=$5 WHERE tenant_id=$6 AND id=$7;",
            cancellationToken, Text(key), Text(name), NullableText(description), NullableText(command.TeamId), Timestamp(command.OccurredAt), Text(command.TenantId), Text(command.Id));
        await tx.CommitAsync(cancellationToken);
        return (await GetSpecialtyAsync(command.TenantId, command.Id, cancellationToken))!;
    }

    public async Task DeleteSpecialtyAsync(SpecialtyDeleteCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var name = await ReadNameAsync(connection, tx, "harness.specialties", command.TenantId, command.Id, cancellationToken)
            ?? throw new TeamSpecialtyCatalogNotFoundException("specialty");
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.agent_definitions WHERE tenant_id=$1 AND specialty=$2);";
            check.Parameters.Add(Text(command.TenantId));
            check.Parameters.Add(Text(name));
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken))!)
                throw new TeamSpecialtyCatalogConflictException("A specialty referenced by an agent definition cannot be deleted.");
        }
        await ExecuteAsync(connection, tx, "DELETE FROM harness.specialties WHERE tenant_id=$1 AND id=$2;",
            cancellationToken, Text(command.TenantId), Text(command.Id));
        await tx.CommitAsync(cancellationToken);
    }

    private static async Task<string?> ReadNameAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, string tenant, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT name FROM {table} WHERE tenant_id=$1 AND id=$2 FOR UPDATE;";
        command.Parameters.Add(Text(tenant));
        command.Parameters.Add(Text(id));
        return (string?)await command.ExecuteScalarAsync(token);
    }

    private static async Task GuardTeamUniquenessAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, string name, string key, string? excludeId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.teams WHERE tenant_id=$1 AND ($2 IS NULL OR id<>$2) AND (lower(name)=lower($3) OR team_key=$4));";
        command.Parameters.Add(Text(tenant));
        command.Parameters.Add(NullableText(excludeId));
        command.Parameters.Add(Text(name));
        command.Parameters.Add(Text(key));
        if ((bool)(await command.ExecuteScalarAsync(token))!)
            throw new TeamSpecialtyCatalogConflictException("A team with the same name or key already exists.");
    }

    private static async Task GuardSpecialtyUniquenessAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, string name, string key, string? excludeId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.specialties WHERE tenant_id=$1 AND ($2 IS NULL OR id<>$2) AND (lower(name)=lower($3) OR specialty_key=$4));";
        command.Parameters.Add(Text(tenant));
        command.Parameters.Add(NullableText(excludeId));
        command.Parameters.Add(Text(name));
        command.Parameters.Add(Text(key));
        if ((bool)(await command.ExecuteScalarAsync(token))!)
            throw new TeamSpecialtyCatalogConflictException("A specialty with the same name or key already exists.");
    }

    private static async Task GuardSpecialtyTeamAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant, string? teamId, CancellationToken token)
    {
        if (teamId is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.teams WHERE tenant_id=$1 AND id=$2);";
        command.Parameters.Add(Text(tenant));
        command.Parameters.Add(Text(teamId));
        if (!(bool)(await command.ExecuteScalarAsync(token))!)
            throw new TeamSpecialtyCatalogValidationException("The referenced team was not found.");
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

    private static TeamRecord ReadTeam(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6));

    private static SpecialtyRecord ReadSpecialty(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5).TrimEnd(),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7));

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken token, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(token);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };
    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new() { TypedValue = value };
    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };
}
