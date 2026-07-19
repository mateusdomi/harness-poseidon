using Harness.Persistence.Abstractions.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresLocalProfileStore(NpgsqlDataSource dataSource) : ILocalProfileStore
{
    private const string SelectSql =
        "SELECT tenant_id,id,display_name,email,avatar_url,locale,created_at,COALESCE(last_active_at,created_at),version,role FROM harness.local_users";

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<LocalProfileRecord?> GetAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, profileId, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalProfileRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = new List<LocalProfileRecord>();
        await using var command = _dataSource.CreateCommand($"{SelectSql} ORDER BY id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(Read(reader));
        }

        return profiles;
    }

    public Task<LocalProfileMutationResult> CreateAsync(
        LocalProfileCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public Task<LocalProfileMutationResult> UpdateAsync(
        LocalProfileUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateCoreAsync(command, cancellationToken);
    }

    private async Task<LocalProfileMutationResult> CreateCoreAsync(
        LocalProfileCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text(command.JoinExistingTenant
                ? $"local-profile:{command.TenantId}"
                : "local-profile:bootstrap"));
        if (command.JoinExistingTenant)
        {
            await using var tenantExists = connection.CreateCommand();
            tenantExists.Transaction = transaction;
            tenantExists.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.tenants WHERE id=$1);";
            tenantExists.Parameters.Add(Text(command.TenantId));
            if (!(bool)(await tenantExists.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not return tenant state.")))
            {
                await transaction.CommitAsync(cancellationToken);
                return new LocalProfileMutationResult(LocalProfileMutationStatus.NotFound);
            }
        }
        else
        {
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT EXISTS(SELECT 1 FROM harness.local_users);";
                if ((bool)(await exists.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("PostgreSQL did not return profile state.")))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new LocalProfileMutationResult(LocalProfileMutationStatus.AlreadyExists);
                }
            }

            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO harness.tenants (id, name, version, created_at) VALUES ($1, $2, 1, $3);",
                cancellationToken,
                Text(command.TenantId),
                Text(command.TenantName),
                Timestamp(command.OccurredAt));
        }
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.local_users
                (id, tenant_id, display_name, email, avatar_url, locale, last_active_at, version, created_at, role)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 1, $7, $8);
            """,
            cancellationToken,
            Text(command.ProfileId),
            Text(command.TenantId),
            Text(command.DisplayName),
            NullableText(command.Email),
            NullableText(command.AvatarUrl),
            Text(command.Locale),
            Timestamp(command.OccurredAt),
            Text(LocalProfileRoleCodec.ToStorage(
                command.JoinExistingTenant ? LocalProfileRole.Member : LocalProfileRole.Admin)));
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.profile_settings (tenant_id, id, profile_id, language, updated_at) VALUES ($1, $2, $2, $3, $4);",
            cancellationToken,
            Text(command.TenantId),
            Text(command.ProfileId),
            Text(command.Locale),
            Timestamp(command.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return new LocalProfileMutationResult(
            LocalProfileMutationStatus.Applied,
            await ReadAsync(connection, command.ProfileId, cancellationToken));
    }

    private async Task<LocalProfileMutationResult> UpdateCoreAsync(
        LocalProfileUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE harness.local_users
            SET display_name=$1,email=$2,avatar_url=$3,locale=$4,last_active_at=$5,version=version+1
            WHERE id=$6 AND version=$7;
            """;
        update.Parameters.Add(Text(command.DisplayName));
        update.Parameters.Add(NullableText(command.Email));
        update.Parameters.Add(NullableText(command.AvatarUrl));
        update.Parameters.Add(Text(command.Locale));
        update.Parameters.Add(Timestamp(command.OccurredAt));
        update.Parameters.Add(Text(command.ProfileId));
        update.Parameters.Add(Bigint(command.ExpectedVersion));
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return new LocalProfileMutationResult(
                LocalProfileMutationStatus.Applied,
                await ReadAsync(connection, command.ProfileId, cancellationToken));
        }

        return new LocalProfileMutationResult(
            await ReadAsync(connection, command.ProfileId, cancellationToken) is null
                ? LocalProfileMutationStatus.NotFound
                : LocalProfileMutationStatus.VersionConflict);
    }

    private static async Task<LocalProfileRecord?> ReadAsync(
        NpgsqlConnection connection,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectSql} WHERE id=$1;";
        command.Parameters.Add(Text(profileId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LocalProfileRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetInt64(8),
            LocalProfileRoleCodec.Parse(reader.GetString(9)));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };
}
